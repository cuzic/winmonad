// Phase 0 feasibility spikes that CAN be verified on a GitHub Actions
// windows-latest runner without a real interactive desktop session.
// See /docs/spikes.md for the full spike plan this implements a subset of
// (S1, S2, S2b, S2c, S3, S4, S4r, S5, S5r, S6, S7, S7r, S9, S10, S11r).
// S2b/S2c were added to ground two ADR-014 assumptions (the 50ms hang
// pre-check timeout, and RestoreBeforeMove's ShowWindowAsync choice) in
// a real CI run instead of a textual guess. S7/S10 probe this machine's
// own privilege/shell environment (process integrity level; whether
// explorer.exe exists and auto-restarts) rather than Win32 call
// semantics. The *r-suffixed spikes (S4r/S5r/S7r/S11r) are real-desktop-
// only: they need a genuine interactive desktop (an existing third-party
// foreground window, a modern UWP shell, an already-running elevated
// window, multiple monitors at different DPI) that a GitHub Actions
// windows-latest runner structurally cannot provide -- they report
// themselves as inconclusive rather than failing when run somewhere
// that can't exercise them. Results are printed to stdout AND written
// to phase0-ci-results.md for the "Upload results artifact" workflow
// step (or read directly from that file when run outside CI).
//
// This is throwaway verification code, not part of the eventual
// ThrowawayWm (Phase 0 deliverable) or the real Core/Shell (Phase A/B).
module Phase0CiSpikes.Program

open System
open System.Diagnostics
open System.Drawing
open System.IO
open System.Runtime.InteropServices
open System.Security.Principal
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
    extern bool ShowWindow(IntPtr hWnd, int nCmdShow)

    [<DllImport("user32.dll")>]
    extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow)

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

    // S7: process integrity level (no UAC prompt, no process spawn -- safe to run in CI)
    [<DllImport("advapi32.dll", SetLastError = true)>]
    extern bool OpenProcessToken(IntPtr ProcessHandle, uint32 DesiredAccess, IntPtr& TokenHandle)

    [<DllImport("advapi32.dll", SetLastError = true)>]
    extern bool GetTokenInformation(IntPtr TokenHandle, uint32 TokenInformationClass, byte[] TokenInformation, uint32 TokenInformationLength, uint32& ReturnLength)

    [<DllImport("kernel32.dll")>]
    extern IntPtr GetCurrentProcess()

    [<DllImport("kernel32.dll")>]
    extern bool CloseHandle(IntPtr hObject)

    [<DllImport("kernel32.dll")>]
    extern uint32 SetErrorMode(uint32 uMode)

    // ===== real-desktop-only spikes (S4r/S5r/S7r/S10/S11r) =====
    [<DllImport("user32.dll")>]
    extern bool EnumWindows(EnumChildProc lpEnumFunc, IntPtr lParam) // same callback shape as EnumChildWindows

    [<DllImport("user32.dll")>]
    extern bool IsWindowVisible(IntPtr hWnd)

    [<DllImport("user32.dll", CharSet = CharSet.Unicode)>]
    extern int GetWindowTextLength(IntPtr hWnd)

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern IntPtr OpenProcess(uint32 dwDesiredAccess, bool bInheritHandle, int dwProcessId)

    type MonitorEnumProc = delegate of IntPtr * IntPtr * IntPtr * IntPtr -> bool

    [<DllImport("user32.dll")>]
    extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData)

    [<DllImport("shcore.dll")>]
    extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, uint32& dpiX, uint32& dpiY)

    [<StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)>]
    type NOTIFYICONDATA =
        struct
            val mutable cbSize: int
            val mutable hWnd: IntPtr
            val mutable uID: int
            val mutable uFlags: int
            val mutable uCallbackMessage: int
            val mutable hIcon: IntPtr
            [<MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)>]
            val mutable szTip: string
        end

    [<DllImport("shell32.dll", CharSet = CharSet.Unicode)>]
    extern bool Shell_NotifyIcon(int dwMessage, NOTIFYICONDATA& lpdata)

    [<DllImport("user32.dll", CharSet = CharSet.Unicode)>]
    extern uint32 RegisterWindowMessage(string lpString)

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
let runHangChild (maximized: bool) =
    try
        let form =
            new Form(Text = "CI_HUNG_WINDOW", StartPosition = FormStartPosition.Manual, Bounds = Rectangle(50, 50, 300, 200))
        form.Show()
        pump 10
        if maximized then
            form.WindowState <- FormWindowState.Maximized
            pump 10 // let the maximize actually take effect before we stop pumping
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

