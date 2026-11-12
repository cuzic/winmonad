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
open System.Collections.Generic
open System.Diagnostics
open System.Drawing
open System.IO
open System.Text.Json
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

/// タスクトレイアイコン・ショートカットキー設定画面向け。常駐ホットキーは固定の5個の操作に対応する
/// Win32 `RegisterHotKey`の組（修飾キー＋VK）として永続化する（`%LOCALAPPDATA%\winmonad\hotkeys.json`）。
/// ケース名に`Hk`を付けているのは、`Wm.Contracts.CoreCommand.CoreCommand`の`FocusNext`/
/// `SwapWithMaster`ケースと同名になり（本ファイルは`open Wm.Contracts.CoreCommand`済み）、裸の
/// ケース識別子が曖昧になるのを避けるため（`onHotkey`の`UserCommand FocusNext`はCoreCommand側を指す）。
type HotkeyAction =
    | HkFocusNext
    | HkSwapWithMaster
    | HkSwitchWorkspace1
    | HkSwitchWorkspace2
    | HkQuit

let private actionLabel (action: HotkeyAction) : string =
    match action with
    | HkFocusNext -> "次のウィンドウへフォーカス移動"
    | HkSwapWithMaster -> "マスターウィンドウと入れ替え"
    | HkSwitchWorkspace1 -> "ワークスペース 1 に切替"
    | HkSwitchWorkspace2 -> "ワークスペース 2 に切替"
    | HkQuit -> "終了"

let private allHotkeyActions = [ HkFocusNext; HkSwapWithMaster; HkSwitchWorkspace1; HkSwitchWorkspace2; HkQuit ]

type HotkeyBinding = { Modifiers: uint32; Vk: uint32 }

type HotkeyBindings =
    { FocusNext: HotkeyBinding
      SwapWithMaster: HotkeyBinding
      SwitchWorkspace1: HotkeyBinding
      SwitchWorkspace2: HotkeyBinding
      Quit: HotkeyBinding }

module HotkeyBindings =
    let defaultWith (modifiers: uint32) : HotkeyBindings =
        { FocusNext = { Modifiers = modifiers; Vk = uint32 VK_J }
          SwapWithMaster = { Modifiers = modifiers; Vk = uint32 VK_K }
          SwitchWorkspace1 = { Modifiers = modifiers; Vk = VK_1 }
          SwitchWorkspace2 = { Modifiers = modifiers; Vk = VK_2 }
          Quit = { Modifiers = modifiers; Vk = VK_Q } }

    /// `RegisterHotKey`のID（1〜5、既存のWndProcディスパッチと一致させる）順に並べたもの。
    let toList (b: HotkeyBindings) : (int * HotkeyAction * HotkeyBinding) list =
        [ 1, HkFocusNext, b.FocusNext
          2, HkSwapWithMaster, b.SwapWithMaster
          3, HkSwitchWorkspace1, b.SwitchWorkspace1
          4, HkSwitchWorkspace2, b.SwitchWorkspace2
          5, HkQuit, b.Quit ]

    let withBinding (action: HotkeyAction) (binding: HotkeyBinding) (b: HotkeyBindings) : HotkeyBindings =
        match action with
        | HkFocusNext -> { b with FocusNext = binding }
        | HkSwapWithMaster -> { b with SwapWithMaster = binding }
        | HkSwitchWorkspace1 -> { b with SwitchWorkspace1 = binding }
        | HkSwitchWorkspace2 -> { b with SwitchWorkspace2 = binding }
        | HkQuit -> { b with Quit = binding }

    let path (appDataDir: string) : string = Path.Combine(appDataDir, "hotkeys.json")

    let private jsonOptions = JsonSerializerOptions(WriteIndented = true)

    let save (filePath: string) (b: HotkeyBindings) : unit =
        File.WriteAllText(filePath, JsonSerializer.Serialize(b, jsonOptions))

    /// 既存ファイルがあれば読む（壊れていたら既定値にフォールバック）。無ければ既定値を書き出して
    /// 返す——設定画面を一度も開かないユーザーでも、手で編集できるファイルが最初から存在するように。
    let loadOrCreate (filePath: string) (defaultModifiers: uint32) : HotkeyBindings =
        try
            if File.Exists filePath then
                JsonSerializer.Deserialize<HotkeyBindings>(File.ReadAllText filePath)
            else
                let defaults = defaultWith defaultModifiers
                save filePath defaults
                defaults
        with _ ->
            defaultWith defaultModifiers

