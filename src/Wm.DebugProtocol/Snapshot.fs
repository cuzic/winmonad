/// Wm.DebugProtocol：Phase CのLive REPL（architecture.md §10）がプロセス境界を越えて運ぶ
/// 型付きDTO。decision4（DTOはそれが写す型と同じプロジェクトに置く、Wm.Journalと同じ方針）に
/// 反するように見えるが、こちらは「Core内部の型をそのまま晒す」のではなく、10.1「生のHWND/
/// ポインターやHost内部の可変オブジェクト参照を渡さない」という独立の境界の型なので、専用
/// プロジェクトに新規に定義する。§17依存方向：Wm.DebugProtocol（Contractsのみ）。
module Wm.DebugProtocol.Snapshot

open Wm.Contracts.Identifiers
open Wm.Contracts.SharedVocabulary

/// Phase CのInspect（§10.2）の最小スライス：`WmClient.SnapshotAsync()`が返す、窓1つぶんの
/// 読み取り専用ビュー。`Management = None`は分類未確定（`AwaitingEvidence`）を表す——Policy側の
/// `ClassificationResult`をそのまま運ばず、Transport層が必要とする最小限の形へ射影する
/// （10.3「REPLが得るSnapshotはコピーであり、内部の現在状態へ書込みアクセスできない」）。
type WindowSnapshotDto = {
    Window: WindowId
    Management: Management option
    MatchedRule: PolicyRuleId option
}

/// 1回のInspect呼び出しが返す全体像。`Revision`は10.4のFork/Intervene前提検査が参照する
/// 楽観的排他の起点（具体的なRevision型自体はCore側で別途定義済み、ここでは単調増加する
/// 不透明な値として運ぶ）。
type SnapshotDto = {
    Revision: int64
    Windows: WindowSnapshotDto list
}

/// architecture.md §10.3のエラー三層分離（v2.4、ADR-014/X-2対応）をそのまま実装する。
/// `Result<_, string>`へ潰すと「前提不成立」「権限不足」「IPC切断」「受理済みだが成否不明」を
/// 区別できなくなる、という設計意図を型でそのまま保つ。
type TransportError =
    | ConnectionLost
    | Timeout of waitedMs: int
    | PayloadTooLarge

type AuthorizationError =
    | TokenExpired
    | TokenInvalid
    | IntegrityMismatch
