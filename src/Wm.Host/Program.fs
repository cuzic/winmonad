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
///
/// ラウンド9：§8.4「安全停止」のうちハング検知だけを切り出す。別スレッドがメインループの心拍を
/// 監視し、一定時間途絶えたら発動——発動したらReducerを一切経由せず、専用のJournalファイルへ
/// 直接`SafetyRecoveryAction`を記録する（8.4の明文「復旧操作はGatedEffectを経由しない例外経路」）。
/// 表示復元・画面内移動・HWND一覧の追跡は、Shellがまだ`SetPresence(shown=false)`を一度も発行して
/// いないため対象が無く、別ラウンドへ持ち越す。
///
/// ラウンド10：§8.4「窓を破壊・終了するEffectを提供しない」の唯一の例外`RequestClose`
/// （`WM_CLOSE`による丁寧な要求）を実際に発行する。`SetPresence`のような§21の未決の設計判断
/// （ワークスペースの非表示方式）に依存しないため、他のEffectより先に実装できた。
///
/// ラウンド11：§21の未決事項がv2.102でプロジェクトオーナーに解決され（最小化を採用）、
/// `SetPresence`を実際に発行できるようになった。`UserCommand (SwitchWorkspace _)`で
/// 窓のいるWorkspaceを隠す/戻すと、Topology→Placementの既存の経路（v2.31以来）が
/// `SetPresence(shown=false/true)`を発行する——これを実際の`ShowWindowAsync(SW_MINIMIZE/SW_RESTORE)`
/// へ変換し、`IsIconic`で実窓が本当に最小化・復元されたかを確認する。
///
/// ラウンド12：ラウンド9（検知のみ）とラウンド11（`SetPresence`実装）を組み合わせ、§8.4の
/// 「直前に隠した窓」の実際の復旧まで通す。Effect適用スレッドが`SetPresence(shown=false)`を
/// 実際に適用するたびに、監視コンポーネント自身がHWND一覧を追跡する（GlazeWMのwm-watcherと同じ）。
/// ウォッチドッグが発動したら、Reducerを一切経由せずこの一覧の窓へ直接`ShowWindowAsync(SW_RESTORE)`
/// を呼ぶ——Coreへは一度も「戻して」と言わずに、復旧経路だけで実際に窓が戻ることを確認する。
module Wm.Host.Program

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.IO
open System.Threading
open System.Windows.Forms
open Wm.Contracts.Identifiers
open Wm.Contracts.SharedVocabulary
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

    // ラウンド8の実機ログ調査で発見した実在のバグ：ここが`while advance3 () do ()`（無条件に
    // 溜まっている仮想タイマーを全部即時発火させる）になっていた。ラウンド2の`pump`はタイマーの
    // 期限が実時間で本当に来てから発火させていた（`NextDeadline <= liveNow()`のゲート）のに、
    // ラウンド3でこのゲートが落ちた——`PendingDeadline`（既定2000ms）が実時間でほぼ0msのうちに
    // 発火し、実際のSetWindowPos/実観測が追いつく前にTimeout→Retryを3回即座に使い切って諦める。
    // 単一窓（ラウンド3/4/6）では同じ目標へ何度再発行しても見た目の結果が変わらないため気づかれず、
    // 2窓のラウンド7/8でMasterStackの分割が実機で崩れて初めて症状が見えた。
    let pump3 (ms: int) =
        let deadline = liveNow () + int64 ms
        while liveNow () < deadline do
            Application.DoEvents()
            drainResults3 (liveNow ())
            while reducer3.Timers.NextDeadline |> Option.exists (fun d -> d <= liveNow ()) do
                advance3 () |> ignore
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
    let mutable dispatchedCount4 = 0
    let mutable skippedCount4 = 0

    let effectApplyThread4 =
        Thread(fun () ->
            while keepDispatching4 do
                match effectQueue4.TryDequeue() with
                | true, gated ->
                    let effect = GatedEffect.effect gated
                    match Wm.Windows.EffectDispatch.apply effect with
                    | Some result ->
                        printfn "  [round7/8 dispatch] %A -> %A" effect result
                        let ridOpt =
                            match effect with
                            | SetRect (rid, _, _) -> Some rid
                            | RequestForeground (rid, _) -> Some rid
                            | RestoreBeforeMove (rid, _) -> Some rid
                            | _ -> None
                        Interlocked.Increment(&dispatchedCount4) |> ignore
                        match ridOpt with
                        | Some rid -> resultQueue4.Enqueue(rid, result)
                        | None -> ()
                    | None -> Interlocked.Increment(&skippedCount4) |> ignore
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

    // ラウンド3のpump3と同じ修正（型直前のコメント参照）：実時間でまだ来ていないタイマーまで
    // 無条件に即時発火させない。
    let pump4 (ms: int) =
        let deadline = liveNow () + int64 ms
        while liveNow () < deadline do
            Application.DoEvents()
            drainResults4 (liveNow ())
            while reducer4.Timers.NextDeadline |> Option.exists (fun d -> d <= liveNow ()) do
                advance4 () |> ignore
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

    // ラウンド8追加修正：ラウンド4と同じ理由で、実際のSetRectが動かした結果を`RawReobserved`で
    // 再投入しないと`checkPendingSatisfied`が一度も一致を検出できず、`PendingDeadline`満了のたびに
    // 同じ目標へリトライし続けてしまう（最初の診断ログで同じ窓へ3回連続で同じSetRectが発行されていた
    // 実際の症状、`pendingRetryMaxAttempts=3`と一致）。自プロセスに絞った`EVENT_OBJECT_LOCATIONCHANGE`
    // を購読し、実際に動いた窓を再観測してCoreへ返す。
    let mutable locationReobserveCount4 = 0
    let locationHook4 =
        hookLocationChangeForProcess (uint32 (Process.GetCurrentProcess().Id)) (fun _ hwnd ->
            if hwnd = testFormA.Handle || hwnd = testFormB.Handle then
                let now = liveNow ()
                locationReobserveCount4 <- locationReobserveCount4 + 1
                printfn "  [round7/8 reobserve] LOCATIONCHANGE hwnd=%A" hwnd
                submit4 now "wm-host-locationchange" (RawReobserved (observeWindow now monitorHandles titleMatcher hwnd) |> normalize |> List.exactlyOne) |> ignore)

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

    // 原因が判明した：`PolicyIR.defaults.DefaultLayout`は"monocle"であり、MonocleはTiled窓全部に
    // 同じWorkArea全域を与える意味論（§5.6）——SwapWithMasterはzipperの並びだけを変え、master/stackの
    // 区別を矩形に反映するのはMasterStackなので、Monocleのままでは矩形が変わらないのは当然の挙動
    // だった（CoreやpumpタイマーのバグではなくこのシナリオがLayoutを明示的に選んでいなかった不備）。
    // SwapWithMasterの効果を実際に見るため、このWorkspaceのLayoutを明示的にmaster-stackへ切り替える。
    submit4 (liveNow ()) "wm-host" (UserCommand (ChangeLayout { Id = LayoutId "master-stack"; ApiVersion = 1; Args = NoArgs })) |> ignore
    pump4 500

    // 診断：両窓が本当に同じWorkspaceのzipperに入っているかを直接確認する（SwapWithMasterの
    // 実矩形検証が実機でfalseになった原因調査、両窓が同一矩形になった実際の原因はまだ未確定）。
    let wA = WindowId (testFormA.Handle.ToInt64())
    let wB = WindowId (testFormB.Handle.ToInt64())
    let wsOfA = Wm.Core.Workspaces.WorkspaceSet.workspaceOf wA reducer4.State.Workspaces
    let wsOfB = Wm.Core.Workspaces.WorkspaceSet.workspaceOf wB reducer4.State.Workspaces
    printfn "Round 7+8 diagnostic: windowA(%A) workspace=%A, windowB(%A) workspace=%A" wA wsOfA wB wsOfB
    for ws in [ wsOfA; wsOfB ] |> List.choose id |> List.distinct do
        printfn "  Workspace %A stack=%A" ws (Wm.Core.Workspaces.WorkspaceSet.stateOf ws reducer4.State.Workspaces).Stack
    printfn "  classification A=%A B=%A" (Map.tryFind wA reducer4.State.Windows.Records |> Option.map (fun r -> r.Classification)) (Map.tryFind wB reducer4.State.Windows.Records |> Option.map (fun r -> r.Classification))
    printfn "  Desired A=%A B=%A" (Map.tryFind wA reducer4.State.Placement.Desired) (Map.tryFind wB reducer4.State.Placement.Desired)
    printfn "  Pending=%A" (reducer4.State.Placement.Pending |> Map.toList)
    printfn "  reobserved(LOCATIONCHANGE) so far=%d" locationReobserveCount4

    let pressChord (modifiers: byte list) (key: byte) =
        // VK_MENU(Alt)は絶対に押さない——型直前のコメント参照、実際のCI実機ハングの既知の罠。
        for m in modifiers do keybd_event (m, 0uy, 0u, UIntPtr.Zero)
        keybd_event (key, 0uy, 0u, UIntPtr.Zero)
        keybd_event (key, 0uy, KEYEVENTF_KEYUP, UIntPtr.Zero)
        for m in List.rev modifiers do keybd_event (m, 0uy, KEYEVENTF_KEYUP, UIntPtr.Zero)

    // 順序が重要：`addWindow`は最後に追加した窓(B)へzipperのFocusを置く（型直前の診断ログの
    // `Before=[A]; Focus=B`参照）。先にFocusNextを押すとFocusがmaster(A)自身へ回ってしまい、
    // その後のSwapWithMasterは「masterとfocused(=master自身)の交換」という無意味な操作になる
    // （実機でこの順序のまま矩形が変化しなかった、最初の実装ミス）。SwapWithMasterは
    // まだstack側(B)にFocusが残っている今のうちに押す。
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

    if registeredFocusNext then pressChord [ VK_LCONTROL; VK_LSHIFT ] VK_J
    pump4 800 // FocusNextのRequestForeground発行・実際のSetForegroundWindow・EVENT_SYSTEM_FOREGROUNDの折返しに余裕を取る

    UnregisterHotKey(hotkeyForm.Handle, hotkeyIdFocusNext) |> ignore
    UnregisterHotKey(hotkeyForm.Handle, hotkeyIdSwapWithMaster) |> ignore
    foregroundHook.Unhook ()
    locationHook4.Unhook ()
    keepDispatching4 <- false
    effectApplyThread4.Join(2000) |> ignore

    // Coreとは無関係に、実際のOSフォアグラウンド窓を直接問い合わせて確認する（ラウンド3/4/6と
    // 同じ、Core状態への依存だけに頼らない二重確認）。
    let realForeground = GetForegroundWindow()
    printfn "\nRound 7 summary: focusNextTarget=%A realForegroundHwnd=%A" focusNextTarget realForeground
    match focusNextTarget with
    | Some (WindowId raw) -> printfn "Real foreground matches FocusNext's RequestForeground target: %b" (IntPtr raw = realForeground)
    | None -> printfn "FocusNext produced no RequestForeground effect (no observable target to compare)"
    let fmtRect (r: RECT) = sprintf "{X=%d;Y=%d;R=%d;B=%d}" r.Left r.Top r.Right r.Bottom
    printfn "Round 7+8 dispatch totals: dispatched=%d skipped(no handler)=%d" dispatchedCount4 skippedCount4
    printfn "Round 8 summary: testFormA rect before=%s after=%s" (fmtRect beforeSwapA) (fmtRect afterSwapA)
    printfn "Round 8 summary: testFormB rect before=%s after=%s" (fmtRect beforeSwapB) (fmtRect afterSwapB)
    printfn "SwapWithMaster actually swapped the two windows' real on-screen rects: %b"
        (beforeSwapA.Left = afterSwapB.Left && beforeSwapA.Top = afterSwapB.Top
         && beforeSwapB.Left = afterSwapA.Left && beforeSwapB.Top = afterSwapA.Top
         && beforeSwapA <> beforeSwapB)

    testFormA.Close()
    testFormB.Close()
    hotkeyForm.Close()

    // 段9：ラウンド9。§8.4「安全停止」の制御経路のうち、最も先に要るハング検知だけを切り出す
    // （本番の復旧対象であるHWND一覧の追跡・表示復元・画面内移動・TaskbarCreated再登録は、
    // 現時点のShellがまだSetPresence(shown=false)を一度も発行していないため対象が無く、
    // 別ラウンドへ持ち越す）。別スレッド（型直前の8.4本文は「できれば別プロセス」と書くが、
    // 初期版としてまずスレッドで分離する）がメインループの心拍（ハートビート）を監視し、
    // 一定時間途絶えたら発動する。発動したら、8.4の明文の要求「復旧操作はGatedEffectを経由しない
    // 例外経路」に従い、Reducerを一切経由せず専用のJournalファイルへ直接`SafetyRecoveryAction`を
    // 記録する——Reducerがハングしていても、この経路だけは独立して動くことを実機で確認する。
    // 本物のハングを起こすのは危険すぎるため、心拍の更新を計画的に一定時間止めるだけで検知経路を
    // 検証する（実際にメインスレッドをブロックさせるのではなく、擬似的に心拍だけを止める）。
    printfn "\n--- Round 9: safety-stop watchdog heartbeat detection (independent control path, §8.4) ---"
    let watchdogJournalPath = Path.Combine(Path.GetTempPath(), sprintf "wm-watchdog-%s.ndjson" (Guid.NewGuid().ToString("N")))
    let mutable lastHeartbeat = Environment.TickCount64
    let mutable watchdogTriggered = false
    let heartbeatTimeoutMs = 500L
    let watchdogThread =
        Thread(fun () ->
            while not watchdogTriggered do
                Thread.Sleep(50)
                let gap = Environment.TickCount64 - lastHeartbeat
                if gap > heartbeatTimeoutMs then
                    watchdogTriggered <- true
                    let entry: JournalEntry =
                        { SchemaVersion = 1
                          SessionId = "wm-watchdog"
                          Sequence = 0L
                          MonotonicTicks = Environment.TickCount64
                          WallTimeUtc = DateTimeOffset.UtcNow
                          CorrelationId = None
                          CausationId = None
                          PolicyArtifactHash = "no-policy-artifact"
                          CoreBuildId = "wm-watchdog"
                          Kind = SafetyRecoveryAction
                          Payload = SafetyRecoveryActionPayload (sprintf "heartbeat gap %dms exceeded %dms threshold -- main loop presumed hung" gap heartbeatTimeoutMs) }
                    Wm.Journal.JournalWriter.appendEntry watchdogJournalPath entry
                    printfn "  [round9 watchdog] triggered: heartbeat gap=%dms > threshold=%dms" gap heartbeatTimeoutMs)
    watchdogThread.IsBackground <- true
    watchdogThread.Name <- "wm-safety-watchdog"
    watchdogThread.Start()

    printfn "Simulating a main-loop heartbeat gap (not the real thing -- see comment above) for 700ms..."
    Thread.Sleep(700)
    lastHeartbeat <- Environment.TickCount64 // 心拍を復帰させてウォッチドッグを止める
    Thread.Sleep(200) // ウォッチドッグがこの間に発火・記録を終えたことを確認する余裕

    let watchdogEntries = Wm.Journal.JournalWriter.readAll watchdogJournalPath |> List.ofSeq
    printfn "Round 9 summary: watchdogTriggered=%b journalEntries=%d" watchdogTriggered watchdogEntries.Length
    for e in watchdogEntries do
        printfn "  %A: %A" e.Kind e.Payload

    // 段10：ラウンド10。§8.4「窓を破壊・終了するEffectを提供しない」の唯一の例外である`RequestClose`
    // （WM_CLOSEによる丁寧な要求）を実際にOSへ発行する。自分で作った使い捨てテスト窓だけを対象にする
    // （ラウンド3/4と同じ安全上の理由）。`focused`前提（`UserCommand CloseFocused`の型直前のコメント
    // 参照）はこのラウンドでは一度だけの明示的な`ForegroundObserved`投入で満たす——単発の確認なので
    // ラウンド7/8のような実時間のEVENT_SYSTEM_FOREGROUND中継は不要。
    printfn "\n--- Round 10: real RequestClose (WM_CLOSE) dispatch against a self-owned test window ---"
    let reducer5 = Reducer(RuntimePolicy.defaults, "phase-b-round10", "wm-host-round10", "no-policy-artifact", None)
    let effectQueue5 = ConcurrentQueue<GatedEffect>()
    let resultQueue5 = ConcurrentQueue<RequestId * Result<unit, NativeCallRecord>>()
    let mutable keepDispatching5 = true
    let effectApplyThread5 =
        Thread(fun () ->
            while keepDispatching5 do
                match effectQueue5.TryDequeue() with
                | true, gated ->
                    let effect = GatedEffect.effect gated
                    match Wm.Windows.EffectDispatch.apply effect with
                    | Some result ->
                        let ridOpt = match effect with RequestClose (rid, _) -> Some rid | _ -> None
                        match ridOpt with
                        | Some rid -> resultQueue5.Enqueue(rid, result)
                        | None -> ()
                    | None -> ()
                | false, _ -> Thread.Sleep(1))
    effectApplyThread5.IsBackground <- true
    effectApplyThread5.Name <- "wm-effect-apply-round10"
    effectApplyThread5.Start()

    let enqueueEffects5 (effects: GatedEffect list) = for g in effects do effectQueue5.Enqueue(g)
    let submit5 (now: int64) (source: string) (input: InputEvent) =
        let sr = reducer5.Submit now source input
        enqueueEffects5 sr.Effects
        sr
    let advance5 () =
        match reducer5.AdvanceToNextTimer() with
        | Some sr -> enqueueEffects5 sr.Effects; true
        | None -> false
    let drainResults5 (now: int64) =
        let mutable more = true
        while more do
            match resultQueue5.TryDequeue() with
            | true, (rid, result) -> submit5 now "wm-host-effect-thread" (EffectCallReturned (rid, result, None)) |> ignore
            | false, _ -> more <- false
    let pump5 (ms: int) =
        let deadline = liveNow () + int64 ms
        while liveNow () < deadline do
            Application.DoEvents()
            drainResults5 (liveNow ())
            while reducer5.Timers.NextDeadline |> Option.exists (fun d -> d <= liveNow ()) do
                advance5 () |> ignore
            Thread.Sleep(1)

    use testForm5 = new Form(Text = "wm-host-round10-test-window", Width = 300, Height = 200)
    testForm5.Show()
    pump5 300
    let hwnd5 = testForm5.Handle // クローズ後に`.Handle`へアクセスし直すとObjectDisposedExceptionになるため先に控える
    let w5 = WindowId (hwnd5.ToInt64())

    submit5 (liveNow ()) "wm-host" (MonitorTopologyObserved monitors) |> ignore
    pump5 300
    submit5 (liveNow ()) "wm-host" (RawFirstSeen (observeWindow (liveNow ()) monitorHandles titleMatcher hwnd5) |> normalize |> List.exactlyOne) |> ignore
    pump5 500
    submit5 (liveNow ()) "wm-host" (ForegroundObserved (observation (liveNow ()) (Ok (Some w5)))) |> ignore
    pump5 300

    submit5 (liveNow ()) "wm-host" (UserCommand CloseFocused) |> ignore
    pump5 500

    keepDispatching5 <- false
    effectApplyThread5.Join(2000) |> ignore

    // Coreとは無関係に、実際にそのHWNDが本当に消えたかを直接問い合わせて確認する（ラウンド3/4/6/7/8と
    // 同じ、Core状態への依存だけに頼らない二重確認）。
    let stillExists = IsWindow(hwnd5)
    printfn "Round 10 summary: real window still exists after RequestClose dispatch: %b (expected false)" stillExists

    // 段11：ラウンド11。§21（v2.102、プロジェクトオーナー確認済み：最小化を採用）に基づき実装した
    // `SetPresence`を実際に発行する。`UserCommand (SwitchWorkspace _)`で今の窓のWorkspaceを隠すと
    // Topology.fsが`WorkspaceHidden`を出し、Placement.onWorkspaceHiddenが`SetPresence(shown=false)`を
    // 発行する——これをEffectDispatchが実際の`ShowWindowAsync(SW_MINIMIZE)`へ変換する。元の
    // Workspaceへ戻せば逆（`shown=true`→復元）も同じ経路で検証できる。隔離された自分専用のテスト窓
    // 1枚だけを対象にする（ラウンド3/4と同じ安全上の理由）。
    printfn "\n--- Round 11: real SetPresence (minimize/restore via SwitchWorkspace) dispatch ---"
    let reducer6 = Reducer(RuntimePolicy.defaults, "phase-b-round11", "wm-host-round11", "no-policy-artifact", None)
    let effectQueue6 = ConcurrentQueue<GatedEffect>()
    let resultQueue6 = ConcurrentQueue<RequestId * Result<unit, NativeCallRecord>>()
    let mutable keepDispatching6 = true
    let effectApplyThread6 =
        Thread(fun () ->
            while keepDispatching6 do
                match effectQueue6.TryDequeue() with
                | true, gated ->
                    let effect = GatedEffect.effect gated
                    match Wm.Windows.EffectDispatch.apply effect with
                    | Some result ->
                        let ridOpt = match effect with SetPresence (rid, _, _) -> Some rid | _ -> None
                        match ridOpt with
                        | Some rid -> resultQueue6.Enqueue(rid, result)
                        | None -> ()
                    | None -> ()
                | false, _ -> Thread.Sleep(1))
    effectApplyThread6.IsBackground <- true
    effectApplyThread6.Name <- "wm-effect-apply-round11"
    effectApplyThread6.Start()

    let enqueueEffects6 (effects: GatedEffect list) = for g in effects do effectQueue6.Enqueue(g)
    let submit6 (now: int64) (source: string) (input: InputEvent) =
        let sr = reducer6.Submit now source input
        enqueueEffects6 sr.Effects
        sr
    let advance6 () =
        match reducer6.AdvanceToNextTimer() with
        | Some sr -> enqueueEffects6 sr.Effects; true
        | None -> false
    let drainResults6 (now: int64) =
        let mutable more = true
        while more do
            match resultQueue6.TryDequeue() with
            | true, (rid, result) -> submit6 now "wm-host-effect-thread" (EffectCallReturned (rid, result, None)) |> ignore
            | false, _ -> more <- false
    let pump6 (ms: int) =
        let deadline = liveNow () + int64 ms
        while liveNow () < deadline do
            Application.DoEvents()
            drainResults6 (liveNow ())
            while reducer6.Timers.NextDeadline |> Option.exists (fun d -> d <= liveNow ()) do
                advance6 () |> ignore
            Thread.Sleep(1)

    use testForm6 = new Form(Text = "wm-host-round11-test-window", Width = 300, Height = 200)
    testForm6.Show()
    pump6 300
    let hwnd6 = testForm6.Handle

    submit6 (liveNow ()) "wm-host" (MonitorTopologyObserved monitors) |> ignore
    pump6 300
    submit6 (liveNow ()) "wm-host" (RawFirstSeen (observeWindow (liveNow ()) monitorHandles titleMatcher hwnd6) |> normalize |> List.exactlyOne) |> ignore
    pump6 500

    let minimizedBeforeHide = IsIconic(hwnd6)
    submit6 (liveNow ()) "wm-host" (UserCommand (SwitchWorkspace (WorkspaceId 1))) |> ignore
    pump6 500
    let minimizedAfterHide = IsIconic(hwnd6)

    submit6 (liveNow ()) "wm-host" (UserCommand (SwitchWorkspace (WorkspaceId 0))) |> ignore
    pump6 500
    let minimizedAfterShow = IsIconic(hwnd6)

    keepDispatching6 <- false
    effectApplyThread6.Join(2000) |> ignore
    testForm6.Close()

    // Coreとは無関係に、実際のIsIconicを直接問い合わせて確認する（ラウンド3/4/6/7/8/10と同じ）。
    printfn "Round 11 summary: IsIconic before-hide=%b after-SwitchWorkspace-hide=%b after-SwitchWorkspace-restore=%b" minimizedBeforeHide minimizedAfterHide minimizedAfterShow
    printfn "SetPresence actually minimized the real window on hide, and restored it on show: %b"
        (not minimizedBeforeHide && minimizedAfterHide && not minimizedAfterShow)

    // 段12：ラウンド12。§8.4の安全停止を、ラウンド9の検知だけでなく実際の復旧動作まで通す。
    // 「直前に隠した窓」のHWND一覧は、Effect適用スレッドが`SetPresence(shown=false)`を実際に適用
    // するたびに監視コンポーネント自身が追跡する（GlazeWMのwm-watcherと同じ、8.4本文参照）。
    // ウォッチドッグが発動したら、Reducerを一切経由せず（復旧操作はGatedEffectを経由しない例外経路、
    // 8.4の明文）、この一覧にある窓だけへ直接`ShowWindowAsync(SW_RESTORE)`を呼び、
    // `SafetyRecoveryAction`を専用Journalへ記録する。隠した後、Coreへは一切「戻して」と言わずに
    // ウォッチドッグの復旧だけで実際に復元されることを実機で確認する。
    printfn "\n--- Round 12: §8.4 safety-stop recovery (watcher restores last-hidden windows, bypassing Core) ---"
    let watchdog2JournalPath = Path.Combine(Path.GetTempPath(), sprintf "wm-watchdog2-%s.ndjson" (Guid.NewGuid().ToString("N")))
    let reducer7 = Reducer(RuntimePolicy.defaults, "phase-b-round12", "wm-host-round12", "no-policy-artifact", None)
    let effectQueue7 = ConcurrentQueue<GatedEffect>()
    let resultQueue7 = ConcurrentQueue<RequestId * Result<unit, NativeCallRecord>>()
    let mutable keepDispatching7 = true
    let lastHiddenHwnds = Collections.Concurrent.ConcurrentDictionary<IntPtr, unit>()
    let effectApplyThread7 =
        Thread(fun () ->
            while keepDispatching7 do
                match effectQueue7.TryDequeue() with
                | true, gated ->
                    let effect = GatedEffect.effect gated
                    match Wm.Windows.EffectDispatch.apply effect with
                    | Some result ->
                        match effect with
                        | SetPresence (rid, WindowId raw, shown) ->
                            (match result with
                             | Ok () ->
                                 let hwnd = IntPtr raw
                                 if shown then lastHiddenHwnds.TryRemove(hwnd) |> ignore
                                 else lastHiddenHwnds.[hwnd] <- ()
                             | Error _ -> ())
                            resultQueue7.Enqueue(rid, result)
                        | _ -> ()
                    | None -> ()
                | false, _ -> Thread.Sleep(1))
    effectApplyThread7.IsBackground <- true
    effectApplyThread7.Name <- "wm-effect-apply-round12"
    effectApplyThread7.Start()

    let enqueueEffects7 (effects: GatedEffect list) = for g in effects do effectQueue7.Enqueue(g)
    let submit7 (now: int64) (source: string) (input: InputEvent) =
        let sr = reducer7.Submit now source input
        enqueueEffects7 sr.Effects
        sr
    let advance7 () =
        match reducer7.AdvanceToNextTimer() with
        | Some sr -> enqueueEffects7 sr.Effects; true
        | None -> false
    let drainResults7 (now: int64) =
        let mutable more = true
        while more do
            match resultQueue7.TryDequeue() with
            | true, (rid, result) -> submit7 now "wm-host-effect-thread" (EffectCallReturned (rid, result, None)) |> ignore
            | false, _ -> more <- false
    let pump7 (ms: int) =
        let deadline = liveNow () + int64 ms
        while liveNow () < deadline do
            Application.DoEvents()
            drainResults7 (liveNow ())
            while reducer7.Timers.NextDeadline |> Option.exists (fun d -> d <= liveNow ()) do
                advance7 () |> ignore
            Thread.Sleep(1)

    use testForm7 = new Form(Text = "wm-host-round12-test-window", Width = 300, Height = 200)
    testForm7.Show()
    pump7 300
    let hwnd7 = testForm7.Handle

    submit7 (liveNow ()) "wm-host" (MonitorTopologyObserved monitors) |> ignore
    pump7 300
    submit7 (liveNow ()) "wm-host" (RawFirstSeen (observeWindow (liveNow ()) monitorHandles titleMatcher hwnd7) |> normalize |> List.exactlyOne) |> ignore
    pump7 500

    submit7 (liveNow ()) "wm-host" (UserCommand (SwitchWorkspace (WorkspaceId 1))) |> ignore
    pump7 500
    let minimizedAfterHide7 = IsIconic(hwnd7)
    let trackedAfterHide7 = lastHiddenHwnds.ContainsKey(hwnd7)
    printfn "Round 12: minimized after hide=%b tracked by watcher=%b" minimizedAfterHide7 trackedAfterHide7

    let mutable lastHeartbeat2 = Environment.TickCount64
    let mutable watchdog2Triggered = false
    let heartbeatTimeoutMs2 = 500L
    let watchdog2Thread =
        Thread(fun () ->
            while not watchdog2Triggered do
                Thread.Sleep(50)
                let gap = Environment.TickCount64 - lastHeartbeat2
                if gap > heartbeatTimeoutMs2 then
                    watchdog2Triggered <- true
                    for KeyValue(hwnd, _) in lastHiddenHwnds do
                        ShowWindowAsync(hwnd, SW_RESTORE) |> ignore
                        lastHiddenHwnds.TryRemove(hwnd) |> ignore
                        let entry: JournalEntry =
                            { SchemaVersion = 1
                              SessionId = "wm-watchdog"
                              Sequence = 0L
                              MonotonicTicks = Environment.TickCount64
                              WallTimeUtc = DateTimeOffset.UtcNow
                              CorrelationId = None
                              CausationId = None
                              PolicyArtifactHash = "no-policy-artifact"
                              CoreBuildId = "wm-watchdog"
                              Kind = SafetyRecoveryAction
                              Payload = SafetyRecoveryActionPayload (sprintf "restored hwnd=%A after heartbeat gap %dms" hwnd gap) }
                        Wm.Journal.JournalWriter.appendEntry watchdog2JournalPath entry
                    printfn "  [round12 watchdog] triggered: restored last-hidden window(s) directly, bypassing Core")
    watchdog2Thread.IsBackground <- true
    watchdog2Thread.Name <- "wm-safety-watchdog-round12"
    watchdog2Thread.Start()

    printfn "Simulating a main-loop heartbeat gap (not the real thing) for 700ms -- Core is never told to restore anything..."
    Thread.Sleep(700)
    lastHeartbeat2 <- Environment.TickCount64
    // メインループが（ハングから）回復したことを表す：`ShowWindowAsync`はキューへ投函するだけなので、
    // 対象窓のスレッド（＝このメインスレッド）が実際にポンプしないと見た目に反映されない
    // （`SetWindowPos`のSWP_ASYNCWINDOWPOSと同じ理由）。単に`Thread.Sleep`するだけでは届かない——
    // 実機で最初に`minimizedAfterRecovery=true`になった実際の原因（ここで発見・修正）。
    let recoveryPumpDeadline = liveNow () + 300L
    while liveNow () < recoveryPumpDeadline do
        Application.DoEvents()
        Thread.Sleep(1)

    let minimizedAfterRecovery = IsIconic(hwnd7)
    let trackedAfterRecovery = lastHiddenHwnds.ContainsKey(hwnd7)

    keepDispatching7 <- false
    effectApplyThread7.Join(2000) |> ignore
    testForm7.Close()

    let watchdog2Entries = Wm.Journal.JournalWriter.readAll watchdog2JournalPath |> List.ofSeq
    printfn "Round 12 summary: watchdog2Triggered=%b minimizedAfterRecovery=%b (expected false) trackedAfterRecovery=%b (expected false) journalEntries=%d"
        watchdog2Triggered minimizedAfterRecovery trackedAfterRecovery watchdog2Entries.Length
    printfn "Watchdog restored the last-hidden window on its own, without Core ever being told to: %b"
        (watchdog2Triggered && not minimizedAfterRecovery && not trackedAfterRecovery)

    printfn "\nDone."
    0
