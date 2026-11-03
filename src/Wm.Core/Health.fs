/// Core.Health（ADR-019実装順14、architecture.md §4.4/§4.5）。応答性（ハング）の信念と再probe。
module Wm.Core.Health

open Wm.Contracts.Identifiers
open Wm.Contracts.ErrorTypes
open Wm.Contracts.Observation
open Wm.Contracts.Beliefs
open Wm.Contracts.Timers
open Wm.Contracts.CoreCommand

/// Responsivenessの信念を修正する証拠（§4.5）。通常の観測（Reprobe/事前確認の結果）と、
/// Effect呼出が`TargetHung`に分類された場合の両方から来る。
type ResponsivenessEvidence =
    | FromObservation of ResponsivenessSample
    | FromEffectFailure of EffectFailure

/// ハング判定済みとみなす閾値（ElapsedUsがこれ以下なら「OSが既にハング判定済み」、§7.1 F06/F12）。
/// settleMs/graceMsと同様、実測確認はPhase 0〜Bで行う暫定値。
let judgedHungThresholdUs = 1_000L

/// Accumulating規則（§4.5）。「Mapに無い」は「まだ証拠が無い」の意味であり（R6-12）、
/// 決定的でない証拠から根拠の無い`Confirmed`を新たに作ってMapへ書き込んではならない（Opusレビュー5-5：
/// 旧実装は証拠が無いときに`respondingDefault`を書き込んでいたため、Explainが「ハングしていないことを
/// 確認済み」と誤って説明していた）。信念が無い窓をRespondingとして扱うのは、呼び出し側が
/// `Map.tryFind`の結果が`None`であることをそのまま読む（R4-24）——この関数は`None`を返して
/// 「Mapへ書かない」ことで実現する。
let reviseResponsiveness
    (now: int64)
    (current: Belief<Responsiveness> option)
    (evidence: ResponsivenessEvidence)
    : Belief<Responsiveness> option =
    match evidence with
    | FromEffectFailure TargetHung -> Some { Value = Hung; Confidence = Confirmed; LastRevisedAt = now }
    | FromEffectFailure _ -> current // 決定的な証拠ではない:既存の信念を保つ（無ければMapに書かない）
    | FromObservation PumpResponded -> Some { Value = Responding; Confidence = Confirmed; LastRevisedAt = now }
    | FromObservation (PumpTimedOut elapsedUs) when elapsedUs <= judgedHungThresholdUs ->
        Some { Value = Hung; Confidence = Confirmed; LastRevisedAt = now }
    | FromObservation (PumpTimedOut _) ->
        // 判定前のタイムアウト満了（まだハング判定中）はHungの決定的な証拠にならない（Transientに相当）。
        current
    | FromObservation IsHungAppWindowTrue ->
        // 補助的証拠であり、単独では決してConfirmedにしない（ゴーストウィンドウは常にTRUEを返すため）。
        // 既存の信念（HungでもRespondingでも）があればそれを保つ。無ければ、この証拠だけを根拠に
        // Inferredで事前の仮定を作ってよい（Explainの材料としては、本来はこの観測を運んだ入力の
        // SampleSequenceを`Inferred [Sample seq]`として積むべきだが、現状のResponsivenessEvidenceは
        // Observation全体ではなくResponsivenessSampleだけを運ぶため情報が無く、`Inferred []`で近似する
        // ——既知の小さなギャップ。Explainの根拠一覧が薄くなるだけで、正しさには影響しない）。
        match current with
        | Some b -> Some b
        | None -> Some { Value = Hung; Confidence = Inferred []; LastRevisedAt = now }

// ===== 再probe（指数バックオフ、§4.5） =====

let reprobeMaxAttempts = 20
let reprobeBaseDelayMs = 1_000L
let reprobeMaxDelayMs = 30_000L

