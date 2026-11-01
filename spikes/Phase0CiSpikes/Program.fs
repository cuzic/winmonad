// Phase 0 feasibility spikes that CAN be verified on a GitHub Actions
// windows-latest runner without a real interactive desktop session.
// See /docs/spikes.md for the full spike plan this implements a subset of
// (S1, S2, S3, S4, S5, S6, S9). Results are printed to stdout AND written
// to phase0-ci-results.md for the "Upload results artifact" workflow step.
//
// This is throwaway verification code, not part of the eventual
// ThrowawayWm (Phase 0 deliverable) or the real Core/Shell (Phase A/B).
module Phase0CiSpikes.Program

open System
open System.Diagnostics
open System.Drawing
open System.IO
open System.Runtime.InteropServices
open System.Text
open System.Threading
open System.Threading.Tasks
open System.Windows.Forms

module Native =

    [<StructLayout(LayoutKind.Sequential)>]
    type RECT =
        struct
            val mutable Left: int
            val mutable Top: int
            val mutable Right: int
            val mutable Bottom: int
        end

    [<DllImport("user32.dll", SetLastError = true)>]
    extern bool RegisterHotKey(IntPtr hWnd, int id, uint32 fsModifiers, uint32 vk)

    [<DllImport("user32.dll")>]
    extern bool UnregisterHotKey(IntPtr hWnd, int id)

    [<DllImport("user32.dll")>]
    extern bool SetForegroundWindow(IntPtr hWnd)

    [<DllImport("user32.dll")>]
    extern IntPtr GetForegroundWindow()

    [<DllImport("user32.dll")>]
    extern bool GetWindowRect(IntPtr hWnd, RECT& lpRect)

    [<DllImport("dwmapi.dll")>]
    extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, RECT& pvAttribute, int cbAttribute)

    [<DllImport("user32.dll")>]
    extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint32 uFlags)

    [<DllImport("user32.dll")>]
    extern IntPtr BeginDeferWindowPos(int nNumWindows)

    [<DllImport("user32.dll")>]
    extern IntPtr DeferWindowPos(IntPtr hWinPosInfo, IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint32 uFlags)

    [<DllImport("user32.dll")>]
    extern bool EndDeferWindowPos(IntPtr hWinPosInfo)

    [<DllImport("user32.dll")>]
    extern bool IsHungAppWindow(IntPtr hwnd)

    [<DllImport("user32.dll")>]
    extern IntPtr SendMessageTimeout(IntPtr hWnd, uint32 Msg, IntPtr wParam, IntPtr lParam, uint32 fuFlags, uint32 uTimeout, IntPtr& lpdwResult)

    [<DllImport("shell32.dll")>]
    extern int SHQueryUserNotificationState(int& pquns)

    [<DllImport("user32.dll")>]
    extern int GetSystemMetrics(int nIndex)

    [<DllImport("user32.dll", CharSet = CharSet.Unicode)>]
    extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount)

    [<DllImport("user32.dll")>]
    extern uint32 GetWindowThreadProcessId(IntPtr hWnd, int& lpdwProcessId)

    type EnumChildProc = delegate of IntPtr * IntPtr -> bool

    [<DllImport("user32.dll")>]
    extern bool EnumChildWindows(IntPtr hWndParent, EnumChildProc lpEnumFunc, IntPtr lParam)

    [<DllImport("user32.dll")>]
    extern void mouse_event(uint32 dwFlags, int dx, int dy, uint32 dwData, UIntPtr dwExtraInfo)

    [<DllImport("kernel32.dll")>]
    extern uint32 SetErrorMode(uint32 uMode)

open Native

/// Suppresses the "<app> has stopped working" / Windows Error Reporting UI.
/// Without this, an unhandled native-level crash can pop a dialog that
/// blocks the process (and thus our parent's `ReadLine`) forever on a
/// headless CI runner with nobody to dismiss it. Must be called by both
/// the parent and the --hang-child process (i.e. unconditionally, before
/// branching on argv).
let suppressCrashDialogs () =
    let SEM_FAILCRITICALERRORS = 0x0001u
    let SEM_NOGPFAULTERRORBOX = 0x0002u
    SetErrorMode(SEM_FAILCRITICALERRORS ||| SEM_NOGPFAULTERRORBOX) |> ignore

