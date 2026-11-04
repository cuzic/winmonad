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
///
/// ラウンド4：ラウンド3で明示的に先送りした「Pending→Satisfied閉路をCore内で閉じる」を実装する。
/// `EVENT_OBJECT_LOCATIONCHANGE`を自プロセスだけに絞って購読し（`EventHooks.hookLocationChangeForProcess`）、
/// 発火ごとに再観測した`WindowSnapshot`を`RawReobserved`（Normalizerの新ケース、ラウンド3の計画の
/// 「`RawFirstSeen`の再利用は意味的に不整合」という指摘への対応）としてCoreへ投入する。Core側
/// （`Placement.checkPendingSatisfied`）は元々どの`WindowObserved`でもPending一致判定を行うので
/// Coreの変更は不要——実機で閉じていなかったのはこの再観測の配線だけだった。
///
/// ラウンド5：`WindowSnapshot.AppIdentity.Exe`（実行ファイルのフルパス）と`TitleMatches`
/// （採用中PolicyのTitleContainsパターンに対する一致ビット列、R4-8：生タイトル文字列自体は運ばない）
/// を実際に問い合わせるようにした。これが無いと実アンビエント窓（ラウンド1/2が観測する69前後の
/// 実窓）を`TitleContains`/`AppIs`規則で安全に絞り込めず、ラウンド3/4の隔離デモを実管理へ統合する
/// 道が開かない（タスクリスト見直しで自己選定）。
///
/// ラウンド8：ラウンド7の2枚のテスト窓・隔離Reducerをそのまま使い、2本目のホットキー
/// （Ctrl+Shift+K）で`UserCommand SwapWithMaster`を発行する。`SwapWithMaster`は
/// `focused`（`Focus.Foreground`の信念）が`ManagedWindow w`であることを前提にするため
/// （WmState.fs型直前のコメント参照）、実際の`EVENT_SYSTEM_FOREGROUND`も購読してラウンド7の
/// FocusNextが変えた実フォアグラウンドをCoreへ中継し、`Focus.Foreground`を実際に満たす。
/// 検証は`GetWindowRect`で両窓の実矩形がSwapWithMaster前後で実際に入れ替わったかを直接確認する。
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
open Wm.Contracts.JournalTypes
open Wm.Core.WmState
open Wm.Policy.PolicyEvaluator
open Wm.Runtime.Reducer
open Wm.Normalization.WindowEventNormalizer
open Wm.Windows.Native
open Wm.Windows.RealObservation
open Wm.Windows.EventHooks