let private modifierNames (m: uint32) : string list =
    [ if m &&& MOD_CONTROL <> 0u then yield "Ctrl"
      if m &&& MOD_ALT <> 0u then yield "Alt"
      if m &&& MOD_SHIFT <> 0u then yield "Shift" ]

let private vkName (vk: uint32) : string =
    try (enum<Keys> (int vk)).ToString() with _ -> sprintf "0x%02X" vk

let private formatBinding (b: HotkeyBinding) : string =
    String.Join("+", modifierNames b.Modifiers @ [ vkName b.Vk ])

/// タスクトレイの「ショートカットキー設定」メニューから開くダイアログ。キー割り当てボタンを押すと
/// 次に押されたキーをそのまま新しい割り当てとして受け取る（`KeyPreview`＋Form自身の`KeyDown`）。
/// 保存は呼び出し側の`onSave`コールバックに新しい`HotkeyBindings`を渡すだけ——ファイルへの永続化・
/// 実際の`RegisterHotKey`再登録は呼び出し側（`startResident`）の責務にする。
type private SettingsForm(initial: HotkeyBindings, onSave: HotkeyBindings -> unit, onError: exn -> unit) as this =
    inherit Form(
        Text = "winmonad — ショートカットキー設定",
        Width = 420,
        Height = 70 + List.length allHotkeyActions * 32 + 60,
        FormBorderStyle = FormBorderStyle.FixedDialog,
        MaximizeBox = false,
        MinimizeBox = false,
        StartPosition = FormStartPosition.CenterScreen,
        KeyPreview = true)
    let mutable pending = initial
    let mutable listeningAction : HotkeyAction option = None
    let buttons = Dictionary<HotkeyAction, Button>()

    let bindingOf (action: HotkeyAction) : HotkeyBinding =
        HotkeyBindings.toList pending |> List.find (fun (_, a, _) -> a = action) |> fun (_, _, b) -> b

    do
        allHotkeyActions
        |> List.iteri (fun i action ->
            let y = 16 + i * 32
            let label = Label(Text = actionLabel action, Left = 12, Top = y + 3, Width = 220)
            let button = Button(Left = 240, Top = y, Width = 150, Text = formatBinding (bindingOf action))
            button.Click.Add(fun _ ->
                try
                    listeningAction <- Some action
                    button.Text <- "キーを押してください…"
                with ex -> onError ex)
            buttons.[action] <- button
            this.Controls.Add(label)
            this.Controls.Add(button))

        let buttonsTop = 16 + List.length allHotkeyActions * 32 + 16
        let saveButton = Button(Text = "保存", Left = 150, Top = buttonsTop, Width = 80)
        let cancelButton = Button(Text = "キャンセル", Left = 240, Top = buttonsTop, Width = 80)
        saveButton.Click.Add(fun _ ->
            try
                let keys = HotkeyBindings.toList pending |> List.map (fun (_, _, b) -> b.Modifiers, b.Vk)
                if keys |> List.distinct |> List.length <> keys.Length then
                    MessageBox.Show(this, "同じキー組み合わせが複数の操作に割り当てられています。重複を解消してください。",
                        "winmonad", MessageBoxButtons.OK, MessageBoxIcon.Warning) |> ignore
                else
                    onSave pending
                    this.Close()
            with ex -> onError ex)
        cancelButton.Click.Add(fun _ -> try this.Close() with ex -> onError ex)
        this.Controls.Add(saveButton)
        this.Controls.Add(cancelButton)
        this.AcceptButton <- saveButton
        this.CancelButton <- cancelButton

        this.KeyDown.Add(fun e ->
            try
                match listeningAction with
                | None -> ()
                | Some action ->
                    let vk = e.KeyCode
                    let isModifierOnly =
                        vk = Keys.ControlKey || vk = Keys.Menu || vk = Keys.ShiftKey
                        || vk = Keys.LWin || vk = Keys.RWin || vk = Keys.None
                    if not isModifierOnly then
                        e.Handled <- true
                        e.SuppressKeyPress <- true
                        let modifiers =
                            (if e.Control then MOD_CONTROL else 0u)
                            ||| (if e.Alt then MOD_ALT else 0u)
                            ||| (if e.Shift then MOD_SHIFT else 0u)
                        let effectiveModifiers = if modifiers = 0u then MOD_CONTROL ||| MOD_ALT else modifiers
                        let binding = { Modifiers = effectiveModifiers; Vk = uint32 vk }
                        pending <- HotkeyBindings.withBinding action binding pending
                        buttons.[action].Text <- formatBinding binding
                        listeningAction <- None
            with ex -> onError ex)

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
          // 2秒・10秒は実機CI検証で誤発火した。原因は2つ重なっていた：(1) 初回のフル列挙という
          // 1回限りのコストが`onTick`を一度も経由せず積み上がる→1件ごとの心拍更新で解消。
          // (2) `RealObservation.queryRawTitleForPolicyMatching`が素の`GetWindowTextW`
          // （内部で`WM_GETTEXT`を対象窓の所有スレッドへ`SendMessage`する）を使っていたため、
          // Round 30が自プロセス別スレッド所有の自作テスト窓を問い合わせた瞬間に無期限デッドロック
          // した（`Native.fs`の`SendMessageTimeoutW`直前のコメント参照、`SMTO_ABORTIFHUNG`＋
          // 200msタイムアウトへ修正済み）。(2)修正後は1窓あたりの最悪コストが有界になったが、
          // 窓数分（実デスクトップで数十〜百以上あり得る）積み上がりうるため、十分に余裕を持たせる。
          HeartbeatTimeoutMs = 20_000L }

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

