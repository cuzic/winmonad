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
open System.Runtime.InteropServices
open System.Threading
open System.Threading.Tasks
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
open Wm.Host.EffectHarness
open Wm.Host.ResidentHost

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

    printfn "\nDone."
    0
