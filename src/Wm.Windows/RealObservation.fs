/// architecture.md §17：Wm.Windowsの観測側（Phase B ラウンド1、読み取りのみ）。
/// 実際のWin32を`Native.fs`経由で問い合わせ、`Wm.Contracts.Observation.MonitorSnapshot`/
/// `WindowSnapshot`を組み立てる。`Wm.Core`には一切依存しない（§17の依存方向）。
///
/// このラウンドで意図的に対象外にしたこと（計画の「このラウンドで対象外にしたこと」参照、
/// いずれも将来のラウンドへ切り出す）：`Process`/`AppIdentity`（Policy分類にはExe名が要るが、
/// このラウンドの目的は「パイプが通ること」の確認であり分類の充実度ではない）、`Owner`/
/// `IsDialog`/`MinWidthHint`/`TargetIntegrity`/`Responsiveness`/`Presence`/`DpiAwareness`/
/// `TitleMatches`。`MonitorId`/`WindowId`はこの1回の実行内でだけ一意（HWND/HMONITORの値そのもの、
/// またはモニターは列挙順の連番）——再接続をまたいだ安定キー付与（`StableKey`、§4.1）は
/// 次のラウンドへ持ち越す。
module Wm.Windows.RealObservation

open System
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

/// `Ok true`/`Ok false`のResultで返す（`callBoolRead`はBOOLの成否自体しか運ばないため）。
let queryVisible (hWnd: IntPtr) : Result<bool, NativeCallRecord> =
    callBoolRead "IsWindowVisible" (fun () -> IsWindowVisible(hWnd)) |> Result.map (fun () -> true)

let queryMinimized (hWnd: IntPtr) : Result<bool, NativeCallRecord> =
    callBoolRead "IsIconic" (fun () -> IsIconic(hWnd)) |> Result.map (fun () -> true)

let private queryMaximized (hWnd: IntPtr) : Result<bool, NativeCallRecord> =
    callBoolRead "IsZoomed" (fun () -> IsZoomed(hWnd)) |> Result.map (fun () -> true)

let queryCloak (hWnd: IntPtr) : Result<CloakState, NativeCallRecord> =
    let mutable cloakVal = 0
    let hr = DwmGetWindowAttributeInt(hWnd, DWMWA_CLOAKED, &cloakVal, 4)
    if hr = 0 (* S_OK *) then
        Ok { App = (cloakVal &&& 1) <> 0; Shell = (cloakVal &&& 2) <> 0; Inherited = (cloakVal &&& 4) <> 0 }
    else
        Error { Api = "DwmGetWindowAttribute"; Space = HResult; Code = hr; ElapsedUs = 1L }

let queryMonitor (monitorHandles: IntPtr list) (hWnd: IntPtr) : Result<MonitorId, NativeCallRecord> =
    let hMonitor = MonitorFromWindow(hWnd, MONITOR_DEFAULTTONEAREST)
    match monitorIdOf monitorHandles hMonitor with
    | Some mid -> Ok mid
    | None -> Error { Api = "MonitorFromWindow"; Space = BoolFalseNoCode; Code = 0; ElapsedUs = 1L }

/// 1件のHWNDについて、ラウンド1が問い合わせる属性一式(`IsVisible`/`NativeRect`/`VisibleFrameRect`/
/// `IsMinimized`/`IsMaximized`/`Cloak`/`Monitor`)を全て問い合わせ、`WindowSnapshot`を組み立てる。
/// `enumerateTopLevelWindows`（全体列挙）と、ラウンド2の`EVENT_OBJECT_CREATE`時の`RawFirstSeen`の
/// 両方がこの同じ関数を使う。`monitorHandles`は`enumerateMonitorHandles`の結果をそのまま渡す。
let observeWindow (now: int64) (monitorHandles: IntPtr list) (hWnd: IntPtr) : WindowSnapshot =
    let w = WindowId (hWnd.ToInt64())
    { Id = w
      Process = None
      AppIdentity = { Exe = None; HostExe = None; Aumid = None; CoreWindowExe = None }
      Owner = None
      IsDialog = None
      NativeRect = Some (obs now (queryNativeRect hWnd))
      VisibleFrameRect = Some (obs now (queryVisibleFrameRect hWnd))
      IsVisible = Some (obs now (queryVisible hWnd))
      IsMinimized = Some (obs now (queryMinimized hWnd))
      IsMaximized = Some (obs now (queryMaximized hWnd))
      Cloak = Some (obs now (queryCloak hWnd))
      Monitor = Some (obs now (queryMonitor monitorHandles hWnd))
      MinWidthHint = None
      TargetIntegrity = None
      Responsiveness = None
      Presence = None
      DpiAwareness = None
      TitleMatches = None }

/// `Observation<'T>`を1件だけ組み立てる薄いヘルパー（ラウンド2の個別イベント再問合せ用、
/// `RawVisibility`/`RawMinimized`/`RawCloak`が運ぶ値を直接作れる）。
let observation (now: int64) (value: Result<'T, NativeCallRecord>) : Observation<'T> = obs now value

/// `EnumWindows`で見つかった全トップレベルHWNDを`WindowSnapshot`へ変換する。
/// `WindowId`はHWNDの値そのもの（この1回の実行内でだけ意味を持つ、再起動後は不変ではない）。
/// `monitorHandles`は`enumerateMonitorHandles`の結果をそのまま渡す（`enumerateMonitors`が割り当てた
/// `MonitorId`と同じ列挙順を共有するため）。
let enumerateTopLevelWindows (now: int64) (monitorHandles: IntPtr list) : WindowSnapshot list =
    let hwnds = ResizeArray<IntPtr>()
    let cb = EnumChildProc(fun hWnd _ -> hwnds.Add(hWnd) |> ignore; true)
    EnumWindows(cb, IntPtr.Zero) |> ignore
    hwnds |> Seq.map (observeWindow now monitorHandles) |> List.ofSeq