let private startHangChildWith (args: string) (exePath: string) =
    let psi = ProcessStartInfo(exePath, args)
    psi.RedirectStandardOutput <- true
    psi.UseShellExecute <- false
    let p = Process.Start(psi)
    let mutable hwnd = IntPtr.Zero
    match readLineWithTimeout p.StandardOutput 8000 with
    | Some l when l <> null && l.StartsWith("HWND=") ->
        let v = Int64.Parse(l.Substring(5))
        hwnd <- IntPtr(v)
    | Some l when l <> null && l.StartsWith("ERROR=") ->
        log (sprintf "- %s reported an error: %s" args (l.Substring(6)))
    | Some _ -> ()
    | None -> log (sprintf "- %s did not print anything within 8000ms (treating as unavailable, not as a true block)" args)
    (p, hwnd)

let startHangChild (exePath: string) = startHangChildWith "--hang-child" exePath

// v2.4で追加（R2-4の再検証）：最大化状態のまま応答停止する子窓。ShowWindow(SW_RESTORE)が
// 実際に状態を変える操作としてブロックするかを見るため、通常状態のままの子窓とは別に用意する。
let startHangChildMaximized (exePath: string) = startHangChildWith "--hang-child-maximized" exePath

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

// S2b: how precisely does SendMessageTimeout honor short timeouts before vs after the
// OS's internal ~5s "not responding" judgment? architecture.md ADR-014 currently assumes
// a 50ms pre-check timeout without having measured it -- this spike measures real
// behavior at several timeout values, before and after the OS's hang judgment, so the
// design can use a real number instead of a guess.
let spikeS2b_PreCheckTiming (exePath: string) =
    let SMTO_ABORTIFHUNG = 0x0002u
    let WM_NULL = 0x0000u

    let probe (hwnd: IntPtr) (timeoutMs: uint32) (label: string) =
        let mutable res = IntPtr.Zero
        let sw = Stopwatch.StartNew()
        let r = SendMessageTimeout(hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, timeoutMs, &res)
        sw.Stop()
        log (sprintf "- %s, requested timeout=%dms: returned=%d elapsedMs=%d" label timeoutMs (int64 r) sw.ElapsedMilliseconds)

    let (p, hwnd) = startHangChild exePath
    try
        if hwnd = IntPtr.Zero then
            log "- failed to obtain hung child window handle"
        else
            Thread.Sleep(1000) // well under the OS's ~5s "not responding" threshold
            for t in [ 10u; 50u; 100u; 500u ] do
                probe hwnd t "BEFORE OS hang-judgment (~1s since last message pump)"
    finally
        killProcessSafely p

    let (p2, hwnd2) = startHangChild exePath
    try
        if hwnd2 = IntPtr.Zero then
            log "- failed to obtain hung child window handle (2nd trial)"
        else
            Thread.Sleep(6000) // past the OS's ~5s "not responding" threshold
            for t in [ 10u; 50u; 100u; 500u ] do
                probe hwnd2 t "AFTER OS hang-judgment (~6s since last message pump)"
    finally
        killProcessSafely p2

// S2c: RestoreBeforeMove (architecture.md 7.1) is specified as ShowWindowAsync(SW_RESTORE)
// rather than the synchronous ShowWindow, on the same reasoning as SWP_ASYNCWINDOWPOS for
// SetWindowPos (S2) -- this spike actually tests that assumption instead of just asserting it.
// The child window is maximized before it stops responding, so SW_RESTORE is a real
// state-changing operation and not a no-op (a no-op could return instantly regardless of
// Async, which would not actually test anything).
let spikeS2c_RestoreAsync (exePath: string) =
    let SW_RESTORE = 9

    let (p, hwnd) = startHangChildMaximized exePath
    try
        if hwnd = IntPtr.Zero then
            log "- failed to obtain hung child window handle"
        else
            Thread.Sleep(6000)
            let (finished, ms) = measureWithTimeout 4000 (fun () -> ShowWindow(hwnd, SW_RESTORE))
            log (
                sprintf
                    "- ShowWindow(SW_RESTORE) WITHOUT Async, on MAXIMIZED hung window: %s (waited %dms)"
                    (if finished then "returned" else "did NOT return within the 4000ms measurement cap")
                    ms
            )
    finally
        killProcessSafely p

    let (p2, hwnd2) = startHangChildMaximized exePath
    try
        if hwnd2 = IntPtr.Zero then
            log "- failed to obtain hung child window handle (2nd trial)"
        else
            Thread.Sleep(6000)
            let (finished2, ms2) = measureWithTimeout 4000 (fun () -> ShowWindowAsync(hwnd2, SW_RESTORE))
            log (
                sprintf
                    "- ShowWindowAsync(SW_RESTORE), on MAXIMIZED hung window: %s (waited %dms)"
                    (if finished2 then "returned" else "did NOT return within the 4000ms measurement cap")
                    ms2
            )
    finally
        killProcessSafely p2

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

