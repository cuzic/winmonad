/// architecture.md §17：Wm.Windowsの観測側（Phase B ラウンド1、読み取りのみ）。
/// 実際のWin32を`Native.fs`経由で問い合わせ、`Wm.Contracts.Observation.MonitorSnapshot`/
/// `WindowSnapshot`を組み立てる。`Wm.Core`には一切依存しない（§17の依存方向）。
///
/// このラウンドで意図的に対象外にしたこと（計画の「このラウンドで対象外にしたこと」参照、
/// いずれも将来のラウンドへ切り出す）：`Process`（`ProcessKey`はPid+CreationTimeの組が必要——
/// HWND再利用検知(Sticky規則、v2.47)に使われるため、CreationTime取得の実機確認が要る別の課題
/// として持ち越す）、`AppIdentity`のUWP系3フィールド(`HostExe`/`Aumid`/`CoreWindowExe`、
/// `ApplicationFrameHost`経由のUWP/WinUI識別には未文書化COM相当の調査が要る、Phase 0の
/// 既知の難所）、`Owner`/`IsDialog`/`MinWidthHint`/`TargetIntegrity`/`Responsiveness`/`Presence`/
/// `DpiAwareness`。`MonitorId`/`WindowId`はこの1回の実行内でだけ一意（HWND/HMONITORの値そのもの、
/// またはモニターは列挙順の連番）——再接続をまたいだ安定キー付与（`StableKey`、§4.1）は
/// 次のラウンドへ持ち越す。
/// **Phase B ラウンド5で`AppIdentity.Exe`と`TitleMatches`を実装した**（タスクリスト見直しで
/// 自己選定：これが無いと実アンビエント窓をPolicyルールで安全に絞り込めず、ラウンド3/4の
/// 隔離デモを実管理へ統合できないため）。
module Wm.Windows.RealObservation

open System
open System.Diagnostics
open System.Runtime.InteropServices
open Wm.Contracts.Identifiers
open Wm.Contracts.ErrorTypes
open Wm.Contracts.Observation
open Wm.Windows.Native

let private nextSampleSequence =
    let counter = ref 0L
    fun () ->
        counter.Value <- counter.Value + 1L
        counter.Value

let private rectOf (r: RECT) : PixelRect =
    { X = r.Left; Y = r.Top; Width = r.Right - r.Left; Height = r.Bottom - r.Top }

let private obs (now: int64) (value: Result<'T, Wm.Contracts.ErrorTypes.NativeCallRecord>) : Observation<'T> =
    { Value = value; SampleSequence = nextSampleSequence (); MonotonicTime = now; Source = "wm-windows" }

/// `EnumDisplayMonitors`で見つかった全HMONITORのハンドル一覧（列挙順）。`enumerateMonitors`と
/// `enumerateTopLevelWindows`の両方が同じ列挙結果（同じ`MonitorId`割り当て）を使えるよう、
/// 呼び出し側（`Program.fs`）がこの関数を1回だけ呼んで両方へ渡す。
let enumerateMonitorHandles () : IntPtr list =
    let handles = ResizeArray<IntPtr>()
    let cb = MonitorEnumProc(fun hMonitor _ _ _ -> handles.Add(hMonitor) |> ignore; true)
    EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, cb, IntPtr.Zero) |> ignore
    List.ofSeq handles