// ===== small shared infrastructure =====

let resultsFile = "phase0-ci-results.md"
let results = ResizeArray<string>()
let mutable lastProgressTicks = DateTime.UtcNow.Ticks

let log (s: string) =
    Console.Out.WriteLine(s: string)
    Console.Out.Flush()
    results.Add(s)
    Volatile.Write(&lastProgressTicks, DateTime.UtcNow.Ticks)
    // Flush to disk after every line so a later hang still leaves everything
    // observed so far on disk for the "Show results"/upload-artifact steps,
    // instead of losing the whole run's output (as happened the first time).
    try File.WriteAllLines(resultsFile, results) with _ -> ()

let heading (s: string) = log ("\n## " + s)

let tryRun (name: string) (f: unit -> unit) =
    heading name
    try
        f ()
    with ex ->
        log (sprintf "ERROR: %s" (ex.ToString()))

/// Force-exits the whole process if no `log` call has happened for
/// `stallSeconds`, after writing a diagnostic line. This is a backstop
/// against a repeat of a spike hanging the *calling* (STA/UI) thread itself
/// (e.g. inside Application.DoEvents()), which measureWithTimeout cannot
/// protect against since there is nothing to Task.Run in that case.
let startWatchdog (stallSeconds: int) =
    let t =
        Thread(fun () ->
            while true do
                Thread.Sleep(2000)
                let idleMs = (DateTime.UtcNow.Ticks - Volatile.Read(&lastProgressTicks)) / TimeSpan.TicksPerMillisecond
                if idleMs > int64 stallSeconds * 1000L then
                    results.Add(sprintf "\nWATCHDOG: no progress for %dms, force-exiting the whole harness to avoid wasting the CI job timeout." idleMs)
                    (try File.WriteAllLines(resultsFile, results) with _ -> ())
                    Environment.Exit(2)
        )
    t.IsBackground <- true
    t.Start()

let pump (times: int) =
    for _ in 1 .. times do
        Application.DoEvents()
        Thread.Sleep(20)

/// Runs `action` on a background thread and waits up to `timeoutMs`.
/// Returns (returnedInTime, elapsedMs). The background thread may still be
/// blocked after this returns if the call genuinely hangs; callers must
/// kill any associated hung child process afterwards to release it.
let measureWithTimeout (timeoutMs: int) (action: unit -> 'a) : bool * int64 =
    let sw = Stopwatch.StartNew()
    let task = Task.Run<'a>(Func<'a>(action))
    let finished = task.Wait(timeoutMs)
    sw.Stop()
    (finished, sw.ElapsedMilliseconds)

let killProcessSafely (p: Process) =
    try
        if not p.HasExited then
            p.Kill()
    with _ -> ()

// ===== child-hang helper process mode =====
// Launched as a copy of this same exe with --hang-child. Creates a window,
// pumps a few messages so it is fully created and visible, prints its HWND,
// then stops pumping entirely (simulating an unresponsive app).
// Must never throw past this function and must always print *something*
// and flush, or the parent's bounded read (see readLineWithTimeout) is the
// only thing standing between us and an indefinite wait.
let runHangChild () =
    try
        let form =
            new Form(Text = "CI_HUNG_WINDOW", StartPosition = FormStartPosition.Manual, Bounds = Rectangle(50, 50, 300, 200))
        form.Show()
        pump 10
        printfn "HWND=%d" (int64 form.Handle)
        Console.Out.Flush()
        Thread.Sleep(50000)
    with ex ->
        printfn "ERROR=%s" (ex.Message.Replace("\n", " ").Replace("\r", ""))
        Console.Out.Flush()
        Environment.Exit(1)

/// Reads one line from `reader` on a background thread, waiting at most
/// `timeoutMs`. Returns None (instead of blocking forever) if the child
/// never writes a line and never closes the stream.
let readLineWithTimeout (reader: IO.StreamReader) (timeoutMs: int) : string option =
    let task = Task.Run<string>(Func<string>(fun () -> reader.ReadLine()))
    if task.Wait(timeoutMs) then Some task.Result else None

