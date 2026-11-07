/// architecture.md §18 Phase B／§8.4：Wm.Host常駐モード（`--resident`）の配線。
///
/// これまでのRound 1〜29はいずれも「1つの機能を実演して終了する検証ラウンド」であり、起動した
/// まま使い続けられるエントリポイントが存在しなかった。本モジュールはOpus敵対的相談
/// `opus-review-resident-daemon.md`の指摘を反映して常駐ループを配線する：
///   - P0-1（`IsDialog`/`Owner`が`None`固定で`recommendedWindowRules`が実窓では機能しなかった）、
///     P0-2（Shellが子窓・ツール窓を候補から除外していなかった）は`Wm.Windows`側
///     （`Native.fs`/`RealObservation.fs`）で別途修正済み——このファイルは両方の修正を前提にする。
///   - 問1：WinEventフック・ホットキー窓の登録・初回のフル列挙・`Run()`の呼び出しは、すべて
///     同じ1本のスレッド（`wmThread`）で、この順序で行う（フック→ホットキー→列挙→Run）。
///     各コールバックはtry/catchで包む（ネイティブ境界を越える例外はプロセスを即終了させるため）。
///   - 問2：`EVENT_OBJECT_LOCATIONCHANGE`はシステム全体で購読するが、コールバックでは
///     「追跡中の窓か」をO(1)で確認して`dirty`集合に積むだけにし、`Run`の`onTick`で1tickに1回だけ
///     合流させる（ネイティブ側の流量とは無関係に、処理量の上限を「追跡窓数×tick頻度」にする）。
///   - 問3：`PolicyMode.Observe`（`DefaultManagement = Excluded`、Effectを一切発行しない）と
///     `PolicyMode.Recommended`（`recommendedWindowRules`、実際にTiled管理する）の2段階を用意する。
///   - 問4：§8.4のウォッチドッグ（Round 9/12と同じ形）を常駐ループにも組み込む——心拍が途絶えたら
///     （mainスレッドがハングしたら）、Coreを経由せず直前に隠した窓を直接復元してプロセスを終了する。
///     「Q（通常終了）」とは別物であることに注意（型直前のコメント参照）。
///   - 問5：名前付きMutexで単一インスタンスを強制する。固定のアプリデータディレクトリ
///     （既定`%LOCALAPPDATA%\winmonad`）にJournal/Policy artifactsを置く（CWD/`%TEMP%`を使わない）。
///   - 6：フックは列挙より前に登録する。定期フル再列挙（既定10秒）を取りこぼしの保険にする。
///     `WM_DISPLAYCHANGE`でモニター構成を再列挙する。常駐用ホットキーはCI実演用
///     （Ctrl+Shift+J/K）とは別のキーマップ（既定Ctrl+Alt、`ResidentConfig.HotkeyModifiers`で
///     変更可能）にし、実アプリのショートカットとの衝突を減らす。`RegisterHotKey`の失敗は
///     起動を止めない。
///
/// **まだ実装していない既知の残課題**（architecture.md §21参照）：Journalのサイズ上限ローテーション
/// （無制限に増え続ける）、独立したウォッチドッグ専用スレッドでの手動緊急停止ホットキー（今は
/// 「mainがハングしたら自動復元＋終了」の自動経路のみ）、HWND再利用検知（instance discriminator）。
module Wm.Host.ResidentLoop

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.IO
open System.Threading
open System.Threading.Tasks
open System.Windows.Forms
open Wm.Contracts.Identifiers
open Wm.Contracts.SharedVocabulary
open Wm.Contracts.ErrorTypes
open Wm.Contracts.CoreCommand
open Wm.Contracts.GatedEffect
open Wm.Contracts.Observation
open Wm.Core.WmState
open Wm.Policy.PolicyTypes
open Wm.Policy.PolicyEvaluator
open Wm.Runtime.Reducer
open Wm.Normalization.WindowEventNormalizer
open Wm.Windows.Native
open Wm.Windows.RealObservation
open Wm.Windows.EventHooks
open Wm.Host.ResidentHost
open Wm.Host.PolicyArtifact
open Wm.Host.InspectPipeServer
open Wm.DebugProtocol.Intervene
open Wm.DebugProtocol.TryPolicy

type PolicyMode =
    /// Effectを一切発行しない（`DefaultManagement = Excluded`、ルール無し）。配線（フック・
    /// ホットキー・パイプ）だけを確かめる、または`op=trypolicy`で分類結果だけを事前に覗く段階。
    | Observe
    /// `PolicyIR.recommendedWindowRules`（2026-11-05にdragonflyg4の棚卸しから決定済み）で
    /// 実際にTiled管理する。
    | Recommended

