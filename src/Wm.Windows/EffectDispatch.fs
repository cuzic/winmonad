/// architecture.md §7.1/§8.1：Effectを実際のWin32へ発行する（Phase B ラウンド3）。
///
/// `Wm.Contracts.ErrorTypes.executeEffectProtocol`（既存・既にテスト済みの純粋関数）を、
/// 実際の`probe`/`dispatch`で埋める——Core/Simulation側が共有する同じ手順を、ここでも素通りさせず
/// 使う（§7.1「Shellと同じEffect実行手順を共有する」）。このラウンドで実際に発行するのは`SetRect`/
/// `RequestForeground`/`RestoreBeforeMove`の3種だけ（計画の「このラウンドで対象外にしたこと」参照、
/// `Reprobe`/`SetPresence`/`ForgetWindow`/`ObserveAll`は`None`を返す——
/// `Wm.Simulation.VirtualWorld`がまだ実装していないEffect種別を`[]`で返すのと同じ「まだ無い
/// 消費者向けに作り込まない」判断）。
/// **Phase B ラウンド10で`RequestClose`を追加した**：§8.4「窓を破壊・終了するEffectを提供しない」の
/// 唯一の例外（`WM_CLOSE`による丁寧な要求、アプリは無視・キャンセルできる）であり、`SetPresence`の
/// ような未決の設計判断（§21「ワークスペースの実装方式」）に依存しないため、他のEffectより先に
/// 実装できた。
module Wm.Windows.EffectDispatch

open System
open Wm.Contracts.Identifiers
open Wm.Contracts.ErrorTypes
open Wm.Contracts.CoreCommand
open Wm.Windows.Native

/// `IsHungAppWindow`だけを使う、ブロッキングしない最速の事前確認（型直前のコメント参照。
/// `SendMessageTimeout`のような実際に待つ確認はこのラウンドでは発行しない）。
let probe (w: WindowId) : ProbeOutcome =
    let (WindowId raw) = w
    let hWnd = IntPtr raw
    if IsHungAppWindow(hWnd) then HungConfirmed else Responsive

let private dispatchSetRect (w: WindowId) (rect: PixelRect) : unit -> Result<unit, NativeCallRecord> =
    fun () ->
        let (WindowId raw) = w
        let hWnd = IntPtr raw
        callBool "SetWindowPos" (fun () -> SetWindowPos(hWnd, IntPtr.Zero, rect.X, rect.Y, rect.Width, rect.Height, SWP_NOZORDER ||| SWP_ASYNCWINDOWPOS))

let private dispatchRequestForeground (w: WindowId) : unit -> Result<unit, NativeCallRecord> =
    fun () ->
        let (WindowId raw) = w
        let hWnd = IntPtr raw
        callBoolNoCode "SetForegroundWindow" (fun () -> SetForegroundWindow(hWnd))

/// §6.2/§7.1「NeedsRestore」：`ShowWindow`（同期）ではなく`ShowWindowAsync(SW_RESTORE)`に固定する
/// （`SetWindowPos`に`SWP_ASYNCWINDOWPOS`を付ける理由と同じ、ハング窓での長時間ブロック回避）。
let private dispatchRestoreBeforeMove (w: WindowId) : unit -> Result<unit, NativeCallRecord> =
    fun () ->
        let (WindowId raw) = w
        let hWnd = IntPtr raw
        callBoolNoCode "ShowWindowAsync" (fun () -> ShowWindowAsync(hWnd, SW_RESTORE))

/// `PostMessage(WM_CLOSE)`はキューへ投函するだけで応答を待たない——ハングした対象でも呼び出し側を
/// 止めない（7.1のADR-012と同じ考え方、`SendMessage`ではなく`PostMessage`を選ぶ理由）。
let private dispatchRequestClose (w: WindowId) : unit -> Result<unit, NativeCallRecord> =
    fun () ->
        let (WindowId raw) = w
        let hWnd = IntPtr raw
        callBool "PostMessage" (fun () -> PostMessage(hWnd, WM_CLOSE, UIntPtr.Zero, IntPtr.Zero))

/// 1件のEffectについて、対象窓と実際の`dispatch`クロージャを返す。このラウンドで対応しない
/// Effect種別（`Reprobe`/`SetPresence`/`ForgetWindow`/`ObserveAll`/`ApplyBatch`）は
/// `None`——呼び出し側はこれを「ログに残すだけで実際には発行しない」と解釈する。
let toDispatch (effect: Effect) : (WindowId * (unit -> Result<unit, NativeCallRecord>)) option =
    match effect with
    | SetRect (_, w, rect) -> Some (w, dispatchSetRect w rect)
    | RequestForeground (_, w) -> Some (w, dispatchRequestForeground w)
    | RestoreBeforeMove (_, w) -> Some (w, dispatchRestoreBeforeMove w)
    | RequestClose (_, w) -> Some (w, dispatchRequestClose w)
    | _ -> None

/// `executeEffectProtocol`（既存、Wm.Contracts.ErrorTypes）を実際の`probe`/`dispatch`で呼ぶ。
/// `toDispatch`が`None`を返すEffectは、プロトコルを通さずそのまま呼び出し側へ「対象外」を伝える。
let apply (effect: Effect) : Result<unit, NativeCallRecord> option =
    toDispatch effect
    |> Option.map (fun (w, dispatch) -> executeEffectProtocol probe dispatch w)