// Shared by S7 and S7r: read a process token's integrity level (TokenIntegrityLevel=25).
// TOKEN_MANDATORY_LABEL = SID_AND_ATTRIBUTES { PSID Sid; DWORD Attributes }.
// SID layout: BYTE Revision; BYTE SubAuthorityCount; BYTE IdentifierAuthority[6]; DWORD SubAuthority[n].
let private integrityLabelOf (rid: int) =
    match rid with
    | 0x0000 -> "Untrusted"
    | 0x1000 -> "Low"
    | 0x2000 -> "Medium"
    | 0x2100 -> "Medium Plus"
    | 0x3000 -> "High"
    | 0x4000 -> "System"
    | 0x5000 -> "Protected Process"
    | v -> sprintf "Unknown(0x%04x)" v

let private getIntegrityRid (hToken: IntPtr) : Result<int, string> =
    let TokenIntegrityLevel = 25u
    let mutable len = 0u
    GetTokenInformation(hToken, TokenIntegrityLevel, null, 0u, &len) |> ignore
    let buf = Array.zeroCreate<byte> (int len)
    if not (GetTokenInformation(hToken, TokenIntegrityLevel, buf, len, &len)) then
        Error (sprintf "GetTokenInformation(TokenIntegrityLevel) failed, GetLastError=%d" (Marshal.GetLastWin32Error()))
    else
        let pSid = IntPtr(BitConverter.ToInt64(buf, 0))
        let subAuthorityCount = int (Marshal.ReadByte(pSid, 1))
        Ok(Marshal.ReadInt32(pSid, 8 + 4 * (subAuthorityCount - 1)))

// S7: how does an elevated-vs-non-elevated operation fail? Actually spawning an elevated
// child (ShellExecute "runas") would pop a UAC consent prompt with nobody to approve it,
// hanging the job indefinitely -- too risky to attempt unattended. Instead, safely determine
// this process's own integrity level (no prompt, no process spawn), which tells us whether
// there is even a privilege asymmetry to observe on this machine, and feeds directly into
// architecture.md's DenialKind.TargetIntegrityHigher classification (currently deferred,
// 21章).
let spikeS7_IntegrityLevel () =
    try
        let principal = WindowsPrincipal(WindowsIdentity.GetCurrent())
        log (sprintf "- current process is in Administrators group: %b" (principal.IsInRole(WindowsBuiltInRole.Administrator)))
    with ex ->
        log (sprintf "- WindowsPrincipal check failed: %s" ex.Message)

    let TOKEN_QUERY = 0x0008u
    let mutable hToken = IntPtr.Zero
    if not (OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &hToken)) then
        log (sprintf "- OpenProcessToken failed, GetLastError=%d" (Marshal.GetLastWin32Error()))
    else
        try
            match getIntegrityRid hToken with
            | Ok rid -> log (sprintf "- current process token integrity level: %s (RID=0x%04x)" (integrityLabelOf rid) rid)
            | Error e -> log (sprintf "- %s" e)
        finally
            CloseHandle(hToken) |> ignore

