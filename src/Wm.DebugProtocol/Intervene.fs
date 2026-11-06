/// `op=intervene`（architecture.md §10.2/§10.3、v2.151）の応答DTO。Opus敵対的相談
/// `opus-review-intervene-trust-boundary.md`のQ2-Bに基づき、architecture.md §10.2のR2-25（「前提検査は
/// `Queued of Sequence`で受理した後、非同期にJournal上の`InterveneRejected`ドメインイベントとして
/// 届く」）を改め、`ResidentHost.EnqueueActivatePolicy`と同じ「Reducer所有スレッドが実際に`step`を
/// 実行し終えるまで応答を待つ」方式にした——`PreconditionFailed`もこの同期応答にそのまま乗る。
/// `InterveneCommandDto`は`Wm.Contracts.CoreCommand.InterveneCommand`をそのまま運ぶ、閉じた許可リスト
/// （`CoreCommand`そのものは運ばない、Q3「CloseFocused等の危険なケースを型で排除する」設計）。
module Wm.DebugProtocol.Intervene

open Wm.Contracts.Identifiers
open Wm.Contracts.SharedVocabulary

/// クライアントが送る命令（v2.151、`InterveneCommand`と同じ2ケースに限定）。
type InterveneCommandDto =
    | SetManagementToDto of WindowId * Management
    | ClearOverrideDto of WindowId

/// クライアントが送るリクエスト全体。`ClientPid`/`ClientImage`はここには無い——Hostが接続済みパイプ
/// 自身から取得する（`InspectPipeServer.fs`参照、§14「クライアントの自己申告を信用しない」と同じ
/// 原則）。`CorrelationId`はクライアントが任意に付け、タイムアウト後の追跡に使う。
type InterveneRequestDto = {
    CorrelationId: string option
    Command: InterveneCommandDto
}

/// Hostの応答（同期）。`WmState.step`が返した`DecisionTrace`（`InterveneAccepted`/`InterveneRejected`）
/// をそのまま射影する。
type InterveneResultDto =
    | InterveneAcceptedDto of sequence: int64
    | InterveneRejectedDto of reason: string