/// Hungと確定した直後、またはReprobeタイマーが発火してまだHungのままの場合に呼ぶ。
/// 上限回数に達していたらNone（再probeを打ち切る。ForegroundObservedかユーザー操作で再開する、R4-25）。
let scheduleReprobe (now: int64) (window: WindowId) (retry: RetryState option) : (RetryState * int64 * TimerCommand<TimerId> list) option =
    let state0 = retry |> Option.defaultValue { AttemptCount = 0; LastAttemptAt = now }
    nextRetry reprobeMaxAttempts reprobeBaseDelayMs reprobeMaxDelayMs now state0
    |> Option.map (fun (state', delay) ->
        state', now + delay, [ StartTimer (TimerId.Health (ResponsivenessReprobe window), delay) ])

/// Responsivenessの信念と、Hungの間の再probe待ち（§4.5）。
type HealthState = {
    Responsiveness: Map<WindowId, Belief<Responsiveness>>
    /// RetryStateと、次の再probeの発火予定時刻（deadline）の組。
    Reprobes: Map<WindowId, RetryState * int64>
}

let initial: HealthState = { Responsiveness = Map.empty; Reprobes = Map.empty }

/// 1件の証拠を適用する。新たにHungになったら再probeを開始し、HungからRespondingへ回復したら
/// 再probeを打ち切る（CancelTimer + Reprobesから除去）。それ以外は信念だけを更新する。
/// **v2.84で`Effect list`を追加**：Hungになった直後に`Reprobe (w, KResponsiveness)`を発行する
/// （本文「`Reprobe`（7.1）で`SendMessageTimeout(WM_NULL)`の再確認を要求する」）。それまで0%実装
/// だった既知のギャップ（型とタイマーのスケジューリングはあったが、実際にOSへ再確認を要求する
/// Effectを一度も発行していなかった）——`Placement.onIntegrityReprobeTimerFired`の`Reprobe (w,
/// KTargetIntegrity)`と同じ形。
let applyEvidence
    (now: int64)
    (window: WindowId)
    (evidence: ResponsivenessEvidence)
    (state: HealthState)
    : HealthState * Effect list * TimerCommand<TimerId> list =
    let before = Map.tryFind window state.Responsiveness
    match reviseResponsiveness now before evidence with
    | None -> state, [], [] // 決定的な証拠が無く、既存の信念も無い:Mapに何も書かない（5-5）
    | Some revised ->
        let responsiveness' = Map.add window revised state.Responsiveness
        let prevValue = before |> Option.map (fun b -> b.Value)
        let becameHung = revised.Value = Hung && prevValue <> Some Hung
        let recoveredFromHung = revised.Value = Responding && prevValue = Some Hung

        if becameHung then
            match scheduleReprobe now window None with
            | Some (retry', deadline, cmds) ->
                { state with
                    Responsiveness = responsiveness'
                    Reprobes = Map.add window (retry', deadline) state.Reprobes },
                [ Reprobe (window, KResponsiveness) ],
                cmds
            | None ->
                // AttemptCount=0から始めるため理論上到達しないが、全域関数として定義する（§7.1「例外を制御フローに使わない」）。
                { state with Responsiveness = responsiveness' }, [], []
        elif recoveredFromHung then
            { state with
                Responsiveness = responsiveness'
                Reprobes = Map.remove window state.Reprobes },
            [],
            [ CancelTimer (TimerId.Health (ResponsivenessReprobe window)) ]
        else
            { state with Responsiveness = responsiveness' }, [], []

/// ResponsivenessReprobeタイマーの発火を処理する。Topology.updateと同じくawaitDeadlineで古い発火を無視する。
/// **v2.84で`Effect list`を追加**：`applyEvidence`と同じ理由で、バックオフの各再試行ごとに
/// `Reprobe (w, KResponsiveness)`を発行する。
let onReprobeTimerFired (now: int64) (window: WindowId) (state: HealthState) : HealthState * Effect list * TimerCommand<TimerId> list =
    match Map.tryFind window state.Responsiveness with
    | Some b when b.Value = Hung ->
        match Map.tryFind window state.Reprobes with
        | Some (retry, deadline) when awaitDeadline (Some deadline) now ->
            match scheduleReprobe now window (Some retry) with
            | Some (retry', deadline', cmds) ->
                { state with Reprobes = Map.add window (retry', deadline') state.Reprobes }, [ Reprobe (window, KResponsiveness) ], cmds
            | None -> { state with Reprobes = Map.remove window state.Reprobes }, [], [] // 上限到達、打ち切り
        | _ -> state, [], [] // 古い発火、またはもう追跡していない
    | _ -> state, [], [] // 既にHungではない（回復済み）。無害
