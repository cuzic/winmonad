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
open System.Drawing
open System.IO
open System.IO.Pipes
open System.Runtime.InteropServices
open System.Security.Principal
open System.Text
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open System.Windows.Forms
open Wm.Contracts.Identifiers
open Wm.Contracts.SharedVocabulary
open Wm.Contracts.ErrorTypes
open Wm.Contracts.CoreCommand
open Wm.Contracts.GatedEffect
open Wm.Contracts.JournalTypes
open Wm.Contracts.Observation
open Wm.Core.WmState
open Wm.Policy.PolicyTypes
open Wm.Policy.PolicyEvaluator
open Wm.Policy.PolicyAnalyzer
open Wm.Layouts.LayoutRegistry
open Wm.Runtime.Reducer
open Wm.Normalization.WindowEventNormalizer
open Wm.Windows.Native
open Wm.Windows.RealObservation
open Wm.Windows.EventHooks
open Wm.Host.EffectHarness
open Wm.Host.ResidentHost
open Wm.Host.PolicyArtifact
open Wm.Host.InspectPipeServer
open Wm.DebugProtocol.Snapshot
open Wm.DebugProtocol.SnapshotJson
open Wm.DebugProtocol.Policy
open Wm.DebugProtocol.Explain
open Wm.DebugProtocol.Analyzer
open Wm.DebugProtocol.Deploy
open Wm.DebugProtocol.Intervene
open Wm.DebugProtocol.WireFraming
open Wm.ReplClient.WmClient

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

/// ラウンド14（E-4全画面検出の前提、architecture.md §21）向けの使い捨て子プロセスモード：
/// Wm.Hostのスコープ（「自分で作った使い捨てのテスト窓だけ」、型直前のラウンド3のコメント参照）
/// を守ったまま他社プロセスの窓を用意するため、notepad.exe等の実アプリの代わりに自分自身を
/// 子プロセスとして起動する（spikes/Phase0CiSpikes/Program.fsの`--hang-child`等と同じ方式）。
let private runRound14Child () =
    try
        let form = new Form(Text = "wm-host-round14-child", StartPosition = FormStartPosition.Manual, Bounds = Rectangle(50, 50, 300, 200))
        form.Show()
        for _ in 1 .. 10 do
            Application.DoEvents()
            Thread.Sleep(20)
        printfn "HWND=%d" (int64 form.Handle)
        Console.Out.Flush()
        // `Thread.Sleep`だけでは止まる——`SetWindowPos`/`SWP_ASYNCWINDOWPOS`はキューへ投函する
        // だけなので、対象窓の持ち主スレッド（この子プロセスのこのスレッド）が実際にポンプし
        // 続けないと親が動かした結果が一切反映されない（S26スパイクの型直前のコメントと同じ地雷。
        // 初版はここで`Thread.Sleep(15000)`にしてしまい、Round 14がrawTrackedEvents=0で実際に
        // 失敗した——ハング耐性のための非同期呼出だからこそポンプが要る、という教訓）。
        let deadline = Environment.TickCount64 + 15000L
        while Environment.TickCount64 < deadline do
            Application.DoEvents()
            Thread.Sleep(10)
    with ex ->
        printfn "ERROR=%s" (ex.Message.Replace("\n", " ").Replace("\r", ""))
        Console.Out.Flush()
        Environment.Exit(1)

