/// Core.Placement（ADR-019実装順14、architecture.md §4.4/§4.5/§6.1/§7.1段⑦/§7.2）。
/// `Geometry`（LatestWins）、`TargetIntegrity`（ProbeOnce）、`Movability`のPending非依存の遷移に加え、
/// このラウンドでは§7.2のPending/Desiredの最小の往復（Reconcile→SetRect→Satisfied/Timeout/GateRejected）
/// を実装する。
///
/// `TargetIntegrity`のProbeOnceの起点（21章で解決済み、v2.18）：窓の発見時、Shellが今回の
/// `WindowObserved`で`TargetIntegrity`を問い合わせていなければ、Coreが能動的に`Reprobe (w, KTargetIntegrity)`
/// Effectを発行して要求する。probeの結果（Ok/Error）は、次に届く`WindowObserved`の
/// `TargetIntegrity`フィールドとして戻る（§7.1「結果は部分的なWindowObservedとして戻る」）ので、
/// `onWindowObserved`が唯一の受け口になる——別の「probe応答」専用の入力は無い。
///
/// `Pending`/`Desired`のラウンド1実装（v2.20）の範囲：`reconcileWorkspace`（§7.1段⑦。Layout呼出→
/// `PlanValidator`→差分だけの`SetRect`発行）、`checkPendingSatisfied`（`WindowObserved`のたびに
/// `Geometry.VisibleFrameRect`とPendingの`TargetRect`を照合し`Satisfied`）、
/// `onPendingDeadlineTimerFired`（`Timeout`→単発の諦め）、`onGateRejected`（`GateRejected`→即時`Cancelled`）。
/// `PendingRequest`に`RequestId`（Mapキー）/`Retry`/`Status`を持たせていない理由は型の直前のコメント参照。
///
/// `EffectCallReturned`（v2.22追加）：Wm.Simulationが`GatedEffect`を実際に適用し`EffectCallReturned`を
/// 返せるようになったので配線した。`onEffectCallReturned`は`classify`の結果に応じて`Cancelled`にし、
/// `Denied OtherDenial`なら`Movability`を`NotMovable`へ修正する。`TargetHung`時の`Health.Responsiveness`
/// 修正はPlacementの管轄外（Health.fsの責務）なので、呼び出し側（`WmState.step`）が
/// `onEffectCallReturned`の戻り値（`(WindowId * EffectFailure) option`）を見て行う。`Unplaced`への
/// 読み替え（`Movability = NotMovable`→Core.Windowsが`EffUnplaced`を導く）はv2.23で閉じた
/// （Windows.fsのファイル冒頭コメント参照）。
///
/// `Movability`の`Satisfied`→`Movable`遷移（v2.24追加）：`checkPendingSatisfied`が一致を検出した
/// その場で`Movable`（`Confirmed`）へ確定する（§4.5。「試したかどうか」の判定は不要——この遷移は
/// 無条件に「配置できた」という直接の証拠であり、既にMovableの窓に対しても確度を`Inferred []`から
/// `Confirmed`へ進める意味を持つ）。
///
/// 複数の可視Workspaceへの拡張（v2.26追加）：`reconcileWorkspace`自体はこのラウンドより前から
/// 可視Workspace1つにつき1回呼ぶ設計だったが、呼び出し側（`WmState.step`）はアクティブな
/// Workspaceだけに適用していた。v2.26でこの制限を外し、現在表示中の全てのWorkspace（他モニターに
/// 表示中の可視Workspaceも含む）を順にReconcileするようになった（呼び出し側のコメント参照）。
///
/// `PendingDeadline`のRetry（v2.28追加）：`onPendingDeadlineTimerFired`が、上限（既定3回、
/// `pendingRetryMaxAttempts`）に達するまで、同じ`TargetRect`/`Epoch`へ新しい`RequestId`で`SetRect`を
/// 即座に再発行する（§7.2「Timeout → Retry/Backoff」のRetry部分）。Backoff（試行間隔を指数的に空ける）
/// は依然対象外：`IntegrityReprobe`の間隔は「外部プロセスを急かさない」という明確な理由があったが、
/// `SetRect`の再試行に同じ理由は無く、適切な遅延量をPhase 0の実測無しに決める根拠が無いため、
/// 即座に再試行し試行回数だけを数える単純な形にする（`RetryAttempts`の型直前のコメント参照）。
/// v2.28時点では`onEffectCallReturned`側（`Transient`/`Unclassified`を含む）は単発の諦めのままだった
/// ——Retryの対象をTimeoutだけに絞ったのは、`classify`の各分岐ごとに「リトライが安全か」を個別に
/// 検討する必要があったため。
///
/// `TopologySettled` → `Superseded`（v2.29追加）：`onTopologySettled`が、モニター構成が確定した
/// （§7.3のScreenSettle→Grace）瞬間に既存の全Pendingを観測を待たず`CancelTimer`付きで一括取り消す
/// （古いモニター配置に基づく目標なので当てにできない）。呼び出し側（`WmState.step`）が同じstepで
/// 必ずReconcileも走らせる（`shouldReconcile`を拡張した）ので、取り消した代わりが即座に発行し直される
/// （窓のモニターが変わらなければ`dispatchPlacements`のdesiredChanged判定により無駄な再発行はしない）。
///
/// `onEffectCallReturned`の`Transient`分類にもRetryを拡張（v2.30追加）：`classify`の各分岐を個別に
/// 検討した結果——`TargetGone`（ハンドル無効、再試行しても同じ結果）、`Denied`（拒否は持続的）、
/// `TargetHung`（Healthの管轄）はいずれも再試行の根拠が無いので単発の諦めのまま、`Unclassified`
/// （未知の失敗）も当てなく再試行するのは危険なので対象外のまま——`Transient`（ERROR_TIMEOUTのうち
/// ハング判定前のもの）だけは「一時的」という性質自体がリトライの根拠になるため、`retryOrGiveUp`
/// （`onPendingDeadlineTimerFired`と共有）で即座に再試行するようにした。この変更で`onEffectCallReturned`
/// がTimerCommandを返せるようになったので、`rid`自身の`PendingDeadline`タイマー（まだ生きている状態で
/// 呼ばれるため、諦める場合もCancelTimerが必要）という既存の取りこぼしも同時に塞いだ。
///