/// `monitorHandles`（`enumerateMonitorHandles`の結果）を`MonitorSnapshot`へ変換する。`MonitorId`は
/// 列挙順の連番（0始まり、`IsPrimary`なモニターが先頭に来るとは限らない——Win32は順序を保証しない）。
let enumerateMonitors (monitorHandles: IntPtr list) : MonitorSnapshot list =
    monitorHandles
    |> Seq.mapi (fun i hMonitor ->
        let mutable mi = MONITORINFO()
        mi.cbSize <- Marshal.SizeOf(typeof<MONITORINFO>)
        let gotInfo = GetMonitorInfo(hMonitor, &mi)
        let mutable dpiX, dpiY = 0u, 0u
        let dpiHr = GetDpiForMonitor(hMonitor, MDT_EFFECTIVE_DPI, &dpiX, &dpiY)
        let dpi = if dpiHr = 0 (* S_OK *) && dpiX > 0u then int dpiX else 96
        { Id = MonitorId (int64 i)
          StableKey = sprintf "hmonitor:%d" (hMonitor.ToInt64())
          Bounds = if gotInfo then rectOf mi.rcMonitor else { X = 0; Y = 0; Width = 0; Height = 0 }
          WorkArea = if gotInfo then rectOf mi.rcWork else { X = 0; Y = 0; Width = 0; Height = 0 }
          Dpi = dpi
          IsPrimary = gotInfo && (mi.dwFlags &&& MONITORINFOF_PRIMARY) <> 0u })
    |> List.ofSeq

/// `hMonitor`から、`enumerateMonitors`が割り当てた`MonitorId`を逆引きする（同じ列挙順が前提）。
let private monitorIdOf (monitorHandles: IntPtr list) (hMonitor: IntPtr) : MonitorId option =
    monitorHandles |> List.tryFindIndex (fun h -> h = hMonitor) |> Option.map (fun i -> MonitorId (int64 i))

let private queryNativeRect (hWnd: IntPtr) : Result<PixelRect, NativeCallRecord> =
    let mutable r = RECT()
    match callBoolRead "GetWindowRect" (fun () -> GetWindowRect(hWnd, &r)) with
    | Ok () -> Ok (rectOf r)
    | Error e -> Error e

let private queryVisibleFrameRect (hWnd: IntPtr) : Result<PixelRect, NativeCallRecord> =
    let mutable r = RECT()
    let hr = DwmGetWindowAttribute(hWnd, DWMWA_EXTENDED_FRAME_BOUNDS, &r, Marshal.SizeOf(typeof<RECT>))
    if hr = 0 (* S_OK *) then Ok (rectOf r)
    else Error { Api = "DwmGetWindowAttribute"; Space = HResult; Code = hr; ElapsedUs = 1L }

// v2.92で修正した実機CIでの実際のバグ：`IsWindowVisible`/`IsIconic`/`IsZoomed`は
// `SetLastError = true`を宣言していない（`Native.fs`参照）、そしてBOOLの戻り値自体が答えそのもの
// （`false`は「非表示/非最小化/非最大化」という正当な結果であり、失敗ではない）。`callBoolRead`
// （`GetWindowRect`のような「成否シグナルとしてのBOOL」専用）をこれらへ誤って適用すると、答えが
// `false`になる度に無関係・陳腐化した`GetLastWin32Error()`を"Error"として運んでしまい、
// `false`という信念を一度も観測できなくなる（実機CIのログで実際に踏んだ：非表示窓が常に
// `Error Code=0`になり、`Ok false`が一度も出なかった）。これらは成否を問わず常に`Ok`で返す。
let queryVisible (hWnd: IntPtr) : Result<bool, NativeCallRecord> = Ok (IsWindowVisible(hWnd))

let queryMinimized (hWnd: IntPtr) : Result<bool, NativeCallRecord> = Ok (IsIconic(hWnd))

let private queryMaximized (hWnd: IntPtr) : Result<bool, NativeCallRecord> = Ok (IsZoomed(hWnd))

