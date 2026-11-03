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
/// `WorkspaceHidden`/`WorkspaceShown` → `HiddenByWm`/`Shown None`（v2.31追加）：`onWorkspaceHidden`/
/// `onWorkspaceShown`が、そのWorkspaceに属する窓の`Desired`を更新し`SetPresence`を発行する
/// （`DesiredPresence`の型直前のコメント参照）。`SwitchWorkspace`自体はまだ未配線だが、
/// `MonitorLifecycle.reconcile`（モニター接続・切断）は既に`WorkspaceShown`/`WorkspaceHidden`を
/// 発行しており（`settle`の中で`TopologySettled`と同時に）、`WmState.step`はこれまで捨てていた
/// （v2.29の`WorkspaceShown`/`WorkspaceHidden`/`CurrentChanged`は依然捨てたままという記述を、
/// `WorkspaceShown`/`WorkspaceHidden`の消費分だけ更新した）。これにより、モニター切断で非表示になった
/// WorkspaceのウィンドウがOS側では表示されたまま取り残される、という実在する不具合（今回初めて
/// `SetPresence`を発行するまで、WMはこの種の窓を一度も隠していなかった）を閉じた。
///
/// `SetPresence`の観測による確認（v2.41追加）：`checkPendingSatisfied`（Geometry/`SetRect`）と
/// 同じ考え方を表示状態へ適用した`PendingPresence`/`checkPendingPresenceSatisfied`を新設し、
/// `onWorkspaceHidden`/`onWorkspaceShown`が発行する`SetPresence`それぞれに対応する待ちを作る。
/// `onWindowObserved`で`Geometry.Presence`（`ObservedPresence`信念、既に`reviseGeometryFromSnapshot`が
/// 修正していた）が`DesiredVisible`と一致した瞬間に待ちを消す。期限到達時（`onPresenceDeadlineTimerFired`）
/// は`PendingRequest`と違いRetryもDesiredの取消しも行わない単発の諦め——`onWorkspaceHidden`/
/// `onWorkspaceShown`は（`dispatchPlacements`の`desiredChanged`のような差分判定を持たず）呼ばれる
/// たびに無条件で`SetPresence`を再発行するので、`Desired`を取り消しても取り消さなくても次回の挙動は
/// 変わらない。`onGateRejected`/`onWindowVanished`も`PendingPresence`を同時に後始末するよう拡張した。
/// `onTopologySettled`は意図的に対象外のまま（下記）：Geometry Pendingと違い、表示状態はモニター配置に
/// 依存しないため、トポロジ確定で失効させる理由が無い。
///
/// `Mismatch → Reevaluate`（v2.42追加）、`MinWidth`Accumulating（v2.43追加）：詳細は下記「対象外に
/// したこと」の該当項目（取り消し線付き、実装済みに更新）参照。要約：`Mismatch`は`PendingDeadline`
/// 満了時点でだけ判定し（段階1）、確定した不一致（`observed.Width > TargetRect.Width`）を証拠に
/// `MinWidth`信念をDIP正規化して蓄積する。`MinWidth`を実際にPolicy/Layoutが読むところ（消費側）は
/// まだ無い——このラウンドは信念を蓄積するところまで。
///
/// `DpiBehavior`の発見時既定値（v2.44追加）：`DpiAwareness`の申告（`PerMonitorV2`→`RescalesItself`、
/// それ以外→`OsBitmapScaled`）を`Inferred`の初期値とする（`Movability`の発見時既定値と同じパターン）。
/// 異なるDPIのモニター間移動後の2段目自己リサイズの有無による修正はまだ無い（下記「対象外」参照）。
///