let policyOf (mode: PolicyMode) : PolicyIR =
    match mode with
    | Observe -> { PolicyIR.defaults with DefaultManagement = Excluded }
    | Recommended -> { PolicyIR.defaults with WindowRules = PolicyIR.recommendedWindowRules }

type ResidentConfig =
    { Policy: PolicyIR
      AppDataDir: string
      /// `RegisterHotKey`の修飾キー（既定はCtrl+Alt——CI実演用のCtrl+Shiftとは意図的に分ける）。
      HotkeyModifiers: uint32
      MutexName: string
      TickMs: int
      RescanIntervalMs: int64
      HeartbeatTimeoutMs: int64
    }

module ResidentConfig =
    let defaultFor (mode: PolicyMode) : ResidentConfig =
        { Policy = policyOf mode
          AppDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "winmonad")
          HotkeyModifiers = MOD_CONTROL ||| MOD_ALT
          MutexName = "winmonad-resident-singleton"
          TickMs = 15
          RescanIntervalMs = 10_000L
          // 2秒は短すぎた（Round 30の実機CI検証で誤発火：初回のフル列挙という1回限りのコストが
          // `onTick`を一度も経由せずに積み上がるため）。実デスクトップの窓数に比例して伸びうる
          // 初回列挙そのものを安全に飲み込めるよう、十分に余裕を持たせる。
          HeartbeatTimeoutMs = 10_000L }

type private ResidentHotkeyForm(onHotkey: int -> unit, onDisplayChange: unit -> unit, onEndSession: unit -> unit) =
    inherit Form(Text = "winmonad-resident-hotkey-sink", ShowInTaskbar = false, Width = 1, Height = 1)
    override _.WndProc(m: Message byref) =
        try
            if m.Msg = WM_HOTKEY then onHotkey (m.WParam.ToInt32())
            elif uint32 m.Msg = WM_DISPLAYCHANGE then onDisplayChange ()
            elif uint32 m.Msg = WM_QUERYENDSESSION || uint32 m.Msg = WM_ENDSESSION then onEndSession ()
        with _ -> ()
        base.WndProc(&m)

type ResidentHandle =
    { RequestStop: unit -> unit
      /// `timeoutMs`以内にWMスレッドが終了し切れば`true`。
      WaitForExit: int -> bool
      /// `SetConsoleCtrlHandler`に渡したデリゲートをGCされないよう保持するためだけに存在する
      /// （`Wm.Windows.EventHooks.HookSet.KeepAlive`と同じ理由）。
      KeepAlive: obj
      /// テスト・診断用——Snapshot/Explain等のクエリを直接投げられる（Round 30参照）。
      ResidentHost: ResidentHost }