/// このラウンドで意図的に対象外にしたこと（いずれも「できればやる」ではなく、別の未実装の仕組みが
/// 前提になっているため実装できない項目であり、専用のラウンドへ切り出す）：
/// - **`PendingDeadline`のBackoff**（§7.2「Timeout → Retry/Backoff」のBackoff部分、§7.3）：Retry自体は
///   v2.28で実装済み（上記）。試行間隔を空ける理由・適切な遅延量はPhase 0の実測が必要。
/// - **`onEffectCallReturned`の`Unclassified`のRetry**：`Transient`はv2.30で対象にしたが（上記）、
///   `Unclassified`（未知の失敗）を当てなく再試行するのは危険なため対象外のまま。
/// - **`Mismatch` → `Reevaluate`**：観測が`TargetRect`と一致しない場合、`checkPendingSatisfied`は
///   何もしない（Pendingはそのまま残り、`PendingDeadline`満了か次のReconcileに委ねる）。即座に
///   `Desired`を取り消して再試行させる実装も検討したが、`SetRect`完了前に届く無関係な観測
///   （他の属性変化による全体Snapshot等）が、まだ反映されていない古い矩形を運んで「不一致」と
///   誤判定し、実際には進行中のPendingを早まって取り消してしまう危険がある（`IntentEpoch`照合や
///   観測の鮮度判定が無いこのラウンドでは安全に区別できない）。安全側に倒して対象外のままにする。
/// - **ユーザー操作による1回だけの試し直し**：ユーザーコマンド経由でPlacementへの新しい入口が要る
///   ——専用のラウンドへ切り出す。
/// - **`MinWidth`/`DpiBehavior`（Accumulating規則）**：証拠（`SetRect`の`Mismatch`、DPI切替後の
///   2段目観測）は収集できるようになったが、信念として蓄積する修正関数はまだ書いていない。
/// - **`TargetIntegrity`のProbeOnce規則が述べる「同じProcessKeyの間は再probeしない」の明示的な検査**：
///   このラウンドの実装では、確定（`Believed`）した窓を再probeする経路自体が無い（`WindowObserved`が
///   `TargetIntegrity = None`で届いても、既に`TargetIntegrity`マップにエントリがあれば何もしない）ため、
///   この規則は構造的に自動で満たされる。Windows.Identity.Processを読む明示的な相互ドメイン参照は不要。
module Wm.Core.Placement

