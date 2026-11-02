/// Core.Focus（ADR-019実装順14、architecture.md §4.4/§4.5）。フォアグラウンドの信念、
/// グローバルなフォーカス履歴（直前の窓へ戻る用）、Workspace切替の揺れの待ち。
module Wm.Core.Focus

open Wm.Contracts.Identifiers
open Wm.Contracts.Observation
open Wm.Contracts.Beliefs
open Wm.Contracts.SharedVocabulary
open Wm.Contracts.Timers

/// LatestWins規則（§4.5）の修正関数そのものはContracts（`Beliefs.LatestWins.revise`）に1つだけ置く
/// （Opusレビュー1-1：ドメインごとに`reviseForeground`を再実装すると、`Belief<'T>.LastRevisedAt`の単位の
/// 解釈が食い違う余地が生まれる。Windowsの`ClassInputBeliefs`が実装される際も、ここをコピーせず
/// `Beliefs.LatestWins.revise`を直接呼ぶこと）。ここでは`FocusState.Foreground`専用の薄いラッパーとして残す。
let reviseForeground (now: int64) (current: LatestWins<WindowId option>) (observation: Observation<WindowId option>) : LatestWins<WindowId option> =
    LatestWins.revise now current observation

/// Workspace内のフォーカスはzipperが持つ（Core.Workspaces）。ここで持つのは
/// 「直前にフォーカスしていた窓へ戻る」専用のグローバルMRUだけで、zipperとは別の小さな構造（§4.4、R3-3）。
type FocusState = {
    Foreground: LatestWins<WindowId option>
    History: WindowId list
    /// フォーカスを渡すべき窓（`Stack = None`ならNone）と、待ちの発火予定時刻（deadline）の組。
    SwitchSettle: (WindowId option * int64) option
}

let initial: FocusState = { Foreground = LatestWins.unformed; History = []; SwitchSettle = None }

/// `focused`の導出値がstepの前後で変わり`ManagedWindow w`になったときだけ`History`の先頭へ`w`を移す
/// （§7.1段⑥、R6-3）。既に含まれていれば重複させず先頭へ移動するだけ。値が変わらなければ何もしない。
let updateHistory (focusedBefore: FocusTarget) (focusedAfter: FocusTarget) (history: WindowId list) : WindowId list =
    match focusedAfter with
    | ManagedWindow w when focusedBefore <> focusedAfter -> w :: (history |> List.filter (fun x -> x <> w))
    | _ -> history

/// `WindowUnmanaged w`を`History`から除く（(X3)、§7.1段⑥）。
let removeFromHistory (w: WindowId) (history: WindowId list) : WindowId list =
    history |> List.filter (fun x -> x <> w)

// ===== SwitchSettle（Workspace切替直後の揺れの吸収、§4.4、R4-5） =====

/// `SwitchWorkspace`の後に開始する待ち。フォーカスを渡すべき窓（`Stack = None`ならNone）を保持する。
let startSwitchSettle (now: int64) (settleMs: int64) (focusTarget: WindowId option) : (WindowId option * int64) * TimerCommand<TimerId> list =
    let deadline = now + settleMs
    (focusTarget, deadline), [ StartTimer (TimerId.Focus WorkspaceSwitchSettle, settleMs) ]

/// SwitchSettleタイマーの発火を処理する。Topology.update/Health.onReprobeTimerFiredと同じ`awaitDeadline`
/// パターンで古い発火を無視する。期限到達時は、フォアグラウンドがまだhiddenなWorkspaceの窓なら
/// 1回だけ再要求すべき窓（保持していたfocusTarget）を返す（`Stack = None`だったならNoneのまま、再要求は無い）。
let onSwitchSettleTimerFired (now: int64) (state: FocusState) : FocusState * WindowId option * TimerCommand<TimerId> list =
    match state.SwitchSettle with
    | Some (focusTarget, deadline) when awaitDeadline (Some deadline) now ->
        { state with SwitchSettle = None }, focusTarget, [ CancelTimer (TimerId.Focus WorkspaceSwitchSettle) ]
    | _ -> state, None, [] // 古い発火、または待ちが存在しない