/// このラウンドで意図的に対象外にしたこと（いずれも「できればやる」ではなく、別の未実装の仕組みが
/// 前提になっているため実装できない項目であり、専用のラウンドへ切り出す）：
/// - **`PendingDeadline`のBackoff**（§7.2「Timeout → Retry/Backoff」のBackoff部分、§7.3）：Retry自体は
///   v2.28で実装済み（上記）。試行間隔を空ける理由・適切な遅延量はPhase 0の実測が必要。
/// - **`onEffectCallReturned`の`Unclassified`のRetry**：`Transient`はv2.30で対象にしたが（上記）、
///   `Unclassified`（未知の失敗）を当てなく再試行するのは危険なため対象外のまま。
/// - ~~**`HiddenByWm`/`Shown None`の観測による確認（`Satisfied`相当）**~~：v2.41で実装（`PendingPresence`/
///   `checkPendingPresenceSatisfied`/`onPresenceDeadlineTimerFired`、型直前のコメント参照）。
///   ただしRetry/Backoffは対象外のまま（`PendingRequest`と同じ理由。期限到達時は単発で諦める）。
///   `SetPresence`呼出の`EffectCallReturned`（`Error`の場合）は依然`onEffectCallReturned`の対象外
///   （`PendingPresence`のRequestIdには対応するGeometryのPendingが無いため「対応するPendingが
///   無い＝無害に無視」されるだけで、失敗を検知しない——専用のラウンドへ切り出す、既知のギャップ）。
/// - ~~**`Mismatch` → `Reevaluate`**~~：v2.42でOpus敵対的レビューの設計（段階1、プロジェクトオーナー
///   確認済み）どおりに実装。`tryMismatchObservation`/`onPendingDeadlineTimerFired`、`PendingRequest.DispatchSeq`/
///   `Baseline`、`PlacementState.InFlightTargets`の各型直前のコメント参照。`PendingDeadline`満了時点
///   でだけ判定するため、進行中のPendingを早まって取り消す経路は構造的に無い（v2.24〜の懸念はこれで
///   コードの論理として塞がる）。早期（`checkPendingSatisfied`内での即時）判定は段階2として対象外の
///   ままにする——DPIの2段リサイズ等の多段変化が実機でどの程度の間隔で起きるかの実測（CIスパイクS5/
///   S11参照）が無いと、中間状態を偽陽性のMismatchと誤判定する危険が残るため（Opus敵対的レビュー
///   `/tmp/.../opus-review-mismatch-reevaluate.md`、段階2の節参照）。「ユーザーのドラッグ」「最小化・
///   非表示中の除外」（同レビューの条件5・6）は、下記`MinWidth`の消費実装（v2.43）に合わせて見直す
///   機会があったが、まだ実装していない——専用のラウンドへ持ち越す。CIスパイクS24/S25
///   （docs/spikes.md）で一次データを得た：条件5（最小化中の除外）は、`EVENT_SYSTEM_MINIMIZESTART`
///   より先に（または同時に）最小化後の`-32000`付近の`EVENT_OBJECT_LOCATIONCHANGE`が届く場合がある
///   ため、「`IsMinimized`信念が先に確定している」ことを前提にできない（デバウンス等の追加対策が
///   要る可能性）。条件6（ユーザードラッグの除外）は、S18と同じ安全な投稿メッセージ技法では
///   `EVENT_SYSTEM_MOVESIZESTART`/`MOVESIZEEND`自体が発火しないと判明し、実デスクトップでの
///   再検証（S11r等と同じ限定）が必要なことが分かった——いずれも実装の判断材料は増えたが、
///   実装自体は依然専用のラウンドへ持ち越す。
/// - **ユーザー操作による1回だけの試し直し**：ユーザーコマンド経由でPlacementへの新しい入口が要る
///   ——専用のラウンドへ切り出す。
/// - ~~**`MinWidth`（Accumulating規則）**~~：v2.43で実装（`reviseMinWidth`、型直前のコメント参照）。
///   `Mismatch`（`observed.Width > TargetRect.Width`）を証拠に、観測時の窓のモニターDPIでDIPへ
///   正規化した値を`Inferred`の最小幅とする。`DpiBehavior = RescalesItself`の2段目リサイズ待ちを
///   除外する規則（§4.5）はv2.45で`onPendingDeadlineTimerFired`に結線済み（`isMidDpiTransition`、
///   `reviseMinWidth`の型直前のコメント参照）。~~信念自体は`MinWidth`を読む`Policy`述語
///   （`MinWidthAtLeast`、§5.4）やLayoutの`LayoutWindow.MinWidthDip`へまだ結線していない~~：**v2.48で
///   両方の射影を配線した**（`Windows.ClassificationCtx.MinWidth`/`classificationView`、`WmState.
///   reconcileOneScreen`のLayoutContext.TiledWindows構築）。ただし`LayoutFunction`実装
///   （MasterStack/Monocle）側が`MinWidthDip`を実際に読んで強制する処理（§6.2「推定最小サイズは
///   ソフト制約、満たせない場合は調整・Floating・Suspended等へ劣化させる」）は、具体的な劣化手順が
///   本書に数値・アルゴリズムとして無いため依然未実装のまま——専用のラウンドへ持ち越す。
/// - ~~**`DpiBehavior`（Accumulating規則）**~~：v2.44で発見時の申告による初期値、v2.45で2段目の
///   自己リサイズ検出（`checkDpiCrossing`、型直前のコメント参照）、同じラウンドで`MinWidth`側の
///   2段目待ち除外（上記）も結線した。CIスパイクS22（docs/spikes.md）で`SetWindowPos`から
///   `LOCATIONCHANGE`までの遅延が0〜1msと確認済みなので、架空のタイムアウトは設けず次の観測だけで
///   判定する最小実装にした。残るのは`Movable`への確定に相当する仕組み（v2.24と同様。今は
///   `Inferred`のまま、2段目を実際に見ても`Confirmed`へは進めない）——専用のラウンドへ持ち越す。
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

/// §4.4「WMが見せているべきか。非表示の実装方式に依存しない」。`HiddenByWm`/`Shown None`は
/// `onWorkspaceHidden`/`onWorkspaceShown`（v2.31）が作る。「実際にそうなったか」の観測による確認
/// （`ObservedPresence`との一致判定、`Satisfied`相当）はまだ無い——発行するだけで確認はしない
/// （ファイル冒頭「対象外」参照）。
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
/// `DispatchSeq`/`Baseline`（v2.42追加、`Mismatch → Reevaluate`。ファイル冒頭コメント参照）：
/// `DispatchSeq`は`EffectCallReturned (rid, Ok (), dispatchSeq)`で受け取った、発行が実際に受け付けられた
/// 時点のSampleSequence（まだ届いていなければ`None`）。`Baseline`は発行時点の`VisibleFrameRect`信念の
/// 値（無ければ`None`）——Mismatch判定の「何も変わっていない」除外に使う。
type PendingRequest =
    { Window: WindowId
      Epoch: IntentEpoch
      TargetRect: PixelRect
      IssuedAt: int64
      Deadline: int64
      DispatchSeq: int64 option
      Baseline: PixelRect option }

