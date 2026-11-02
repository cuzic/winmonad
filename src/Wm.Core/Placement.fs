/// Core.Placement（ADR-019実装順14、architecture.md §4.4/§4.5）。このラウンドで実装するのは`Geometry`
/// の信念（LatestWins規則）に加え、`TargetIntegrity`（ProbeOnce規則）と`Movability`のうち
/// Pendingに依存しない遷移（発見時の楽観的既定値、TargetIntegrity probeで`Higher`が観測された場合）。
///
/// `TargetIntegrity`のProbeOnceの起点（21章で解決済み、v2.18）：窓の発見時、Shellが今回の
/// `WindowObserved`で`TargetIntegrity`を問い合わせていなければ、Coreが能動的に`Reprobe (w, KTargetIntegrity)`
/// Effectを発行して要求する。probeの結果（Ok/Error）は、次に届く`WindowObserved`の
/// `TargetIntegrity`フィールドとして戻る（§7.1「結果は部分的なWindowObservedとして戻る」）ので、
/// `onWindowObserved`が唯一の受け口になる——別の「probe応答」専用の入力は無い。
///
/// このラウンドで意図的に対象外にしたこと（§7.2「Desired/Observed/Pending」、§7.1段③④⑦）。いずれも
/// 「できればやる」ではなく、根拠（証拠の供給元）そのものが無いため実装できない項目であり、
/// 専用のラウンドへ切り出す：
/// - `Desired`/`Pending`/`NextRequestId`/`NextBatchId`/`Epochs`/`HeldFocusTarget`：§7.2のPendingの
///   状態機械（Idle→Planned→Dispatched→AwaitingObservation→Satisfied/Mismatch/Timeout/…）全体。
///   `RequestId`/`IntentEpoch`のライフサイクルは、1ラウンドで実装するには判断点が多すぎる。
/// - `MinWidth`/`DpiBehavior`（Accumulating規則）：§4.5の証拠（`SetRect`の`Mismatch`、DPI切替後の
///   2段目の自己リサイズ観測）は、いずれもPendingの産物であり、Pendingが無い今は証拠を一切供給できない。
/// - `Movability`の残りの遷移（`SetRect`の`Denied OtherDenial`→`NotMovable`、`Satisfied`→`Movable`、
///   ユーザーの`SetManagement`/`ToggleFloating`による1回だけの試し直し）も同じ理由で対象外——
///   「試し直す」の実際の意味は次のReconcileで`SetRect`を再発行することであり、Pending無しでは
///   試したかどうかを確認する手段が無い。
/// - `TargetIntegrity`のProbeOnce規則が述べる「同じProcessKeyの間は再probeしない」の明示的な検査：
///   このラウンドの実装では、確定（`Believed`）した窓を再probeする経路自体が無い（`WindowObserved`が
///   `TargetIntegrity = None`で届いても、既に`TargetIntegrity`マップにエントリがあれば何もしない）ため、
///   この規則は構造的に自動で満たされる。Windows.Identity.Processを読む明示的な相互ドメイン参照は
///   不要（HWND再利用時にWindowIdが再利用される経路はWindows.fs側で別途未対応、ファイル冒頭参照）。
module Wm.Core.Placement

open Wm.Contracts.Identifiers
open Wm.Contracts.Observation
open Wm.Contracts.Beliefs
open Wm.Contracts.Timers
open Wm.Contracts.CoreCommand

let private geometryInitial: GeometryBeliefs =
    { NativeRect = Unformed []
      VisibleFrameRect = LatestWins.unformed
      Monitor = LatestWins.unformed
      IsMinimized = LatestWins.unformed
      Presence = LatestWins.unformed }