let startHangChild (exePath: string) =
    let psi = ProcessStartInfo(exePath, "--hang-child")
    psi.RedirectStandardOutput <- true
    psi.UseShellExecute <- false
    let p = Process.Start(psi)
    let mutable hwnd = IntPtr.Zero
    match readLineWithTimeout p.StandardOutput 8000 with
    | Some l when l <> null && l.StartsWith("HWND=") ->
        let v = Int64.Parse(l.Substring(5))
        hwnd <- IntPtr(v)
    | Some l when l <> null && l.StartsWith("ERROR=") ->
        log (sprintf "- --hang-child reported an error: %s" (l.Substring(6)))
    | Some _ -> ()
    | None -> log "- --hang-child did not print anything within 8000ms (treating as unavailable, not as a true block)"
    (p, hwnd)

// ===== spikes =====

// S3: which hotkey combinations can RegisterHotKey actually secure on this runner.
let spikeS3_RegisterHotKey () =
    let MOD_ALT = 0x0001u
    let MOD_SHIFT = 0x0004u
    let MOD_WIN = 0x0008u
    let VK_RETURN = 0x0Du
    let VK_L = 0x4Cu
    let VK_J = 0x4Au
    let VK_1 = 0x31u

    let candidates =
        [ "Alt+J", MOD_ALT, VK_J
          "Alt+Shift+Return", (MOD_ALT ||| MOD_SHIFT), VK_RETURN
          "Alt+Shift+1", (MOD_ALT ||| MOD_SHIFT), VK_1
          "Win+Return", MOD_WIN, VK_RETURN
          "Win+1", MOD_WIN, VK_1
          "Win+L", MOD_WIN, VK_L ]

    let mutable id = 1
    for (name, modif, vk) in candidates do
        let ok = RegisterHotKey(IntPtr.Zero, id, modif, vk)
        if ok then
            log (sprintf "- %s: OK (registered)" name)
            UnregisterHotKey(IntPtr.Zero, id) |> ignore
        else
            let err = Marshal.GetLastWin32Error()
            log (sprintf "- %s: FAILED (GetLastError=%d)" name err)
        id <- id + 1

// S1: GetWindowRect vs DwmGetWindowAttribute(DWMWA_EXTENDED_FRAME_BOUNDS) diff.
let spikeS1_RectDiff () =
    let DWMWA_EXTENDED_FRAME_BOUNDS = 9

    let measure (hwnd: IntPtr) (label: string) =
        let mutable r1 = RECT()
        let got1 = GetWindowRect(hwnd, &r1)
        let mutable r2 = RECT()
        let hr = DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, &r2, Marshal.SizeOf(typeof<RECT>))
        if got1 && hr = 0 then
            log (
                sprintf
                    "- %s: GetWindowRect=(%d,%d,%d,%d) DWM=(%d,%d,%d,%d) diff(L,T,R,B)=(%d,%d,%d,%d)"
                    label
                    r1.Left r1.Top r1.Right r1.Bottom
                    r2.Left r2.Top r2.Right r2.Bottom
                    (r2.Left - r1.Left) (r2.Top - r1.Top) (r1.Right - r2.Right) (r1.Bottom - r2.Bottom)
            )
        else
            log (sprintf "- %s: GetWindowRect=%b DwmGetWindowAttribute hr=0x%08x" label got1 hr)

    use form =
        new Form(Text = "S1", StartPosition = FormStartPosition.Manual, Bounds = Rectangle(100, 100, 400, 300))
    form.Show()
    pump 5
    measure form.Handle "own WinForms window (resizable, no maximize yet)"

    try
        let p = Process.Start("notepad.exe")
        Thread.Sleep(1500)
        p.Refresh()
        if p.MainWindowHandle <> IntPtr.Zero then
            measure p.MainWindowHandle "notepad.exe"
        else
            log "- notepad.exe: no MainWindowHandle found within timeout"
        killProcessSafely p
    with ex ->
        log (sprintf "- notepad.exe: ERROR %s" ex.Message)

    try
        let p = Process.Start("calc.exe")
        Thread.Sleep(2000)
        p.Refresh()
        if p.MainWindowHandle <> IntPtr.Zero then
            measure p.MainWindowHandle "calc.exe (direct MainWindowHandle)"
        else
            log "- calc.exe: no MainWindowHandle found within timeout (see S5 for UWP host details)"
        killProcessSafely p
    with ex ->
        log (sprintf "- calc.exe: ERROR %s" ex.Message)