/// `PendingRequest`と同じ考え方（§7.2「待ちをデータで表す」）を`SetPresence`へ適用した最小形
/// （v2.41新設、ファイル冒頭「`SetPresence`の観測による確認」参照）。`Epoch`/`Retry`は持たない——
/// `onWorkspaceHidden`/`onWorkspaceShown`は呼ばれるたびに無条件で新しい`SetPresence`を発行する
/// （`dispatchPlacements`の`desiredChanged`のような「前回と同じ意図かどうか」の判定を持たない）ため、
/// 同じ窓を指す古い`PendingPresence`が残っていれば（例：隠した直後に再び見せた）`dispatchPlacements`と
/// 同じ要領で必ず先に失効させる（下記）。この不変条件（1窓につき同時に1つまで）が保たれる限り、
/// 複数のPendingPresenceが同じ窓を指して競合することは無く、`Epoch`で古い要求を失効させる仕組みは
/// 要らない。
type PendingPresence = { Window: WindowId; DesiredVisible: bool; IssuedAt: int64; Deadline: int64 }

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
      /// `SetPresence`の観測による確認の待ち（v2.41新設、型直前のコメント参照）。1窓につき同時に
      /// 1つまで（`onWorkspaceHidden`/`onWorkspaceShown`が古いエントリを必ず先に失効させる）。
      PendingPresence: Map<RequestId, PendingPresence>
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
      RetryAttempts: Map<WindowId, int>
      /// `Mismatch → Reevaluate`（v2.42、ファイル冒頭コメント参照）：窓ごとに、`supersede`された
      /// （まだ応答が来るかもしれない）旧Epochの目標矩形を短期保持する。`IntentEpoch`は観測に載らない
      /// ため（Win32に要求↔応答の対応付けプリミティブが無い、§9章）、Epochの使い道はここだけ——
      /// 旧Epochの`SetRect`が遅れて適用された結果の観測を、新Epochの不一致と取り違えないための除外に
      /// 使う。`dispatchPlacements`が新しいPendingで古いPendingを失効させる際にここへ移し、該当の
      /// 矩形が観測された時・窓の消滅・`TopologySettled`で消す。
      InFlightTargets: Map<WindowId, (IntentEpoch * PixelRect) list>
      /// §4.5「最小幅」（Accumulating）。`Map`に無い＝まだ証拠が無い（他のAccumulating信念と同じ規約、
      /// §4.5「`Map`に無いことを『まだ証拠が無い』の意味に使う」）。`reviseMinWidth`参照。
      MinWidth: Map<WindowId, Belief<int>>
      /// §4.5「DPIの振る舞い」（Accumulating、S11）。`reviseDpiBehaviorDeclaration`の型直前のコメント参照。
      DpiBehavior: Map<WindowId, Belief<DpiBehavior>>
      /// `DpiBehavior`の2段目自己リサイズ検出（v2.45）。`checkDpiCrossing`の型直前のコメント参照。
      DpiCrossing: Map<WindowId, PixelRect> }

let initial: PlacementState =
    { Geometry = Map.empty
      Movability = Map.empty
      TargetIntegrity = Map.empty
      IntegrityReprobes = Map.empty
      Desired = Map.empty
      Pending = Map.empty
      PendingPresence = Map.empty
      NextRequestId = 0L
      Epochs = Map.empty
      RetryAttempts = Map.empty
      InFlightTargets = Map.empty
      MinWidth = Map.empty
      DpiBehavior = Map.empty
      DpiCrossing = Map.empty }

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

/// `SetPresence`の観測による確認（v2.41新設、ファイル冒頭「`SetPresence`の観測による確認」参照）：
/// `checkPendingSatisfied`と同じ形で、窓`w`の現在の`Geometry.Presence`（`ObservedPresence`信念）と、
/// `w`宛てのPendingPresenceの`DesiredVisible`を照合する。`DesiredVisible = true`なら`VisibleToUser`、
/// `false`なら`HiddenByWmMethod`で一致（`HiddenOther`は一致させない——§4.4の「WMが隠した」という
/// 意図への確認であり、ユーザー等の別経路での非表示は別の信念・別の反応の対象であって、ここで
/// WMの意図が叶ったと誤認してはならない）。
let private checkPendingPresenceSatisfied (w: WindowId) (state: PlacementState) : PlacementState * TimerCommand<TimerId> list =
    let observedPresence = Map.tryFind w state.Geometry |> Option.bind (fun g -> LatestWins.value g.Presence)
    match observedPresence with
    | None -> state, []
    | Some presence ->
        match state.PendingPresence |> Map.toSeq |> Seq.tryFind (fun (_, pp) -> pp.Window = w) with
        | Some (rid, pp) ->
            let matches =
                match pp.DesiredVisible, presence with
                | true, VisibleToUser -> true
                | false, HiddenByWmMethod -> true
                | _ -> false
            if matches then
                { state with PendingPresence = Map.remove rid state.PendingPresence },
                [ CancelTimer (TimerId.Placement (PresenceDeadline rid)) ]
            else
                state, []
        | None -> state, []

/// `WindowObserved`：§7.1段①「Windows→Placement→Health→Focus」のPlacement分。Windowsと同じ入力から
/// 自分の信念（Geometry/Movability/TargetIntegrity）だけを修正する（ADR-018：ドメインは自分の状態だけを
/// 返す）。`Reprobe`Effectの発行、再probeタイマーの開始・取消も、この入力の処理に含める。
/// §4.5「DPIの振る舞い」（Accumulating、S11）：`DpiAwareness`の申告（`PerMonitorV2`なら
/// `RescalesItself`、それ以外なら`OsBitmapScaled`）を`Inferred [Sample …]`の初期値とする
/// （窓の発見時だけ——`Movability`の発見時既定値と同じ「最初の1回だけ書く」パターン）。異なるDPIの
/// モニター間を移動した後、`WM_DPICHANGED`に応じた2段目の自己リサイズが実際に観測されたかどうかで
/// この信念を修正する規則（申告と実際の挙動の食い違いを検出する）は、モニター間移動の検出自体が
/// このラウンドでは未実装のため対象外のまま（ファイル冒頭「対象外」参照、既知のギャップ）——今は
/// 申告をそのまま初期値として採用するだけ。
let private reviseDpiBehaviorDeclaration (now: int64) (declared: Observation<DpiAwarenessDeclared>) : Belief<DpiBehavior> =
    let value = match declared.Value with Ok PerMonitorV2 -> RescalesItself | _ -> OsBitmapScaled
    { Value = value; Confidence = Inferred [ Sample declared.SampleSequence ]; LastRevisedAt = now }