/// `GetWindowLong`は失敗時も0を返すことがあり（Opus敵対的レビュー第2ラウンド§3-A）、`queryVisible`
/// 等の「falseも正当な結果」とは逆に「0は正当な値にも失敗にもなる」——`SetLastSystemError(0)`で
/// 消してから呼び、戻り値が0のときだけ`GetLastWin32Error()`で区別する（`callBoolRead`と同じ考え方）。
/// **注**：`Marshal`に`SetLastWin32Error`という対になるsetterは存在しない（`GetLastWin32Error`は
/// getterのみ）。実在するのは`SetLastSystemError`（`GetLastWin32Error`と対で使うレガシーAPI）と
/// `SetLastPInvokeError`（.NET 6+の推奨API、`GetLastPInvokeError`と対）——`SetLastWin32Error`と
/// 誤って書いたコードがLinux上の`dotnet build`（ubuntu-latest、net8.0-windowsの参照アセンブリに
/// 寄せたクロスコンパイル）では検出されず、実機Windows（GCP self-hosted runner、v2.131）で初めて
/// `FS0039`として表面化した——Linux CIがWindows専用APIの実在チェックとして完全な代用にはならない
/// という教訓（architecture.md §21参照）。
let private queryStyle (hWnd: IntPtr) : Result<int, NativeCallRecord> =
    let sw = Stopwatch.StartNew()
    Marshal.SetLastSystemError(0)
    let style = GetWindowLong(hWnd, GWL_STYLE)
    sw.Stop()
    if style <> 0 then
        Ok style
    else
        let code = Marshal.GetLastWin32Error()
        if code = 0 then Ok style else Error { Api = "GetWindowLong"; Space = Win32LastError; Code = code; ElapsedUs = elapsedUsOf sw }

/// スタイルと矩形は同じ瞬間には読めない（Opus敵対的レビュー第2ラウンド§3-B、F36/S28a/bが実測した
/// とおり全画面の出入りでスタイルと矩形は別タイミングで変わる）。スタイル→矩形→スタイルの順に読み、
/// 2回のスタイルが食い違えば「この観測内では信頼できない」としてErrorにする——`Placement.checkFullscreen`
/// の更新規則（両方が同じ観測内で`Ok`のときだけ更新）により、食い違った回は前の状態をそのまま保つ。
let private queryStyleAndNativeRect (hWnd: IntPtr) : Result<int, NativeCallRecord> * Result<PixelRect, NativeCallRecord> =
    let styleBefore = queryStyle hWnd
    let nativeRect = queryNativeRect hWnd
    let styleAfter = queryStyle hWnd
    let style =
        match styleBefore, styleAfter with
        | Ok a, Ok b when a = b -> Ok a
        | Error _, _ -> styleBefore
        | _, Error _ -> styleAfter
        | Ok _, Ok _ -> Error { Api = "GetWindowLong"; Space = Win32LastError; Code = -1; ElapsedUs = 1L } // 不一致：2回の読み取りの間にスタイルが変わった（§3-B）
    style, nativeRect

let queryCloak (hWnd: IntPtr) : Result<CloakState, NativeCallRecord> =
    let mutable cloakVal = 0
    let hr = DwmGetWindowAttributeInt(hWnd, DWMWA_CLOAKED, &cloakVal, 4)
    if hr = 0 (* S_OK *) then
        Ok { App = (cloakVal &&& 1) <> 0; Shell = (cloakVal &&& 2) <> 0; Inherited = (cloakVal &&& 4) <> 0 }
    else
        Error { Api = "DwmGetWindowAttribute"; Space = HResult; Code = hr; ElapsedUs = 1L }

/// `GetWindowThreadProcessId`→`OpenProcess`(`PROCESS_QUERY_LIMITED_INFORMATION`)→
/// `QueryFullProcessImageNameW`で実行ファイルのフルパスを取得する。UWP/WinUIは`ApplicationFrameHost`
/// 自身のパスが返るだけで実アプリ本体のパスにはならない（ファイル冒頭「対象外」参照、`HostExe`の
/// 位置づけと同じ区別——このラウンドでは`Exe`に素直に格納するだけで、UWPを見分ける判断は加えない）。
let private queryExePath (hWnd: IntPtr) : Result<string, NativeCallRecord> =
    let sw = Stopwatch.StartNew()
    let mutable pid = 0u
    GetWindowThreadProcessId(hWnd, &pid) |> ignore
    let hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid)
    if hProcess = IntPtr.Zero then
        sw.Stop()
        Error { Api = "OpenProcess"; Space = Win32LastError; Code = Marshal.GetLastWin32Error(); ElapsedUs = elapsedUsOf sw }
    else
        try
            let sb = Text.StringBuilder(1024)
            let mutable size = uint32 sb.Capacity
            if QueryFullProcessImageNameW(hProcess, 0u, sb, &size) then
                sw.Stop()
                Ok (sb.ToString())
            else
                sw.Stop()
                Error { Api = "QueryFullProcessImageNameW"; Space = Win32LastError; Code = Marshal.GetLastWin32Error(); ElapsedUs = elapsedUsOf sw }
        finally
            CloseHandle(hProcess) |> ignore