// S5: does calc.exe resolve to ApplicationFrameHost.exe with a CoreWindow child, or run as itself.
let spikeS5_Uwp () =
    try
        let p = Process.Start("calc.exe")
        Thread.Sleep(2500)
        p.Refresh()
        let topHwnd = p.MainWindowHandle
        if topHwnd = IntPtr.Zero then
            log "- calc.exe: no top-level window detected within timeout (process may not exist on this runner image)"
        else
            let topClass = StringBuilder(256)
            GetClassName(topHwnd, topClass, 256) |> ignore
            log (sprintf "- calc.exe: owning process name=%s, top-level window class=%s" p.ProcessName (topClass.ToString()))

            let found = ResizeArray<IntPtr>()
            let cb = EnumChildProc(fun h _ -> found.Add(h); true)
            EnumChildWindows(topHwnd, cb, IntPtr.Zero) |> ignore
            log (sprintf "- calc.exe: %d direct child window(s) found" found.Count)
            for h in found do
                let cls = StringBuilder(256)
                GetClassName(h, cls, 256) |> ignore
                let mutable pid = 0
                GetWindowThreadProcessId(h, &pid) |> ignore
                let procName =
                    try (Process.GetProcessById(pid)).ProcessName with _ -> "?"
                log (sprintf "  - child hwnd class=%s owning pid=%d process=%s" (cls.ToString()) pid procName)
        killProcessSafely p
    with ex ->
        log (sprintf "- calc.exe: ERROR %s" ex.Message)

// S9: a borderless window sized to the whole screen -- does SHQueryUserNotificationState
// flag it, and can it be identified purely from window geometry/style?
let spikeS9_Fullscreen () =
    let SM_CXSCREEN = 0
    let SM_CYSCREEN = 1
    let sw = GetSystemMetrics(SM_CXSCREEN)
    let sh = GetSystemMetrics(SM_CYSCREEN)

    use form =
        new Form(
            Text = "S9",
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual,
            Bounds = Rectangle(0, 0, sw, sh)
        )
    form.Show()
    pump 5

    let mutable state = 0
    let hr = SHQueryUserNotificationState(&state)
    log (sprintf "- borderless window sized to screen (%dx%d): SHQueryUserNotificationState hr=0x%08x state=%d" sw sh hr state)

    let mutable r = RECT()
    GetWindowRect(form.Handle, &r) |> ignore
    let matchesScreen = (r.Left = 0 && r.Top = 0 && (r.Right - r.Left) = sw && (r.Bottom - r.Top) = sh)
    log (sprintf "- geometry heuristic (borderless AND rect == monitor rect) matches: %b" matchesScreen)

// S4: SetForegroundWindow permission after a simulated async-queue delay, with and
// without the komorebi/GlazeWM self-priming trick (here using the simpler
// single-purpose mouse_event instead of SendInput, since it needs no
// MOUSEINPUT/INPUT union marshaling, but still posts a real mouse input
// event like SendInput(INPUT_MOUSE) does).
// NOTE: an earlier version of this spike used keybd_event(VK_MENU, ...) (Alt)
// as the "self-priming" input and, on real CI hardware, hung for the rest of
// the job: injecting a bare Alt keydown/keyup makes Windows enter menu-loop
// handling (WM_SYSKEYDOWN / WM_ENTERMENULOOP) on the active window, and
// Application.DoEvents() does not return out of that internal modal loop
// with nothing (no Escape, no click) to dismiss it. komorebi/GlazeWM avoid
// this entirely by using a *mouse* input event for the self-priming trick
// (see docs/feasibility.md §2) -- this is why that choice matters in
// practice, not just in theory.
let spikeS4_ForegroundWindow (exePath: string) =
    let MOUSEEVENTF_MOVE = 0x0001u

    use formB = new Form(Text = "B", StartPosition = FormStartPosition.Manual, Bounds = Rectangle(250, 0, 200, 150))
    formB.Show()
    pump 5

    // Each trial gives the foreground to a genuinely different *process*
    // first, so that "can we steal it back" is a real test and not trivially
    // satisfied by formB belonging to the already-foreground process.
    let runTrial (label: string) (prime: unit -> unit) =
        let (p, hwndOther) = startHangChild exePath
        try
            if hwndOther = IntPtr.Zero then
                log (sprintf "- %s: could not start the other-process window, skipping this trial" label)
            else
                SetForegroundWindow(hwndOther) |> ignore
                pump 3
                Thread.Sleep(300) // simulate queue -> Reducer -> Effect latency before RequestForeground is applied
                prime ()
                let ok = SetForegroundWindow(formB.Handle)
                pump 3
                let fg = GetForegroundWindow()
                log (sprintf "- %s: API returned=%b, actually foreground=%b" label ok (fg = formB.Handle))
        finally
            killProcessSafely p

    runTrial "plain SetForegroundWindow, 300ms after another process took the foreground" (fun () -> ())
    runTrial
        "mouse_event self-priming + SetForegroundWindow, 300ms after another process took the foreground"
        (fun () -> mouse_event (MOUSEEVENTF_MOVE, 0, 0, 0u, UIntPtr.Zero))

