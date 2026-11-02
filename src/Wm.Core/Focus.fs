/// Core.Focus（ADR-019実装順14、architecture.md §4.4/§4.5）。フォアグラウンドの信念、
/// グローバルなフォーカス履歴（直前の窓へ戻る用）、Workspace切替の揺れの待ち。
module Wm.Core.Focus

open Wm.Contracts.Identifiers
open Wm.Contracts.Observation
open Wm.Contracts.Beliefs
open Wm.Contracts.SharedVocabulary
open Wm.Contracts.Timers

/// LatestWins規則（§4.5）。SampleSequenceの比較のため、`Belief<'T>.LastRevisedAt`を
/// 「最後に採用した（Ok/Error問わず処理した）観測のSampleSequence」として使う——Coreは環境時刻を
/// 読まない（§3.4）ので、このフィールドは実時間ではなく入力由来の単調な番号として扱われる、という解釈。
/// より新しいSampleSequenceのOk観測で置き換える（Confirmed）。Errorの観測は値を保ったままStaleにする
/// （ただしLastRevisedAtはそのSampleSequenceまで進める。これにより、さらに古い観測が後から届いても
/// 「古いSampleSequenceの観測は新しい信念を上書きしない」が成り立ち続ける）。同じSampleSequenceの
/// 再提示は上書きしない（厳密に新しい場合だけ採用）。
let reviseForeground
    (current: BeliefSlot<WindowId option> option)
    (observation: Observation<WindowId option>)
    : BeliefSlot<WindowId option> =
    let isNewer =
        match current with
        | None
        | Some (Unformed _) -> true
        | Some (Believed b) -> observation.SampleSequence > b.LastRevisedAt

    if not isNewer then
        current |> Option.defaultValue (Unformed [])
    else
        match observation.Value with
        | Ok v -> Believed { Value = v; Confidence = Confirmed; LastRevisedAt = observation.SampleSequence }
        | Error _ ->
            match current with
            | Some (Believed b) ->
                Believed { b with Confidence = Stale observation.SampleSequence; LastRevisedAt = observation.SampleSequence }
            | Some (Unformed gaps) -> Unformed gaps
            | None -> Unformed []

/// Workspace内のフォーカスはzipperが持つ（Core.Workspaces）。ここで持つのは
/// 「直前にフォーカスしていた窓へ戻る」専用のグローバルMRUだけで、zipperとは別の小さな構造（§4.4、R3-3）。
type FocusState = {
    Foreground: BeliefSlot<WindowId option>
    History: WindowId list
    /// フォーカスを渡すべき窓（`Stack = None`ならNone）と、待ちの発火予定時刻（deadline）の組。
    SwitchSettle: (WindowId option * int64) option
}

let initial: FocusState = { Foreground = Unformed []; History = []; SwitchSettle = None }

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
    (focusTarget, deadline), [ StartTimer (Focus WorkspaceSwitchSettle, settleMs) ]

/// SwitchSettleタイマーの発火を処理する。Topology.update/Health.onReprobeTimerFiredと同じ`awaitDeadline`
/// パターンで古い発火を無視する。期限到達時は、フォアグラウンドがまだhiddenなWorkspaceの窓なら
/// 1回だけ再要求すべき窓（保持していたfocusTarget）を返す（`Stack = None`だったならNoneのまま、再要求は無い）。
let onSwitchSettleTimerFired (now: int64) (state: FocusState) : FocusState * WindowId option * TimerCommand<TimerId> list =
    match state.SwitchSettle with
    | Some (focusTarget, deadline) when awaitDeadline (Some deadline) now ->
        { state with SwitchSettle = None }, focusTarget, [ CancelTimer (Focus WorkspaceSwitchSettle) ]
    | _ -> state, None, [] // 古い発火、または待ちが存在しない