open Wm.Contracts.Identifiers
open Wm.Contracts.ErrorTypes
open Wm.Contracts.Observation
open Wm.Contracts.Beliefs
open Wm.Contracts.Timers
open Wm.Contracts.CoreCommand
open Wm.Contracts.SharedVocabulary
open Wm.Layouts.LayoutRegistry
open Wm.Layouts.PlanValidator

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

/// §4.4「WMが見せているべきか。非表示の実装方式に依存しない」。このラウンドでは`Shown (Some rect)`
/// しか作らない（`HiddenByWm`/`Shown None`はWorkspaceの表示状態の反映が必要で、まだ対象外）。
type DesiredPresence =
    | Shown of PixelRect option
    | HiddenByWm

/// §7.2「PendingRequestは最低限、RequestId/WindowId/IntentEpoch/TargetRect/IssuedAt/Deadline/Retry/
/// Statusを持つ」のうち、このラウンドで実装する部分。`RequestId`はPendingのMapキーなのでフィールドに
/// しない。`Retry`/`Status`は省く——このラウンドはTimeout時に1回だけ諦める単発の待ちであり、
/// リトライ・バックオフ（§7.2「Retry/Backoff」）は実装しない（ファイル冒頭の対象外リスト参照）。
/// `Status`（Idle/Planned/Dispatched/AwaitingObservation/…）も実装しない：このラウンドでは
/// 「Pendingマップに存在する＝AwaitingObservation、存在しない＝終端状態」という、HealthのReprobes/
/// WindowsのAwaitingEvidenceと同じ「存在で状態を表す」規約で足りる。
type PendingRequest = { Window: WindowId; Epoch: IntentEpoch; TargetRect: PixelRect; IssuedAt: int64; Deadline: int64 }

type PlacementState =
    { Geometry: Map<WindowId, GeometryBeliefs>
      Movability: Map<WindowId, Belief<Movability>>
      TargetIntegrity: Map<WindowId, BeliefSlot<IntegrityRelation>>
      /// `RetryState`と、次の再probeの発火予定時刻（deadline）の組（§4.5 ProbeOnce、HealthのReprobesと同じ形）。
      IntegrityReprobes: Map<WindowId, RetryState * int64>
      /// §7.1段⑦「Reconcile」が計算した、窓ごとの意図。`SetRect`を実際に発行したときだけ更新する
      /// （Reconcileが「前回と同じ意図」と判断する基準。§7.3「未完了の同一Targetへの重複要求を発行しない」）。
      Desired: Map<WindowId, DesiredPresence>
      Pending: Map<RequestId, PendingRequest>
      /// `RequestId`の単一の採番元（§7.1「`EffectCallReturned`の`RequestId`で相関する」ので、採番元は
      /// プロセス全体で1つでなければならない）。物理的にここに置くのは実装の都合——意味的には
      /// `WmState`全体の資源であり、`WmState.step`は`allocateRequestId`（下記）経由で、Placement以外の
      /// 理由（Focus.SwitchSettleの再要求、`MoveFocusedToWorkspace`のRequestForeground等、v2.27）でも
      /// ここから新しいRequestIdを発行できる。
      NextRequestId: int64
      /// 窓ごとのIntentEpoch（§7.2「全体で単一のEpochにすると、1窓の新しい意図が他の全窓のPendingを
      /// 失効させてしまう」）。新しい`SetRect`を発行するたびに1つ進める。
      Epochs: Map<WindowId, IntentEpoch>
      /// §7.2「Timeout → Retry/Backoff」のうち、このラウンド（v2.28）で実装するRetryの試行回数
      /// （同じ`TargetRect`への再試行のみ。新しい意図（`dispatchPlacements`が`Desired`の変化を見て
      /// 新しい`SetRect`を発行するとき）やSatisfied/GateRejected/vanishで0へ戻す）。Backoff（試行間隔を
      /// 指数的に空ける`IntegrityReprobe`と同じ仕組み）は対象外のまま（下記）：`SetRect`の再試行には
      /// `IntegrityReprobe`のような「外部プロセスを急かさないための間隔」という理由が無く、適切な遅延量を
      /// Phase 0の実測無しに決める根拠が無いため、即座に再試行し試行回数だけを数える単純な形にする。
      RetryAttempts: Map<WindowId, int> }