// S2: hang detection -- does SetWindowPos block on an unresponsive window, does
// SWP_ASYNCWINDOWPOS avoid that, and how do IsHungAppWindow / SendMessageTimeout behave.
let spikeS2_Hang (exePath: string) =
    let SWP_NOZORDER = 0x0004u
    let SWP_ASYNCWINDOWPOS = 0x4000u
    let SMTO_ABORTIFHUNG = 0x0002u
    let WM_NULL = 0x0000u

    let (p, hwnd) = startHangChild exePath
    try
        if hwnd = IntPtr.Zero then
            log "- failed to obtain hung child window handle"
        else
            log (sprintf "- hung child window handle=%d (pid=%d)" (int64 hwnd) p.Id)

            // Windows' internal "not responding" heuristic needs ~5s of no PeekMessage/GetMessage.
            Thread.Sleep(6000)
            let hung = IsHungAppWindow(hwnd)
            log (sprintf "- IsHungAppWindow after 6s with no message pumping: %b" hung)

            let mutable res = IntPtr.Zero
            let sw1 = Stopwatch.StartNew()
            let r = SendMessageTimeout(hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, 2000u, &res)
            sw1.Stop()
            log (sprintf "- SendMessageTimeout(WM_NULL, SMTO_ABORTIFHUNG, 2000ms): returned=%d elapsedMs=%d" (int64 r) sw1.ElapsedMilliseconds)

            let (finished, ms) =
                measureWithTimeout 4000 (fun () -> SetWindowPos(hwnd, IntPtr.Zero, 10, 10, 300, 200, SWP_NOZORDER))
            log (
                sprintf
                    "- SetWindowPos WITHOUT SWP_ASYNCWINDOWPOS: %s (waited %dms)"
                    (if finished then "returned" else "did NOT return within the 4000ms measurement cap")
                    ms
            )

            let (finished2, ms2) =
                measureWithTimeout 4000 (fun () -> SetWindowPos(hwnd, IntPtr.Zero, 10, 10, 300, 200, SWP_NOZORDER ||| SWP_ASYNCWINDOWPOS))
            log (
                sprintf
                    "- SetWindowPos WITH SWP_ASYNCWINDOWPOS: %s (waited %dms)"
                    (if finished2 then "returned" else "did NOT return within the 4000ms measurement cap")
                    ms2
            )
    finally
        killProcessSafely p