/// `printfn`/`eprintfn`はリモート起動（`Start-Process`でのリダイレクト等）では確実に届かないことが
/// 実機検証で分かった（出力がバッファされたまま、プロセスが`Environment.Exit`等で即終了すると
/// フラッシュされない）。タスクトレイ・設定画面の例外はファイルへ同期書き込みで残す——ここが
/// 無ければ「操作中に落ちた気がする」という報告からは原因を一切追えない。
let private logCrash (appDataDir: string) (context: string) (ex: exn) : unit =
    try
        let line = sprintf "[%s] %s: %s\n" (DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")) context (ex.ToString())
        File.AppendAllText(Path.Combine(appDataDir, "crash.log"), line)
    with _ -> ()

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

    Directory.CreateDirectory(config.AppDataDir) |> ignore
    let hotkeyBindingsPath = HotkeyBindings.path config.AppDataDir
    let mutable currentBindings = HotkeyBindings.loadOrCreate hotkeyBindingsPath config.HotkeyModifiers
    let journalPath = Path.Combine(config.AppDataDir, "wm-host-resident.ndjson")
    let runtimePolicy = { RuntimePolicy.defaults with Policy = config.Policy; PatternSet = buildTitlePatternSet config.Policy }
    let reducer = Reducer(runtimePolicy, "wm-host-resident", "wm-host-resident", "no-policy-artifact", Some journalPath)
    let sw = Stopwatch.StartNew()
    let liveNow () = sw.ElapsedMilliseconds

    let hiddenHwnds = ConcurrentDictionary<IntPtr, unit>()
    let dirty = ConcurrentDictionary<IntPtr, unit>()
    let mutable lastHeartbeat = Environment.TickCount64
    let mutable lastRescanAt = Environment.TickCount64
    let mutable monitorHandles = enumerateMonitorHandles ()
    let titleMatcher = computeTitleMatchVector runtimePolicy.PatternSet

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
            Some acceptThread, Some pipe

    // 問1：フック登録・ホットキー窓の生成・初回のフル列挙・`Run()`の呼び出しは、すべて
    // この1本のスレッド上で、この順序で行う（Opus敵対的相談：フックとホットキー窓はRun()を呼ぶ
    // のと同じスレッドで、Run()の前に登録する——OUTOFCONTEXTコールバックはそのスレッドが
    // `Application.DoEvents()`の中でメッセージを取り出す時にだけ配送されるため）。
    // AppDomain全体の最後の保険。これが発火した後もプロセスは終了するが、原因をファイルに残せる
    // （実機検証で、タスクトレイ操作中の例外が記録を一切残さずプロセスを終了させた実例があった）。
    AppDomain.CurrentDomain.UnhandledException.Add(fun e ->
        match e.ExceptionObject with
        | :? exn as ex -> logCrash config.AppDataDir "appdomain-unhandled" ex
        | _ -> ())

    let wmThread =
        Thread(fun () ->
          try
            // `Application.DoEvents()`経由でメッセージを配送する間に、どこかのイベントハンドラが
            // 自前のtry/withで拾いきれなかった例外を投げた場合の保険（WinFormsの既定の
            // ThreadExceptionダイアログは常駐プロセスには出せないため、ログに残すだけにする）。
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException)
            Application.ThreadException.Add(fun e -> logCrash config.AppDataDir "application-thread-exception" e.Exception)
            // ウォッチドッグ（問4）の心拍は`Run()`開始後は`handleTick`が1tickごとに更新するが、
            // `Run()`開始前のセットアップ（フック登録・ホットキー登録・初回のフル列挙）は1回も
            // `onTick`を経由しない。各ステップの直後にも心拍を打つことで、ウォッチドッグが
            // 「セットアップの合計時間」ではなく「個々のステップの所要時間」だけを基準に判定する
            // ようにする（実機CIで実際に誤発火した——`queryRawTitleForPolicyMatching`の
            // `SendMessageTimeoutW`化で各窓あたりの最悪コストは有界になったが、それでも窓数分
            // 積み上がるため、1件ごとに心拍を更新する方が安全）。
            lastHeartbeat <- Environment.TickCount64
            let eventHooks = hookEvents handleEvent
            let locationHook =
                hookSingleEvent EVENT_OBJECT_LOCATIONCHANGE (fun _ hwnd ->
                    try
                        if Map.containsKey (WindowId (hwnd.ToInt64())) reducer.State.Windows.Records then
                            dirty.[hwnd] <- ()
                    with _ -> ())
            lastHeartbeat <- Environment.TickCount64

            let mutable hotkeyForm = new ResidentHotkeyForm(onHotkey, onDisplayChange, onEndSession)
            hotkeyForm.Show()

            // 実機検証で、`hotkeyForm`（ホットキー受信専用の隠しウィンドウ）がWM_QUERYENDSESSION/
            // WM_ENDSESSION等を契機に予期せず破棄される事例を確認した（2026-11-12、タスクトレイの
            // 「ショートカットキー設定」がObjectDisposedExceptionで無反応になった——`RequestStop`は
            // 呼ばれていないため`finally`経由の正規の破棄ではない）。破棄されたまま
            // `RegisterHotKey`/`UnregisterHotKey`/`ShowDialog`の所有者参照に使うと全滅するので、
            // 使う直前に必ずこれを通し、壊れていたら作り直してホットキーも再登録する。
            let ensureHotkeyFormAlive () =
                if hotkeyForm.IsDisposed then
                    logCrash config.AppDataDir "hotkeyform-recreate"
                        (Exception "hotkeyForm found disposed outside the normal shutdown path (likely WM_QUERYENDSESSION/WM_ENDSESSION); recreating it to keep hotkeys/settings working")
                    hotkeyForm <- new ResidentHotkeyForm(onHotkey, onDisplayChange, onEndSession)
                    hotkeyForm.Show()

            // タスクトレイ設定画面から保存されたキー割り当てで、既存の5個のIDを登録し直す
            // （WndProcのID→操作ディスパッチ自体は変わらないので、ここは組み合わせの差し替えのみ）。
            let registerBindings (bindings: HotkeyBindings) =
                ensureHotkeyFormAlive ()
                HotkeyBindings.toList bindings
                |> List.map (fun (id, action, b) -> actionLabel action, formatBinding b, RegisterHotKey(hotkeyForm.Handle, id, b.Modifiers, b.Vk))
                |> List.iter (fun (name, combo, ok) ->
                    if not ok then
                        // `eprintfn`はリモート起動では確実に届かない（292行目のコメント参照）ので、
                        // 「押しても何も起きない／別アプリが起動する」の原因調査にはこのログが要る。
                        logCrash config.AppDataDir "register-hotkey-failed"
                            (Exception (sprintf "%s (%s) already bound by another app on this machine" name combo))
                        eprintfn "[wm-host-resident] RegisterHotKey failed for %s (already bound by another app?)" name)
            let unregisterBindings () =
                ensureHotkeyFormAlive ()
                for id in [ 1; 2; 3; 4; 5 ] do UnregisterHotKey(hotkeyForm.Handle, id) |> ignore
            let reregisterHotkeys (newBindings: HotkeyBindings) =
                unregisterBindings ()
                currentBindings <- newBindings
                registerBindings newBindings
            registerBindings currentBindings
            lastHeartbeat <- Environment.TickCount64

            // タスクトレイアイコン：右クリックでショートカットキー設定画面を開く／終了する。タスク
            // トレイ（Explorerの通知領域）が無い環境（最小構成のCIランナー等）でも常駐ループ自体は
            // 止めない——失敗したら警告を出すだけでトレイ無しで続行する（RegisterHotKey失敗と同じ方針）。
            let openSettings () =
                try
                    ensureHotkeyFormAlive ()
                    use settingsForm =
                        new SettingsForm(
                            currentBindings,
                            (fun newBindings ->
                                HotkeyBindings.save hotkeyBindingsPath newBindings
                                reregisterHotkeys newBindings),
                            (fun ex -> logCrash config.AppDataDir "settings-form" ex))
                    settingsForm.ShowDialog(hotkeyForm) |> ignore
                with ex -> logCrash config.AppDataDir "open-settings" ex
            let trayIcon =
                try
                    let icon = new NotifyIcon(Text = "winmonad (resident)")
                    // `Icon`を設定する前に`Visible <- true`にすると例外になるので、先にIconを決める。
                    icon.Icon <- (try Icon.ExtractAssociatedIcon(Application.ExecutablePath) with _ -> SystemIcons.Application)
                    let trayMenu = new ContextMenuStrip()
                    let settingsMenuItem = trayMenu.Items.Add("ショートカットキー設定(&S)...")
                    trayMenu.Items.Add(new ToolStripSeparator()) |> ignore
                    let quitMenuItem = trayMenu.Items.Add("終了(&Q)")
                    icon.ContextMenuStrip <- trayMenu
                    // ここから下はすべてUIイベントハンドラ——例外を自前で握らないとwmThread全体が
                    // 落ち（finallyが走った後プロセスがexitする、問1参照）、常駐ループ全体が
                    // ユーザー操作一つで終了してしまう。実機検証で実際にこの経路でクラッシュした。
                    settingsMenuItem.Click.Add(fun _ ->
                        try openSettings () with ex -> logCrash config.AppDataDir "tray-settings-click" ex)
                    quitMenuItem.Click.Add(fun _ ->
                        try residentHost.RequestStop() with ex -> logCrash config.AppDataDir "tray-quit-click" ex)
                    icon.DoubleClick.Add(fun _ ->
                        try openSettings () with ex -> logCrash config.AppDataDir "tray-doubleclick" ex)
                    icon.Visible <- true
                    Some icon
                with ex ->
                    logCrash config.AppDataDir "tray-icon-setup" ex
                    eprintfn "[wm-host-resident] tray icon setup failed, continuing without it: %s" ex.Message
                    None
            lastHeartbeat <- Environment.TickCount64

            // 初回のフル列挙（フック・ホットキー登録の後、`Run()`の前）。1件ごとに心拍を打つので、
            // 窓数が多い実デスクトップでも合計時間ではなく1窓あたりの観測コストだけが基準になる。
            submit (liveNow ()) "wm-host-resident" (MonitorTopologyObserved (enumerateMonitors monitorHandles))
            lastHeartbeat <- Environment.TickCount64
            for snapshot in enumerateTopLevelWindows (liveNow ()) monitorHandles titleMatcher do
                for input in normalize (RawFirstSeen snapshot) do
                    submit (liveNow ()) "wm-host-resident" input
                lastHeartbeat <- Environment.TickCount64

            try
                residentHost.Run(tickMs = config.TickMs)
            finally
                // 正常終了順序（問4）：パイプaccept停止→Unhook/UnregisterHotKey→
                // HiddenByWm復元（Coreを経由しない直接API、Round 12と同じ）→
                // discoveryファイル削除→Mutex解放。
                pipeCts.Cancel()
                pipeAcceptThread |> Option.iter (fun t -> t.Join(3000) |> ignore)
                pipeHandle |> Option.iter (fun p -> try p.Dispose() with _ -> ())
                trayIcon |> Option.iter (fun icon -> try icon.Visible <- false; icon.Dispose() with _ -> ())
                unregisterBindings ()
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
            // セットアップ中（フック登録〜初回列挙）の未処理例外はネイティブコールバック境界の外
            // （P/Invoke呼出しそのものの中ではない）なので、ここで受け止めればプロセス全体を落とさず
            // 済む——問1の「各コールバックはtry/catchで包む」をスレッド本体自体にも適用する。
            logCrash config.AppDataDir "wmthread-fatal" ex
            eprintfn "[wm-host-resident] FATAL in wmThread: %s" (ex.ToString()))
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
        printfn "Right-click the tray icon to edit shortcut keys or quit."
        handle.WaitForExit(Timeout.Infinite) |> ignore
        0