let initial: PlacementState =
    { Geometry = Map.empty
      Movability = Map.empty
      TargetIntegrity = Map.empty
      IntegrityReprobes = Map.empty
      Desired = Map.empty
      Pending = Map.empty
      NextRequestId = 0L
      Epochs = Map.empty
      RetryAttempts = Map.empty }

/// `WmState.step`が、Placement自身のReconcile以外の理由（Focus.SwitchSettleの再要求、
/// `MoveFocusedToWorkspace`のRequestForeground等、v2.27）で新しいRequestIdが要るときに呼ぶ最小の窓口。
/// `dispatchPlacements`と同じ採番元を共有するので、`EffectCallReturned`の相関が重複することはない
/// （NextRequestIdの型直前のコメント参照）。
let allocateRequestId (state: PlacementState) : RequestId * PlacementState =
    RequestId state.NextRequestId, { state with NextRequestId = state.NextRequestId + 1L }

/// §7.2「PendingのDeadline」の既定値。実測未確定のため暫定値（他のタイムアウトと同様）。
let pendingDeadlineMs = 2_000L

/// §7.2「Timeout → Retry/Backoff」のRetryの上限試行回数（`integrityReprobeMaxAttempts`と同じ桁の
/// 暫定値。`PlacementState.RetryAttempts`の型直前のコメント参照）。
let pendingRetryMaxAttempts = 3

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

