/// architecture.md §17：Wm.Host、Phase B ラウンド1+2+3の実行入口。
///
/// ラウンド1：実際のWin32(`Wm.Windows`)から1回だけ観測を取り、`Wm.Normalization`→
/// `Wm.Runtime.Reducer`へ通して分類結果を報告する。
/// ラウンド2：`SetWinEventHook`を購読し、実時間（`Application.DoEvents()`によるメッセージポンプ、
/// spikes/Phase0CiSpikes/Program.fsのS20/S20b等と同じ確立済みの方式）でライブに観測を続ける。
/// 使い捨てのテスト窓(`Form`)をこのプロセス自身がWinForms APIで直接操作して確定的なイベント系列
/// （Show→Minimize→Restore→Hide→Close）を発生させ、パイプが正しく追従することを検証する。
///
/// ラウンド1/2はどちらも読み取りだけで、`SetRect`/`RequestForeground`等のEffectは一切実際のOSへ
/// 発行しない。`StepResult.Effects`はこの投入で実際に発生した値をそのまま記録し、表示するだけに
/// 留める（同じ観測の再投入は一切しない——状態が壊れるため、ラウンド1の時点でこれは一度実際に
/// 踏んで修正済みの誤り）。テスト窓はProgram.fs自身がWinForms APIで直接操作するだけで、
/// `GatedEffect`/Reducerの発行経路を一切経由しない（Coreはこの窓を一切「管理」しない）。
///
/// ラウンド3：実際にEffectをOSへ発行する最初のラウンド。ラウンド1/2が観測する実アンビエント窓
/// （CI実機で69〜75個、`AppIdentity`/`TitleMatches`が未配線のため`PolicyIR.defaults`の
/// `DefaultManagement = Tiled`で全てTiled判定される）を実際に動かすと実機上の無関係な窓まで
/// 動かしてしまうため、ラウンド3は意図的に**隔離された別の`Reducer`と、自分で作った使い捨ての
/// テスト窓だけ**を対象にする（計画の「重要なスコープ決定」参照、spikes/Phase0CiSpikes/
/// Program.fsのS2/S2c/S4が実際にSetWindowPos/SetForegroundWindowを呼ぶときも常に自分で
/// 作ったForm/子プロセスだけが対象という既存の方針と同じ）。architecture.md §7.1/ADR-012の
/// 「Effect適用はWinEventコールバックや安全停止の制御経路と同じスレッドで行わない」という
/// 明文の要求に従い、専用のEffect適用スレッドを新設する——メインスレッド(`Application.DoEvents()`
/// を回す側)はEffectをキューへ積むだけで、実際のOS呼出は専用スレッドが行う。
module Wm.Host.Program

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.IO
open System.Threading
open System.Windows.Forms
open Wm.Contracts.Identifiers
open Wm.Contracts.ErrorTypes
open Wm.Contracts.CoreCommand
open Wm.Contracts.GatedEffect
open Wm.Core.WmState
open Wm.Runtime.Reducer
open Wm.Normalization.WindowEventNormalizer
open Wm.Windows.Native
open Wm.Windows.RealObservation
open Wm.Windows.EventHooks

/// 保留中の全タイマーを仮想時計で発火し切る。発火したStepResultが運んだEffectも集めて返す
/// (Reconcile・分類期限超過等、タイマー発火の結果として生まれるEffectも報告対象にするため)。
let private drainTimers (reducer: Reducer) : Effect list * int =
    let mutable fired = 0
    let mutable effects: Effect list = []
    let mutable more = true
    while more do
        match reducer.AdvanceToNextTimer() with
        | Some stepResult ->
            fired <- fired + 1
            effects <- effects @ (stepResult.Effects |> List.map GatedEffect.effect)
        | None -> more <- false
    effects, fired

let private printClassification (label: string) (reducer: Reducer) =
    printfn "\nClassification %s (%d windows):" label (Map.count reducer.State.Windows.Records)
    reducer.State.Windows.Records
    |> Map.toSeq
    |> Seq.iter (fun (w, record) -> printfn "  %A -> %A" w record.Classification)