// S7r (real-desktop only): unlike S7 (which only reads our own integrity level), this looks
// for an ALREADY-RUNNING window owned by a higher-integrity process (e.g. an admin Command
// Prompt the user happens to have open) and tries a harmless no-op SetWindowPos (same rect)
// against it, to observe the real ERROR_ACCESS_DENIED failure mode without ever spawning
// anything elevated ourselves (no UAC prompt risk).
let spikeS7r_ElevationAsymmetry () =
    let selfRid =
        let TOKEN_QUERY = 0x0008u
        let mutable hToken = IntPtr.Zero
        if OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &hToken) then
            try
                match getIntegrityRid hToken with
                | Ok r -> Some r
                | Error _ -> None
            finally
                CloseHandle(hToken) |> ignore
        else
            None

    match selfRid with
    | None -> log "- could not determine own integrity level, skipping"
    | Some myRid ->
        log (sprintf "- own integrity level: %s" (integrityLabelOf myRid))
        let PROCESS_QUERY_INFORMATION = 0x0400u
        let TOKEN_QUERY = 0x0008u
        let candidates = ResizeArray<IntPtr * int * int>()
        let cb =
            EnumChildProc(fun hwnd _ ->
                if IsWindowVisible(hwnd) && GetWindowTextLength(hwnd) > 0 then
                    let mutable pid = 0
                    GetWindowThreadProcessId(hwnd, &pid) |> ignore
                    if pid <> 0 then
                        let hProc = OpenProcess(PROCESS_QUERY_INFORMATION, false, pid)
                        if hProc <> IntPtr.Zero then
                            let mutable hTok = IntPtr.Zero
                            if OpenProcessToken(hProc, TOKEN_QUERY, &hTok) then
                                (match getIntegrityRid hTok with
                                 | Ok rid when rid > myRid -> candidates.Add(hwnd, pid, rid)
                                 | _ -> ())
                                CloseHandle(hTok) |> ignore
                            CloseHandle(hProc) |> ignore
                true)
        EnumWindows(cb, IntPtr.Zero) |> ignore

        if candidates.Count = 0 then
            log "- no visible top-level window owned by a higher-integrity process was found on this desktop right now (open an elevated app, e.g. an admin Command Prompt, to test this)"
        else
            let (hwnd, pid, rid) = candidates.[0]
            log (sprintf "- found higher-integrity window: pid=%d integrity=%s" pid (integrityLabelOf rid))
            let mutable r = RECT()
            GetWindowRect(hwnd, &r) |> ignore
            // No-op move (same rect) so we don't actually disturb the user's window if this succeeds.
            let ok = SetWindowPos(hwnd, IntPtr.Zero, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, 0x0004u (* SWP_NOZORDER *))
            let err = Marshal.GetLastWin32Error()
            log (sprintf "- SetWindowPos (no-op, same rect) against higher-integrity window: returned=%b GetLastError=%d" ok err)

// Minimal Form subclass that can observe whether it received the "TaskbarCreated" broadcast
// (sent to all top-level windows when Explorer restarts), used by S10's full tray-icon test.
type private TrayTestForm(taskbarCreatedMsg: uint32) =
    inherit Form()
    let mutable received = false
    member _.Received = received
    override this.WndProc(m: Message byref) =
        if taskbarCreatedMsg <> 0u && uint32 m.Msg = taskbarCreatedMsg then
            received <- true
        base.WndProc(&m)

// S10: does explorer.exe exist on this machine, and if we kill it, does it auto-restart (and
// how fast)? Then the full loop 8.4 actually depends on: register "TaskbarCreated", add a
// tray icon, kill Explorer, and see whether we both receive the broadcast and can re-add the
// icon afterward. NOTE: on a real desktop (unlike an ephemeral CI runner) this visibly
// restarts the user's Explorer shell/taskbar for a moment -- intentional here, since this is
// exactly the real-world event 8.4 needs to recover from.
let spikeS10_ExplorerRestart () =
    let before = Process.GetProcessesByName("explorer")
    log (sprintf "- explorer.exe processes found: %d" before.Length)
    if before.Length = 0 then
        log "- no explorer.exe running on this machine; S10's premise (TaskbarCreated-based tray icon recovery) does not apply here"
    else
        let taskbarCreatedMsg = RegisterWindowMessage("TaskbarCreated")
        log (sprintf "- RegisterWindowMessage(\"TaskbarCreated\") = 0x%08x" taskbarCreatedMsg)

        use form = new TrayTestForm(taskbarCreatedMsg, Bounds = Rectangle(0, 0, 1, 1))
        form.Show()
        pump 5

        let mutable nid = NOTIFYICONDATA()
        nid.cbSize <- Marshal.SizeOf(typeof<NOTIFYICONDATA>)
        nid.hWnd <- form.Handle
        nid.uID <- 1
        nid.uFlags <- 0x00000002 (* NIF_ICON *) ||| 0x00000004 (* NIF_TIP *)
        nid.hIcon <- SystemIcons.Application.Handle
        nid.szTip <- "winmonad S10 test"
        let added = Shell_NotifyIcon(0 (* NIM_ADD *), &nid)
        log (sprintf "- Shell_NotifyIcon(NIM_ADD) before killing Explorer: %b" added)

        let pid = before.[0].Id
        try
            before.[0].Kill()
            log (sprintf "- killed explorer.exe (pid=%d)" pid)
        with ex ->
            log (sprintf "- failed to kill explorer.exe (pid=%d): %s" pid ex.Message)

        let sw = Stopwatch.StartNew()
        let mutable restarted = false
        while not restarted && sw.ElapsedMilliseconds < 10000L do
            Thread.Sleep(300)
            pump 2 // keep pumping so TaskbarCreated can actually reach TrayTestForm.WndProc
            if Process.GetProcessesByName("explorer").Length > 0 then
                restarted <- true
        log (sprintf "- explorer.exe auto-restarted within 10s: %b (observed after %dms)" restarted sw.ElapsedMilliseconds)

        // The broadcast can lag a little behind the new explorer.exe process actually starting.
        for _ in 1 .. 20 do
            pump 5
            Thread.Sleep(100)
        log (sprintf "- received TaskbarCreated broadcast: %b" form.Received)

        if form.Received then
            let readded = Shell_NotifyIcon(0 (* NIM_ADD *), &nid)
            log (sprintf "- re-added tray icon after TaskbarCreated: %b" readded)

        Shell_NotifyIcon(2 (* NIM_DELETE *), &nid) |> ignore