/// §7.2のPending→Satisfied遷移：窓`w`の現在の`Geometry.VisibleFrameRect`と、`w`宛てのPendingの
/// `TargetRect`が一致すれば、そのPendingを取り除き待ちのタイマーを取り消す。複数のPendingが同じ窓を
/// 指すことは無い（`dispatchPlacements`が新しい意図を発行する際に古いPendingを必ず失効させるため、下記）。
/// §4.5「`SetRect`の目標矩形が観測されたら（7.2のSatisfied）`Movable`（`Confirmed`）」も同じ場所で行う
/// （v2.24追加）——発見時の楽観的既定値（`Inferred []`）を、実際に配置できたという直接の証拠で確定へ進める。
let private checkPendingSatisfied (now: int64) (w: WindowId) (state: PlacementState) : PlacementState * TimerCommand<TimerId> list =
    let observedRect = Map.tryFind w state.Geometry |> Option.bind (fun g -> LatestWins.value g.VisibleFrameRect)
    match observedRect with
    | None -> state, []
    | Some rect ->
        match state.Pending |> Map.toSeq |> Seq.tryFind (fun (_, pr) -> pr.Window = w && pr.TargetRect = rect) with
        | Some (rid, _) ->
            { state with
                Pending = Map.remove rid state.Pending
                Movability = Map.add w { Value = Movable; Confidence = Confirmed; LastRevisedAt = now } state.Movability
                RetryAttempts = Map.remove w state.RetryAttempts },
            [ CancelTimer (TimerId.Placement (PendingDeadline rid)) ]
        | None -> state, []

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

    let stateAfterBeliefs =
        { state with
            Geometry = geometry'
            Movability = movability'
            TargetIntegrity = targetIntegrity'
            IntegrityReprobes = integrityReprobes' }

    // §7.2「SetRectのネイティブAPI呼出が成功しても、目標矩形を観測するまではSatisfiedにしない」：
    // ここでGeometryが今まさに更新されたVisibleFrameRectと、窓宛てのPendingのTargetRectを照合する。
    // Mismatch（§7.2の`Reevaluate`）はこのラウンドでは明示的に扱わず、一致しなければ次のPendingDeadline
    // タイマーの発火（またはこの先の再Reconcile）に委ねる（ファイル冒頭「対象外」参照）。
    let pendingState, pendingTimerCmds = checkPendingSatisfied now w stateAfterBeliefs
    pendingState, effects, timerCmds @ pendingTimerCmds

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

// ===== §7.1段⑦「Reconcile」：Layout呼び出し→PlanValidator→差分のSetRect発行 =====

/// `ValidatedPlan`の各配置について、`Desired`と異なる場合だけ`SetRect`を発行し新しいPendingを作る
/// （§7.3「未完了の同一Targetへの重複要求を発行しない」）。既存のPendingが同じ窓を指していれば、
/// 新しい意図（Epochを1つ進める）がそれを失効させる（§7.2「同じ窓へ新しい意図が生じた場合、古い要求を
/// 失効させる」「Superseded intent → Superseded」）。
let private dispatchPlacements (now: int64) (validated: ValidatedPlan) (state: PlacementState) : PlacementState * Effect list * TimerCommand<TimerId> list =
    ValidatedPlan.placements validated
    |> List.fold
        (fun (st: PlacementState, effects, cmds) (p: Placement) ->
            let desiredChanged =
                match Map.tryFind p.Window st.Desired with
                | Some (Shown (Some r)) -> r <> p.Target
                | _ -> true
            if not desiredChanged then
                st, effects, cmds
            else
                let supersededCmds =
                    st.Pending
                    |> Map.toSeq
                    |> Seq.filter (fun (_, pr) -> pr.Window = p.Window)
                    |> Seq.map (fun (rid, _) -> CancelTimer (TimerId.Placement (PendingDeadline rid)))
                    |> List.ofSeq
                let pendingWithoutOld = st.Pending |> Map.filter (fun _ pr -> pr.Window <> p.Window)
                let epoch' =
                    match Map.tryFind p.Window st.Epochs with
                    | Some (IntentEpoch e) -> IntentEpoch (e + 1L)
                    | None -> IntentEpoch 0L
                let rid = RequestId st.NextRequestId
                let deadline = now + pendingDeadlineMs
                let pendingRequest = { Window = p.Window; Epoch = epoch'; TargetRect = p.Target; IssuedAt = now; Deadline = deadline }
                let st' =
                    { st with
                        Desired = Map.add p.Window (Shown (Some p.Target)) st.Desired
                        Pending = Map.add rid pendingRequest pendingWithoutOld
                        NextRequestId = st.NextRequestId + 1L
                        Epochs = Map.add p.Window epoch' st.Epochs
                        // 新しい意図（Reconcileが計算した新しいTarget）なので、古いTargetへのRetryの
                        // 試行回数は引き継がない（`RetryAttempts`はPendingDeadlineの再試行専用、型直前の
                        // コメント参照）。
                        RetryAttempts = Map.remove p.Window st.RetryAttempts }
                st', effects @ [ SetRect (rid, p.Window, p.Target) ], cmds @ supersededCmds @ [ StartTimer (TimerId.Placement (PendingDeadline rid), pendingDeadlineMs) ])
        (state, [], [])

/// 登録済みのLayoutを呼び、PlanValidatorで検証し、差分だけを`SetRect`として発行する。`ctx`/`management`/
/// `alive`は呼び出し側（`WmState.step`）がこのstepの状態から組み立てる読み取り専用の投影であり
/// （`Windows.ClassificationCtx`と同じ位置づけ、ADR-019「ドメインは互いの型を参照しない」）、
/// PlacementはWorkspaces/Windowsの型を一切知らない。可視Workspace1つにつき1回呼ぶ想定（v2.26で
/// アクティブなWorkspaceだけという制限を外し、現在表示中の全Workspaceについて呼ぶようになった）。
/// `management`/`alive`は呼び出し側がこのWorkspaceに属する窓だけへ絞り込んで渡す必要がある
/// （PlanValidator.checkNoMissingPlacementが「渡されたTiled×Aliveな窓は全てこの`placements`に
/// 現れる」ことを要求するため——グローバルな集合をそのまま渡すと、他のWorkspaceの窓が「この計画に
/// 無い」という誤検知でRejectWholePlanになる。呼び出し側のコメント参照）。
let reconcileWorkspace
    (now: int64)
    (registry: Registry)
    (layoutRef: LayoutRef)
    (ctx: LayoutContext)
    (management: Map<WindowId, EffectiveManagement>)
    (alive: Set<WindowId>)
    (state: PlacementState)
    : PlacementState * Effect list * TimerCommand<TimerId> list =
    match Registry.resolve layoutRef.Id layoutRef.ApiVersion registry with
    | None -> state, [], [] // 未登録のLayout（Policy/Registryの構成ミス）。既知のギャップ、診断は未実装
    | Some impl ->
        let attempt (c: LayoutContext) : Result<ValidatedPlan, ValidationError list> option =
            match invokeLayout impl layoutRef.Args c with
            | Error _ -> None // LayoutError。既知のギャップ、Explain未実装
            | Ok placements -> Some (validate { Context = c; Management = management; Alive = alive } placements)
        match attempt ctx with
        | None -> state, [], []
        | Some (Ok validated) -> dispatchPlacements now validated state
        | Some (Error errors) ->
            match decide errors with
            | RejectWholePlan -> state, [], []
            | RelayoutWithout targets ->
                // 検証時点で死亡/Excluded化していた窓を除いて1回だけ再計算する（PlanValidator.fsのdecide参照）。
                let ctx' = { ctx with TiledWindows = ctx.TiledWindows |> List.filter (fun w -> not (Set.contains w.Id targets)) }
                match attempt ctx' with
                | Some (Ok validated) -> dispatchPlacements now validated state
                | _ -> state, [], [] // 再計算してもダメなら諦める(既知のギャップ、Explain未実装)

/// `Timeout`（`onPendingDeadlineTimerFired`）と`Transient`な`EffectCallReturned`（下記、v2.30）の両方が
/// 使う、同じ`TargetRect`/`Epoch`への即座の再試行（§7.2「Timeout → Retry/Backoff」のRetry部分、
/// Backoffは`RetryAttempts`の型直前のコメントの理由により対象外）。上限（既定3回）に達したら単発の
/// 諦め（`Desired`も取り消すので、次にこの窓を含むReconcileが走れば最初から再試行される）。
/// `rid`自身の`PendingDeadline`タイマーの後始末（`CancelTimer`）は呼び出し側の責務——
/// `onPendingDeadlineTimerFired`はタイマーが発火した直後で既に表から消えているため不要だが、
/// `onEffectCallReturned`はタイマーがまだ生きている状態で呼ばれるため必要、という違いがあるため。
let private retryOrGiveUp (now: int64) (rid: RequestId) (pr: PendingRequest) (state: PlacementState) : PlacementState * Effect list * TimerCommand<TimerId> list =
    let attemptsSoFar = Map.tryFind pr.Window state.RetryAttempts |> Option.defaultValue 0
    if attemptsSoFar + 1 >= pendingRetryMaxAttempts then
        { state with
            Pending = Map.remove rid state.Pending
            Desired = Map.remove pr.Window state.Desired
            RetryAttempts = Map.remove pr.Window state.RetryAttempts },
        [], []
    else
        let newRid = RequestId state.NextRequestId
        let deadline = now + pendingDeadlineMs
        let retried = { pr with IssuedAt = now; Deadline = deadline }
        { state with
            Pending = Map.add newRid retried (Map.remove rid state.Pending)
            NextRequestId = state.NextRequestId + 1L
            RetryAttempts = Map.add pr.Window (attemptsSoFar + 1) state.RetryAttempts },
        [ SetRect (newRid, pr.Window, pr.TargetRect) ],
        [ StartTimer (TimerId.Placement (PendingDeadline newRid), pendingDeadlineMs) ]

/// `TimerElapsed (Placement (PendingDeadline rid))`：§7.2「Timeout → Retry/Backoff、上限後は配置対象外」
/// （v2.28）。`retryOrGiveUp`参照。
let onPendingDeadlineTimerFired (now: int64) (rid: RequestId) (state: PlacementState) : PlacementState * Effect list * TimerCommand<TimerId> list =
    match Map.tryFind rid state.Pending with
    | Some pr when awaitDeadline (Some pr.Deadline) now -> retryOrGiveUp now rid pr state
    | _ -> state, [], []

/// §7.2「GateRejected → Cancelled（Deadlineまで待たず同じstep内で即時に遷移する）」。
/// `GatedEffect.gate`が返す拒否リストのうち、このstepでPlacementが発行したPendingに対応する
/// RequestIdだけが対象になる（他ドメインのRequestIdは`Map.tryFind`が`None`を返すだけで無害）。
let onGateRejected (rejectedRequestIds: RequestId list) (state: PlacementState) : PlacementState * TimerCommand<TimerId> list =
    rejectedRequestIds
    |> List.fold
        (fun (st: PlacementState, cmds) rid ->
            match Map.tryFind rid st.Pending with
            | Some pr ->
                { st with
                    Pending = Map.remove rid st.Pending
                    Desired = Map.remove pr.Window st.Desired
                    RetryAttempts = Map.remove pr.Window st.RetryAttempts },
                cmds @ [ CancelTimer (TimerId.Placement (PendingDeadline rid)) ]
            | None -> st, cmds)
        (state, [])

/// `EffectCallReturned`：§7.2のPending終端遷移のうち、Coreが直接判断できる部分（ファイル冒頭コメント
/// 参照）。`Ok ()`は「受付が成功した」だけを意味し、`Satisfied`にするのは観測の一致だけなので
/// （§7.2「SetRectのネイティブAPI呼出が成功しても、目標矩形を観測するまではSatisfiedにしない」）、
/// 何もしない。`Error`は`classify`で`EffectFailure`へ変換する。`Transient`だけは`retryOrGiveUp`で
/// 即座に再試行する（v2.30。ERROR_TIMEOUTのうちハング判定前のもので、「名前の通り一時的」という
/// 性質そのものがリトライの根拠になる、ファイル冒頭「このラウンドで意図的に対象外にしたこと」参照）。
/// 他の分類（`TargetGone`：ハンドルが無効で再試行しても同じ結果になる、`Denied`：拒否は持続的、
/// `TargetHung`：Healthが別途Hungとして扱う、`Unclassified`：未知の失敗を当てなく再試行するのは
/// 危険）は依然`Cancelled`にする単発の諦め（`Desired`も取り消し、次のReconcileに最初からの再試行を
/// 委ねる）。`Denied OtherDenial`はさらに`Movability`を`NotMovable`へ修正する（§4.5）。戻り値の
/// `(WindowId * EffectFailure) option`は、`TargetHung`時のHealth.Responsiveness修正（Placementの
/// 管轄外）を呼び出し側（`WmState.step`）が行うために必要な情報を運ぶ。
let onEffectCallReturned
    (now: int64)
    (evidenceSequence: int64)
    (rid: RequestId)
    (result: Result<unit, NativeCallRecord>)
    (state: PlacementState)
    : PlacementState * Effect list * TimerCommand<TimerId> list * (WindowId * EffectFailure) option =
    match result with
    | Ok () -> state, [], [], None
    | Error record ->
        match Map.tryFind rid state.Pending with
        | None -> state, [], [], None // 古い結果、またはこのPendingはすでに別の経路で終端済み
        | Some pr ->
            let failure = classify record
            // `rid`自身の`PendingDeadline`タイマーはまだ生きている（Timeoutより先にこの結果が届いた）
            // ので、再試行するか諦めるかに関わらず必ず取り消す（`retryOrGiveUp`の型直前のコメント参照。
            // v2.30でこの関数がTimerCommandを返せるようになって初めて修正できた既存の取りこぼし）。
            let cancelOldTimer = [ CancelTimer (TimerId.Placement (PendingDeadline rid)) ]
            match failure with
            | Transient ->
                let state', effects, cmds = retryOrGiveUp now rid pr state
                state', effects, cancelOldTimer @ cmds, Some (pr.Window, failure)
            | _ ->
                let cancelled =
                    { state with
                        Pending = Map.remove rid state.Pending
                        Desired = Map.remove pr.Window state.Desired
                        RetryAttempts = Map.remove pr.Window state.RetryAttempts }
                let state' =
                    match failure with
                    | Denied OtherDenial ->
                        { cancelled with
                            Movability =
                                Map.add pr.Window { Value = NotMovable; Confidence = Inferred [ Outcome (evidenceSequence, rid) ]; LastRevisedAt = now } cancelled.Movability }
                    | _ -> cancelled
                state', [], cancelOldTimer, Some (pr.Window, failure)

/// `DomainEvents.TopologySettled`（§7.3のScreenSettle→Grace。モニター構成が確定した）：既存の全Pendingは
/// 古いモニター配置に基づく目標なので、観測を待たず即座に`Superseded`（Cancelled）にする（§7.2
/// 「TopologySettled → Superseded」）。`Desired`はあえて取り消さない——新しい配置でも偶然同じ座標に
/// なる窓があれば、`dispatchPlacements`の`desiredChanged`判定が無駄なSetRectの再発行を防ぐ。
/// 呼び出し側（`WmState.step`）は、この呼び出しと同じstepで必ずReconcileも走らせる責務を負う
/// （そうしないと、ここで取り消したPendingの代わりが二度と発行されない。呼び出し側のコメント参照）。
/// どの窓のPendingも、変わったモニターに属するかに関わらず一律で取り消す簡略化（個々のPendingが
/// 実際に影響を受けたかを判定する根拠が今は無いため、安全側に広く取り消す）。
let onTopologySettled (state: PlacementState) : PlacementState * TimerCommand<TimerId> list =
    let cancelCmds = state.Pending |> Map.toSeq |> Seq.map (fun (rid, _) -> CancelTimer (TimerId.Placement (PendingDeadline rid))) |> List.ofSeq
    let affectedWindows = state.Pending |> Map.toSeq |> Seq.map (fun (_, pr) -> pr.Window) |> Set.ofSeq
    { state with
        Pending = Map.empty
        RetryAttempts = affectedWindows |> Set.fold (fun m w -> Map.remove w m) state.RetryAttempts },
    cancelCmds

/// 窓の消滅時、`Geometry`/`Movability`/`TargetIntegrity`/`IntegrityReprobes`/`Desired`/`Pending`/`Epochs`
/// を除く（§4.4「各ドメインの信念は窓が消滅した時点でのみ除去する」）。動いている再probeタイマーと、
/// その窓宛てのPendingが持つ`PendingDeadline`タイマーがあれば`CancelTimer`を返す
/// （review-phase-a-round1.md 4-3と同じ理由、Windows.ClassificationDeadline/Health.Reprobesと同じ扱い）。
/// `WmState.vanishWindow`から呼ぶ。
let onWindowVanished (w: WindowId) (state: PlacementState) : PlacementState * TimerCommand<TimerId> list =
    let integrityCancel = if Map.containsKey w state.IntegrityReprobes then [ CancelTimer (TimerId.Placement (IntegrityReprobe w)) ] else []
    let pendingForWindow = state.Pending |> Map.toSeq |> Seq.filter (fun (_, pr) -> pr.Window = w) |> Seq.map fst |> List.ofSeq
    let pendingCancel = pendingForWindow |> List.map (fun rid -> CancelTimer (TimerId.Placement (PendingDeadline rid)))
    { state with
        Geometry = Map.remove w state.Geometry
        Movability = Map.remove w state.Movability
        TargetIntegrity = Map.remove w state.TargetIntegrity
        IntegrityReprobes = Map.remove w state.IntegrityReprobes
        Desired = Map.remove w state.Desired
        Pending = pendingForWindow |> List.fold (fun p rid -> Map.remove rid p) state.Pending
        Epochs = Map.remove w state.Epochs
        RetryAttempts = Map.remove w state.RetryAttempts },
    integrityCancel @ pendingCancel