/// `WindowSnapshot`から`GeometryBeliefs`をLatestWins規則で修正する（§4.5。`Beliefs.LatestWins.revise`を
/// 直接使う。R4-9/Opusレビュー1-1：ドメインごとに再実装しない）。`NativeRect`は修正規則が本書に
/// 明記されていないため、このラウンドでは手を付けない（`Unformed`のまま、既知のギャップ。Beliefs.fs参照）。
let reviseGeometryFromSnapshot (now: int64) (current: GeometryBeliefs) (snapshot: WindowSnapshot) : GeometryBeliefs =
    { current with
        VisibleFrameRect =
            snapshot.VisibleFrameRect |> Option.map (LatestWins.revise now current.VisibleFrameRect) |> Option.defaultValue current.VisibleFrameRect
        Monitor = snapshot.Monitor |> Option.map (LatestWins.revise now current.Monitor) |> Option.defaultValue current.Monitor
        IsMinimized =
            snapshot.IsMinimized |> Option.map (LatestWins.revise now current.IsMinimized) |> Option.defaultValue current.IsMinimized
        Presence = snapshot.Presence |> Option.map (LatestWins.revise now current.Presence) |> Option.defaultValue current.Presence }

/// `MinimizedStateChanged`（ADR-019実装順10のWm.Normalizationが既に定義していたが、`WmState.step`に
/// 配線先が無かった入力。前ラウンドでそれを塞いだ）。
let reviseMinimized (now: int64) (current: GeometryBeliefs) (obs: Observation<bool>) : GeometryBeliefs =
    { current with IsMinimized = LatestWins.revise now current.IsMinimized obs }

type PlacementState =
    { Geometry: Map<WindowId, GeometryBeliefs>
      Movability: Map<WindowId, Belief<Movability>>
      TargetIntegrity: Map<WindowId, BeliefSlot<IntegrityRelation>>
      /// `RetryState`と、次の再probeの発火予定時刻（deadline）の組（§4.5 ProbeOnce、HealthのReprobesと同じ形）。
      IntegrityReprobes: Map<WindowId, RetryState * int64> }

let initial: PlacementState =
    { Geometry = Map.empty; Movability = Map.empty; TargetIntegrity = Map.empty; IntegrityReprobes = Map.empty }

// ===== TargetIntegrityのProbeOnce（§4.5）。既定値は未確定なので21章・HealthのResponsivenessReprobeと
// 同じ桁の暫定値を使う（既定3回、指数バックオフ）。 =====

let integrityReprobeMaxAttempts = 3
let integrityReprobeBaseDelayMs = 1_000L
let integrityReprobeMaxDelayMs = 8_000L