[<EntryPoint>]
let main argv =
    // spikes/Phase0CiSpikes/Program.fsの`suppressCrashDialogs`と同じ理由で、分岐より前に両方の
    // プロセス（親・`--round14-child`子）で呼ぶ：子の未処理例外がWindows Error Reporting UIを
    // 出すと、親の`StandardOutput.ReadLine()`がヘッドレスCIで無期限に止まる。
    SetErrorMode(SEM_FAILCRITICALERRORS ||| SEM_NOGPFAULTERRORBOX) |> ignore
    if argv.Length > 0 && argv.[0] = "--round14-child" then
        runRound14Child ()
        exit 0
    // DPI awareness宣言（v2.116、Opus敵対的レビュー`opus-review-frame-inset.md`論点1-G「Wm.Hostに
    // DPI awarenessの宣言が見当たらない」対策）：Per-Monitor V2を宣言しないプロセス（既定のUnaware）
    // では、`GetWindowRect`/`SetWindowPos`/`GetMonitorInfo`は仮想化された論理座標を返すが、
    // `DwmGetWindowAttribute(DWMWA_EXTENDED_FRAME_BOUNDS)`は常に物理座標を返す——両者の差が
    // v2.112で実測した「不可視リサイズ枠のinset」ではなく「スケール誤差＋inset」になり、将来の
    // 補正実装が100%以外のモニターで暴走する。今までの実機検証（pad2py-pad-host・GitHub Actions
    // windows-latest）がどちらも100%DPIだったため、この宣言漏れ自体は症状を出していなかった
    // （Opusレビューの推測どおり）。どの窓も生成・問い合わせされる前、エントリポイントの最初で
    // 呼ぶ必要がある。
    Application.SetHighDpiMode(HighDpiMode.PerMonitorV2) |> ignore

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
    let mutable dispatchedCount = 0
    let mutable skippedCount = 0
    let harness3 =
        EffectHarness(
            reducer3, liveNow,
            onDispatch = (fun _ _ -> Interlocked.Increment(&dispatchedCount) |> ignore),
            onSkip = (fun () -> Interlocked.Increment(&skippedCount) |> ignore))
    let submit3 = harness3.Submit
    let pump3 (ms: int) = harness3.Pump ms

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
    harness3.Stop()

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
    let mutable dispatchedCount4 = 0
    let mutable skippedCount4 = 0
    let harness4 =
        EffectHarness(
            reducer4, liveNow,
            onDispatch = (fun effect result ->
                printfn "  [round7/8 dispatch] %A -> %A" effect result
                Interlocked.Increment(&dispatchedCount4) |> ignore),
            onSkip = (fun () -> Interlocked.Increment(&skippedCount4) |> ignore))
    let submit4 = harness4.Submit
    let pump4 (ms: int) = harness4.Pump ms

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
    harness4.Stop()

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
    let harness5 = EffectHarness(reducer5, liveNow)
    let submit5 = harness5.Submit
    let pump5 (ms: int) = harness5.Pump ms

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

    harness5.Stop()

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
    let harness6 = EffectHarness(reducer6, liveNow)
    let submit6 = harness6.Submit
    let pump6 (ms: int) = harness6.Pump ms

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

    harness6.Stop()
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
    let lastHiddenHwnds = Collections.Concurrent.ConcurrentDictionary<IntPtr, unit>()
    let harness7 =
        EffectHarness(
            reducer7, liveNow,
            onDispatch = (fun effect result ->
                match effect, result with
                | SetPresence (_, WindowId raw, shown), Ok () ->
                    let hwnd = IntPtr raw
                    if shown then lastHiddenHwnds.TryRemove(hwnd) |> ignore
                    else lastHiddenHwnds.[hwnd] <- ()
                | _ -> ()))
    let submit7 = harness7.Submit
    let pump7 (ms: int) = harness7.Pump ms

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

    harness7.Stop()
    testForm7.Close()

    let watchdog2Entries = Wm.Journal.JournalWriter.readAll watchdog2JournalPath |> List.ofSeq
    printfn "Round 12 summary: watchdog2Triggered=%b minimizedAfterRecovery=%b (expected false) trackedAfterRecovery=%b (expected false) journalEntries=%d"
        watchdog2Triggered minimizedAfterRecovery trackedAfterRecovery watchdog2Entries.Length
    printfn "Watchdog restored the last-hidden window on its own, without Core ever being told to: %b"
        (watchdog2Triggered && not minimizedAfterRecovery && not trackedAfterRecovery)

    // 段9：ラウンド13（v2.124）。v2.122の3-B再発行ループ対策（`dispatchPlacements`の`alreadyAtTarget`
    // ショートカット、Placement.fsの型直前のコメント参照）の実際の発火条件——「発見時点で既に
    // 窓がTargetどおりの位置にある」——を実機で再現する。既存のラウンド3〜12は毎回新しいテスト窓を
    // 任意の初期位置に作るため、この条件を自然には再現できない（v2.122実装時に明記した既知の未検証
    // ギャップ）。ここでは、テスト窓をCoreへ発見させる前に、実際にOSへ問い合わせたinset（S21と同じ
    // 手法：GetWindowRectとDwmGetWindowAttributeの差）で逆算した位置へ`SetWindowPos`で動かし、
    // VisibleFrameRectが目標（この1枚だけのTiled窓ならMonocleが計算するWorkArea全体）と完全に一致する
    // ことを確認してから、初めて`RawFirstSeen`を投入する——実機の`observeWindow`はこの時点で
    // NativeRect/VisibleFrameRectの対を一度に問い合わせる（v2.122/v2.123で読み取りから確認済みの
    // 事実）ので、`FrameInsets`も`alreadyAtTarget`の判定も同じ発見イベントの中で揃う。
    printfn "\n--- Round 13: already-at-target discovery dispatches zero SetRect (v2.122 3-B fix) ---"
    let journalPath13 = Path.Combine(Path.GetTempPath(), sprintf "wm-host-round13-%s.ndjson" (Guid.NewGuid().ToString("N")))
    printfn "Round 13 journal: %s" journalPath13
    let reducer13 = Reducer(RuntimePolicy.defaults, "phase-b-round13", "wm-host-round13", "no-policy-artifact", Some journalPath13)
    let mutable dispatchedCount13 = 0
    let mutable skippedCount13 = 0
    let harness13 =
        EffectHarness(
            reducer13, liveNow,
            onDispatch = (fun e _ -> Interlocked.Increment(&dispatchedCount13) |> ignore; printfn "  [round13] UNEXPECTED dispatch: %A" e),
            onSkip = (fun () -> Interlocked.Increment(&skippedCount13) |> ignore))
    let submit13 = harness13.Submit
    let pump13 (ms: int) = harness13.Pump ms

    submit13 (liveNow ()) "wm-host" (MonitorTopologyObserved monitors) |> ignore
    pump13 500

    use testForm13 = new Form(Text = "wm-host-round13-test-window", Width = 300, Height = 200)
    testForm13.Show()
    pump13 300
    let hwnd13 = testForm13.Handle

    // Monocle（既定Layout）が1枚のTiled窓に計算するTargetは、プライマリモニターのWorkArea全体
    // （ラウンド3と同じ前提）。
    let primaryMonitor = monitors |> List.find (fun m -> m.IsPrimary)
    let target = primaryMonitor.WorkArea

    let readNative (hwnd: IntPtr) : RECT option =
        let mutable r = RECT()
        if GetWindowRect(hwnd, &r) then Some r else None
    let readVisible (hwnd: IntPtr) : RECT option =
        let mutable r = RECT()
        let hr = DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, &r, Marshal.SizeOf(typeof<RECT>))
        if hr = 0 then Some r else None

    match readNative hwnd13, readVisible hwnd13 with
    | Some native0, Some visible0 ->
        // S21/S26と同じ手法：今の位置でのシャドウ（不可視リサイズ枠）を測り、そのシャドウぶんだけ
        // 外側へ広げた位置へ動かせば、VisibleFrameRectがちょうどtargetに一致するはず。
        let shadowL = visible0.Left - native0.Left
        let shadowT = visible0.Top - native0.Top
        let shadowR = native0.Right - visible0.Right
        let shadowB = native0.Bottom - visible0.Bottom
        let callX = target.X - shadowL
        let callY = target.Y - shadowT
        let callW = target.Width + shadowL + shadowR
        let callH = target.Height + shadowT + shadowB
        SetWindowPos(hwnd13, IntPtr.Zero, callX, callY, callW, callH, SWP_NOZORDER) |> ignore
        pump13 300

        match readVisible hwnd13 with
        | Some visibleAfter when
            visibleAfter.Left = target.X && visibleAfter.Top = target.Y
            && visibleAfter.Right - visibleAfter.Left = target.Width && visibleAfter.Bottom - visibleAfter.Top = target.Height ->
            printfn "  pre-positioned test window: VisibleFrameRect now exactly matches target=%A (sanity check passed)" target
            submit13 (liveNow ()) "wm-host" (RawFirstSeen (observeWindow (liveNow ()) monitorHandles titleMatcher hwnd13) |> normalize |> List.exactlyOne) |> ignore
            pump13 1500 // 分類・Reconcileに十分な余裕を取る（ラウンド3と同じ）

            let testWindowId13 = WindowId (hwnd13.ToInt64())
            let pending13 = Map.count reducer13.State.Placement.Pending
            let movability13 = Map.tryFind testWindowId13 reducer13.State.Placement.Movability
            printfn "Round 13 summary: dispatched=%d skipped(no handler)=%d Pending=%d Movability=%A" dispatchedCount13 skippedCount13 pending13 movability13
            // `skipped(no handler)`はこのラウンドの判定とは無関係（型直前のコメント参照）：窓の発見は
            // 常に`Reprobe (w, KTargetIntegrity)`等（ProbeOnce、`architecture.md`§4.5）を能動的に
            // 発行するため、新規発見のたびに2件前後の「ハンドラ無し」が出るのが通常——ラウンド3の
            // 最初の発見（SetRectも1件発行する）でも同じ`skipped(no handler)=2`が観測されている。
            // このラウンドが確認したいのは「SetRectが0件（＝3-Bの再発行が起きない）」だけ。
            let passed = dispatchedCount13 = 0 && pending13 = 0
            printfn "Round 13 PASSED (zero SetRect dispatched for an already-at-target discovery): %b" passed
        | other ->
            printfn "  pre-positioning failed, VisibleFrameRect after SetWindowPos=%A (expected exact match with target=%A) -- skipping Round 13's actual check" other target

    | _ -> printfn "  could not read this window's NativeRect/DWM frame -- skipping Round 13"

    harness13.Stop()
    testForm13.Close()

    // 段10：ラウンド14（E-4全画面検出の前提、architecture.md §21、opus-review-fullscreen-detection.md
    // §0-1対応）。他社プロセス（自分自身を`--round14-child`で起動した子プロセス、ラウンド3の
    // コメントが守る「自分で作った使い捨てのテスト窓だけ」を保ったまま他社プロセス扱いにできる）の
    // 窓の矩形変化を、ラウンド4が使う既存の再観測経路（`RawReobserved`）で捉えられるかを、実際の
    // Wm.Normalization→Wm.Runtimeパイプラインで確認する。`EventHooks.hookLocationChangeForProcess`
    // （自プロセス限定）ではなく、ラウンド8がEVENT_SYSTEM_FOREGROUND向けに既に使っている汎用版
    // `hookSingleEvent`（システム全体を購読し、呼び出し側のコールバックでHWNDを絞り込む）を使う——
    // `EventHooks.fs`の`hookLocationChangeForProcess`の型直前のコメントが挙げる「全プロセス購読は
    // 流量が大きすぎる」という懸念への対策は、この「安価なHWNDフィルタをコールバック内で先に行う」
    // こと自体であり、それが実際に機能すること（追跡対象の子窓は再観測・Core投入まで届き、
    // 追跡対象外の自作窓は一切Core投入に繋がらない）をここで確認する。spikes/Phase0CiSpikes/
    // Program.fsのS28（F36）がWinEventHook単体で実証した「他社窓のLOCATIONCHANGEはシステム全体
    // 購読で観測できる」ことを、ここでは実際のプロダクションパイプライン（Normalizer/Reducer）で
    // 再確認する。境界スタイル（borderless）の読み取り（`GetWindowLong`）もここで配線するが、
    // Core側の信念・抑止（`IsBorderless`/`PlacementState.Fullscreen`）はまだ実装していないため、
    // このラウンドでは診断ログに出すだけで判断には使わない。
    printfn "\n--- Round 14: filtered system-wide LOCATIONCHANGE observes a cross-process tracked window, ignores an untracked one (E-4 prerequisite) ---"
    let reducer14 = Reducer(RuntimePolicy.defaults, "phase-b-round14", "wm-host-round14", "no-policy-artifact", None)
    let mutable dispatchedCount14 = 0
    let harness14 = EffectHarness(reducer14, liveNow, onDispatch = (fun e _ -> Interlocked.Increment(&dispatchedCount14) |> ignore; printfn "  [round14] UNEXPECTED dispatch: %A" e))
    let submit14 = harness14.Submit
    let pump14 (ms: int) = harness14.Pump ms

    let exePath14 = Process.GetCurrentProcess().MainModule.FileName
    let psi14 = ProcessStartInfo(exePath14, "--round14-child")
    psi14.RedirectStandardOutput <- true
    psi14.UseShellExecute <- false
    let child14 = Process.Start(psi14)
    // spikes/Phase0CiSpikes/Program.fsの`readLineWithTimeout`と同じ理由：`StandardOutput.ReadLine()`
    // を無条件で待つと、子が何も書かずに終わる経路でここが無期限に止まる（同ファイルの実機CIで
    // 実際に踏んだ既知の地雷と同じ形）。
    let readLine14WithTimeout (timeoutMs: int) : string option =
        let task = Task.Run<string>(Func<string>(fun () -> child14.StandardOutput.ReadLine()))
        if task.Wait(timeoutMs) then Some task.Result else None
    let mutable trackedHwnd = IntPtr.Zero
    match readLine14WithTimeout 8000 with
    | Some l when l <> null && l.StartsWith("HWND=") -> trackedHwnd <- IntPtr(Int64.Parse(l.Substring(5)))
    | other -> printfn "  child did not report an HWND within timeout as expected (got %A)" other

    use untrackedForm14 = new Form(Text = "wm-host-round14-untracked", StartPosition = FormStartPosition.Manual, Bounds = Rectangle(400, 400, 300, 200))
    untrackedForm14.Show()
    pump14 300
    let untrackedHwnd14 = untrackedForm14.Handle

    if trackedHwnd = IntPtr.Zero then
        printfn "  skipping Round 14 -- child window HWND unavailable"
    else
        let mutable trackedReobserveCount = 0
        let mutable rawTrackedEvents = 0
        let mutable rawUntrackedEvents = 0
        let handleLocationChange14 (_eventType: uint32) (hwnd: IntPtr) =
            if hwnd = trackedHwnd then
                rawTrackedEvents <- rawTrackedEvents + 1
                let now = liveNow ()
                let snapshot = observeWindow now monitorHandles titleMatcher hwnd
                let style = GetWindowLong(hwnd, GWL_STYLE)
                let borderless = (style &&& (WS_CAPTION ||| WS_THICKFRAME)) = 0
                printfn "  [round14] tracked (cross-process) window reobserved via system-wide LOCATIONCHANGE: borderless=%b rect=%A" borderless snapshot.NativeRect
                for input in normalize (RawReobserved snapshot) do
                    trackedReobserveCount <- trackedReobserveCount + 1
                    submit14 now "wm-host-locationchange" input |> ignore
            elif hwnd = untrackedHwnd14 then
                // 意図的に何もしない：安価なHWNDフィルタで流量を抑えること自体の実証。
                rawUntrackedEvents <- rawUntrackedEvents + 1

        let locationHook14 = hookSingleEvent EVENT_OBJECT_LOCATIONCHANGE handleLocationChange14

        SetWindowPos(trackedHwnd, IntPtr.Zero, 60, 60, 500, 350, SWP_NOZORDER ||| SWP_ASYNCWINDOWPOS) |> ignore
        SetWindowPos(untrackedHwnd14, IntPtr.Zero, 410, 410, 350, 250, SWP_NOZORDER ||| SWP_ASYNCWINDOWPOS) |> ignore
        pump14 500

        locationHook14.Unhook()
        printfn "Round 14 summary: rawTrackedEvents=%d rawUntrackedEvents=%d trackedReobserveCount=%d dispatched(unexpected)=%d"
            rawTrackedEvents rawUntrackedEvents trackedReobserveCount dispatchedCount14
        printfn "Round 14 PASSED (cross-process tracked window observed via the real pipeline, untracked window produced zero reobservation): %b"
            (rawTrackedEvents > 0 && trackedReobserveCount > 0 && dispatchedCount14 = 0)

    harness14.Stop()
    (try
        if not child14.HasExited then child14.Kill()
     with _ -> ())
    untrackedForm14.Close()

    // Round 15：常駐Hostモードの骨格（architecture.md v2.135、opus-review-named-pipe-transport.md
    // 1-2対応）。Round 1〜14はいずれも「固定の実時間だけ回して終わる」デモだったが、ここで初めて
    // ResidentHost（Reducer所有スレッド＋外部スレッドからのSnapshotクエリキュー＋スレッドセーフな
    // 停止要求）を実際に別スレッドで走らせ、(1)別スレッドからのクエリがResidentHost自身のループ
    // スレッドによって処理され往復すること、(2)別スレッドからのRequestStop()で実際にループが
    // 終了すること、の2点を実機で確認する。Named Pipeサーバー自体やS31〜S36のACL/整合性レベル
    // 検査はまだ繋がない——これは次のスライス。
    printfn "\n--- Round 15: ResidentHost skeleton (cross-thread snapshot query + graceful stop) ---"
    let journalPath15 = Path.Combine(Path.GetTempPath(), sprintf "wm-host-round15-%s.ndjson" (Guid.NewGuid().ToString("N")))
    printfn "Round 15 journal: %s" journalPath15
    let reducer15 = Reducer(RuntimePolicy.defaults, "phase-b-round15", "wm-host-round15", "no-policy-artifact", Some journalPath15)
    let residentHost15 = ResidentHost(reducer15, liveNow)
    let loopThread15 = Thread(fun () -> residentHost15.Run(tickMs = 1))
    loopThread15.IsBackground <- true
    loopThread15.Start()

    // ループスレッドが実際に回り始める猶予。
    Thread.Sleep(50)
    let queryTask15 = residentHost15.EnqueueSnapshotQuery()
    let queryCompleted15 = queryTask15.Wait(2000)
    let snapshotOk15 =
        queryCompleted15
        && queryTask15.Result.Revision = 0L
        && List.isEmpty queryTask15.Result.Windows
    printfn "Round 15 snapshot query: completed=%b revision=%s windows=%d"
        queryCompleted15
        (if queryCompleted15 then string queryTask15.Result.Revision else "n/a")
        (if queryCompleted15 then List.length queryTask15.Result.Windows else -1)

    residentHost15.RequestStop()
    let stoppedInTime15 = loopThread15.Join(3000)
    printfn "Round 15 PASSED (cross-thread snapshot query answered by the owning loop thread, RequestStop() actually terminated the loop): %b"
        (snapshotOk15 && stoppedInTime15)

    // Round 16：Named Pipeサーバー、最初の接続スライス（architecture.md v2.135/10.1、S31〜S36、
    // opus-review-named-pipe-transport.md）。P/Invoke経由のCreateNamedPipe＋Host自身の整合性
    // レベルへ動的設定したDACL/ラベル（S31/S32）、クライアントのImpersonationレベル接続＋
    // RunAsClientによる実トークン検査（F41/F42、ユーザー決定2026-11-05）を初めて実際に1本の
    // パイプとして組み合わせる。ワイヤプロトコル（長さプレフィックス＋JSON）・発見用ファイル配布・
    // Snapshot本体の応答はまだ無い——このラウンドは「正しくACLされたパイプへImpersonationレベルで
    // 接続したクライアントを、サーバーが実トークンまで検査して受け付けられるか」だけを確認する。
    printfn "\n--- Round 16: Named Pipe server, first connection slice (dynamic ACL + RunAsClient check) ---"
    let pipeName16 = generatePipeName ()
    match createRestrictedPipe pipeName16 with
    | Error e -> printfn "Round 16 FAILED (createRestrictedPipe): %s" e
    | Ok pipe16 ->
        use pipe16 = pipe16
        use cts16 = new CancellationTokenSource()
        let mutable observedPrincipal16: Result<ClientPrincipal, string> option = None
        let acceptThread16 =
            Thread(fun () -> runAcceptLoop pipe16 cts16.Token (fun _ principal _ -> observedPrincipal16 <- Some principal))
        acceptThread16.IsBackground <- true
        acceptThread16.Start()

        // サーバーのaccept loopが実際にWaitForConnectionAsyncへ入る猶予。
        Thread.Sleep(100)
        let connectOk16, connectError16 =
            try
                use client16 = new NamedPipeClientStream(".", pipeName16, PipeDirection.InOut, PipeOptions.None, TokenImpersonationLevel.Impersonation)
                client16.Connect(2000)
                true, ""
            with ex ->
                false, ex.Message

        // サーバー側のRunAsClient検査が終わるまでの猶予（別スレッドなので待つ必要がある）。
        let deadline16 = Environment.TickCount64 + 2000L
        while observedPrincipal16.IsNone && Environment.TickCount64 < deadline16 do
            Thread.Sleep(10)

        let ownRid16 = ownIntegrityRid ()
        let principalMatchesOwnIntegrity16 =
            match observedPrincipal16, ownRid16 with
            | Some(Ok principal), Some ownRid -> principal.IntegrityRid = ownRid
            | _ -> false
        printfn "Round 16 client connect: ok=%b error=%s" connectOk16 connectError16
        printfn "Round 16 server-observed client principal: %A (own integrity RID=%A)" observedPrincipal16 ownRid16

        cts16.Cancel()
        let stoppedInTime16 = acceptThread16.Join(3000)
        printfn "Round 16 PASSED (Impersonation-level client connected to the dynamically-ACL'd pipe, server verified the real client token via RunAsClient matches this process's own integrity level, accept loop stopped cleanly): %b"
            (connectOk16 && principalMatchesOwnIntegrity16 && stoppedInTime16)

    // Round 17：Named Pipeのワイヤプロトコル＋Snapshot応答（architecture.md v2.135/10.1）。
    // ResidentHost（Round 15）とInspectPipeServer（Round 16）を初めて組み合わせ、Phase Cの
    // 最初のゴール——別プロセス相当のクライアントが実際のパイプ経由でSnapshotAsync()を呼んで
    // 実データを受け取る——を完成させる。長さプレフィックス＋`System.Text.Json`での受信
    // （opus-review 1-5対応）、`protocolVersion`付きリクエスト、Inspectの多層防御（接続時の
    // カーネルACL/ラベルに加え、アプリ層でも整合性レベルを再確認）を実際に1回のパイプ往復で通す。
    printfn "\n--- Round 17: Named Pipe wire protocol, end-to-end Snapshot round-trip ---"
    let journalPath17 = Path.Combine(Path.GetTempPath(), sprintf "wm-host-round17-%s.ndjson" (Guid.NewGuid().ToString("N")))
    let reducer17 = Reducer(RuntimePolicy.defaults, "phase-b-round17", "wm-host-round17", "no-policy-artifact", Some journalPath17)
    let residentHost17 = ResidentHost(reducer17, liveNow)
    let loopThread17 = Thread(fun () -> residentHost17.Run(tickMs = 1))
    loopThread17.IsBackground <- true
    loopThread17.Start()
    Thread.Sleep(50)

    let pipeName17 = generatePipeName ()
    match createRestrictedPipe pipeName17 with
    | Error e ->
        printfn "Round 17 FAILED (createRestrictedPipe): %s" e
        residentHost17.RequestStop()
        loopThread17.Join(3000) |> ignore
    | Ok pipe17 ->
        use pipe17 = pipe17
        use cts17 = new CancellationTokenSource()
        let ownRid17 = ownIntegrityRid ()
        let acceptThread17 =
            Thread(fun () ->
                runAcceptLoop pipe17 cts17.Token (fun p principal reqCt ->
                    try
                        (handleOneRequestAsync p principal ownRid17 (fun () -> residentHost17.EnqueueSnapshotQuery().Result) (fun () -> residentHost17.Policy) (fun () -> residentHost17.RecentJournal(defaultJournalEntryCount)) (fun w -> residentHost17.EnqueueExplainQuery(w)) (fun c -> residentHost17.AnalyzeCandidate(c)) (fun (_: PolicyDto) -> Task.FromResult(DeployIntegrityFailedDto("not-exercised-this-round", "not-exercised-this-round"))) (fun (_: string option) (_: int) (_: string) (_: InterveneCommandDto) -> Error "not-exercised-this-round") reqCt).GetAwaiter().GetResult()
                    with _ -> ()))
        acceptThread17.IsBackground <- true
        acceptThread17.Start()
        Thread.Sleep(100)

        let roundTripOk17, roundTripError17, receivedSnapshot17 =
            try
                use client17 = new NamedPipeClientStream(".", pipeName17, PipeDirection.InOut, PipeOptions.None, TokenImpersonationLevel.Impersonation)
                client17.Connect(2000)
                writeFramedMessage client17 (Encoding.UTF8.GetBytes("""{"v":1,"op":"snapshot"}"""))
                let responseBytes = readFramedMessage client17
                let json = JsonNode.Parse(Encoding.UTF8.GetString responseBytes)
                true, "", Some(snapshotDtoOfJson json)
            with ex ->
                false, ex.Message, None

        let directSnapshot17 = residentHost17.EnqueueSnapshotQuery().Result
        let matches17 =
            match receivedSnapshot17 with
            | Some s -> s.Revision = directSnapshot17.Revision && s.Windows = directSnapshot17.Windows
            | None -> false

        printfn "Round 17 wire round-trip: ok=%b error=%s" roundTripOk17 roundTripError17
        printfn "Round 17 received=%A" receivedSnapshot17
        printfn "Round 17 direct  =%A" directSnapshot17

        cts17.Cancel()
        let acceptStopped17 = acceptThread17.Join(3000)
        residentHost17.RequestStop()
        let loopStopped17 = loopThread17.Join(3000)
        printfn "Round 17 PASSED (real SnapshotDto delivered end-to-end over the actual Named Pipe, matches a direct ResidentHost query, both threads stopped cleanly): %b"
            (roundTripOk17 && matches17 && acceptStopped17 && loopStopped17)

    // Round 18：接続ごとのリクエストタイムアウト（opus-review-named-pipe-transport.md 5-E対応、
    // architecture.md v2.142）が実際にスロウロリスを防ぐことを実機で確認する。`runAcceptLoop`は
    // シングルスレッドで1接続を処理し終えてから次を受け付けるため、接続だけして何も送らない
    // クライアントがいると、タイムアウトが無ければ以後の全クライアントが永久に待たされる。
    // `runAcceptLoopWithTimeout`に短いタイムアウトを渡して同じ挙動をCIで速く確認する——このパイプは
    // `nMaxInstances=1`(`createRestrictedPipe`)なので、2人目のクライアントの`Connect`はサーバーが
    // 1人目を切ってから次の`WaitForConnectionAsync`に戻るまで実際にブロックされる。タイムアウトが
    // 機能していれば、1人目を開いたまま放置しても2人目が有限時間内に接続・応答を受け取れる。
    printfn "\n--- Round 18: per-connection request timeout defeats a stuck (slowloris-style) client ---"
    let journalPath18 = Path.Combine(Path.GetTempPath(), sprintf "wm-host-round18-%s.ndjson" (Guid.NewGuid().ToString("N")))
    let reducer18 = Reducer(RuntimePolicy.defaults, "phase-b-round18", "wm-host-round18", "no-policy-artifact", Some journalPath18)
    let residentHost18 = ResidentHost(reducer18, liveNow)
    let loopThread18 = Thread(fun () -> residentHost18.Run(tickMs = 1))
    loopThread18.IsBackground <- true
    loopThread18.Start()
    Thread.Sleep(50)

    let pipeName18 = generatePipeName ()
    match createRestrictedPipe pipeName18 with
    | Error e ->
        printfn "Round 18 FAILED (createRestrictedPipe): %s" e
        residentHost18.RequestStop()
        loopThread18.Join(3000) |> ignore
    | Ok pipe18 ->
        use pipe18 = pipe18
        use cts18 = new CancellationTokenSource()
        let ownRid18 = ownIntegrityRid ()
        let shortTimeoutMs18 = 800
        let acceptThread18 =
            Thread(fun () ->
                runAcceptLoopWithTimeout pipe18 cts18.Token shortTimeoutMs18 (fun p principal reqCt ->
                    try
                        (handleOneRequestAsync p principal ownRid18 (fun () -> residentHost18.EnqueueSnapshotQuery().Result) (fun () -> residentHost18.Policy) (fun () -> residentHost18.RecentJournal(defaultJournalEntryCount)) (fun w -> residentHost18.EnqueueExplainQuery(w)) (fun c -> residentHost18.AnalyzeCandidate(c)) (fun (_: PolicyDto) -> Task.FromResult(DeployIntegrityFailedDto("not-exercised-this-round", "not-exercised-this-round"))) (fun (_: string option) (_: int) (_: string) (_: InterveneCommandDto) -> Error "not-exercised-this-round") reqCt).GetAwaiter().GetResult()
                    with _ -> ()))
        acceptThread18.IsBackground <- true
        acceptThread18.Start()
        Thread.Sleep(100)

        // 1人目：接続だけして何も送らず、開いたまま放置する（stuck client）。
        use stuckClient18 = new NamedPipeClientStream(".", pipeName18, PipeDirection.InOut, PipeOptions.None, TokenImpersonationLevel.Impersonation)
        let stuckConnectOk18 =
            try
                stuckClient18.Connect(2000)
                true
            with _ -> false

        // 2人目：1人目がまだ開いたままの状態で接続を試みる。パイプは1インスタンスしか無いので、
        // サーバーが1人目をタイムアウトで切って次の`WaitForConnectionAsync`に戻るまでブロックされる
        // ——`shortTimeoutMs18`より十分大きいが無限ではない`Connect`タイムアウトを与え、実際に
        // 有限時間内に通ることを確認する。
        let roundTripOk18, roundTripError18, receivedSnapshot18 =
            try
                use secondClient18 = new NamedPipeClientStream(".", pipeName18, PipeDirection.InOut, PipeOptions.None, TokenImpersonationLevel.Impersonation)
                secondClient18.Connect(5000)
                writeFramedMessage secondClient18 (Encoding.UTF8.GetBytes("""{"v":1,"op":"snapshot"}"""))
                let responseBytes = readFramedMessage secondClient18
                let json = JsonNode.Parse(Encoding.UTF8.GetString responseBytes)
                true, "", Some(snapshotDtoOfJson json)
            with ex ->
                false, ex.Message, None

        let directSnapshot18 = residentHost18.EnqueueSnapshotQuery().Result
        let matches18 =
            match receivedSnapshot18 with
            | Some s -> s.Revision = directSnapshot18.Revision && s.Windows = directSnapshot18.Windows
            | None -> false

        printfn "Round 18 stuck client connected=%b, second client round-trip: ok=%b error=%s" stuckConnectOk18 roundTripOk18 roundTripError18
        printfn "Round 18 received=%A" receivedSnapshot18

        cts18.Cancel()
        let acceptStopped18 = acceptThread18.Join(3000)
        residentHost18.RequestStop()
        let loopStopped18 = loopThread18.Join(3000)
        printfn "Round 18 PASSED (a stuck first client that never sends anything does not block a well-behaved second client from connecting and getting a correct Snapshot within a bounded time, both threads stopped cleanly): %b"
            (stuckConnectOk18 && roundTripOk18 && matches18 && acceptStopped18 && loopStopped18)

    // Round 19：パイプ名の発見用ファイル配布（opus-review-named-pipe-transport.md 5-C対応、
    // architecture.md v2.143）。クライアントはこれまでのRoundと違い、パイプ名を直接渡されず、
    // Hostが書いた発見用ファイルを読んで自分でパイプ名を知る——実際のクライアントプロセスが
    // 起動時にどうやってパイプ名を知るかという、これまで素通りしていた経路を初めて通す。
    printfn "\n--- Round 19: pipe-name discovery file (client doesn't know the pipe name in advance) ---"
    let journalPath19 = Path.Combine(Path.GetTempPath(), sprintf "wm-host-round19-%s.ndjson" (Guid.NewGuid().ToString("N")))
    let reducer19 = Reducer(RuntimePolicy.defaults, "phase-b-round19", "wm-host-round19", "no-policy-artifact", Some journalPath19)
    let residentHost19 = ResidentHost(reducer19, liveNow)
    let loopThread19 = Thread(fun () -> residentHost19.Run(tickMs = 1))
    loopThread19.IsBackground <- true
    loopThread19.Start()
    Thread.Sleep(50)

    let pipeName19 = generatePipeName ()
    match createRestrictedPipe pipeName19 with
    | Error e ->
        printfn "Round 19 FAILED (createRestrictedPipe): %s" e
        residentHost19.RequestStop()
        loopThread19.Join(3000) |> ignore
    | Ok pipe19 ->
        use pipe19 = pipe19
        use cts19 = new CancellationTokenSource()
        let ownRid19 = ownIntegrityRid ()

        let discoveryWriteOk19 =
            match writeDiscoveryFile pipeName19 with
            | Ok () -> true
            | Error _ -> false

        let acceptThread19 =
            Thread(fun () ->
                runAcceptLoop pipe19 cts19.Token (fun p principal reqCt ->
                    try
                        (handleOneRequestAsync p principal ownRid19 (fun () -> residentHost19.EnqueueSnapshotQuery().Result) (fun () -> residentHost19.Policy) (fun () -> residentHost19.RecentJournal(defaultJournalEntryCount)) (fun w -> residentHost19.EnqueueExplainQuery(w)) (fun c -> residentHost19.AnalyzeCandidate(c)) (fun (_: PolicyDto) -> Task.FromResult(DeployIntegrityFailedDto("not-exercised-this-round", "not-exercised-this-round"))) (fun (_: string option) (_: int) (_: string) (_: InterveneCommandDto) -> Error "not-exercised-this-round") reqCt).GetAwaiter().GetResult()
                    with _ -> ()))
        acceptThread19.IsBackground <- true
        acceptThread19.Start()
        Thread.Sleep(100)

        // クライアントはここで初めて発見用ファイルを読む。`pipeName19`を直接渡さず、本当に
        // ファイル経由で知った名前だけを使って接続する。
        let discovered19 = tryReadDiscoveryFile ()
        let discoveredMatches19 =
            match discovered19 with
            | Ok info -> info.PipeName = pipeName19 && info.Pid = Process.GetCurrentProcess().Id
            | Error _ -> false

        let roundTripOk19, roundTripError19, receivedSnapshot19 =
            match discovered19 with
            | Error e -> false, e, None
            | Ok info ->
                try
                    use client19 = new NamedPipeClientStream(".", info.PipeName, PipeDirection.InOut, PipeOptions.None, TokenImpersonationLevel.Impersonation)
                    client19.Connect(2000)
                    writeFramedMessage client19 (Encoding.UTF8.GetBytes("""{"v":1,"op":"snapshot"}"""))
                    let responseBytes = readFramedMessage client19
                    let json = JsonNode.Parse(Encoding.UTF8.GetString responseBytes)
                    true, "", Some(snapshotDtoOfJson json)
                with ex ->
                    false, ex.Message, None

        let directSnapshot19 = residentHost19.EnqueueSnapshotQuery().Result
        let matches19 =
            match receivedSnapshot19 with
            | Some s -> s.Revision = directSnapshot19.Revision && s.Windows = directSnapshot19.Windows
            | None -> false

        printfn "Round 19 discovery write=%b, discovered=%A, round-trip: ok=%b error=%s" discoveryWriteOk19 discovered19 roundTripOk19 roundTripError19

        cts19.Cancel()
        let acceptStopped19 = acceptThread19.Join(3000)
        residentHost19.RequestStop()
        let loopStopped19 = loopThread19.Join(3000)
        removeDiscoveryFile ()
        let cleanedUp19 = not (File.Exists(discoveryFilePath ()))
        printfn "Round 19 PASSED (a client with no prior knowledge of the pipe name discovers it from the Host-written file, connects, and gets a correct Snapshot; the file is removed on shutdown): %b"
            (discoveryWriteOk19 && discoveredMatches19 && roundTripOk19 && matches19 && acceptStopped19 && loopStopped19 && cleanedUp19)

    // Round 20：Round 17〜19ではクライアント側コードを生の`NamedPipeClientStream`＋手動フレーミング
    // 呼出としてこのファイル内に書いていたが、v2.144でそれを独立プロジェクト`Wm.ReplClient`
    // （`Wm.Contracts`/`Wm.DebugProtocol`のみに依存、Wm.Core/Wm.Hostには依存しない）へ切り出した。
    // ここではその切り出した`Wm.ReplClient.WmClient`を実際に使って、Round 19と同じ「発見用ファイル
    // 経由で接続しSnapshotを取得する」という往復が、切り出し後も実機で変わらず動くことを確認する。
    printfn "\n--- Round 20: Wm.ReplClient (split-out client library) connects via discovery file and fetches a Snapshot ---"
    let journalPath20 = Path.Combine(Path.GetTempPath(), sprintf "wm-host-round20-%s.ndjson" (Guid.NewGuid().ToString("N")))
    let reducer20 = Reducer(RuntimePolicy.defaults, "phase-b-round20", "wm-host-round20", "no-policy-artifact", Some journalPath20)
    let residentHost20 = ResidentHost(reducer20, liveNow)
    let loopThread20 = Thread(fun () -> residentHost20.Run(tickMs = 1))
    loopThread20.IsBackground <- true
    loopThread20.Start()
    Thread.Sleep(50)

    let pipeName20 = generatePipeName ()
    match createRestrictedPipe pipeName20 with
    | Error e ->
        printfn "Round 20 FAILED (createRestrictedPipe): %s" e
        residentHost20.RequestStop()
        loopThread20.Join(3000) |> ignore
    | Ok pipe20 ->
        use pipe20 = pipe20
        use cts20 = new CancellationTokenSource()
        let ownRid20 = ownIntegrityRid ()

        let discoveryWriteOk20 =
            match writeDiscoveryFile pipeName20 with
            | Ok () -> true
            | Error _ -> false

        let acceptThread20 =
            Thread(fun () ->
                runAcceptLoop pipe20 cts20.Token (fun p principal reqCt ->
                    try
                        (handleOneRequestAsync p principal ownRid20 (fun () -> residentHost20.EnqueueSnapshotQuery().Result) (fun () -> residentHost20.Policy) (fun () -> residentHost20.RecentJournal(defaultJournalEntryCount)) (fun w -> residentHost20.EnqueueExplainQuery(w)) (fun c -> residentHost20.AnalyzeCandidate(c)) (fun (_: PolicyDto) -> Task.FromResult(DeployIntegrityFailedDto("not-exercised-this-round", "not-exercised-this-round"))) (fun (_: string option) (_: int) (_: string) (_: InterveneCommandDto) -> Error "not-exercised-this-round") reqCt).GetAwaiter().GetResult()
                    with _ -> ()))
        acceptThread20.IsBackground <- true
        acceptThread20.Start()
        Thread.Sleep(100)

        // `Wm.ReplClient.WmClient.connectLocalAsync`がここで初めて実際のパイプ越しに使われる。
        let roundTripOk20, receivedSnapshot20 =
            match connectLocalAsync 2000 |> Async.AwaitTask |> Async.RunSynchronously with
            | Error _ -> false, None
            | Ok client ->
                use client = client
                match client.SnapshotAsync() |> Async.AwaitTask |> Async.RunSynchronously with
                | Ok snapshot -> true, Some snapshot
                | Error _ -> false, None

        let directSnapshot20 = residentHost20.EnqueueSnapshotQuery().Result
        let matches20 =
            match receivedSnapshot20 with
            | Some s -> s.Revision = directSnapshot20.Revision && s.Windows = directSnapshot20.Windows
            | None -> false

        printfn "Round 20 discovery write=%b, Wm.ReplClient round-trip: ok=%b" discoveryWriteOk20 roundTripOk20
        printfn "Round 20 received=%A" receivedSnapshot20

        cts20.Cancel()
        let acceptStopped20 = acceptThread20.Join(3000)
        residentHost20.RequestStop()
        let loopStopped20 = loopThread20.Join(3000)
        removeDiscoveryFile ()
        printfn "Round 20 PASSED (the split-out Wm.ReplClient project connects via the discovery file and fetches a correct Snapshot through the real Named Pipe, matching a direct ResidentHost query): %b"
            (discoveryWriteOk20 && roundTripOk20 && matches20 && acceptStopped20 && loopStopped20)

    // Round 21：Policy Inspect操作（v2.145、memory `project_named_pipe_ipc_task_breakdown.md`の
    // Tier 2の2項目目）。これまでのSnapshotと違い、`PolicyIR.recommendedWindowRules`（2026-11-05の
    // ユーザー決定、WindowRules製品判断）を実際に採用したReducerを立てて、ルール内容（`AppIs`/
    // `DialogIs`述語を含む）が本物のNamed Pipeを通して正しく往復することを確認する——`PolicyIR.defaults`
    // （ルール無し）だけだと、`WindowRuleDto`の再帰的なJSON符号化が実際に機能しているかを実機で
    // 確認できない。
    printfn "\n--- Round 21: Policy Inspect op carries real WindowRules (AppIs/DialogIs) through the real Named Pipe ---"
    let journalPath21 = Path.Combine(Path.GetTempPath(), sprintf "wm-host-round21-%s.ndjson" (Guid.NewGuid().ToString("N")))
    let policy21 = { PolicyIR.defaults with WindowRules = PolicyIR.recommendedWindowRules }
    let runtimePolicy21 = { RuntimePolicy.defaults with Policy = policy21 }
    let reducer21 = Reducer(runtimePolicy21, "phase-b-round21", "wm-host-round21", "no-policy-artifact", Some journalPath21)
    let residentHost21 = ResidentHost(reducer21, liveNow)
    let loopThread21 = Thread(fun () -> residentHost21.Run(tickMs = 1))
    loopThread21.IsBackground <- true
    loopThread21.Start()
    Thread.Sleep(50)

    let pipeName21 = generatePipeName ()
    match createRestrictedPipe pipeName21 with
    | Error e ->
        printfn "Round 21 FAILED (createRestrictedPipe): %s" e
        residentHost21.RequestStop()
        loopThread21.Join(3000) |> ignore
    | Ok pipe21 ->
        use pipe21 = pipe21
        use cts21 = new CancellationTokenSource()
        let ownRid21 = ownIntegrityRid ()

        let discoveryWriteOk21 =
            match writeDiscoveryFile pipeName21 with
            | Ok () -> true
            | Error _ -> false

        let acceptThread21 =
            Thread(fun () ->
                runAcceptLoop pipe21 cts21.Token (fun p principal reqCt ->
                    try
                        (handleOneRequestAsync p principal ownRid21 (fun () -> residentHost21.EnqueueSnapshotQuery().Result) (fun () -> residentHost21.Policy) (fun () -> residentHost21.RecentJournal(defaultJournalEntryCount)) (fun w -> residentHost21.EnqueueExplainQuery(w)) (fun c -> residentHost21.AnalyzeCandidate(c)) (fun (_: PolicyDto) -> Task.FromResult(DeployIntegrityFailedDto("not-exercised-this-round", "not-exercised-this-round"))) (fun (_: string option) (_: int) (_: string) (_: InterveneCommandDto) -> Error "not-exercised-this-round") reqCt).GetAwaiter().GetResult()
                    with _ -> ()))
        acceptThread21.IsBackground <- true
        acceptThread21.Start()
        Thread.Sleep(100)

        let roundTripOk21, receivedPolicy21 =
            match connectLocalAsync 2000 |> Async.AwaitTask |> Async.RunSynchronously with
            | Error _ -> false, None
            | Ok client ->
                use client = client
                match client.PolicyAsync() |> Async.AwaitTask |> Async.RunSynchronously with
                | Ok policy -> true, Some policy
                | Error _ -> false, None

        let directPolicy21 = residentHost21.Policy
        let matches21 =
            match receivedPolicy21 with
            | Some p -> p = directPolicy21
            | None -> false
        let carriesRealRules21 =
            match receivedPolicy21 with
            | Some p -> List.length p.WindowRules = List.length PolicyIR.recommendedWindowRules && not (List.isEmpty p.WindowRules)
            | None -> false

        printfn "Round 21 discovery write=%b, Wm.ReplClient Policy round-trip: ok=%b" discoveryWriteOk21 roundTripOk21
        printfn "Round 21 received=%A" receivedPolicy21

        cts21.Cancel()
        let acceptStopped21 = acceptThread21.Join(3000)
        residentHost21.RequestStop()
        let loopStopped21 = loopThread21.Join(3000)
        removeDiscoveryFile ()
        printfn "Round 21 PASSED (op=policy carries the real recommendedWindowRules, including AppIs/DialogIs predicates, through the real Named Pipe and matches a direct query): %b"
            (discoveryWriteOk21 && roundTripOk21 && matches21 && carriesRealRules21 && acceptStopped21 && loopStopped21)

    // Round 22：Journal Inspect操作（v2.147、memory `project_named_pipe_ipc_task_breakdown.md`の
    // Tier 3の1項目目）。ユーザー決定2026-11-06：直近50〜100件だけ、シンプルに保つ。トポロジ確定
    // （MonitorTopologyObserved→タイマー2本の発火）＋窓の発見という、意図的に複数のJournalEntryを
    // 生む現実的なシナリオを使う——Reducerがまだ何も処理していない空のJournalだけを確認しても
    // 「直近N件」の意味が検証できない。
    printfn "\n--- Round 22: Journal Inspect op (op=journal) carries recent entries through the real Named Pipe ---"
    let journalPath22 = Path.Combine(Path.GetTempPath(), sprintf "wm-host-round22-%s.ndjson" (Guid.NewGuid().ToString("N")))
    let reducer22 = Reducer(RuntimePolicy.defaults, "phase-b-round22", "wm-host-round22", "no-policy-artifact", Some journalPath22)
    let residentHost22 = ResidentHost(reducer22, liveNow)
    let loopThread22 = Thread(fun () -> residentHost22.Run(tickMs = 1))
    loopThread22.IsBackground <- true
    loopThread22.Start()
    Thread.Sleep(50)

    let testMonitor22: MonitorSnapshot =
        { Id = MonitorId 0L
          StableKey = "test-monitor-round22"
          Bounds = { X = 0; Y = 0; Width = 1920; Height = 1080 }
          WorkArea = { X = 0; Y = 0; Width = 1920; Height = 1080 }
          Dpi = 96
          IsPrimary = true }
    let visibleWindowObserved22 (w: WindowId) : InputEvent =
        WindowObserved
            { Id = w
              Process = None
              AppIdentity = { Exe = None; HostExe = None; Aumid = None; CoreWindowExe = None }
              Owner = None
              IsDialog = None
              NativeRect = None
              VisibleFrameRect = None
              IsVisible = Some { Value = Ok true; SampleSequence = 0L; MonotonicTime = 0L; Source = "round22" }
              IsMinimized = None
              IsMaximized = None
              Style = None
              Cloak = None
              Monitor = None
              MinWidthHint = None
              TargetIntegrity = None
              Responsiveness = None
              Presence = None
              DpiAwareness = None
              TitleMatches = None }

    residentHost22.Submit (liveNow ()) "round22" (MonitorTopologyObserved [ testMonitor22 ]) |> ignore
    Thread.Sleep(900) // Settling(500ms)→Grace(250ms)が実時間で発火するのを待つ(TopologyConfig.defaults)
    residentHost22.Submit (liveNow ()) "round22" (visibleWindowObserved22 (WindowId 1L)) |> ignore
    residentHost22.Submit (liveNow ()) "round22" (visibleWindowObserved22 (WindowId 2L)) |> ignore
    Thread.Sleep(100)

    let pipeName22 = generatePipeName ()
    match createRestrictedPipe pipeName22 with
    | Error e ->
        printfn "Round 22 FAILED (createRestrictedPipe): %s" e
        residentHost22.RequestStop()
        loopThread22.Join(3000) |> ignore
    | Ok pipe22 ->
        use pipe22 = pipe22
        use cts22 = new CancellationTokenSource()
        let ownRid22 = ownIntegrityRid ()

        let discoveryWriteOk22 =
            match writeDiscoveryFile pipeName22 with
            | Ok () -> true
            | Error _ -> false

        let acceptThread22 =
            Thread(fun () ->
                runAcceptLoop pipe22 cts22.Token (fun p principal reqCt ->
                    try
                        (handleOneRequestAsync
                            p
                            principal
                            ownRid22
                            (fun () -> residentHost22.EnqueueSnapshotQuery().Result)
                            (fun () -> residentHost22.Policy)
                            (fun () -> residentHost22.RecentJournal(defaultJournalEntryCount))
                            (fun w -> residentHost22.EnqueueExplainQuery(w))
                            (fun c -> residentHost22.AnalyzeCandidate(c))
                            (fun (_: PolicyDto) -> Task.FromResult(DeployIntegrityFailedDto("not-exercised-this-round", "not-exercised-this-round")))
                            (fun (_: string option) (_: int) (_: string) (_: InterveneCommandDto) -> Error "not-exercised-this-round")
                            reqCt)
                            .GetAwaiter()
                            .GetResult()
                    with _ -> ()))
        acceptThread22.IsBackground <- true
        acceptThread22.Start()
        Thread.Sleep(100)

        let roundTripOk22, receivedJournal22 =
            match connectLocalAsync 2000 |> Async.AwaitTask |> Async.RunSynchronously with
            | Error _ -> false, None
            | Ok client ->
                use client = client
                match client.JournalAsync() |> Async.AwaitTask |> Async.RunSynchronously with
                | Ok journal -> true, Some journal
                | Error _ -> false, None

        let hasMultipleEntries22 =
            match receivedJournal22 with
            | Some j -> List.length j.Entries > 1
            | None -> false

        printfn "Round 22 discovery write=%b, Journal round-trip: ok=%b, entryCount=%A" discoveryWriteOk22 roundTripOk22 (receivedJournal22 |> Option.map (fun j -> List.length j.Entries))

        cts22.Cancel()
        let acceptStopped22 = acceptThread22.Join(3000)
        residentHost22.RequestStop()
        let loopStopped22 = loopThread22.Join(3000)
        removeDiscoveryFile ()
        printfn "Round 22 PASSED (op=journal carries multiple recent JournalEntry through the real Named Pipe, reflecting a real multi-step scenario): %b"
            (discoveryWriteOk22 && roundTripOk22 && hasMultipleEntries22 && acceptStopped22 && loopStopped22)

    // Round 23：Explain操作（v2.147、Tier 3の2項目目）。ユーザー決定2026-11-06：DecisionTraceも含める。
    // 実際にDecisionTraceを1件生む最小のシナリオとして、`DefaultLayout`が`LayoutRegistry`に登録されて
    // いないPolicyを使う（`DecisionTrace.LayoutNotRegistered`の型直前コメント「Policy/Registryの
    // 設定ミス」そのもの）——既定の空Policy（ルール無し）だけだと`DecisionTrace`が一度も生まれず、
    // Explainの「DecisionTraceを含める」という決定自体を検証できない。
    printfn "\n--- Round 23: Explain op (op=explain) carries a real DecisionTrace through the real Named Pipe ---"
    let journalPath23 = Path.Combine(Path.GetTempPath(), sprintf "wm-host-round23-%s.ndjson" (Guid.NewGuid().ToString("N")))
    let policy23 = { PolicyIR.defaults with DefaultLayout = { Id = LayoutId "does-not-exist-round23"; ApiVersion = 1; Args = NoArgs } }
    let runtimePolicy23 = { RuntimePolicy.defaults with Policy = policy23 }
    let reducer23 = Reducer(runtimePolicy23, "phase-b-round23", "wm-host-round23", "no-policy-artifact", Some journalPath23)
    let residentHost23 = ResidentHost(reducer23, liveNow)
    let loopThread23 = Thread(fun () -> residentHost23.Run(tickMs = 1))
    loopThread23.IsBackground <- true
    loopThread23.Start()
    Thread.Sleep(50)

    let testMonitor23: MonitorSnapshot =
        { Id = MonitorId 0L
          StableKey = "test-monitor-round23"
          Bounds = { X = 0; Y = 0; Width = 1920; Height = 1080 }
          WorkArea = { X = 0; Y = 0; Width = 1920; Height = 1080 }
          Dpi = 96
          IsPrimary = true }
    let visibleWindowObserved23 (w: WindowId) : InputEvent =
        WindowObserved
            { Id = w
              Process = None
              AppIdentity = { Exe = None; HostExe = None; Aumid = None; CoreWindowExe = None }
              Owner = None
              IsDialog = None
              NativeRect = None
              VisibleFrameRect = None
              IsVisible = Some { Value = Ok true; SampleSequence = 0L; MonotonicTime = 0L; Source = "round23" }
              IsMinimized = None
              IsMaximized = None
              Style = None
              Cloak = None
              Monitor = None
              MinWidthHint = None
              TargetIntegrity = None
              Responsiveness = None
              Presence = None
              DpiAwareness = None
              TitleMatches = None }

    let w23 = WindowId 1L
    residentHost23.Submit (liveNow ()) "round23" (MonitorTopologyObserved [ testMonitor23 ]) |> ignore
    Thread.Sleep(900)
    residentHost23.Submit (liveNow ()) "round23" (visibleWindowObserved23 w23) |> ignore
    Thread.Sleep(100)

    let pipeName23 = generatePipeName ()
    match createRestrictedPipe pipeName23 with
    | Error e ->
        printfn "Round 23 FAILED (createRestrictedPipe): %s" e
        residentHost23.RequestStop()
        loopThread23.Join(3000) |> ignore
    | Ok pipe23 ->
        use pipe23 = pipe23
        use cts23 = new CancellationTokenSource()
        let ownRid23 = ownIntegrityRid ()

        let discoveryWriteOk23 =
            match writeDiscoveryFile pipeName23 with
            | Ok () -> true
            | Error _ -> false

        let acceptThread23 =
            Thread(fun () ->
                runAcceptLoop pipe23 cts23.Token (fun p principal reqCt ->
                    try
                        (handleOneRequestAsync
                            p
                            principal
                            ownRid23
                            (fun () -> residentHost23.EnqueueSnapshotQuery().Result)
                            (fun () -> residentHost23.Policy)
                            (fun () -> residentHost23.RecentJournal(defaultJournalEntryCount))
                            (fun w -> residentHost23.EnqueueExplainQuery(w))
                            (fun c -> residentHost23.AnalyzeCandidate(c))
                            (fun (_: PolicyDto) -> Task.FromResult(DeployIntegrityFailedDto("not-exercised-this-round", "not-exercised-this-round")))
                            (fun (_: string option) (_: int) (_: string) (_: InterveneCommandDto) -> Error "not-exercised-this-round")
                            reqCt)
                            .GetAwaiter()
                            .GetResult()
                    with _ -> ()))
        acceptThread23.IsBackground <- true
        acceptThread23.Start()
        Thread.Sleep(100)

        let roundTripOk23, receivedExplain23 =
            match connectLocalAsync 2000 |> Async.AwaitTask |> Async.RunSynchronously with
            | Error _ -> false, None
            | Ok client ->
                use client = client
                match client.ExplainAsync(w23) |> Async.AwaitTask |> Async.RunSynchronously with
                | Ok explain -> true, explain
                | Error _ -> false, None

        let carriesLayoutNotRegistered23 =
            match receivedExplain23 with
            | Some e -> e.RecentTrace |> List.exists (function LayoutNotRegisteredDto(LayoutId "does-not-exist-round23", _) -> true | _ -> false)
            | None -> false

        printfn "Round 23 discovery write=%b, Explain round-trip: ok=%b" discoveryWriteOk23 roundTripOk23
        printfn "Round 23 received=%A" receivedExplain23

        cts23.Cancel()
        let acceptStopped23 = acceptThread23.Join(3000)
        residentHost23.RequestStop()
        let loopStopped23 = loopThread23.Join(3000)
        removeDiscoveryFile ()
        printfn "Round 23 PASSED (op=explain carries a real DecisionTrace(LayoutNotRegistered) through the real Named Pipe for a window using a misconfigured Policy): %b"
            (discoveryWriteOk23 && roundTripOk23 && receivedExplain23.IsSome && carriesLayoutNotRegistered23 && acceptStopped23 && loopStopped23)

    // Round 24：§14 PolicyArtifact（PinnedPolicy/DeployablePolicy/link/Rollback/起動時の「報告のみ
    // モード」、v2.148、ユーザー決定2026-11-06：§14をほぼ全部、一気に）。このRoundはNamed Pipeを
    // 経由せず、`Wm.Host.PolicyArtifact`の各関数を直接呼ぶ——§14自身がIPC配線(op=deploy)を要求
    // していない（Deployは今回あえてIPCへ未配線、ファイル冒頭コメント参照）ため、実機で確認すべきは
    // この一式の判断ロジックとファイルI/O自体。
    printfn "\n--- Round 24: PolicyArtifact (PinnedPolicy/DeployablePolicy/link/Rollback/report-only mode, §14) ---"
    let baseDir24 = Path.Combine(Path.GetTempPath(), sprintf "wm-host-round24-%s" (Guid.NewGuid().ToString("N")))
    let journalPath24 = Path.Combine(Path.GetTempPath(), sprintf "wm-host-round24-%s.ndjson" (Guid.NewGuid().ToString("N")))
    let sessionId24 = "phase-b-round24"
    let coreBuildId24 = "wm-host-round24"

    // (a) canonical hashが同じ入力から常に同じ値になる(実際にrecommendedWindowRulesという複数ルールの
    // 現実的なPolicyで確認——空Policyだけだと符号化の取りこぼしを拾えない)。
    let policy24 = { PolicyIR.defaults with WindowRules = PolicyIR.recommendedWindowRules }
    let manifest24 = Registry.toManifest RuntimePolicy.defaults.LayoutRegistry
    let identity24a = buildArtifactIdentity 1 policy24 manifest24 coreBuildId24 1 "v1"
    let identity24b = buildArtifactIdentity 1 policy24 manifest24 coreBuildId24 1 "v1"
    let hashReproducible24 =
        identity24a.ArtifactHash = identity24b.ArtifactHash && identity24a.CanonicalPolicyIrHash = identity24b.CanonicalPolicyIrHash

    // (b) PinnedPolicy構築→保存→読込みの往復(実際のPolicyIRが一致することまで確認)。
    let pinnedResult24 = PinnedPolicy.ofStored (policyDtoOf policy24) identity24a
    let savedAndLoadedOk24 =
        match pinnedResult24 with
        | Error _ -> false
        | Ok pinned ->
            savePinnedPolicy baseDir24 pinned
            match tryLoadPinnedPolicy baseDir24 identity24a.ArtifactHash with
            | Error _ -> false
            | Ok loaded -> (PinnedPolicy.identity loaded).ArtifactHash = identity24a.ArtifactHash && PinnedPolicy.ir loaded = policy24

    // (c) Deploy：クリーンなPolicyは成功し、現行Analyzerが実際に拒否するPolicy(重複RuleId)は
    // DeployAnalysisFailedで失敗する——「クライアントの自己申告を信用しない」の実演。
    let deployOk24 =
        match deploy manifest24 (policyDtoOf policy24) identity24a with
        | Ok _ -> true
        | Error _ -> false

    let duplicateRulePolicy24 =
        { PolicyIR.defaults with
            WindowRules =
                [ { Id = PolicyRuleId "dup-round24"; Priority = 1; When = AnyWindow; Apply = Tiled }
                  { Id = PolicyRuleId "dup-round24"; Priority = 2; When = AnyWindow; Apply = Floating } ] }
    let duplicateIdentity24 = buildArtifactIdentity 1 duplicateRulePolicy24 manifest24 coreBuildId24 1 "v1"
    let deployFailsOnDirtyAnalysis24 =
        match deploy manifest24 (policyDtoOf duplicateRulePolicy24) duplicateIdentity24 with
        | Error(DeployAnalysisFailed report) -> not (isClean report)
        | _ -> false

    // (d) link：登録済みLayout(既定のmonocle)は成功し、未登録Layoutは実際にLinkErrorで失敗する。
    let linkOk24 =
        match pinnedResult24 with
        | Error _ -> false
        | Ok pinned ->
            match link manifest24 pinned with
            | Ok _ -> true
            | Error _ -> false

    let unregisteredLayoutPolicy24 =
        { PolicyIR.defaults with DefaultLayout = { Id = LayoutId "does-not-exist-round24"; ApiVersion = 1; Args = NoArgs } }
    let unregisteredIdentity24 = buildArtifactIdentity 1 unregisteredLayoutPolicy24 manifest24 coreBuildId24 1 "v1"
    let linkFailsOnUnregistered24 =
        match PinnedPolicy.ofStored (policyDtoOf unregisteredLayoutPolicy24) unregisteredIdentity24 with
        | Error _ -> false
        | Ok pinned ->
            match link manifest24 pinned with
            | Error errs ->
                errs
                |> List.exists (function
                    | UnregisteredLayoutInManifest(LayoutId "does-not-exist-round24", 1) -> true
                    | _ -> false)
            | Ok _ -> false

    // (e) Rollback：Host自身の採用記録に無いhashは常に拒否し、記録した直後だけ成功する
    // （ファイルを直接置いて「最後の正常版」を騙る迂回を塞ぐ、§14の設計そのもの）。
    let rollbackBeforeActivation24 =
        match rollback baseDir24 journalPath24 manifest24 identity24a.ArtifactHash with
        | Error(NotPreviouslyActivated h) -> h = identity24a.ArtifactHash
        | _ -> false

    recordPolicyActivated journalPath24 sessionId24 coreBuildId24 identity24a.ArtifactHash "deploy" (liveNow ())

    let rollbackAfterActivation24 =
        match rollback baseDir24 journalPath24 manifest24 identity24a.ArtifactHash with
        | Ok pinned -> (PinnedPolicy.identity pinned).ArtifactHash = identity24a.ArtifactHash
        | Error _ -> false

    // (f) 起動時/Rollback時の「報告のみモード」：再分析の結果が実際にErrorでも採用はブロックせず、
    // PolicyStaleAnalysisとしてJournalへ記録されるだけであることを確認する。
    match PinnedPolicy.ofStored (policyDtoOf duplicateRulePolicy24) duplicateIdentity24 with
    | Error _ -> ()
    | Ok dirtyPinned ->
        let report24 = reportOnlyAnalysis manifest24 dirtyPinned
        recordPolicyStaleAnalysis journalPath24 sessionId24 coreBuildId24 duplicateIdentity24.ArtifactHash report24 (liveNow ())

    let staleAnalysisJournaled24 =
        Wm.Journal.JournalWriter.readAll journalPath24
        |> Seq.exists (fun e -> e.Kind = PolicyStaleAnalysisKind && e.PolicyArtifactHash = duplicateIdentity24.ArtifactHash)

    let allPassed24 =
        hashReproducible24
        && savedAndLoadedOk24
        && deployOk24
        && deployFailsOnDirtyAnalysis24
        && linkOk24
        && linkFailsOnUnregistered24
        && rollbackBeforeActivation24
        && rollbackAfterActivation24
        && staleAnalysisJournaled24

    printfn
        "Round 24 checks: hashReproducible=%b savedAndLoadedOk=%b deployOk=%b deployFailsOnDirtyAnalysis=%b linkOk=%b linkFailsOnUnregistered=%b rollbackBeforeActivation=%b rollbackAfterActivation=%b staleAnalysisJournaled=%b"
        hashReproducible24
        savedAndLoadedOk24
        deployOk24
        deployFailsOnDirtyAnalysis24
        linkOk24
        linkFailsOnUnregistered24
        rollbackBeforeActivation24
        rollbackAfterActivation24
        staleAnalysisJournaled24
    printfn
        "Round 24 PASSED (§14 PolicyArtifact: canonical hash reproducible, save/load round-trip, Deploy accepts clean/rejects dirty via real Analyzer, link accepts registered/rejects unregistered Layout, Rollback trusts only Host's own activation record, report-only mode journals PolicyStaleAnalysis without blocking adoption): %b"
        allPassed24

    // Round 25：Analyzer Inspect操作（v2.149、§14完成により前提解消）。クライアントが候補Policyを
    // 送り、Hostが自分の採用中Layout Registryに対して現行Analyzerで実際に分析した結果（クリーン/
    // 重複RuleIdで実際に拒否の両方）が、本物のNamed Pipeを通して正しく届くことを確認する。
    printfn "\n--- Round 25: Analyzer Inspect op (op=analyze) analyzes a candidate Policy through the real Named Pipe ---"
    let journalPath25 = Path.Combine(Path.GetTempPath(), sprintf "wm-host-round25-%s.ndjson" (Guid.NewGuid().ToString("N")))
    let reducer25 = Reducer(RuntimePolicy.defaults, "phase-b-round25", "wm-host-round25", "no-policy-artifact", Some journalPath25)
    let residentHost25 = ResidentHost(reducer25, liveNow)
    let loopThread25 = Thread(fun () -> residentHost25.Run(tickMs = 1))
    loopThread25.IsBackground <- true
    loopThread25.Start()
    Thread.Sleep(50)

    let pipeName25 = generatePipeName ()
    match createRestrictedPipe pipeName25 with
    | Error e ->
        printfn "Round 25 FAILED (createRestrictedPipe): %s" e
        residentHost25.RequestStop()
        loopThread25.Join(3000) |> ignore
    | Ok pipe25 ->
        use pipe25 = pipe25
        use cts25 = new CancellationTokenSource()
        let ownRid25 = ownIntegrityRid ()

        let discoveryWriteOk25 =
            match writeDiscoveryFile pipeName25 with
            | Ok () -> true
            | Error _ -> false

        let acceptThread25 =
            Thread(fun () ->
                runAcceptLoop pipe25 cts25.Token (fun p principal reqCt ->
                    try
                        (handleOneRequestAsync
                            p
                            principal
                            ownRid25
                            (fun () -> residentHost25.EnqueueSnapshotQuery().Result)
                            (fun () -> residentHost25.Policy)
                            (fun () -> residentHost25.RecentJournal(defaultJournalEntryCount))
                            (fun w -> residentHost25.EnqueueExplainQuery(w))
                            (fun c -> residentHost25.AnalyzeCandidate(c))
                            (fun (_: PolicyDto) -> Task.FromResult(DeployIntegrityFailedDto("not-exercised-this-round", "not-exercised-this-round")))
                            (fun (_: string option) (_: int) (_: string) (_: InterveneCommandDto) -> Error "not-exercised-this-round")
                            reqCt)
                            .GetAwaiter()
                            .GetResult()
                    with _ -> ()))
        acceptThread25.IsBackground <- true
        acceptThread25.Start()
        Thread.Sleep(100)

        let cleanCandidate25 = policyDtoOf { PolicyIR.defaults with WindowRules = PolicyIR.recommendedWindowRules }
        let dirtyCandidate25 =
            policyDtoOf
                { PolicyIR.defaults with
                    WindowRules =
                        [ { Id = PolicyRuleId "dup-round25"; Priority = 1; When = AnyWindow; Apply = Tiled }
                          { Id = PolicyRuleId "dup-round25"; Priority = 2; When = AnyWindow; Apply = Floating } ] }

        let analyzeViaClient25 (candidate: PolicyDto) =
            match connectLocalAsync 2000 |> Async.AwaitTask |> Async.RunSynchronously with
            | Error _ -> None
            | Ok client ->
                use client = client
                match client.AnalyzeAsync(candidate) |> Async.AwaitTask |> Async.RunSynchronously with
                | Ok report -> Some report
                | Error _ -> None

        let cleanReport25 = analyzeViaClient25 cleanCandidate25
        let dirtyReport25 = analyzeViaClient25 dirtyCandidate25

        let cleanOk25 = match cleanReport25 with Some r -> List.isEmpty r.Errors | None -> false
        let dirtyFlaggedOk25 =
            match dirtyReport25 with
            | Some r -> r.Errors |> List.exists (function DuplicateWindowRuleIdDto(PolicyRuleId "dup-round25") -> true | _ -> false)
            | None -> false

        printfn "Round 25 discovery write=%b, clean report=%A, dirty report=%A" discoveryWriteOk25 cleanReport25 dirtyReport25

        cts25.Cancel()
        let acceptStopped25 = acceptThread25.Join(3000)
        residentHost25.RequestStop()
        let loopStopped25 = loopThread25.Join(3000)
        removeDiscoveryFile ()
        printfn "Round 25 PASSED (op=analyze correctly reports a clean candidate Policy as clean and a dirty one (duplicate WindowRuleId) as flagged, through the real Named Pipe against the Host's own real Analyzer/Registry): %b"
            (discoveryWriteOk25 && cleanOk25 && dirtyFlaggedOk25 && acceptStopped25 && loopStopped25)

    // Round 26：Deploy Inspect操作（op=deploy、v2.150、§14の続き）。クライアントが候補Policyを送り、
    // Hostが自分でArtifactIdentityを組み立てて現行Analyzerで再分析し、クリーンなら
    // artifacts/policy/へ保存してJournalにPolicyActivatedを記録する。v2.150時点のユーザー決定
    // 2026-11-06では、ここまでがこのスライスの範囲でReducerの実行中Policyはswapしなかったが、
    // vNext（PolicyActivated実配線、同日「次のゴール」決定）で`deployCandidateOf`自体が実際に
    // ライブ反映まで行うよう拡張された——このRound自体はそれを直接検証しないが（Round 27が検証）、
    // 副作用として同じ拡張を受けている。
    printfn "\n--- Round 26: Deploy Inspect op (op=deploy) analyzes+persists+journals a candidate Policy through the real Named Pipe ---"
    let baseDir26 = Path.Combine(Path.GetTempPath(), sprintf "wm-host-round26-%s" (Guid.NewGuid().ToString("N")))
    let journalPath26 = Path.Combine(Path.GetTempPath(), sprintf "wm-host-round26-%s.ndjson" (Guid.NewGuid().ToString("N")))
    let reducer26 = Reducer(RuntimePolicy.defaults, "phase-b-round26", "wm-host-round26", "no-policy-artifact", Some journalPath26)
    let residentHost26 = ResidentHost(reducer26, liveNow)
    let loopThread26 = Thread(fun () -> residentHost26.Run(tickMs = 1))
    loopThread26.IsBackground <- true
    loopThread26.Start()
    Thread.Sleep(50)

    let pipeName26 = generatePipeName ()
    match createRestrictedPipe pipeName26 with
    | Error e ->
        printfn "Round 26 FAILED (createRestrictedPipe): %s" e
        residentHost26.RequestStop()
        loopThread26.Join(3000) |> ignore
    | Ok pipe26 ->
        use pipe26 = pipe26
        use cts26 = new CancellationTokenSource()
        let ownRid26 = ownIntegrityRid ()

        let discoveryWriteOk26 =
            match writeDiscoveryFile pipeName26 with
            | Ok () -> true
            | Error _ -> false

        let acceptThread26 =
            Thread(fun () ->
                runAcceptLoop pipe26 cts26.Token (fun p principal reqCt ->
                    try
                        (handleOneRequestAsync
                            p
                            principal
                            ownRid26
                            (fun () -> residentHost26.EnqueueSnapshotQuery().Result)
                            (fun () -> residentHost26.Policy)
                            (fun () -> residentHost26.RecentJournal(defaultJournalEntryCount))
                            (fun w -> residentHost26.EnqueueExplainQuery(w))
                            (fun c -> residentHost26.AnalyzeCandidate(c))
                            (fun c -> deployCandidateOf baseDir26 reducer26.LayoutRegistry reducer26.JournalPath reducer26.SessionId reducer26.CoreBuildId (liveNow ()) residentHost26.EnqueueActivatePolicy c)
                            (fun correlationId pid image cmd -> residentHost26.EnqueueIntervene(correlationId, pid, image, cmd))
                            reqCt)
                            .GetAwaiter()
                            .GetResult()
                    with _ -> ()))
        acceptThread26.IsBackground <- true
        acceptThread26.Start()
        Thread.Sleep(100)

        let cleanCandidate26 = policyDtoOf { PolicyIR.defaults with WindowRules = PolicyIR.recommendedWindowRules }
        let dirtyCandidate26 =
            policyDtoOf
                { PolicyIR.defaults with
                    WindowRules =
                        [ { Id = PolicyRuleId "dup-round26"; Priority = 1; When = AnyWindow; Apply = Tiled }
                          { Id = PolicyRuleId "dup-round26"; Priority = 2; When = AnyWindow; Apply = Floating } ] }

        let deployViaClient26 (candidate: PolicyDto) =
            match connectLocalAsync 2000 |> Async.AwaitTask |> Async.RunSynchronously with
            | Error _ -> None
            | Ok client ->
                use client = client
                match client.DeployAsync(candidate) |> Async.AwaitTask |> Async.RunSynchronously with
                | Ok result -> Some result
                | Error _ -> None

        let cleanResult26 = deployViaClient26 cleanCandidate26
        let dirtyResult26 = deployViaClient26 dirtyCandidate26

        let cleanDeployedOk26, activatedHash26 =
            match cleanResult26 with
            | Some(DeploySucceededDto hash) -> true, Some hash
            | _ -> false, None

        let dirtyRejectedOk26 =
            match dirtyResult26 with
            | Some(DeployAnalysisFailedDto report) ->
                report.Errors |> List.exists (function DuplicateWindowRuleIdDto(PolicyRuleId "dup-round26") -> true | _ -> false)
            | _ -> false

        let artifactSavedOk26 =
            match activatedHash26 with
            | None -> false
            | Some hash -> File.Exists(Path.Combine(baseDir26, "artifacts", "policy", sprintf "%s.json" hash))

        let journalRecordedOk26 =
            match activatedHash26 with
            | None -> false
            | Some hash ->
                try
                    Wm.Journal.JournalWriter.readAll journalPath26
                    |> Seq.exists (fun e -> e.Kind = PolicyActivatedKind && e.PolicyArtifactHash = hash)
                with _ ->
                    false

        printfn "Round 26 discovery write=%b, clean result=%A, dirty result=%A, artifact saved=%b, journal recorded=%b"
            discoveryWriteOk26 cleanResult26 dirtyResult26 artifactSavedOk26 journalRecordedOk26

        cts26.Cancel()
        let acceptStopped26 = acceptThread26.Join(3000)
        residentHost26.RequestStop()
        let loopStopped26 = loopThread26.Join(3000)
        removeDiscoveryFile ()
        printfn
            "Round 26 PASSED (op=deploy deploys a clean candidate Policy, persisting it to artifacts/policy/ and journaling PolicyActivated, and rejects a dirty one (duplicate WindowRuleId) via DeployAnalysisFailedDto, through the real Named Pipe against the Host's own real Analyzer/Registry; op=deploy's live Policy swap itself is verified separately in Round 27): %b"
            (discoveryWriteOk26
             && cleanDeployedOk26
             && dirtyRejectedOk26
             && artifactSavedOk26
             && journalRecordedOk26
             && acceptStopped26
             && loopStopped26)

    // Round 27：PolicyActivated実配線（vNext、architecture.md §14/§21、ユーザー決定2026-11-06
    // 「次のゴール」）。Round 26まではop=deployが検証・保存・Journal記録までだったが、
    // `deployCandidateOf`がvNextで`activatePolicy`コールバック（`ResidentHost.EnqueueActivatePolicy`）
    // を呼ぶよう拡張され、実際にReducerの実行中Policyをswapするようになった。ここで検証するのは
    // 3点：(1) op=deployの前後でop=policyが返す内容が実際に変わること（＝ライブ反映そのもの）、
    // (2) swap前に観測され既にByPolicyで確定済みの窓（WindowId 1）は、新Policyの下でも遡って
    // 再分類されないこと（ユーザー決定2026-11-06「再評価しない」、`Windows.fs`の`reconsider`の
    // `Classified (_, ByPolicy _) -> record, [], [], []`がこれを保証する——追加実装は無い）、
    // (3) swap後に新しく観測される窓（WindowId 2）は新Policyで分類されること。`DeployAsync`の
    // `Task`はHost側で`activatePolicy`の`await`完了後にしか応答を返さないため、`deployResult27`を
    // 受け取った時点でswap自体は既に完了している保証があり、追加の待機は不要（`Thread.Sleep`による
    // タイミング依存を避ける、このセッションで繰り返し踏んだFrameInset/3-B系の教訓）。
    printfn "\n--- Round 27: op=deploy actually swaps the Reducer's live Policy (PolicyActivated wiring) ---"
    let journalPath27 = Path.Combine(Path.GetTempPath(), sprintf "wm-host-round27-%s.ndjson" (Guid.NewGuid().ToString("N")))
    let baseDir27 = Path.Combine(Path.GetTempPath(), sprintf "wm-host-round27-%s" (Guid.NewGuid().ToString("N")))
    let reducer27 = Reducer(RuntimePolicy.defaults, "phase-b-round27", "wm-host-round27", "no-policy-artifact", Some journalPath27)
    let residentHost27 = ResidentHost(reducer27, liveNow)
    let loopThread27 = Thread(fun () -> residentHost27.Run(tickMs = 1))
    loopThread27.IsBackground <- true
    loopThread27.Start()
    Thread.Sleep(50)

    let testMonitor27: MonitorSnapshot =
        { Id = MonitorId 0L
          StableKey = "test-monitor-round27"
          Bounds = { X = 0; Y = 0; Width = 1920; Height = 1080 }
          WorkArea = { X = 0; Y = 0; Width = 1920; Height = 1080 }
          Dpi = 96
          IsPrimary = true }
    let visibleWindowObserved27 (w: WindowId) : InputEvent =
        WindowObserved
            { Id = w
              Process = None
              AppIdentity = { Exe = None; HostExe = None; Aumid = None; CoreWindowExe = None }
              Owner = None
              IsDialog = None
              NativeRect = None
              VisibleFrameRect = None
              IsVisible = Some { Value = Ok true; SampleSequence = 0L; MonotonicTime = 0L; Source = "round27" }
              IsMinimized = None
              IsMaximized = None
              Style = None
              Cloak = None
              Monitor = None
              MinWidthHint = None
              TargetIntegrity = None
              Responsiveness = None
              Presence = None
              DpiAwareness = None
              TitleMatches = None }

    residentHost27.Submit (liveNow ()) "round27" (MonitorTopologyObserved [ testMonitor27 ]) |> ignore
    Thread.Sleep(900) // Settling(500ms)→Grace(250ms)、Round 22と同じ既定値待ち
    // swap前に1つ観測・分類させておく（既定Policy、WindowRules無し→DefaultManagement=Tiled）。
    // これが「swap後も遡って再分類されない」ことの確認対象になる。
    residentHost27.Submit (liveNow ()) "round27" (visibleWindowObserved27 (WindowId 1L)) |> ignore
    Thread.Sleep(100)

    let pipeName27 = generatePipeName ()
    match createRestrictedPipe pipeName27 with
    | Error e ->
        printfn "Round 27 FAILED (createRestrictedPipe): %s" e
        residentHost27.RequestStop()
        loopThread27.Join(3000) |> ignore
    | Ok pipe27 ->
        use pipe27 = pipe27
        use cts27 = new CancellationTokenSource()
        let ownRid27 = ownIntegrityRid ()

        let discoveryWriteOk27 =
            match writeDiscoveryFile pipeName27 with
            | Ok () -> true
            | Error _ -> false

        let acceptThread27 =
            Thread(fun () ->
                runAcceptLoop pipe27 cts27.Token (fun p principal reqCt ->
                    try
                        (handleOneRequestAsync
                            p
                            principal
                            ownRid27
                            (fun () -> residentHost27.EnqueueSnapshotQuery().Result)
                            (fun () -> residentHost27.Policy)
                            (fun () -> residentHost27.RecentJournal(defaultJournalEntryCount))
                            (fun w -> residentHost27.EnqueueExplainQuery(w))
                            (fun c -> residentHost27.AnalyzeCandidate(c))
                            (fun c -> deployCandidateOf baseDir27 reducer27.LayoutRegistry reducer27.JournalPath reducer27.SessionId reducer27.CoreBuildId (liveNow ()) residentHost27.EnqueueActivatePolicy c)
                            (fun correlationId pid image cmd -> residentHost27.EnqueueIntervene(correlationId, pid, image, cmd))
                            reqCt)
                            .GetAwaiter()
                            .GetResult()
                    with _ -> ()))
        acceptThread27.IsBackground <- true
        acceptThread27.Start()
        Thread.Sleep(100)

        let policyViaClient27 () : PolicyDto option =
            match connectLocalAsync 2000 |> Async.AwaitTask |> Async.RunSynchronously with
            | Error _ -> None
            | Ok client ->
                use client = client
                match client.PolicyAsync() |> Async.AwaitTask |> Async.RunSynchronously with
                | Ok p -> Some p
                | Error _ -> None

        let deployViaClient27 (candidate: PolicyDto) : DeployResultDto option =
            match connectLocalAsync 2000 |> Async.AwaitTask |> Async.RunSynchronously with
            | Error _ -> None
            | Ok client ->
                use client = client
                match client.DeployAsync(candidate) |> Async.AwaitTask |> Async.RunSynchronously with
                | Ok r -> Some r
                | Error _ -> None

        // 既定(WindowRules無し)から確実に区別できる新Policy：全窓をFloatingへ（AnyWindow、優先度1）。
        let newCandidate27 =
            policyDtoOf
                { PolicyIR.defaults with
                    WindowRules = [ { Id = PolicyRuleId "float-all-round27"; Priority = 1; When = AnyWindow; Apply = Floating } ] }

        let policyBeforeDeploy27 = policyViaClient27 ()
        let deployResult27 = deployViaClient27 newCandidate27
        let policyAfterDeploy27 = policyViaClient27 ()

        // swap後に新しい窓(WindowId 2)を観測→新Policy(Floating)で分類されるはず。
        residentHost27.Submit (liveNow ()) "round27" (visibleWindowObserved27 (WindowId 2L)) |> ignore
        Thread.Sleep(100)

        let snapshotAfter27 =
            match connectLocalAsync 2000 |> Async.AwaitTask |> Async.RunSynchronously with
            | Error _ -> None
            | Ok client ->
                use client = client
                match client.SnapshotAsync() |> Async.AwaitTask |> Async.RunSynchronously with
                | Ok s -> Some s
                | Error _ -> None

        let managementOf (snap: SnapshotDto option) (w: WindowId) : Management option =
            snap
            |> Option.bind (fun s -> s.Windows |> List.tryFind (fun ws -> ws.Window = w))
            |> Option.bind (fun ws -> ws.Management)

        let deploySucceededOk27 =
            match deployResult27 with
            | Some(DeploySucceededDto _) -> true
            | _ -> false

        let policyLiveSwapOk27 =
            match policyBeforeDeploy27, policyAfterDeploy27 with
            | Some before, Some after -> List.isEmpty before.WindowRules && not (List.isEmpty after.WindowRules)
            | _ -> false

        let oldWindowNotReclassifiedOk27 = managementOf snapshotAfter27 (WindowId 1L) = Some Tiled
        let newWindowUsesNewPolicyOk27 = managementOf snapshotAfter27 (WindowId 2L) = Some Floating

        printfn
            "Round 27 policy before=%A, deploy result=%A, policy after=%A, window1(old)=%A, window2(new)=%A"
            policyBeforeDeploy27 deployResult27 policyAfterDeploy27
            (managementOf snapshotAfter27 (WindowId 1L)) (managementOf snapshotAfter27 (WindowId 2L))

        cts27.Cancel()
        let acceptStopped27 = acceptThread27.Join(3000)
        residentHost27.RequestStop()
        let loopStopped27 = loopThread27.Join(3000)
        removeDiscoveryFile ()
        printfn
            "Round 27 PASSED (op=deploy actually swaps the Reducer's live Policy through the real Named Pipe: op=policy reflects the new candidate right after op=deploy returns, a window already Classified by the old Policy before the swap is NOT retroactively reclassified, and a window observed after the swap IS classified under the new Policy): %b"
            (discoveryWriteOk27
             && deploySucceededOk27
             && policyLiveSwapOk27
             && oldWindowNotReclassifiedOk27
             && newWindowUsesNewPolicyOk27
             && acceptStopped27
             && loopStopped27)

    // Round 28：Intervene操作（op=intervene、architecture.md §10.2/§10.3、v2.151）。ユーザー決定
    // 2026-11-06（Opus敵対的相談`opus-review-intervene-trust-boundary.md`を踏まえ、architecture.md
    // §10.2/F-1のGate条件を改定して進める決定）。検証する5点：(1) 既存の窓に対する
    // `SetManagementTo(Floating)`が実際にSnapshot上のManagementを変えること、(2) 存在しない窓への
    // 要求は前提検査（F-4）で`InterveneRejectedDto`になり状態を変えないこと、(3) `ClearOverride`が
    // 実際にPolicyの分類（既定Policy、ルール無しのためTiled）へ戻すこと、(4) クライアントPID/
    // イメージパスがクライアントの自己申告ではなく接続自身から取れること（Opusレビュー Q5、
    // `GetNamedPipeClientProcessId`/`QueryFullProcessImageName`が制限SDDLパイプ下で動くかのCI
    // スパイク）、(5) レート制限が実際に働くこと。
    printfn "\n--- Round 28: Intervene op (op=intervene) applies SetManagement/ClearUserOverride through the real Named Pipe, with per-window precondition checks and client attribution ---"
    let journalPath28 = Path.Combine(Path.GetTempPath(), sprintf "wm-host-round28-%s.ndjson" (Guid.NewGuid().ToString("N")))
    let reducer28 = Reducer(RuntimePolicy.defaults, "phase-b-round28", "wm-host-round28", "no-policy-artifact", Some journalPath28)
    let residentHost28 = ResidentHost(reducer28, liveNow)
    let loopThread28 = Thread(fun () -> residentHost28.Run(tickMs = 1))
    loopThread28.IsBackground <- true
    loopThread28.Start()
    Thread.Sleep(50)

    let testMonitor28: MonitorSnapshot =
        { Id = MonitorId 0L
          StableKey = "test-monitor-round28"
          Bounds = { X = 0; Y = 0; Width = 1920; Height = 1080 }
          WorkArea = { X = 0; Y = 0; Width = 1920; Height = 1080 }
          Dpi = 96
          IsPrimary = true }
    let visibleWindowObserved28 (w: WindowId) : InputEvent =
        WindowObserved
            { Id = w
              Process = None
              AppIdentity = { Exe = None; HostExe = None; Aumid = None; CoreWindowExe = None }
              Owner = None
              IsDialog = None
              NativeRect = None
              VisibleFrameRect = None
              IsVisible = Some { Value = Ok true; SampleSequence = 0L; MonotonicTime = 0L; Source = "round28" }
              IsMinimized = None
              IsMaximized = None
              Style = None
              Cloak = None
              Monitor = None
              MinWidthHint = None
              TargetIntegrity = None
              Responsiveness = None
              Presence = None
              DpiAwareness = None
              TitleMatches = None }

    residentHost28.Submit (liveNow ()) "round28" (MonitorTopologyObserved [ testMonitor28 ]) |> ignore
    Thread.Sleep(900)
    residentHost28.Submit (liveNow ()) "round28" (visibleWindowObserved28 (WindowId 1L)) |> ignore
    Thread.Sleep(100)

    let pipeName28 = generatePipeName ()
    match createRestrictedPipe pipeName28 with
    | Error e ->
        printfn "Round 28 FAILED (createRestrictedPipe): %s" e
        residentHost28.RequestStop()
        loopThread28.Join(3000) |> ignore
    | Ok pipe28 ->
        use pipe28 = pipe28
        use cts28 = new CancellationTokenSource()
        let ownRid28 = ownIntegrityRid ()

        let discoveryWriteOk28 =
            match writeDiscoveryFile pipeName28 with
            | Ok () -> true
            | Error _ -> false

        let acceptThread28 =
            Thread(fun () ->
                runAcceptLoop pipe28 cts28.Token (fun p principal reqCt ->
                    try
                        (handleOneRequestAsync
                            p
                            principal
                            ownRid28
                            (fun () -> residentHost28.EnqueueSnapshotQuery().Result)
                            (fun () -> residentHost28.Policy)
                            (fun () -> residentHost28.RecentJournal(defaultJournalEntryCount))
                            (fun w -> residentHost28.EnqueueExplainQuery(w))
                            (fun c -> residentHost28.AnalyzeCandidate(c))
                            (fun (_: PolicyDto) -> Task.FromResult(DeployIntegrityFailedDto("not-exercised-this-round", "not-exercised-this-round")))
                            (fun correlationId pid image cmd -> residentHost28.EnqueueIntervene(correlationId, pid, image, cmd))
                            reqCt)
                            .GetAwaiter()
                            .GetResult()
                    with _ -> ()))
        acceptThread28.IsBackground <- true
        acceptThread28.Start()
        Thread.Sleep(100)

        let interveneViaClient28 (cmd: InterveneCommandDto) : InterveneResultDto option =
            match connectLocalAsync 2000 |> Async.AwaitTask |> Async.RunSynchronously with
            | Error _ -> None
            | Ok client ->
                use client = client
                match client.InterveneAsync(None, cmd) |> Async.AwaitTask |> Async.RunSynchronously with
                | Ok r -> Some r
                | Error _ -> None

        let snapshotViaClient28 () : SnapshotDto option =
            match connectLocalAsync 2000 |> Async.AwaitTask |> Async.RunSynchronously with
            | Error _ -> None
            | Ok client ->
                use client = client
                match client.SnapshotAsync() |> Async.AwaitTask |> Async.RunSynchronously with
                | Ok s -> Some s
                | Error _ -> None

        let managementOf28 (snap: SnapshotDto option) (w: WindowId) : Management option =
            snap
            |> Option.bind (fun s -> s.Windows |> List.tryFind (fun ws -> ws.Window = w))
            |> Option.bind (fun ws -> ws.Management)

        // (1) 既存の窓をFloatingへ。
        let floatResult28 = interveneViaClient28 (SetManagementToDto(WindowId 1L, Floating))
        let floatedOk28 = match floatResult28 with Some(InterveneAcceptedDto _) -> true | _ -> false
        let snapshotAfterFloat28 = snapshotViaClient28 ()
        let managementIsFloatingOk28 = managementOf28 snapshotAfterFloat28 (WindowId 1L) = Some Floating

        // (2) 存在しない窓は前提検査で拒否される。
        let rejectResult28 = interveneViaClient28 (SetManagementToDto(WindowId 999L, Floating))
        let rejectedOk28 = match rejectResult28 with Some(InterveneRejectedDto _) -> true | _ -> false

        // (3) ClearOverrideでPolicyの分類(既定、ルール無し→Tiled)へ戻す。
        let clearResult28 = interveneViaClient28 (ClearOverrideDto(WindowId 1L))
        let clearedOk28 = match clearResult28 with Some(InterveneAcceptedDto _) -> true | _ -> false
        let snapshotAfterClear28 = snapshotViaClient28 ()
        let managementIsTiledAgainOk28 = managementOf28 snapshotAfterClear28 (WindowId 1L) = Some Tiled

        // (4) Journalに記録されたClientPid/ClientImageが、クライアントの自己申告ではなく実際に
        // 接続自身から取れていること（Opusレビュー Q5のCIスパイク）。
        let clientAttributionOk28 =
            try
                Wm.Journal.JournalWriter.readAll journalPath28
                |> Seq.exists (fun e ->
                    match e.Payload with
                    | InputPayload(InterveneRequested req) -> req.ClientPid > 0 && req.ClientImage <> "unknown"
                    | _ -> false)
            with _ ->
                false

        // (5) レート制限：短時間に多数送ると、少なくとも1件は受付拒否されるはず。
        let rateLimitTriggeredOk28 =
            [ 1..30 ]
            |> List.map (fun _ -> interveneViaClient28 (ClearOverrideDto(WindowId 1L)))
            |> List.exists (function Some(InterveneRejectedDto "rate limit exceeded") -> true | _ -> false)

        printfn
            "Round 28 float=%A (management=%A), reject(unknown window)=%A, clear=%A (management=%A), clientAttribution=%b, rateLimitTriggered=%b"
            floatResult28 (managementOf28 snapshotAfterFloat28 (WindowId 1L))
            rejectResult28 clearResult28 (managementOf28 snapshotAfterClear28 (WindowId 1L))
            clientAttributionOk28 rateLimitTriggeredOk28

        cts28.Cancel()
        let acceptStopped28 = acceptThread28.Join(3000)
        residentHost28.RequestStop()
        let loopStopped28 = loopThread28.Join(3000)
        removeDiscoveryFile ()
        printfn
            "Round 28 PASSED (op=intervene applies SetManagement/ClearUserOverride through the real Named Pipe, rejects a request targeting an unknown/unmanaged window via the in-step F-4 precondition check, records real (non-self-reported) client PID/image path in the Journal's InterveneRequested entry, and enforces a request-rate limit): %b"
            (discoveryWriteOk28
             && floatedOk28
             && managementIsFloatingOk28
             && rejectedOk28
             && clearedOk28
             && managementIsTiledAgainOk28
             && clientAttributionOk28
             && rateLimitTriggeredOk28
             && acceptStopped28
             && loopStopped28)

    printfn "\nDone."
    0
