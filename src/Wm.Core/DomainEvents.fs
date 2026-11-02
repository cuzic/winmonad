/// ADR-019決定7：§19の一覧に無かった追加ファイル。`DomainEvent`のDU。ペイロードのうち基準(1)〜(3)で
/// Contractsへ移さなかった小さな型（MoveCause、ShownBy、PendingResolvedの結果DU）をここに置く
/// （decision2：`DomainEvent`自体がCheckpoint/Journal非対象なので§9.2のDTO対象でもない）。
module Wm.Core.DomainEvents

open Wm.Contracts.Identifiers
open Wm.Contracts.ErrorTypes
open Wm.Contracts.SharedVocabulary
open Wm.Contracts.GatedEffect

/// §4.4「観測されたモニターと意図されたモニターが食い違うとき」の原因ラベル（R3-4）。
type MoveCause =
    | OsInduced
    | UserDrag
    | Unattributed

/// Workspaceが表示された原因（§4.4、R7-1）。フォーカスを要求するかどうかが原因で変わる。
type ShownBy =
    | UserView
    | ForegroundFollow
    | Reconcile

/// §7.2のPendingの終端遷移。段③④でPlacementが出し、Healthが読む（decision2）。
type PendingOutcome =
    | Satisfied
    | Mismatch
    | TimedOut
    | CancelledTargetGone
    | CancelledHung
    | CancelledDenied of DenialKind
    | CancelledGateRejected of GateRejection
    | CancelledWindowDestroyed
    | Superseded

/// 後段のドメインへ伝える事実（例：WindowClassified、WorkspaceShown）。§7.1の表参照。
/// Placement/Windows/Workspacesドメインがまだ無いため、それらが出す予定のケースも含めて
/// 型としては今から確定する（ADR-018：ドメイン間は常にDomainEventで渡す規約を崩さないため）。
type DomainEvent =
    | ForegroundChanged of WindowId option
    | WindowUnmanaged of WindowId
    | MoveAttributed of WindowId * MoveCause
    | TopologySettled of Wm.Contracts.Identifiers.MonitorSnapshot list
    | FocusTargetRequested of WindowId
    | WindowClassified of WindowId * EffectiveManagement * homeWorkspace: WorkspaceId option
    | WorkspaceShown of WorkspaceId * ShownBy
    | WorkspaceHidden of WorkspaceId
    | CurrentChanged of WorkspaceId
    | CloseRequested of WindowId
    | FocusObserved of WindowId
    | PendingResolved of RequestId * PendingOutcome