/// 生のタイトル文字列（R4-8：`WindowSnapshot`へ一切載せない。呼び出し側(`attachTitleMatches`)が
/// 即座に`TitleMatchVector`へ変換してから捨てる、一時的な戻り値専用）。`GetWindowTextW`の戻り値0は
/// 「タイトルが空」という正当な結果であり得る（オーナー窓等）ので、`queryVisible`等と同じ理由で
/// 常に`Ok`（失敗シグナルとして扱わない）。
let queryRawTitleForPolicyMatching (hWnd: IntPtr) : Result<string, NativeCallRecord> =
    let sb = Text.StringBuilder(512)
    GetWindowTextW(hWnd, sb, sb.Capacity) |> ignore
    Ok (sb.ToString())

/// P0-2（Opus敵対的相談`opus-review-resident-daemon.md`）：Core（`Wm.Core.Windows.isCandidate`の
/// 型直前のコメント）は「非ツール窓（`WS_EX_TOOLWINDOW`相当）はShellの列挙時点で既に除外されている
/// 前提」と明記しているが、この判定を実装するコードがどこにも無かった——システム全体のWinEvent購読
/// （`EVENT_OBJECT_CREATE`/`SHOW`は`idObject = OBJID_WINDOW`のままボタン等の子コントロールにも発火
/// する）やトップレベル窓の全体列挙に、子窓・ツール窓が素通りしていた。`enumerateTopLevelWindows`と
/// 常駐ループのライブイベント経路の両方から呼ぶ。
let isManageableTopLevel (hWnd: IntPtr) : bool =
    GetAncestor(hWnd, GA_ROOT) = hWnd
    && (GetWindowLong(hWnd, GWL_STYLE) &&& WS_CHILD) = 0
    && (GetWindowLong(hWnd, GWL_EXSTYLE) &&& WS_EX_TOOLWINDOW) = 0

let private queryOwner (hWnd: IntPtr) : Result<WindowId option, NativeCallRecord> =
    let owner = GetWindow(hWnd, GW_OWNER)
    Ok (if owner = IntPtr.Zero then None else Some (WindowId (owner.ToInt64())))

/// P0-1（Opus敵対的相談`opus-review-resident-daemon.md`）：`IsDialog`が`None`固定だったため、
/// `PolicyIR.recommendedWindowRules`の`DialogIs true -> Floating`（Priority 50）が常に`Unformed`で
/// 止まり、他の全ての実窓が`AwaitingEvidence`→`ClassificationTimeoutMs`後に`Floating`へ落ちていた
/// （`DefaultManagement = Tiled`が実窓では一切効かない、「何もタイルしないWM」になっていた）。
/// ダイアログクラス（`#32770`、`MessageBox`/コモンダイアログが使う標準クラス名）と
/// `WS_EX_DLGMODALFRAME`のいずれかを満たせばダイアログとみなす——`queryVisible`等と同じ理由で
/// 常に`Ok`（「ダイアログではない」も正当な結果であり、失敗ではない）。
let private queryIsDialog (hWnd: IntPtr) : Result<bool, NativeCallRecord> =
    let sb = Text.StringBuilder(256)
    GetClassNameW(hWnd, sb, sb.Capacity) |> ignore
    let exStyle = GetWindowLong(hWnd, GWL_EXSTYLE)
    Ok (sb.ToString() = "#32770" || (exStyle &&& WS_EX_DLGMODALFRAME) <> 0)

let queryMonitor (monitorHandles: IntPtr list) (hWnd: IntPtr) : Result<MonitorId, NativeCallRecord> =
    let hMonitor = MonitorFromWindow(hWnd, MONITOR_DEFAULTTONEAREST)
    match monitorIdOf monitorHandles hMonitor with
    | Some mid -> Ok mid
    | None -> Error { Api = "MonitorFromWindow"; Space = BoolFalseNoCode; Code = 0; ElapsedUs = 1L }