[<EntryPoint>]
let main _argv =
    let journalPath = Path.Combine(Path.GetTempPath(), sprintf "wm-host-round1-%s.ndjson" (Guid.NewGuid().ToString("N")))
    printfn "Phase B round 1+2: real-Windows observation slice"
    printfn "Journal: %s" journalPath

    let reducer = Reducer(RuntimePolicy.defaults, "phase-b-round12", "wm-host-round12", "no-policy-artifact", Some journalPath)

    // 段1：実モニターを列挙し、MonitorTopologyObservedを投入してTopologyを確定させる。
    let monitorHandles = enumerateMonitorHandles ()
    let monitors = enumerateMonitors monitorHandles
    printfn "\nMonitors observed: %d" monitors.Length
    for m in monitors do
        printfn "  %A primary=%b bounds=%A workArea=%A dpi=%d" m.Id m.IsPrimary m.Bounds m.WorkArea m.Dpi

    let topologyResult = reducer.Submit 0L "wm-host" (MonitorTopologyObserved monitors)
    let topologyEffects = topologyResult.Effects |> List.map GatedEffect.effect
    let settleEffects, settledAfterTopology = drainTimers reducer
    printfn "Timers fired while settling Topology: %d" settledAfterTopology

    // 段2：実トップレベル窓を列挙し、RawFirstSeen→normalize→Reducer.Submitで1回だけ投入する。
    let windows = enumerateTopLevelWindows 1L monitorHandles
    printfn "\nTop-level windows observed: %d" windows.Length

    let windowSubmissionResults =
        windows
        |> List.collect (fun snapshot -> normalize (RawFirstSeen snapshot))
        |> List.map (fun input -> reducer.Submit 1L "wm-host" input)
    let windowEffects = windowSubmissionResults |> List.collect (fun sr -> sr.Effects) |> List.map GatedEffect.effect
    let classifyEffects, settledAfterWindows = drainTimers reducer
    printfn "Timers fired while settling classification: %d" settledAfterWindows

    // 段3：ラウンド1の結果報告。Effectは一切実行しない(読み取り専用の安全境界)。
    printClassification "after round 1 one-shot enumeration" reducer
    let round1Effects = topologyEffects @ settleEffects @ windowEffects @ classifyEffects
    printfn "\n(diagnostic only, not dispatched to real Win32) Effects round 1 actually issued: %d" round1Effects.Length
    for e in round1Effects do
        printfn "  %A" e

    // 段4：ラウンド2。SetWinEventHookを購読し、実時間でタイマーを駆動しながらライブに観測する。
    printfn "\n--- Round 2: live SetWinEventHook observation ---"
    let liveBaseNow = 1_000_000L
    let sw = Stopwatch.StartNew()
    let liveNow () = liveBaseNow + sw.ElapsedMilliseconds
    let mutable liveEventCount = 0

    let handleEvent (eventType: uint32) (hwnd: IntPtr) =
        let now = liveNow ()
        let w = WindowId (hwnd.ToInt64())
        let raw =
            if eventType = EVENT_OBJECT_CREATE then Some (RawFirstSeen (observeWindow now monitorHandles hwnd))
            elif eventType = EVENT_OBJECT_DESTROY then Some (RawDestroyed w)
            elif eventType = EVENT_OBJECT_SHOW || eventType = EVENT_OBJECT_HIDE then
                Some (RawVisibility (w, observation now (queryVisible hwnd)))
            elif eventType = EVENT_SYSTEM_MINIMIZESTART || eventType = EVENT_SYSTEM_MINIMIZEEND then
                Some (RawMinimized (w, observation now (queryMinimized hwnd)))
            elif eventType = EVENT_OBJECT_CLOAKED || eventType = EVENT_OBJECT_UNCLOAKED then
                Some (RawCloak (w, observation now (queryCloak hwnd)))
            elif eventType = EVENT_SYSTEM_FOREGROUND then
                let value = if hwnd = IntPtr.Zero then None else Some w
                Some (RawForeground (observation now (Ok value)))
            elif eventType = EVENT_SYSTEM_MOVESIZESTART then
                Some (RawMoveSizeStart w)
            elif eventType = EVENT_SYSTEM_MOVESIZEEND then
                let snapshot = observeWindow now monitorHandles hwnd
                match snapshot.VisibleFrameRect, snapshot.Monitor with
                | Some rectObs, Some monObs -> Some (RawMoveSizeEnd (w, rectObs, monObs))
                | _ -> None
            else None

        match raw with
        | None -> ()
        | Some rawEvent ->
            for input in normalize rawEvent do
                printfn "  [t=%dms] %A" (now - liveBaseNow) input
                liveEventCount <- liveEventCount + 1
                reducer.Submit now "wm-host-live" input |> ignore

    let hooks = hookEvents handleEvent
    printfn "Hooks registered."

    // タイマーも実時間で駆動する(Reducerには変更を加えない、既存のSimulationTimerExecutorを
    // 実時間でポーリングするだけ——計画の「real-timer-executor不要」参照)。
    let pump (ms: int) =
        let deadline = liveNow () + int64 ms
        while liveNow () < deadline do
            Application.DoEvents()
            while reducer.Timers.NextDeadline |> Option.exists (fun d -> d <= liveNow ()) do
                reducer.AdvanceToNextTimer () |> ignore
            Thread.Sleep(1)

    printfn "Driving a disposable test window through Show -> Minimize -> Restore -> Hide -> Close..."
    use testForm = new Form(Text = "wm-host-round2-test-window", Width = 300, Height = 200)
    testForm.Show()
    pump 300
    testForm.WindowState <- FormWindowState.Minimized
    pump 300
    testForm.WindowState <- FormWindowState.Normal
    pump 300
    testForm.Hide()
    pump 300
    testForm.Close()
    pump 500

    hooks.Unhook ()
    printfn "Hooks unregistered. Live InputEvents processed: %d" liveEventCount

    // 段5：ラウンド2後の最終報告。
    printClassification "after round 2 live phase" reducer

    // 段6：ラウンド3。隔離された別のReducer・自分で作ったテスト窓だけを対象に、実際にEffectを
    // OSへ発行する（型直前のコメント参照）。
    printfn "\n--- Round 3: real Effect dispatch against an isolated test window ---"
    let reducer3 = Reducer(RuntimePolicy.defaults, "phase-b-round3", "wm-host-round3", "no-policy-artifact", None)
    let effectQueue = ConcurrentQueue<GatedEffect>()
    let resultQueue = ConcurrentQueue<RequestId * Result<unit, NativeCallRecord>>()
    let mutable keepDispatching = true
    let mutable dispatchedCount = 0
    let mutable skippedCount = 0

    // §7.1/ADR-012「Effect適用は専用スレッドで行う」：ここだけが実際のOS変更APIを呼ぶ。
    let effectApplyThread =
        Thread(fun () ->
            while keepDispatching do
                match effectQueue.TryDequeue() with
                | true, gated ->
                    let effect = GatedEffect.effect gated
                    match Wm.Windows.EffectDispatch.apply effect with
                    | Some result ->
                        let ridOpt =
                            match effect with
                            | SetRect (rid, _, _) -> Some rid
                            | RequestForeground (rid, _) -> Some rid
                            | RestoreBeforeMove (rid, _) -> Some rid
                            | _ -> None
                        match ridOpt with
                        | Some rid ->
                            Interlocked.Increment(&dispatchedCount) |> ignore
                            resultQueue.Enqueue(rid, result)
                        | None -> ()
                    | None -> Interlocked.Increment(&skippedCount) |> ignore
                | false, _ -> Thread.Sleep(1))
    effectApplyThread.IsBackground <- true
    effectApplyThread.Name <- "wm-effect-apply"
    effectApplyThread.Start()

    let enqueueEffects (effects: GatedEffect list) = for g in effects do effectQueue.Enqueue(g)

    let submit3 (now: int64) (source: string) (input: InputEvent) =
        let sr = reducer3.Submit now source input
        enqueueEffects sr.Effects
        sr

    let advance3 () =
        match reducer3.AdvanceToNextTimer() with
        | Some sr -> enqueueEffects sr.Effects; true
        | None -> false

    let drainResults3 (now: int64) =
        let mutable more = true
        while more do
            match resultQueue.TryDequeue() with
            | true, (rid, result) -> submit3 now "wm-host-effect-thread" (EffectCallReturned (rid, result, None)) |> ignore
            | false, _ -> more <- false

    let pump3 (ms: int) =
        let deadline = liveNow () + int64 ms
        while liveNow () < deadline do
            Application.DoEvents()
            drainResults3 (liveNow ())
            while advance3 () do ()
            Thread.Sleep(1)

    use testForm3 = new Form(Text = "wm-host-round3-test-window", Width = 300, Height = 200)
    testForm3.Show()
    pump3 300

    submit3 (liveNow ()) "wm-host" (MonitorTopologyObserved monitors) |> ignore
    pump3 500

    let testHWnd = testForm3.Handle
    let testWindowId = WindowId (testHWnd.ToInt64())
    submit3 (liveNow ()) "wm-host" (RawFirstSeen (observeWindow (liveNow ()) monitorHandles testHWnd) |> normalize |> List.exactlyOne) |> ignore
    pump3 1500 // 分類・Reconcile・実際のSetWindowPos・結果の折り返しに十分な余裕を取る

    keepDispatching <- false
    effectApplyThread.Join(2000) |> ignore

    // Coreとは無関係に、実際にOSが報告する最終状態を直接問い合わせて確認する
    // （計画の「Pending→Satisfiedループはこのラウンドでは閉じない」参照）。
    let mutable finalRect = RECT()
    let gotFinalRect = GetWindowRect(testHWnd, &finalRect)
    printfn "\nRound 3 dispatch summary: dispatched=%d skipped(no handler)=%d" dispatchedCount skippedCount
    printfn "Test window's real on-screen rect after dispatch (queried directly, bypassing Core): found=%b rect=%A"
        gotFinalRect
        { X = finalRect.Left; Y = finalRect.Top; Width = finalRect.Right - finalRect.Left; Height = finalRect.Bottom - finalRect.Top }
    match Map.tryFind testWindowId reducer3.State.Windows.Records with
    | Some record -> printfn "Core's classification for the test window: %A" record.Classification
    | None -> printfn "Core's classification for the test window: (not found in Windows.Records)"
    printfn "Core's remaining Pending requests: %d" (Map.count reducer3.State.Placement.Pending)

    testForm3.Close()

    printfn "\nDone."
    0