// S5r (real-desktop only): calc.exe (and ms-settings:) may resolve to the modern UWP app here,
// unlike the CI runner image which only has the classic win32calc (failure-catalog.md F02).
let spikeS5r_RealUwp () =
    let inspect (label: string) (topHwnd: IntPtr) (ownerProcessName: string) =
        let topClass = StringBuilder(256)
        GetClassName(topHwnd, topClass, 256) |> ignore
        log (sprintf "- %s: owning process name=%s, top-level window class=%s" label ownerProcessName (topClass.ToString()))
        let found = ResizeArray<IntPtr>()
        let cb = EnumChildProc(fun h _ -> found.Add(h); true)
        EnumChildWindows(topHwnd, cb, IntPtr.Zero) |> ignore
        log (sprintf "- %s: %d direct child window(s) found" label found.Count)
        for h in found do
            let cls = StringBuilder(256)
            GetClassName(h, cls, 256) |> ignore
            let mutable pid = 0
            GetWindowThreadProcessId(h, &pid) |> ignore
            let procName =
                try (Process.GetProcessById(pid)).ProcessName with _ -> "?"
            log (sprintf "  - %s child hwnd class=%s owning pid=%d process=%s" label (cls.ToString()) pid procName)

    try
        let p = Process.Start("calc.exe")
        Thread.Sleep(2500)
        p.Refresh()
        if p.MainWindowHandle = IntPtr.Zero then
            log "- calc.exe: no top-level window detected within timeout"
        else
            inspect "calc.exe" p.MainWindowHandle p.ProcessName
        killProcessSafely p
    with ex ->
        log (sprintf "- calc.exe: ERROR %s" ex.Message)

    try
        let psi = ProcessStartInfo("ms-settings:", UseShellExecute = true)
        use p = Process.Start(psi)
        Thread.Sleep(2500)
        // ms-settings: launches SystemSettings.exe as a separate process from the one Process.Start returns.
        let settings = Process.GetProcessesByName("SystemSettings")
        if settings.Length = 0 then
            log "- ms-settings:: no SystemSettings.exe process found within timeout"
        else
            let sp = settings.[0]
            if sp.MainWindowHandle = IntPtr.Zero then
                log "- ms-settings:: SystemSettings.exe found but no MainWindowHandle yet"
            else
                inspect "ms-settings:" sp.MainWindowHandle sp.ProcessName
            killProcessSafely sp
    with ex ->
        log (sprintf "- ms-settings:: ERROR %s" ex.Message)