/// Phase B ラウンド7：`RegisterHotKey`→`WM_HOTKEY`を受け取るためだけの、見えない専用窓。
/// `WM_HOTKEY`は`RegisterHotKey`に渡したHWNDへ配送されるので、`Application.DoEvents()`で
/// 既にポンプしているのと同じスレッド上にこの窓を作るだけでよい（新しいメッセージループは要らない）。
type private HotkeyForm(onHotkey: int -> unit) =
    inherit Form(Text = "wm-host-round7-hotkey-sink", ShowInTaskbar = false, Width = 1, Height = 1)
    override _.WndProc(m: Message byref) =
        if m.Msg = WM_HOTKEY then onHotkey (m.WParam.ToInt32())
        base.WndProc(&m)

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

    // ラウンド5：`AppIdentity.Exe`/`TitleMatches`を実際に問い合わせる（型直前のコメント参照）。
    // `RuntimePolicy.defaults.PatternSet`は`PolicyIR.defaults`（ルール無し）から組んだものなので、
    // 現時点では常に`Matches = Set.empty`になるが、将来WindowRulesにTitleContainsが増えても
    // Shell側の配線はこのラウンドで完成する。
    let titleMatcher = computeTitleMatchVector RuntimePolicy.defaults.PatternSet

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
    let windows = enumerateTopLevelWindows 1L monitorHandles titleMatcher
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
            if eventType = EVENT_OBJECT_CREATE then Some (RawFirstSeen (observeWindow now monitorHandles titleMatcher hwnd))
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
                let snapshot = observeWindow now monitorHandles titleMatcher hwnd
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
    // ラウンド6：reducer3にも実際にJournalを持たせる（round1/2のreducerは既にjournalPathを持つが、
    // round3/4のreducer3は`None`のままだった）。`Wm.Runtime.Reducer.Submit`は`EffectCallReturned`な
    // 入力だけ`Kind = EffectCallReturnedKind`で書き、他は`InputReceived`（型直前のコメント参照、v2.82）
    // ——これを読み返すだけでOS発行結果(`EffectCallReturnedKind`)と観測結果(`InputReceived`の
    // `WindowObserved`)がJournal上で区別して追跡できることを実機で確認する（architecture.md §18
    // Phase B Gate「実機の1件の不具合を入力→判断→Effect→実観測として調査できる」の直接の検証）。
    let journalPath3 = Path.Combine(Path.GetTempPath(), sprintf "wm-host-round3-%s.ndjson" (Guid.NewGuid().ToString("N")))
    printfn "Round 3+4+6 journal: %s" journalPath3
    let reducer3 = Reducer(RuntimePolicy.defaults, "phase-b-round3", "wm-host-round3", "no-policy-artifact", Some journalPath3)
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

    let testHWnd = testForm3.Handle
    let testWindowId = WindowId (testHWnd.ToInt64())

    // 段7：ラウンド4。Pending→Satisfied閉路を閉じる（ラウンド3の計画で明示的に先送りした論点）。
    // `EVENT_OBJECT_LOCATIONCHANGE`は自プロセス(`idProcess`)に絞って購読する——`hookEvents`（ラウンド2、
    // 全プロセス対象）に混ぜると流量が大きすぎるため専用の購読にする（EventHooks.fsの型直前の
    // コメント参照）。発火ごとに再観測し`RawReobserved`としてCoreへ投入する——ディスパッチ直前に
    // 購読しておくことで、SetRectが実際に動かした結果のLOCATIONCHANGEを取り逃さない。
    let mutable reobserveCount = 0
    let handleLocationChange (_eventType: uint32) (hwnd: IntPtr) =
        if hwnd = testHWnd then
            let now = liveNow ()
            let snapshot = observeWindow now monitorHandles titleMatcher hwnd
            for input in normalize (RawReobserved snapshot) do
                reobserveCount <- reobserveCount + 1
                printfn "  [round4 t=%dms] reobserved via LOCATIONCHANGE: %A" (now - liveBaseNow) input
                submit3 now "wm-host-locationchange" input |> ignore
    let pid = uint32 (Process.GetCurrentProcess().Id)
    let locationHook = hookLocationChangeForProcess pid handleLocationChange

    submit3 (liveNow ()) "wm-host" (MonitorTopologyObserved monitors) |> ignore
    pump3 500

    submit3 (liveNow ()) "wm-host" (RawFirstSeen (observeWindow (liveNow ()) monitorHandles titleMatcher testHWnd) |> normalize |> List.exactlyOne) |> ignore
    pump3 1500 // 分類・Reconcile・実際のSetWindowPos・LOCATIONCHANGEによる再観測に十分な余裕を取る

    let pendingBeforeUnhook = Map.count reducer3.State.Placement.Pending
    locationHook.Unhook ()
    keepDispatching <- false
    effectApplyThread.Join(2000) |> ignore

    // Coreとは無関係に、実際にOSが報告する最終状態を直接問い合わせて確認する（ラウンド3と同じ、
    // Core状態への依存だけに頼らない二重確認）。
    let mutable finalRect = RECT()
    let gotFinalRect = GetWindowRect(testHWnd, &finalRect)
    printfn "\nRound 3+4 dispatch summary: dispatched=%d skipped(no handler)=%d reobserved(LOCATIONCHANGE)=%d" dispatchedCount skippedCount reobserveCount
    printfn "Test window's real on-screen rect after dispatch (queried directly, bypassing Core): found=%b rect=%A"
        gotFinalRect
        { X = finalRect.Left; Y = finalRect.Top; Width = finalRect.Right - finalRect.Left; Height = finalRect.Bottom - finalRect.Top }
    match Map.tryFind testWindowId reducer3.State.Windows.Records with
    | Some record -> printfn "Core's classification for the test window: %A" record.Classification
    | None -> printfn "Core's classification for the test window: (not found in Windows.Records)"
    printfn "Core's remaining Pending requests just before unhooking LOCATIONCHANGE: %d" pendingBeforeUnhook
    printfn "Core's remaining Pending requests (final): %d" (Map.count reducer3.State.Placement.Pending)

    // ラウンド6：Journalを読み返し、OS発行結果(`EffectCallReturnedKind`)と観測結果
    // (`InputReceived`のうち`WindowObserved`)が実際に区別して記録されていることを確認する。
    let journalEntries = Wm.Journal.JournalWriter.readAll journalPath3 |> List.ofSeq
    printfn "\nRound 6: journal entries written=%d" journalEntries.Length
    journalEntries
    |> List.countBy (fun e -> e.Kind)
    |> List.iter (fun (kind, n) -> printfn "  Kind=%A count=%d" kind n)
    journalEntries
    |> List.filter (fun e -> e.Kind = EffectCallReturnedKind)
    |> List.iter (fun e -> printfn "  [seq=%d] EffectCallReturnedKind: %A" e.Sequence e.Payload)
    journalEntries
    |> List.filter (fun e -> match e.Payload with InputPayload (WindowObserved _) -> true | _ -> false)
    |> List.iter (fun e -> printfn "  [seq=%d] InputReceived(WindowObserved)" e.Sequence)

    testForm3.Close()

    // 段8：ラウンド7。実際のホットキー（RegisterHotKey→WM_HOTKEY）からUserCommand FocusNextを
    // 投入し、Coreが発行するRequestForegroundを実際のOSへ発行する。Alt系の合成はCI実機で実際に
    // ハングした既知の罠（型直前の`Native.fs`のコメント参照）のため、合成するキーはCtrl+Shift+Jに
    // 限定する。ラウンド3/4と同じ理由で、専用の隔離された別Reducer・自分で作ったテスト窓2枚だけを
    // 対象にする。
    printfn "\n--- Round 7: real hotkey (RegisterHotKey -> WM_HOTKEY -> UserCommand FocusNext -> RequestForeground) ---"
    let journalPath4 = Path.Combine(Path.GetTempPath(), sprintf "wm-host-round7-%s.ndjson" (Guid.NewGuid().ToString("N")))
    let reducer4 = Reducer(RuntimePolicy.defaults, "phase-b-round7", "wm-host-round7", "no-policy-artifact", Some journalPath4)
    let effectQueue4 = ConcurrentQueue<GatedEffect>()
    let resultQueue4 = ConcurrentQueue<RequestId * Result<unit, NativeCallRecord>>()
    let mutable keepDispatching4 = true

    let effectApplyThread4 =
        Thread(fun () ->
            while keepDispatching4 do
                match effectQueue4.TryDequeue() with
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
                        | Some rid -> resultQueue4.Enqueue(rid, result)
                        | None -> ()
                    | None -> ()
                | false, _ -> Thread.Sleep(1))
    effectApplyThread4.IsBackground <- true
    effectApplyThread4.Name <- "wm-effect-apply-round7"
    effectApplyThread4.Start()

    let enqueueEffects4 (effects: GatedEffect list) = for g in effects do effectQueue4.Enqueue(g)

    let submit4 (now: int64) (source: string) (input: InputEvent) =
        let sr = reducer4.Submit now source input
        enqueueEffects4 sr.Effects
        sr

    let advance4 () =
        match reducer4.AdvanceToNextTimer() with
        | Some sr -> enqueueEffects4 sr.Effects; true
        | None -> false

    let drainResults4 (now: int64) =
        let mutable more = true
        while more do
            match resultQueue4.TryDequeue() with
            | true, (rid, result) -> submit4 now "wm-host-effect-thread" (EffectCallReturned (rid, result, None)) |> ignore
            | false, _ -> more <- false

    let pump4 (ms: int) =
        let deadline = liveNow () + int64 ms
        while liveNow () < deadline do
            Application.DoEvents()
            drainResults4 (liveNow ())
            while advance4 () do ()
            Thread.Sleep(1)

    let mutable focusNextTarget : WindowId option = None
    let onHotkey (id: int) =
        let now = liveNow ()
        match id with
        | 1 ->
            let sr = submit4 now "wm-host-hotkey" (UserCommand FocusNext)
            let target =
                sr.Effects
                |> List.map GatedEffect.effect
                |> List.tryPick (function RequestForeground (_, w) -> Some w | _ -> None)
            focusNextTarget <- target
            printfn "  [round7] WM_HOTKEY id=%d -> UserCommand FocusNext -> RequestForeground target=%A" id target
        | 2 ->
            submit4 now "wm-host-hotkey" (UserCommand SwapWithMaster) |> ignore
            printfn "  [round8] WM_HOTKEY id=%d -> UserCommand SwapWithMaster" id
        | _ -> ()

    use hotkeyForm = new HotkeyForm(onHotkey)
    hotkeyForm.Show()
    use testFormA = new Form(Text = "wm-host-round7-a", Width = 300, Height = 200)
    use testFormB = new Form(Text = "wm-host-round7-b", Width = 300, Height = 200)
    testFormA.Show()
    testFormB.Show()
    pump4 300

    // ラウンド8：`UserCommand SwapWithMaster`は`focused`（`Focus.Foreground`の信念）が
    // `ManagedWindow w`であることを前提にする（WmState.fs型直前のコメント参照）——FocusNextと違い、
    // zipperの意図だけでは発行されない。実際の`EVENT_SYSTEM_FOREGROUND`を購読してCoreへ中継し、
    // ラウンド7のFocusNextが実際に変えたOSフォアグラウンドをCore自身の信念として往復させる
    // （ラウンド7では`GetForegroundWindow()`で直接確認するだけに留めていた経路を、ここでは
    // Core内部の`Focus.Foreground`まで実際に閉じる）。
    let foregroundHook =
        hookSingleEvent EVENT_SYSTEM_FOREGROUND (fun _ hwnd ->
            if hwnd = testFormA.Handle || hwnd = testFormB.Handle then
                let now = liveNow ()
                let w = WindowId (hwnd.ToInt64())
                submit4 now "wm-host-foreground" (ForegroundObserved (observation now (Ok (Some w)))) |> ignore)

    let hotkeyIdFocusNext = 1
    let hotkeyIdSwapWithMaster = 2
    let registeredFocusNext = RegisterHotKey(hotkeyForm.Handle, hotkeyIdFocusNext, MOD_CONTROL ||| MOD_SHIFT, uint32 VK_J)
    let registeredSwap = RegisterHotKey(hotkeyForm.Handle, hotkeyIdSwapWithMaster, MOD_CONTROL ||| MOD_SHIFT, uint32 VK_K)
    printfn "RegisterHotKey(Ctrl+Shift+J)=%b RegisterHotKey(Ctrl+Shift+K)=%b" registeredFocusNext registeredSwap

    submit4 (liveNow ()) "wm-host" (MonitorTopologyObserved monitors) |> ignore
    pump4 300
    submit4 (liveNow ()) "wm-host" (RawFirstSeen (observeWindow (liveNow ()) monitorHandles titleMatcher testFormA.Handle) |> normalize |> List.exactlyOne) |> ignore
    pump4 800
    submit4 (liveNow ()) "wm-host" (RawFirstSeen (observeWindow (liveNow ()) monitorHandles titleMatcher testFormB.Handle) |> normalize |> List.exactlyOne) |> ignore
    pump4 800

    let pressChord (modifiers: byte list) (key: byte) =
        // VK_MENU(Alt)は絶対に押さない——型直前のコメント参照、実際のCI実機ハングの既知の罠。
        for m in modifiers do keybd_event (m, 0uy, 0u, UIntPtr.Zero)
        keybd_event (key, 0uy, 0u, UIntPtr.Zero)
        keybd_event (key, 0uy, KEYEVENTF_KEYUP, UIntPtr.Zero)
        for m in List.rev modifiers do keybd_event (m, 0uy, KEYEVENTF_KEYUP, UIntPtr.Zero)

    if registeredFocusNext then pressChord [ VK_LCONTROL; VK_LSHIFT ] VK_J
    pump4 800 // FocusNextのRequestForeground発行・実際のSetForegroundWindow・EVENT_SYSTEM_FOREGROUNDの折返しに余裕を取る

    let rectOfForm (form: Form) : RECT =
        let mutable r = RECT()
        GetWindowRect(form.Handle, &r) |> ignore
        r
    let beforeSwapA = rectOfForm testFormA
    let beforeSwapB = rectOfForm testFormB

    if registeredSwap then pressChord [ VK_LCONTROL; VK_LSHIFT ] VK_K
    pump4 800

    let afterSwapA = rectOfForm testFormA
    let afterSwapB = rectOfForm testFormB

    UnregisterHotKey(hotkeyForm.Handle, hotkeyIdFocusNext) |> ignore
    UnregisterHotKey(hotkeyForm.Handle, hotkeyIdSwapWithMaster) |> ignore
    foregroundHook.Unhook ()
    keepDispatching4 <- false
    effectApplyThread4.Join(2000) |> ignore

    // Coreとは無関係に、実際のOSフォアグラウンド窓を直接問い合わせて確認する（ラウンド3/4/6と
    // 同じ、Core状態への依存だけに頼らない二重確認）。
    let realForeground = GetForegroundWindow()
    printfn "\nRound 7 summary: focusNextTarget=%A realForegroundHwnd=%A" focusNextTarget realForeground
    match focusNextTarget with
    | Some (WindowId raw) -> printfn "Real foreground matches FocusNext's RequestForeground target: %b" (IntPtr raw = realForeground)
    | None -> printfn "FocusNext produced no RequestForeground effect (no observable target to compare)"
    printfn "Round 8 summary: testFormA rect before=%A after=%A" beforeSwapA afterSwapA
    printfn "Round 8 summary: testFormB rect before=%A after=%A" beforeSwapB afterSwapB
    printfn "SwapWithMaster actually swapped the two windows' real on-screen rects: %b"
        (beforeSwapA.Left = afterSwapB.Left && beforeSwapA.Top = afterSwapB.Top
         && beforeSwapB.Left = afterSwapA.Left && beforeSwapB.Top = afterSwapA.Top
         && beforeSwapA <> beforeSwapB)

    testFormA.Close()
    testFormB.Close()
    hotkeyForm.Close()

    printfn "\nDone."
    0