let onWindowObserved (now: int64) (snapshot: WindowSnapshot) (state: PlacementState) : PlacementState * Effect list * TimerCommand<TimerId> list =
    let w = snapshot.Id
    let geometry' =
        Map.add w (reviseGeometryFromSnapshot now (Map.tryFind w state.Geometry |> Option.defaultValue geometryInitial) snapshot) state.Geometry

    // DpiBehaviorの発見時の既定値（§4.5、型直前のコメント参照）。既にエントリがあれば変えない
    // （「申告は初期値」——その後の修正は実際の挙動の観測からのみ行う、というspecの区別を保つ）。
    // `Error`観測（Shellが問い合わせられなかった等）は証拠が無いので何もしない。
    let dpiBehavior' =
        if Map.containsKey w state.DpiBehavior then
            state.DpiBehavior
        else
            match snapshot.DpiAwareness with
            | Some ({ Value = Ok _ } as obs) -> Map.add w (reviseDpiBehaviorDeclaration now obs) state.DpiBehavior
            | _ -> state.DpiBehavior

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
            IntegrityReprobes = integrityReprobes'
            DpiBehavior = dpiBehavior' }

    // §7.2「SetRectのネイティブAPI呼出が成功しても、目標矩形を観測するまではSatisfiedにしない」：
    // ここでGeometryが今まさに更新されたVisibleFrameRectと、窓宛てのPendingのTargetRectを照合する。
    // Mismatch（§7.2の`Reevaluate`）はこのラウンドでは明示的に扱わず、一致しなければ次のPendingDeadline
    // タイマーの発火（またはこの先の再Reconcile）に委ねる（ファイル冒頭「対象外」参照）。
    let pendingState, pendingTimerCmds = checkPendingSatisfied now w stateAfterBeliefs
    let pendingPresenceState, pendingPresenceTimerCmds = checkPendingPresenceSatisfied w pendingState
    pendingPresenceState, effects, timerCmds @ pendingTimerCmds @ pendingPresenceTimerCmds