let private scheduleIntegrityReprobe
    (now: int64)
    (w: WindowId)
    (retry: RetryState option)
    : (RetryState * int64 * TimerCommand<TimerId> list) option =
    let state0 = retry |> Option.defaultValue { AttemptCount = 0; LastAttemptAt = now }
    nextRetry integrityReprobeMaxAttempts integrityReprobeBaseDelayMs integrityReprobeMaxDelayMs now state0
    |> Option.map (fun (state', delay) -> state', now + delay, [ StartTimer (TimerId.Placement (IntegrityReprobe w), delay) ])

/// `WindowObserved`：§7.1段①「Windows→Placement→Health→Focus」のPlacement分。Windowsと同じ入力から
/// 自分の信念（Geometry/Movability/TargetIntegrity）だけを修正する（ADR-018：ドメインは自分の状態だけを
/// 返す）。`Reprobe`Effectの発行、再probeタイマーの開始・取消も、この入力の処理に含める。
let onWindowObserved (now: int64) (snapshot: WindowSnapshot) (state: PlacementState) : PlacementState * Effect list * TimerCommand<TimerId> list =
    let w = snapshot.Id
    let geometry' =
        Map.add w (reviseGeometryFromSnapshot now (Map.tryFind w state.Geometry |> Option.defaultValue geometryInitial) snapshot) state.Geometry

    // Movabilityの発見時の楽観的既定値（§4.5）。既にエントリがあれば変えない。
    let isFirstSeen = not (Map.containsKey w state.Movability)
    let movability0 =
        if isFirstSeen then
            Map.add w { Value = Movable; Confidence = Inferred []; LastRevisedAt = now } state.Movability
        else
            state.Movability

    let targetIntegrityWasKnown = Map.containsKey w state.TargetIntegrity

    let targetIntegrity', movability', integrityReprobes', effects, timerCmds =
        match snapshot.TargetIntegrity with
        | Some obs ->
            match obs.Value with
            | Ok v ->
                let ti' = Map.add w (Believed { Value = v; Confidence = Confirmed; LastRevisedAt = now }) state.TargetIntegrity
                let wasReprobing = Map.containsKey w state.IntegrityReprobes
                let cancelCmd = if wasReprobing then [ CancelTimer (TimerId.Placement (IntegrityReprobe w)) ] else []
                // TargetIntegrityの観測イベントでHigherが得られたその時に限りNotMovableへ修正する（§4.5）。
                let movability'' =
                    if v = Higher then
                        Map.add w { Value = NotMovable; Confidence = Inferred [ Sample obs.SampleSequence ]; LastRevisedAt = now } movability0
                    else
                        movability0
                ti', movability'', Map.remove w state.IntegrityReprobes, [], cancelCmd
            | Error _ ->
                let ti' =
                    if targetIntegrityWasKnown then
                        state.TargetIntegrity
                    else
                        Map.add w (Unformed [ MissingTargetIntegrity w ]) state.TargetIntegrity
                match scheduleIntegrityReprobe now w (Map.tryFind w state.IntegrityReprobes |> Option.map fst) with
                | Some (retry', deadline, cmds) -> ti', movability0, Map.add w (retry', deadline) state.IntegrityReprobes, [], cmds
                | None -> ti', movability0, Map.remove w state.IntegrityReprobes, [], [] // 上限到達、打ち切り
        | None ->
            if targetIntegrityWasKnown then
                state.TargetIntegrity, movability0, state.IntegrityReprobes, [], []
            else
                // 発見時、Shellが今回問い合わせていない：Coreが能動的にprobeを要求する（21章で解決済み）。
                let ti' = Map.add w (Unformed [ MissingTargetIntegrity w ]) state.TargetIntegrity
                ti', movability0, state.IntegrityReprobes, [ Reprobe (w, KTargetIntegrity) ], []

    { state with
        Geometry = geometry'
        Movability = movability'
        TargetIntegrity = targetIntegrity'
        IntegrityReprobes = integrityReprobes' },
    effects,
    timerCmds

let onMinimizedStateChanged (now: int64) (w: WindowId) (obs: Observation<bool>) (state: PlacementState) : PlacementState =
    { state with Geometry = Map.add w (reviseMinimized now (Map.tryFind w state.Geometry |> Option.defaultValue geometryInitial) obs) state.Geometry }

/// `TimerElapsed (Placement (IntegrityReprobe w))`：`Reprobe`Effectを再発行し、次の試行を予約する
/// （TopologyやHealthの再probeと同じ`awaitDeadline`パターンで古い発火を無視する）。
let onIntegrityReprobeTimerFired (now: int64) (w: WindowId) (state: PlacementState) : PlacementState * Effect list * TimerCommand<TimerId> list =
    match Map.tryFind w state.IntegrityReprobes with
    | Some (retry, deadline) when awaitDeadline (Some deadline) now ->
        match scheduleIntegrityReprobe now w (Some retry) with
        | Some (retry', deadline', cmds) ->
            { state with IntegrityReprobes = Map.add w (retry', deadline') state.IntegrityReprobes }, [ Reprobe (w, KTargetIntegrity) ], cmds
        | None -> { state with IntegrityReprobes = Map.remove w state.IntegrityReprobes }, [], [] // 上限到達、打ち切り
    | _ -> state, [], [] // 古い発火、またはもう追跡していない

/// 窓の消滅時、`Geometry`/`Movability`/`TargetIntegrity`/`IntegrityReprobes`を除く（§4.4「各ドメインの
/// 信念は窓が消滅した時点でのみ除去する」）。動いている再probeタイマーがあれば`CancelTimer`を返す
/// （review-phase-a-round1.md 4-3と同じ理由、Windows.ClassificationDeadline/Health.Reprobesと同じ扱い）。
/// `WmState.vanishWindow`から呼ぶ。
let onWindowVanished (w: WindowId) (state: PlacementState) : PlacementState * TimerCommand<TimerId> list =
    let cancelCmd = if Map.containsKey w state.IntegrityReprobes then [ CancelTimer (TimerId.Placement (IntegrityReprobe w)) ] else []
    { state with
        Geometry = Map.remove w state.Geometry
        Movability = Map.remove w state.Movability
        TargetIntegrity = Map.remove w state.TargetIntegrity
        IntegrityReprobes = Map.remove w state.IntegrityReprobes },
    cancelCmd