// S11r (real-desktop only): enumerate real monitors and their DPI, to see whether this
// desktop can even exercise the Per-Monitor-V2 DPI-transition question (architecture.md 8.3).
let spikeS11r_MonitorDpi () =
    let MDT_EFFECTIVE_DPI = 0
    let dpis = ResizeArray<int>()
    let cb =
        MonitorEnumProc(fun hMonitor _ _ _ ->
            let mutable dpiX = 0u
            let mutable dpiY = 0u
            let hr = GetDpiForMonitor(hMonitor, MDT_EFFECTIVE_DPI, &dpiX, &dpiY)
            dpis.Add(if hr = 0 then int dpiX else -1)
            true)
    EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, cb, IntPtr.Zero) |> ignore
    log (sprintf "- monitor count: %d, DPI values: %s" dpis.Count (String.Join(", ", dpis)))
    let distinct = dpis |> Seq.filter (fun d -> d > 0) |> Seq.distinct |> Seq.toList
    if distinct.Length <= 1 then
        log "- all monitors share the same DPI (or only one monitor present); cannot test a Per-Monitor-V2 DPI transition on this desktop right now"
    else
        log (
            sprintf
                "- found %d distinct DPI values across monitors: %s -- moving a real window between them would exercise S11, not automated by this spike yet"
                distinct.Length
                (String.Join(", ", distinct))
        )

// S4r (real-desktop only): unlike S4 (which only tests stealing focus from a child process we
// ourselves spawned, confounded by the "launched by" permission rule -- failure-catalog.md
// F04), this uses whatever window genuinely already has the foreground on this real desktop.
let spikeS4r_ThirdPartyFocus () =
    let before = GetForegroundWindow()
    let selfPid = Process.GetCurrentProcess().Id
    if before = IntPtr.Zero then
        log "- no foreground window detected right now, skipping"
    else
        let mutable pidBefore = 0
        GetWindowThreadProcessId(before, &pidBefore) |> ignore
        if pidBefore = selfPid then
            log "- the current foreground window already belongs to this test process; run this with a genuinely different app focused to test third-party stealing"
        else
            log (sprintf "- genuine third-party foreground window detected: pid=%d (this process is pid=%d)" pidBefore selfPid)
            use formB = new Form(Text = "S4r", StartPosition = FormStartPosition.Manual, Bounds = Rectangle(300, 300, 200, 150))
            formB.Show()
            pump 5

            let ok1 = SetForegroundWindow(formB.Handle)
            pump 3
            let fg1 = GetForegroundWindow()
            log (sprintf "- plain SetForegroundWindow against genuine third-party foreground: API returned=%b, actually foreground=%b" ok1 (fg1 = formB.Handle))

            if fg1 <> formB.Handle then
                SetForegroundWindow(before) |> ignore
                pump 3
                let MOUSEEVENTF_MOVE = 0x0001u
                mouse_event (MOUSEEVENTF_MOVE, 0, 0, 0u, UIntPtr.Zero)
                let ok2 = SetForegroundWindow(formB.Handle)
                pump 3
                let fg2 = GetForegroundWindow()
                log (
                    sprintf
                        "- mouse_event self-priming + SetForegroundWindow against genuine third-party foreground: API returned=%b, actually foreground=%b"
                        ok2
                        (fg2 = formB.Handle)
                )
            else
                log "- plain SetForegroundWindow already succeeded against a genuine third party; the priming trick may not be necessary here"

            // Courtesy: put the user's original foreground window back.
            SetForegroundWindow(before) |> ignore

[<STAThread>]
[<EntryPoint>]
let main argv =
    suppressCrashDialogs ()
    if argv.Length > 0 && argv.[0] = "--hang-child" then
        runHangChild false
        0
    elif argv.Length > 0 && argv.[0] = "--hang-child-maximized" then
        runHangChild true
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
        tryRun "S2b 事前確認のタイムアウト値の実測(ADR-014)" (fun () -> spikeS2b_PreCheckTiming exePath)
        tryRun "S2c RestoreBeforeMoveのShowWindowAsync化(ADR-014/R2-4)" (fun () -> spikeS2c_RestoreAsync exePath)
        tryRun "S6 バッチ適用(DeferWindowPos vs 個別SetWindowPos+ASYNC)" (fun () -> spikeS6_BatchApply exePath)
        tryRun "S7 プロセスの整合性レベル" spikeS7_IntegrityLevel
        tryRun "S10 Explorer再起動時のTaskbarCreated前提" spikeS10_ExplorerRestart
        tryRun "S4r 無関係な第三者プロセスからのフォーカス奪取(実デスクトップ限定)" spikeS4r_ThirdPartyFocus
        tryRun "S5r 実際のUWPアプリ判定(実デスクトップ限定)" spikeS5r_RealUwp
        tryRun "S7r 昇格ウィンドウの非対称性(実デスクトップ限定)" spikeS7r_ElevationAsymmetry
        tryRun "S11r 複数モニタのDPI検出(実デスクトップ限定)" spikeS11r_MonitorDpi

        log "\n(all spikes finished without the watchdog firing)"
        0