/// 常駐ループを1つ起動する。`config.MutexName`の名前付きMutexを先に取得できなければ
/// （＝既に別の`--resident`インスタンスが動いている）`Error`を返し、何も起動しない（問5）。
let startResident (config: ResidentConfig) : Result<ResidentHandle, string> =
    // 単一インスタンス強制（問5）：所有権は取らない（`initiallyOwned=false`）——`createdNew`は
    // 「このOSカーネルオブジェクトを今作ったか、既に存在する（＝別インスタンスがまだ生きている）か」
    // だけを見る。所有権を取らないので、どのスレッドが`Dispose`しても安全（Mutexの所有権はスレッド
    // 単位だが、今回は一度も`WaitOne`していないので無関係）。
    let mutable createdNew = false
    let mutex = new Mutex(false, config.MutexName, &createdNew)
    if not createdNew then
        mutex.Dispose()
        Error "another winmonad --resident instance is already running (named mutex held)"
    else

    eprintfn "[wm-host-resident-debug] mutex acquired"
    Directory.CreateDirectory(config.AppDataDir) |> ignore
    let journalPath = Path.Combine(config.AppDataDir, "wm-host-resident.ndjson")
    let runtimePolicy = { RuntimePolicy.defaults with Policy = config.Policy; PatternSet = buildTitlePatternSet config.Policy }
    let reducer = Reducer(runtimePolicy, "wm-host-resident", "wm-host-resident", "no-policy-artifact", Some journalPath)
    let sw = Stopwatch.StartNew()
    let liveNow () = sw.ElapsedMilliseconds
    eprintfn "[wm-host-resident-debug] reducer constructed"

    let hiddenHwnds = ConcurrentDictionary<IntPtr, unit>()
    let dirty = ConcurrentDictionary<IntPtr, unit>()
    let mutable lastHeartbeat = Environment.TickCount64
    let mutable lastRescanAt = Environment.TickCount64
    let mutable monitorHandles = enumerateMonitorHandles ()
    let titleMatcher = computeTitleMatchVector runtimePolicy.PatternSet
    eprintfn "[wm-host-resident-debug] monitor handles enumerated: %d" monitorHandles.Length

    // `handleTick`/`handleEvent`は`ResidentHost`自身を閉じ込めたいが、`ResidentHost`の構築には
    // そのフックを渡す必要がある（前方参照）——可変セル経由で後から結び付ける。
    let mutable residentHostCell : ResidentHost option = None
    let submit (now: int64) (source: string) (input: InputEvent) : unit =
        match residentHostCell with
        | Some rh -> rh.Submit now source input |> ignore
        | None -> ()

    let handleDispatch (effect: Effect) (result: Result<unit, NativeCallRecord>) =
        match effect, result with
        | SetPresence (_, WindowId raw, shown), Ok () ->
            let hwnd = IntPtr raw
            if shown then hiddenHwnds.TryRemove(hwnd) |> ignore else hiddenHwnds.[hwnd] <- ()
        | _ -> ()

    // 問2：LOCATIONCHANGEの合流はここで1tickに1回だけ。6：定期フル再列挙を取りこぼしの保険にする。
    let handleTick () =
        lastHeartbeat <- Environment.TickCount64
        if dirty.Count > 0 then
            for hwnd in dirty.Keys |> Seq.toList do
                dirty.TryRemove(hwnd) |> ignore
                if IsWindow(hwnd) then
                    let now = liveNow ()
                    for input in normalize (RawReobserved (observeWindow now monitorHandles titleMatcher hwnd)) do
                        submit now "wm-host-resident-locationchange" input
        let nowTick = Environment.TickCount64
        if nowTick - lastRescanAt > config.RescanIntervalMs then
            lastRescanAt <- nowTick
            let now = liveNow ()
            let known = reducer.State.Windows.Records
            for snapshot in enumerateTopLevelWindows now monitorHandles titleMatcher do
                if not (Map.containsKey snapshot.Id known) then
                    for input in normalize (RawFirstSeen snapshot) do
                        submit now "wm-host-resident-rescan" input

    let residentHost = ResidentHost(reducer, liveNow, onDispatch = handleDispatch, onTick = handleTick)
    residentHostCell <- Some residentHost
    eprintfn "[wm-host-resident-debug] residentHost constructed"

    // P0-2：`isManageableTopLevel`はCREATEだけでなく、ほぼ全イベント種別の前段で確認する——
    // `Wm.Core.Windows.withRecord`は未知の`WindowId`に対しても`WindowRecord.initial`を既定値に
    // 新規レコードを作ってしまうため（`Map.tryFind w registry.Records |> Option.defaultValue
    // WindowRecord.initial`）、CREATEだけ絞ってもSHOW/CLOAK/MINIMIZE等の単独イベントから子窓・
    // ツール窓が素通りでCoreに入ってしまう（Opus敵対的相談のP0-2が実際に懸念した経路）。
    // 例外：DESTROYは対象窓が破棄され掛けている時点のスタイル照会が未定義動作になるため確認しない
    // （未知の`WindowId`への`RawDestroyed`はCore側で無害な無視になる、§4.4）。
    // FOREGROUNDのhwnd=0（「フォアグラウンド窓が無い」という正当な遷移）も確認しない。
    // 各コールバックはtry/catchで包む（ネイティブ境界を越える例外はプロセスを即終了させるため）。
    let handleEvent (eventType: uint32) (hwnd: IntPtr) =
        try
            let now = liveNow ()
            let w = WindowId (hwnd.ToInt64())
            if eventType = EVENT_OBJECT_DESTROY then
                for input in normalize (RawDestroyed w) do submit now "wm-host-resident-live" input
            elif eventType = EVENT_SYSTEM_FOREGROUND && hwnd = IntPtr.Zero then
                for input in normalize (RawForeground (observation now (Ok None))) do submit now "wm-host-resident-live" input
            elif not (isManageableTopLevel hwnd) then
                ()
            else
                let raw =
                    if eventType = EVENT_OBJECT_CREATE then Some (RawFirstSeen (observeWindow now monitorHandles titleMatcher hwnd))
                    elif eventType = EVENT_OBJECT_SHOW || eventType = EVENT_OBJECT_HIDE then
                        Some (RawVisibility (w, observation now (queryVisible hwnd)))
                    elif eventType = EVENT_SYSTEM_MINIMIZESTART || eventType = EVENT_SYSTEM_MINIMIZEEND then
                        Some (RawMinimized (w, observation now (queryMinimized hwnd)))
                    elif eventType = EVENT_OBJECT_CLOAKED || eventType = EVENT_OBJECT_UNCLOAKED then
                        Some (RawCloak (w, observation now (queryCloak hwnd)))
                    elif eventType = EVENT_SYSTEM_FOREGROUND then Some (RawForeground (observation now (Ok (Some w))))
                    elif eventType = EVENT_SYSTEM_MOVESIZESTART then Some (RawMoveSizeStart w)
                    elif eventType = EVENT_SYSTEM_MOVESIZEEND then
                        let snapshot = observeWindow now monitorHandles titleMatcher hwnd
                        match snapshot.VisibleFrameRect, snapshot.Monitor with
                        | Some rectObs, Some monObs -> Some (RawMoveSizeEnd (w, rectObs, monObs))
                        | _ -> None
                    else None
                match raw with
                | None -> ()
                | Some rawEvent -> for input in normalize rawEvent do submit now "wm-host-resident-live" input
        with _ -> ()

    let onHotkey (id: int) =
        let now = liveNow ()
        match id with
        | 1 -> submit now "wm-host-resident-hotkey" (UserCommand FocusNext)
        | 2 -> submit now "wm-host-resident-hotkey" (UserCommand SwapWithMaster)
        | 3 -> submit now "wm-host-resident-hotkey" (UserCommand (SwitchWorkspace (WorkspaceId 0)))
        | 4 -> submit now "wm-host-resident-hotkey" (UserCommand (SwitchWorkspace (WorkspaceId 1)))
        | 5 -> residentHost.RequestStop()
        | _ -> ()
    // 6：WM_DISPLAYCHANGEでモニター構成を再列挙する（ドッキング・外部モニター抜き差し対応）。
    let onDisplayChange () =
        try
            monitorHandles <- enumerateMonitorHandles ()
            submit (liveNow ()) "wm-host-resident" (MonitorTopologyObserved (enumerateMonitors monitorHandles))
        with _ -> ()
    let onEndSession () = residentHost.RequestStop()

    // 問4：§8.4のウォッチドッグ（Round 9/12と同じ形）。mainスレッドの心拍（`handleTick`が
    // `Run`の1ループごとに更新する）が途絶えたら、Coreを一切経由せず直前に隠した窓を直接復元し、
    // プロセスを終了する。「Q（通常終了）」とは別物（まだ実装していない残課題にも記載：独立した
    // 手動緊急停止ホットキーは無く、この自動経路だけが異常時の復旧手段）。`ShowWindowAsync`は
    // どのスレッドから呼んでも安全（Win32のウィンドウ所有スレッド制約を受けない非同期API）。
    let watchdogThread =
        Thread(fun () ->
            let mutable triggered = false
            while not triggered do
                Thread.Sleep(200)
                let gap = Environment.TickCount64 - lastHeartbeat
                if gap > config.HeartbeatTimeoutMs then
                    triggered <- true
                    for KeyValue(hwnd, _) in hiddenHwnds do
                        try ShowWindowAsync(hwnd, SW_RESTORE) |> ignore with _ -> ()
                    eprintfn "[wm-host-resident] watchdog: heartbeat gap=%dms > %dms -- restored hidden windows, exiting"
                        gap config.HeartbeatTimeoutMs
                    Environment.Exit(1))
    watchdogThread.IsBackground <- true
    watchdogThread.Name <- "wm-host-resident-watchdog"
    watchdogThread.Start()
    eprintfn "[wm-host-resident-debug] watchdog thread started"

    // Named Pipe（Phase C）。失敗しても常駐ループ自体は続ける——配置機能はIPCに依存しない。
    let pipeName = generatePipeName ()
    let pipeSetup =
        match createRestrictedPipe pipeName with
        | Error e -> Error e
        | Ok pipe ->
            match writeDiscoveryFile pipeName with
            | Error e -> pipe.Dispose(); Error e
            | Ok () -> Ok pipe
    let pipeCts = new CancellationTokenSource()
    let pipeAcceptThread, pipeHandle =
        match pipeSetup with
        | Error e ->
            eprintfn "[wm-host-resident] Named Pipe setup failed, continuing without IPC: %s" e
            None, None
        | Ok pipe ->
            let ownRid = ownIntegrityRid ()
            let acceptThread =
                Thread(fun () ->
                    try
                        runAcceptLoop pipe pipeCts.Token (fun p principal reqCt ->
                            try
                                (handleOneRequestAsync p principal ownRid
                                    (fun () -> residentHost.EnqueueSnapshotQuery().Result)
                                    (fun () -> residentHost.Policy)
                                    (fun () -> residentHost.RecentJournal(defaultJournalEntryCount))
                                    (fun w -> residentHost.EnqueueExplainQuery(w))
                                    (fun c -> residentHost.AnalyzeCandidate(c))
                                    (fun c ->
                                        deployCandidateOf config.AppDataDir reducer.LayoutRegistry reducer.JournalPath
                                            reducer.SessionId reducer.CoreBuildId (liveNow ()) residentHost.EnqueueActivatePolicy c)
                                    (fun correlationId pid image cmd -> residentHost.EnqueueIntervene(correlationId, pid, image, cmd))
                                    (fun c -> residentHost.EnqueueTryPolicyQuery(c))
                                    reqCt).GetAwaiter().GetResult()
                            with _ -> ())
                    with _ -> ())
            acceptThread.IsBackground <- true
            acceptThread.Name <- "wm-host-resident-pipe-accept"
            acceptThread.Start()
            eprintfn "[wm-host-resident-debug] pipe accept thread started"
            Some acceptThread, Some pipe

    // 問1：フック登録・ホットキー窓の生成・初回のフル列挙・`Run()`の呼び出しは、すべて
    // この1本のスレッド上で、この順序で行う（Opus敵対的相談：フックとホットキー窓はRun()を呼ぶ
    // のと同じスレッドで、Run()の前に登録する——OUTOFCONTEXTコールバックはそのスレッドが
    // `Application.DoEvents()`の中でメッセージを取り出す時にだけ配送されるため）。
    let wmThread =
        Thread(fun () ->
          try
            eprintfn "[wm-host-resident-debug] wmThread started, apartment=%A" (Thread.CurrentThread.GetApartmentState())
            // ウォッチドッグ（問4）の心拍は`Run()`開始後は`handleTick`が1tickごとに更新するが、
            // `Run()`開始前のセットアップ（フック登録・ホットキー登録・初回のフル列挙——実窓数に
            // 比例し、busy desktopでは未検証なだけ遅くなりうる、opus-review-resident-daemon.md
            // 問2が指摘した同種の不確実性）は1回も`onTick`を経由しない。各ステップの直後にも
            // 心拍を打つことで、ウォッチドッグが「セットアップの合計時間」ではなく「個々のステップの
            // 所要時間」だけを基準に判定するようにする（セットアップがどれだけ実窓数に比例して
            // 長くなっても誤発火しない）。
            lastHeartbeat <- Environment.TickCount64
            let eventHooks = hookEvents handleEvent
            let locationHook =
                hookSingleEvent EVENT_OBJECT_LOCATIONCHANGE (fun _ hwnd ->
                    try
                        if Map.containsKey (WindowId (hwnd.ToInt64())) reducer.State.Windows.Records then
                            dirty.[hwnd] <- ()
                    with _ -> ())
            lastHeartbeat <- Environment.TickCount64
            eprintfn "[wm-host-resident-debug] hooks registered"

            let hotkeyForm = new ResidentHotkeyForm(onHotkey, onDisplayChange, onEndSession)
            hotkeyForm.Show()
            eprintfn "[wm-host-resident-debug] hotkeyForm shown"
            let hotkeyRegistrations =
                [ 1, uint32 VK_J, "FocusNext"
                  2, uint32 VK_K, "SwapWithMaster"
                  3, VK_1, "SwitchWorkspace(0)"
                  4, VK_2, "SwitchWorkspace(1)"
                  5, VK_Q, "Quit" ]
                |> List.map (fun (id, vk, name) -> name, RegisterHotKey(hotkeyForm.Handle, id, config.HotkeyModifiers, vk))
            for name, ok in hotkeyRegistrations do
                if not ok then eprintfn "[wm-host-resident] RegisterHotKey failed for %s (already bound by another app?)" name
            lastHeartbeat <- Environment.TickCount64
            eprintfn "[wm-host-resident-debug] hotkeys registered: %A" hotkeyRegistrations

            // 初回のフル列挙（フック・ホットキー登録の後、`Run()`の前）。1件ごとに心拍を打つので、
            // 窓数が多い実デスクトップでも合計時間ではなく1窓あたりの観測コストだけが基準になる。
            submit (liveNow ()) "wm-host-resident" (MonitorTopologyObserved (enumerateMonitors monitorHandles))
            lastHeartbeat <- Environment.TickCount64
            eprintfn "[wm-host-resident-debug] monitors submitted, enumerating top-level windows..."
            let windows0 = enumerateTopLevelWindows (liveNow ()) monitorHandles titleMatcher
            eprintfn "[wm-host-resident-debug] enumerated %d manageable top-level windows" windows0.Length
            for snapshot in windows0 do
                for input in normalize (RawFirstSeen snapshot) do
                    submit (liveNow ()) "wm-host-resident" input
                lastHeartbeat <- Environment.TickCount64
            eprintfn "[wm-host-resident-debug] initial enumeration submitted, entering Run()"

            try
                residentHost.Run(tickMs = config.TickMs)
            finally
                // 正常終了順序（問4）：パイプaccept停止→Unhook/UnregisterHotKey→
                // HiddenByWm復元（Coreを経由しない直接API、Round 12と同じ）→
                // discoveryファイル削除→Mutex解放。
                pipeCts.Cancel()
                pipeAcceptThread |> Option.iter (fun t -> t.Join(3000) |> ignore)
                pipeHandle |> Option.iter (fun p -> try p.Dispose() with _ -> ())
                for id in [ 1; 2; 3; 4; 5 ] do UnregisterHotKey(hotkeyForm.Handle, id) |> ignore
                eventHooks.Unhook ()
                locationHook.Unhook ()
                // この`finally`自体が`hotkeyForm`を作ったのと同じスレッド上で走っているので、
                // `Invoke`によるスレッド間マーシャリングは不要。
                hotkeyForm.Close()
                hotkeyForm.Dispose()
                for KeyValue(hwnd, _) in hiddenHwnds do
                    try ShowWindowAsync(hwnd, SW_RESTORE) |> ignore with _ -> ()
                removeDiscoveryFile ()
                mutex.Dispose()
          with ex ->
            eprintfn "[wm-host-resident-debug] FATAL in wmThread: %s" (ex.ToString()))
    wmThread.IsBackground <- true
    wmThread.Name <- "wm-host-resident-wm"
    // `Form`/`RegisterHotKey`はWinFormsの一般的な前提としてSTAを要求する——既存のRound 2/7等は
    // `[<EntryPoint>]`のmainスレッド（既定でSTAになっている場合が多い）で直接動かしていたため
    // 気づきにくかったが、ここは新規に起こす`Thread`なので明示的にSTAへ設定する。
    wmThread.SetApartmentState(ApartmentState.STA)
    wmThread.Start()

    let consoleCtrlDelegate =
        ConsoleCtrlDelegate(fun ctrlType ->
            if ctrlType = CTRL_C_EVENT || ctrlType = CTRL_BREAK_EVENT || ctrlType = CTRL_CLOSE_EVENT then
                residentHost.RequestStop()
                wmThread.Join(5000) |> ignore
                true
            else
                false)
    SetConsoleCtrlHandler(consoleCtrlDelegate, true) |> ignore

    Ok { RequestStop = (fun () -> residentHost.RequestStop())
         WaitForExit = (fun timeoutMs -> wmThread.Join(timeoutMs))
         KeepAlive = box consoleCtrlDelegate
         ResidentHost = residentHost }

/// CLIエントリポイント（`--resident --policy=observe|recommended`）向けの薄いラッパー。
/// 呼び出したスレッドをブロックし、`RequestStop()`が呼ばれる（ホットキー・Ctrl+C・ウォッチドッグ）
/// まで戻らない。戻り値は終了コード。
let run (config: ResidentConfig) : int =
    match startResident config with
    | Error e ->
        eprintfn "[wm-host-resident] failed to start: %s" e
        1
    | Ok handle ->
        printfn "winmonad resident mode started. AppDataDir / Mutex single-instance check passed."
        printfn "Hotkeys (modifiers as configured): FocusNext / SwapWithMaster / SwitchWorkspace(0/1) / Quit."
        handle.WaitForExit(Timeout.Infinite) |> ignore
        0