/// 1件のHWNDについて、ラウンド1が問い合わせる属性一式(`IsVisible`/`NativeRect`/`VisibleFrameRect`/
/// `IsMinimized`/`IsMaximized`/`Cloak`/`Monitor`)に加え、ラウンド5で`AppIdentity.Exe`と
/// `TitleMatches`を問い合わせ、`WindowSnapshot`を組み立てる。`enumerateTopLevelWindows`（全体列挙）と、
/// ラウンド2の`EVENT_OBJECT_CREATE`時の`RawFirstSeen`の両方がこの同じ関数を使う。`monitorHandles`は
/// `enumerateMonitorHandles`の結果をそのまま渡す。`titleMatcher`は呼び出し側（Wm.Host）が
/// `Wm.Policy.PolicyEvaluator.computeTitleMatchVector`等から組み立てて渡すクロージャ——`Wm.Windows`は
/// `Wm.Policy`に依存できない（§17の依存方向）ため、関数そのものではなく「生タイトル文字列→
/// `TitleMatchVector`」という値だけを受け取ることで依存方向を保つ（R4-8：生タイトル文字列自体は
/// ここで変換に使うだけで、`WindowSnapshot`には一切載せない）。
let observeWindow (now: int64) (monitorHandles: IntPtr list) (titleMatcher: string -> TitleMatchVector) (hWnd: IntPtr) : WindowSnapshot =
    let w = WindowId (hWnd.ToInt64())
    let styleResult, nativeRectResult = queryStyleAndNativeRect hWnd
    { Id = w
      Process = None
      AppIdentity = { Exe = Some (obs now (queryExePath hWnd)); HostExe = None; Aumid = None; CoreWindowExe = None }
      Owner = Some (obs now (queryOwner hWnd))
      IsDialog = Some (obs now (queryIsDialog hWnd))
      NativeRect = Some (obs now nativeRectResult)
      VisibleFrameRect = Some (obs now (queryVisibleFrameRect hWnd))
      IsVisible = Some (obs now (queryVisible hWnd))
      IsMinimized = Some (obs now (queryMinimized hWnd))
      IsMaximized = Some (obs now (queryMaximized hWnd))
      Style = Some (obs now styleResult)
      Cloak = Some (obs now (queryCloak hWnd))
      Monitor = Some (obs now (queryMonitor monitorHandles hWnd))
      MinWidthHint = None
      TargetIntegrity = None
      Responsiveness = None
      Presence = None
      DpiAwareness = None
      TitleMatches = Some (obs now (queryRawTitleForPolicyMatching hWnd |> Result.map titleMatcher)) }

/// `Observation<'T>`を1件だけ組み立てる薄いヘルパー（ラウンド2の個別イベント再問合せ用、
/// `RawVisibility`/`RawMinimized`/`RawCloak`が運ぶ値を直接作れる）。
let observation (now: int64) (value: Result<'T, NativeCallRecord>) : Observation<'T> = obs now value

/// `EnumWindows`で見つかった全トップレベルHWNDを`WindowSnapshot`へ変換する。
/// `WindowId`はHWNDの値そのもの（この1回の実行内でだけ意味を持つ、再起動後は不変ではない）。
/// `monitorHandles`は`enumerateMonitorHandles`の結果をそのまま渡す（`enumerateMonitors`が割り当てた
/// `MonitorId`と同じ列挙順を共有するため）。
let enumerateTopLevelWindows (now: int64) (monitorHandles: IntPtr list) (titleMatcher: string -> TitleMatchVector) : WindowSnapshot list =
    let hwnds = ResizeArray<IntPtr>()
    let cb = EnumChildProc(fun hWnd _ -> hwnds.Add(hWnd) |> ignore; true)
    EnumWindows(cb, IntPtr.Zero) |> ignore
    hwnds |> Seq.filter isManageableTopLevel |> Seq.map (observeWindow now monitorHandles titleMatcher) |> List.ofSeq