/// §4.5「DPIの振る舞い」の2段目自己リサイズによる修正（v2.45）：`onWindowObserved`が終わった直後に
/// `WmState.step`が呼ぶ（`dpiOf`は他ドメインの状態を直接読まないための読み取り専用の投影、
/// `MinWidth`の型直前のコメントと同じ考え方、ADR-018）。架空のタイマーは設けない最小実装——
/// `oldMonitor`（この観測の前のMonitor信念）と、観測後の新しいMonitor信念が、異なるDPIの別モニターを
/// 指していれば「DPIをまたいだ」と判定し、その時点の`VisibleFrameRect`を`DpiCrossing`へ記録する
/// （同じ窓で前回の記録が残っていれば上書きする——新しい意図のEpochを進める`dispatchPlacements`と
/// 同じ「最新の意図を信じる」考え方）。`DpiCrossing`に記録が残っている窓で、矩形が記録時点と異なる
/// 観測が届けば、2段目の自己リサイズが実際に起きた証拠として`RescalesItself`を`Inferred`で記録し、
/// 記録を消す。矩形が記録時点のまま（まだ変化無し）なら記録を残して待つ——タイムアウトは設けない。
/// いつまでも変化が来なければ、v2.44の申告ベースの既定値をそのまま信じ続けるだけで安全側に倒れる
/// （申告が間違っていても、`MinWidth`の2段目除外（まだ未実装）以外への実害は無い）。
/// `dispatchPlacements`が同じ窓へ新しい`SetRect`を発行すると`DpiCrossing`を消す（型直前のコメント
/// 参照）——これにより、ここで「矩形が変わった」と見る観測は、Core自身の要求ではまだ説明できない
/// 変化だけに限られる（同じstepで要求が`Satisfied`になった場合でも、`Pending`の有無を見るのではなく
/// 発行時点で先に記録を消しているので、取り違えない）。
let checkDpiCrossing (now: int64) (dpiOf: MonitorId -> int option) (w: WindowId) (oldMonitor: MonitorId option) (state: PlacementState) : PlacementState =
    let newMonitor = Map.tryFind w state.Geometry |> Option.bind (fun g -> LatestWins.value g.Monitor)
    let crossedDifferentDpi =
        match oldMonitor, newMonitor with
        | Some oldM, Some newM when oldM <> newM ->
            match dpiOf oldM, dpiOf newM with
            | Some dOld, Some dNew -> dOld <> dNew
            | _ -> false
        | _ -> false
    let currentRect = Map.tryFind w state.Geometry |> Option.bind (fun g -> LatestWins.value g.VisibleFrameRect)
    let state =
        match crossedDifferentDpi, currentRect with
        | true, Some rect -> { state with DpiCrossing = Map.add w rect state.DpiCrossing }
        | _ -> state
    match Map.tryFind w state.DpiCrossing, currentRect with
    | Some baseline, Some observed when observed <> baseline ->
        let valueSeq = Map.tryFind w state.Geometry |> Option.bind (fun g -> g.VisibleFrameRect.ValueSeq)
        let evidence = valueSeq |> Option.map Sample |> Option.toList
        { state with
            DpiCrossing = Map.remove w state.DpiCrossing
            DpiBehavior = Map.add w { Value = RescalesItself; Confidence = Inferred evidence; LastRevisedAt = now } state.DpiBehavior }
    | _ -> state // まだ変化無し、またはこの窓は対象外

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
                let superseded = st.Pending |> Map.toSeq |> Seq.filter (fun (_, pr) -> pr.Window = p.Window) |> List.ofSeq
                let supersededCmds = superseded |> List.map (fun (rid, _) -> CancelTimer (TimerId.Placement (PendingDeadline rid)))
                let pendingWithoutOld = st.Pending |> Map.filter (fun _ pr -> pr.Window <> p.Window)
                // `Mismatch → Reevaluate`（v2.42）：失効させる旧Pendingの(Epoch, TargetRect)を
                // `InFlightTargets`へ移す——まだ遅れて適用されるかもしれない旧要求の結果を、新しい
                // Pendingの不一致と取り違えないため（型直前のコメント参照）。
                let inFlightTargets' =
                    let old = Map.tryFind p.Window st.InFlightTargets |> Option.defaultValue []
                    let moved = superseded |> List.map (fun (_, pr) -> pr.Epoch, pr.TargetRect)
                    Map.add p.Window (old @ moved) st.InFlightTargets
                let epoch' =
                    match Map.tryFind p.Window st.Epochs with
                    | Some (IntentEpoch e) -> IntentEpoch (e + 1L)
                    | None -> IntentEpoch 0L
                let rid = RequestId st.NextRequestId
                let deadline = now + pendingDeadlineMs
                // `Baseline`（v2.42）：発行時点の`VisibleFrameRect`信念。Mismatch判定の「何も変わって
                // いない」除外に使う（型直前のコメント参照）。`DispatchSeq`は`EffectCallReturned`が
                // 届くまで`None`。
                let baseline = Map.tryFind p.Window st.Geometry |> Option.bind (fun g -> LatestWins.value g.VisibleFrameRect)
                let pendingRequest =
                    { Window = p.Window; Epoch = epoch'; TargetRect = p.Target; IssuedAt = now; Deadline = deadline; DispatchSeq = None; Baseline = baseline }
                let st' =
                    { st with
                        Desired = Map.add p.Window (Shown (Some p.Target)) st.Desired
                        Pending = Map.add rid pendingRequest pendingWithoutOld
                        NextRequestId = st.NextRequestId + 1L
                        Epochs = Map.add p.Window epoch' st.Epochs
                        InFlightTargets = inFlightTargets'
                        // 新しい意図（Reconcileが計算した新しいTarget）なので、古いTargetへのRetryの
                        // 試行回数は引き継がない（`RetryAttempts`はPendingDeadlineの再試行専用、型直前の
                        // コメント参照）。
                        RetryAttempts = Map.remove p.Window st.RetryAttempts
                        // `DpiCrossing`（v2.45）：ここから先、この窓の矩形変化はCore自身の`SetRect`で
                        // 説明できてしまうので、まだ決着していない2段目自己リサイズの観測機会があれば
                        // 帰属不能として諦める（`checkDpiCrossing`の型直前のコメント参照）。
                        DpiCrossing = Map.remove p.Window st.DpiCrossing }
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
        // `DispatchSeq`は新しい`rid`専用の確認待ちなのでリセットする（v2.42）——古い`rid`の
        // `DispatchSeq`をそのまま引き継ぐと、まだ届いていない新しい`EffectCallReturned`より前の
        // 観測をMismatch判定に使ってしまう。`Baseline`（同じ目標への再試行なので）はそのまま引き継ぐ。
        let retried = { pr with IssuedAt = now; Deadline = deadline; DispatchSeq = None }
        { state with
            Pending = Map.add newRid retried (Map.remove rid state.Pending)
            NextRequestId = state.NextRequestId + 1L
            RetryAttempts = Map.add pr.Window (attemptsSoFar + 1) state.RetryAttempts },
        [ SetRect (newRid, pr.Window, pr.TargetRect) ],
        [ StartTimer (TimerId.Placement (PendingDeadline newRid), pendingDeadlineMs) ]

/// `Mismatch → Reevaluate`（§7.2、v2.42。Opus敵対的レビューで設計、ファイル冒頭コメント参照）：
/// `PendingDeadline`満了時点で、発行後に取得された新鮮な観測が、発行前の状態（`Baseline`）とも、
/// まだ応答が来るかもしれない旧Epochの目標（`InFlightTargets`）とも異なるなら、要求は実際にOSへ
/// 届いたが別の矩形へ適用された（アプリの自己再配置・最小幅等での丸め）とみなしてMismatchとする。
/// 条件（全てAND）：
/// 1. `DispatchSeq`が既に分かっている（`EffectCallReturned Ok`が届いている）
/// 2. 観測の`ValueSeq`が`DispatchSeq`より新しい（発行前に取得された観測で判定しない）
/// 3. 観測が`TargetRect`と異なる
/// 4. 観測が`Baseline`と異なる（「何も変わっていない」＝要求が無反応の可能性を、不一致と呼ばない）
/// 5. 観測が`InFlightTargets`のどの旧目標とも異なる（旧Epochの遅延適用を新Epochの不一致と取り違えない）
/// 「ユーザーのドラッグ」「最小化・非表示中」の除外（Opusレビューの条件5・6）は、MinWidth Accumulating
/// （§4.5、未実装）が実際にこのMismatch証拠を消費するようになってから、その消費側と合わせて設計する
/// 専用のラウンドへ持ち越す——今は証拠として使う消費者が無いので、取り違えても実害はまだ無い。
let private tryMismatchObservation (pr: PendingRequest) (state: PlacementState) : (PixelRect * int64) option =
    match pr.DispatchSeq with
    | None -> None
    | Some dispatchSeq ->
        match Map.tryFind pr.Window state.Geometry with
        | None -> None
        | Some g ->
            match LatestWins.value g.VisibleFrameRect, g.VisibleFrameRect.ValueSeq with
            | Some observed, Some valueSeq when valueSeq > dispatchSeq ->
                let matchesBaseline = Some observed = pr.Baseline
                let matchesInFlight =
                    Map.tryFind pr.Window state.InFlightTargets |> Option.defaultValue [] |> List.exists (fun (_, r) -> r = observed)
                if observed <> pr.TargetRect && not matchesBaseline && not matchesInFlight then Some (observed, valueSeq) else None
            | _ -> None

/// §4.5「最小幅」（Accumulating）：`SetRect`で幅`r`を要求して幅`o > r`が観測された（`Mismatch`）ことを
/// 証拠に、`o`を物理ピクセルからDIPへ正規化した値（`o × 96 / dpi`）を`Inferred`の最小幅とする
/// （`MinWidth`の型直前のコメント参照）。より小さい幅が観測されたら値を下げる——最新の証拠をそのまま
/// 採用する単純な規則とし、具体的な閾値（何回観測されたら配置に反映するか）は実機Journalでの
/// チューニングを待つ専用のラウンドへ持ち越す（§21）。`DpiBehavior = RescalesItself`の窓がモニター間を
/// 移った直後の2段目リサイズ待ちを除外する規則（§4.5）はv2.45で`onPendingDeadlineTimerFired`側に
/// 結線した（`isMidDpiTransition`、呼び出し側のコメント参照）——`DpiCrossing`に未決着の記録が残る
/// `RescalesItself`の窓だけ、観測した`Mismatch`をこの関数の証拠対象から外す。
let private toDip (dpi: int) (physicalPx: int) : int = physicalPx * 96 / dpi

let private reviseMinWidth (now: int64) (evidence: EvidenceRef) (observedWidthDip: int) (current: Belief<int> option) : Belief<int> =
    match current with
    | Some b when b.Value = observedWidthDip ->
        // 同じ値が繰り返し観測された：「同じ結果が繰り返されれば根拠が増える」（§4.5）——
        // 既存の根拠へ追加するだけで値は変えない。
        match b.Confidence with
        | Inferred refs -> { b with Confidence = Inferred (refs @ [ evidence ]); LastRevisedAt = now }
        | _ -> { b with Confidence = Inferred [ evidence ]; LastRevisedAt = now }
    | _ ->
        // 値が変わった（上がる/下がる、どちらも）：最新の証拠から作り直す（§4.5「より小さい幅が
        // 観測されたら値を下げる」——単調増加のratchetにはしない）。
        { Value = observedWidthDip; Confidence = Inferred [ evidence ]; LastRevisedAt = now }

/// `TimerElapsed (Placement (PendingDeadline rid))`：§7.2「Timeout → Retry/Backoff、上限後は配置対象外」
/// （v2.28）に加え、v2.42で`tryMismatchObservation`がSomeを返す場合の`Mismatch → Reevaluate`を分岐させる（v2.43で
/// 併せてMinWidthの証拠収集も配線した）。
/// Reevaluate＝「アプリと戦わない」（オーナー確認済み）：Pendingを終端し`Desired`は目標のまま残す
/// （`dispatchPlacements`の`desiredChanged`判定が同じ目標の再発行を止める、v2.41のPendingPresence版
/// Reevaluateと同じ考え方）、Retryしない。`RetryAttempts`は次に本当に新しい意図が来たときのために
/// クリアする。`retryOrGiveUp`とは違いDesiredを取り消さない点が非対称だが、理由が異なる——Timeoutの
/// 諦めは「無反応で判断材料が無い」、Mismatchは「要求は届いたが決定的に拒否された」なので、後者で
/// 同じTargetを諦めても`Desired`自体は変える理由が無い（Layoutの入力が変われば自然に新しい意図になる）。
/// `dpiOf`：不一致の窓が今いるモニターのDPIを引く読み取り専用の投影（`WmState.step`が`Topology.displays`
/// から組み立てて渡す、`LayoutContext.Dpi`と同じ考え方——Placementは他ドメインの状態を直接読まない、
/// ADR-018）。最小幅の証拠をDIPへ正規化するために使う（型直前のコメント参照）。窓の`Monitor`信念が
/// 無い、または`dpiOf`がそのモニターを引けない（トポロジが変わった直後等）場合は証拠を記録しない
/// （安全側：誤った正規化で不正確な最小幅を蓄積するより、今回分を見送る方を選ぶ）。
let onPendingDeadlineTimerFired
    (now: int64)
    (rid: RequestId)
    (dpiOf: MonitorId -> int option)
    (state: PlacementState)
    : PlacementState * Effect list * TimerCommand<TimerId> list =
    match Map.tryFind rid state.Pending with
    | Some pr when awaitDeadline (Some pr.Deadline) now ->
        match tryMismatchObservation pr state with
        | Some (observed, valueSeq) ->
            // §4.5「`DpiBehavior = RescalesItself`の窓が異なるDPIのモニターへ移った直後、2段目の
            // 自己リサイズを待っている間に観測した`Mismatch`は証拠にしない（1段目のOSの拡大と2段目の
            // 間の値を誤って正規化しないため、R5-11）」（v2.45で結線。`reviseMinWidth`の型直前の
            // コメント参照）：`DpiCrossing`に未決着の記録が残っている（＝`checkDpiCrossing`がまだ
            // 2段目を見ていない）窓で、信念が`RescalesItself`なら、このMismatchをMinWidthの証拠にしない
            // （Mismatch自体の終端・Pending解消は変えない——証拠として採用しないだけ）。
            let isMidDpiTransition =
                Map.containsKey pr.Window state.DpiCrossing
                && (Map.tryFind pr.Window state.DpiBehavior |> Option.map (fun b -> b.Value)) = Some RescalesItself
            let minWidth' =
                if observed.Width > pr.TargetRect.Width && not isMidDpiTransition then
                    match Map.tryFind pr.Window state.Geometry |> Option.bind (fun g -> LatestWins.value g.Monitor) |> Option.bind dpiOf with
                    | Some dpi ->
                        let observedWidthDip = toDip dpi observed.Width
                        let evidence = Outcome (valueSeq, rid)
                        Map.add pr.Window (reviseMinWidth now evidence observedWidthDip (Map.tryFind pr.Window state.MinWidth)) state.MinWidth
                    | None -> state.MinWidth
                else
                    state.MinWidth
            { state with
                Pending = Map.remove rid state.Pending
                RetryAttempts = Map.remove pr.Window state.RetryAttempts
                MinWidth = minWidth' },
            [], []
        | None -> retryOrGiveUp now rid pr state
    | _ -> state, [], []

/// §7.2「GateRejected → Cancelled（Deadlineまで待たず同じstep内で即時に遷移する）」。
/// `GatedEffect.gate`が返す拒否リストのうち、このstepでPlacementが発行したPending（`SetRect`）
/// またはPendingPresence（`SetPresence`、v2.41）に対応するRequestIdだけが対象になる（他ドメインの
/// RequestIdは`Map.tryFind`が`None`を返すだけで無害）。`SetPresence`側は`Desired`を取り消さない
/// （`PendingPresence`の型直前のコメントと同じ理由）。
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
            | None ->
                match Map.tryFind rid st.PendingPresence with
                | Some _ -> { st with PendingPresence = Map.remove rid st.PendingPresence }, cmds @ [ CancelTimer (TimerId.Placement (PresenceDeadline rid)) ]
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
    (dispatchSeq: int64 option)
    (state: PlacementState)
    : PlacementState * Effect list * TimerCommand<TimerId> list * (WindowId * EffectFailure) option =
    match result with
    | Ok () ->
        // `Mismatch → Reevaluate`（v2.42）：発行が実際に受け付けられた時点のSampleSequenceを記録する
        // （`tryMismatchObservation`の条件1・2）。「受付が成功した」こと自体はSatisfiedにしない、という既存の規約
        // は変えない（§7.2「SetRectのネイティブAPI呼出が成功しても、目標矩形を観測するまではSatisfied
        // にしない」）。
        match Map.tryFind rid state.Pending with
        | Some pr -> { state with Pending = Map.add rid { pr with DispatchSeq = dispatchSeq } state.Pending }, [], [], None
        | None -> state, [], [], None
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
        RetryAttempts = affectedWindows |> Set.fold (fun m w -> Map.remove w m) state.RetryAttempts
        // Pendingを全て取り消すので、旧Epochの目標を覚えておく理由も消える（v2.42）。
        InFlightTargets = affectedWindows |> Set.fold (fun m w -> Map.remove w m) state.InFlightTargets },
    cancelCmds

/// 窓`w`を指す既存の`PendingPresence`があれば取り除き`CancelTimer`する（§7.2「同じ窓へ新しい意図が
/// 生じた場合、古い要求を失効させる」と同じ理由。`PendingPresence`の型直前のコメント参照）。
/// `onWorkspaceHidden`/`onWorkspaceShown`の両方が、新しいエントリを追加する前に必ず呼ぶ。
let private supersedePendingPresence (w: WindowId) (state: PlacementState) : PlacementState * TimerCommand<TimerId> list =
    let existing = state.PendingPresence |> Map.toSeq |> Seq.filter (fun (_, pp) -> pp.Window = w) |> Seq.map fst |> List.ofSeq
    { state with PendingPresence = existing |> List.fold (fun m rid -> Map.remove rid m) state.PendingPresence },
    existing |> List.map (fun rid -> CancelTimer (TimerId.Placement (PresenceDeadline rid)))

/// `DomainEvents.WorkspaceHidden`（§4.4「WMが見せているべきか。非表示の実装方式に依存しない」、v2.31で
/// 初めて消費する）：そのWorkspaceに属していた窓を`HiddenByWm`にし、`SetPresence(_, w, false)`を発行
/// する。「実際に非表示になったか」の観測による確認（`checkPendingPresenceSatisfied`、v2.41）のため
/// `PendingPresence`を作り`PresenceDeadline`タイマーを開始する。各窓ごとに`allocateRequestId`で新しい
/// RequestIdを採番する（SetRectと同じ採番元を共有、§7.1）。
let onWorkspaceHidden (now: int64) (windows: WindowId list) (state: PlacementState) : PlacementState * Effect list * TimerCommand<TimerId> list =
    windows
    |> List.fold
        (fun (st: PlacementState, effects, cmds) w ->
            let stSuperseded, supersedeCmds = supersedePendingPresence w st
            let rid, st' = allocateRequestId stSuperseded
            let deadline = now + pendingDeadlineMs
            let pp = { Window = w; DesiredVisible = false; IssuedAt = now; Deadline = deadline }
            { st' with
                Desired = Map.add w HiddenByWm st'.Desired
                PendingPresence = Map.add rid pp st'.PendingPresence },
            effects @ [ SetPresence (rid, w, false) ],
            cmds @ supersedeCmds @ [ StartTimer (TimerId.Placement (PresenceDeadline rid), pendingDeadlineMs) ])
        (state, [], [])

/// `DomainEvents.WorkspaceShown`（上記の逆）：そのWorkspaceに属する窓を`Shown None`にし、
/// `SetPresence(_, w, true)`を発行する。目標矩形はまだ無い（`Shown None`、`DesiredPresence`の型直前の
/// コメント参照）——このWorkspaceが`current`/`visible`になった同じstepのReconcileが実際の目標を計算し、
/// `dispatchPlacements`の`desiredChanged`判定は`Shown (Some _)`以外を常に「変化あり」と見なすので、
/// 必ず新しいSetRectが発行される（隠されていた間に実際の位置が変わっていたかもしれないため、
/// 古い矩形を信用しない）。`PendingPresence`/`PresenceDeadline`の開始は`onWorkspaceHidden`と同じ（v2.41）。
let onWorkspaceShown (now: int64) (windows: WindowId list) (state: PlacementState) : PlacementState * Effect list * TimerCommand<TimerId> list =
    windows
    |> List.fold
        (fun (st: PlacementState, effects, cmds) w ->
            let stSuperseded, supersedeCmds = supersedePendingPresence w st
            let rid, st' = allocateRequestId stSuperseded
            let deadline = now + pendingDeadlineMs
            let pp = { Window = w; DesiredVisible = true; IssuedAt = now; Deadline = deadline }
            { st' with
                Desired = Map.add w (Shown None) st'.Desired
                PendingPresence = Map.add rid pp st'.PendingPresence },
            effects @ [ SetPresence (rid, w, true) ],
            cmds @ supersedeCmds @ [ StartTimer (TimerId.Placement (PresenceDeadline rid), pendingDeadlineMs) ])
        (state, [], [])

/// `TimerElapsed (Placement (PresenceDeadline rid))`：単発の諦め（`PendingPresence`の型直前のコメント
/// 参照、Retry/Backoffは対象外）。`Desired`は取り消さない——`onWorkspaceHidden`/`onWorkspaceShown`は
/// 呼ばれるたびに無条件で`SetPresence`を再発行するため（`dispatchPlacements`の`desiredChanged`相当の
/// 判定を持たない）、ここで取り消しても次回の挙動は変わらない。
let onPresenceDeadlineTimerFired (now: int64) (rid: RequestId) (state: PlacementState) : PlacementState * TimerCommand<TimerId> list =
    match Map.tryFind rid state.PendingPresence with
    | Some pp when awaitDeadline (Some pp.Deadline) now -> { state with PendingPresence = Map.remove rid state.PendingPresence }, []
    | _ -> state, [] // 古い発火、またはもう追跡していない（確認済み、GateRejected、窓の消滅等）

/// 窓の消滅時、`Geometry`/`Movability`/`TargetIntegrity`/`IntegrityReprobes`/`Desired`/`Pending`/
/// `PendingPresence`/`Epochs`を除く（§4.4「各ドメインの信念は窓が消滅した時点でのみ除去する」）。
/// 動いている再probeタイマーと、その窓宛てのPending/PendingPresenceが持つ`PendingDeadline`/
/// `PresenceDeadline`タイマーがあれば`CancelTimer`を返す（review-phase-a-round1.md 4-3と同じ理由、
/// Windows.ClassificationDeadline/Health.Reprobesと同じ扱い）。`WmState.vanishWindow`から呼ぶ。
let onWindowVanished (w: WindowId) (state: PlacementState) : PlacementState * TimerCommand<TimerId> list =
    let integrityCancel = if Map.containsKey w state.IntegrityReprobes then [ CancelTimer (TimerId.Placement (IntegrityReprobe w)) ] else []
    let pendingForWindow = state.Pending |> Map.toSeq |> Seq.filter (fun (_, pr) -> pr.Window = w) |> Seq.map fst |> List.ofSeq
    let pendingCancel = pendingForWindow |> List.map (fun rid -> CancelTimer (TimerId.Placement (PendingDeadline rid)))
    let pendingPresenceForWindow = state.PendingPresence |> Map.toSeq |> Seq.filter (fun (_, pp) -> pp.Window = w) |> Seq.map fst |> List.ofSeq
    let pendingPresenceCancel = pendingPresenceForWindow |> List.map (fun rid -> CancelTimer (TimerId.Placement (PresenceDeadline rid)))
    { state with
        Geometry = Map.remove w state.Geometry
        Movability = Map.remove w state.Movability
        TargetIntegrity = Map.remove w state.TargetIntegrity
        IntegrityReprobes = Map.remove w state.IntegrityReprobes
        Desired = Map.remove w state.Desired
        Pending = pendingForWindow |> List.fold (fun p rid -> Map.remove rid p) state.Pending
        PendingPresence = pendingPresenceForWindow |> List.fold (fun p rid -> Map.remove rid p) state.PendingPresence
        Epochs = Map.remove w state.Epochs
        RetryAttempts = Map.remove w state.RetryAttempts
        InFlightTargets = Map.remove w state.InFlightTargets
        MinWidth = Map.remove w state.MinWidth
        DpiBehavior = Map.remove w state.DpiBehavior
        DpiCrossing = Map.remove w state.DpiCrossing },
    integrityCancel @ pendingCancel @ pendingPresenceCancel