// S6: DeferWindowPos batch vs individual SetWindowPos+SWP_ASYNCWINDOWPOS, with one
// window in the batch being hung, to see whether the hung window stalls the others.
let spikeS6_BatchApply (exePath: string) =
    let SWP_NOZORDER = 0x0004u
    let SWP_ASYNCWINDOWPOS = 0x4000u

    use formA = new Form(Text = "BatchA", StartPosition = FormStartPosition.Manual, Bounds = Rectangle(0, 0, 150, 150))
    use formB = new Form(Text = "BatchB", StartPosition = FormStartPosition.Manual, Bounds = Rectangle(200, 0, 150, 150))
    formA.Show()
    formB.Show()
    pump 5

    let (p, hwndH) = startHangChild exePath
    try
        if hwndH = IntPtr.Zero then
            log "- failed to obtain hung child window handle for batch test"
        else
            Thread.Sleep(500)

            let (finished, ms) =
                measureWithTimeout 4000 (fun () ->
                    let hdwp0 = BeginDeferWindowPos(3)
                    let hdwp1 = DeferWindowPos(hdwp0, formA.Handle, IntPtr.Zero, 10, 10, 150, 150, SWP_NOZORDER)
                    let hdwp2 = DeferWindowPos(hdwp1, formB.Handle, IntPtr.Zero, 220, 10, 150, 150, SWP_NOZORDER)
                    let hdwp3 = DeferWindowPos(hdwp2, hwndH, IntPtr.Zero, 430, 10, 150, 150, SWP_NOZORDER)
                    EndDeferWindowPos(hdwp3))
            log (
                sprintf
                    "- DeferWindowPos batch incl. hung window: %s (waited %dms)"
                    (if finished then "returned" else "did NOT return within the 4000ms measurement cap")
                    ms
            )
    finally
        killProcessSafely p

    // Fresh hung child for the second half, since the first may have left the batch
    // call's background thread still blocked against the old one.
    let (p2, hwndH2) = startHangChild exePath
    try
        if hwndH2 = IntPtr.Zero then
            log "- failed to obtain hung child window handle for individual-apply test"
        else
            Thread.Sleep(500)
            let flags = SWP_NOZORDER ||| SWP_ASYNCWINDOWPOS
            let (finishedA, msA) = measureWithTimeout 2000 (fun () -> SetWindowPos(formA.Handle, IntPtr.Zero, 15, 15, 150, 150, flags))
            let (finishedB, msB) = measureWithTimeout 2000 (fun () -> SetWindowPos(formB.Handle, IntPtr.Zero, 225, 15, 150, 150, flags))
            let (finishedH, msH) = measureWithTimeout 2000 (fun () -> SetWindowPos(hwndH2, IntPtr.Zero, 435, 15, 150, 150, flags))
            log (
                sprintf
                    "- individual SetWindowPos+SWP_ASYNCWINDOWPOS: A %s(%dms) B %s(%dms) H(hung) %s(%dms)"
                    (if finishedA then "OK" else "TIMEOUT") msA
                    (if finishedB then "OK" else "TIMEOUT") msB
                    (if finishedH then "OK" else "TIMEOUT") msH
            )
    finally
        killProcessSafely p2

[<STAThread>]
[<EntryPoint>]
let main argv =
    suppressCrashDialogs ()
    if argv.Length > 0 && argv.[0] = "--hang-child" then
        runHangChild ()
        0
    else
        let exePath = Process.GetCurrentProcess().MainModule.FileName
        // Backstop: if any single spike blocks the STA thread itself (not just
        // a background Task we could time out), force-exit after 60s of no
        // `log` progress instead of riding out the job's 15-minute timeout
        // with nothing recorded, as happened the first time this was run.
        startWatchdog 60

        log "# Phase 0 CI実機検証結果"
        log (sprintf "- OS: %s" (Environment.OSVersion.ToString()))
        log (sprintf "- 64bit OS: %b" Environment.Is64BitOperatingSystem)
        log (sprintf "- 実行日時(UTC): %s" (DateTime.UtcNow.ToString("o")))
        log (sprintf "- exePath: %s" exePath)

        tryRun "S3 RegisterHotKey競合" spikeS3_RegisterHotKey
        tryRun "S1 可視フレームとウィンドウ矩形の差分" spikeS1_RectDiff
        tryRun "S5 UWP(calc.exe)のトップレベル窓・子窓" spikeS5_Uwp
        tryRun "S9 全画面相当ウィンドウの検出" spikeS9_Fullscreen
        tryRun "S4 SetForegroundWindowの許可条件" (fun () -> spikeS4_ForegroundWindow exePath)
        tryRun "S2 ハング検知とEffect適用" (fun () -> spikeS2_Hang exePath)
        tryRun "S6 バッチ適用(DeferWindowPos vs 個別SetWindowPos+ASYNC)" (fun () -> spikeS6_BatchApply exePath)

        log "\n(all spikes finished without the watchdog firing)"
        0
