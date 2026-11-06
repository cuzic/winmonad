/// 決定6層9（ADR-019）：ADR-004の一次事実のうち、Contractsの型だけで書ける永続化対象の種別。
/// architecture.md §9.2。Phase Aの範囲に収まる種別に絞る（R5-03：ReplIntervention等Phase C/D向けは対象外）。
module Wm.Contracts.JournalTypes

open Wm.Contracts.Identifiers
open Wm.Contracts.ErrorTypes
open Wm.Contracts.CoreCommand

type JournalKind =
    | InputReceived
    | EffectDispatchStarted
    | EffectCallReturnedKind
    | EffectBatchReturnedKind
    | InternalFaultKind
    | JournalDegradedKind
    | JournalRecoveredKind
    | SafetyRecoveryAction
    | ObservationReceived
    | PendingSatisfied
    | PendingTimedOut
    /// §14のPolicyArtifact採用記録（v2.148）。「以前に承認済み」の根拠はこのJournalエントリ自身の
    /// `JournalEntry.PolicyArtifactHash`に置く（ファイル自身の申告ではなくHost自身の記録を信頼の
    /// 根拠にする、§14の設計どおり）。
    | PolicyActivatedKind
    /// 起動時/Rollback時の「報告のみモード」（§14、v2.4/R2-11対応）：再分析しないという決定の下でも
    /// 現行Analyzerを報告専用で実行し、Errorが出たら採用はブロックせずここへ記録するだけ。
    | PolicyStaleAnalysisKind

/// Phase Aの範囲に絞ったPayload。
type JournalPayload =
    /// InputReceived：Reducerへ投入した入力そのもの。
    | InputPayload of InputEvent
    /// EffectDispatchStarted：発行を試みたEffect（Shellへ渡す直前の記録）。
    | EffectDispatchPayload of RequestId option * Effect
    /// EffectCallReturned/EffectBatchReturned：NativeCallRecordベースの生の結果のみ。EffectFailureは含めない（ADR-014/1-2・X-3）。
    | EffectCallReturnedPayload of RequestId * Result<unit, NativeCallRecord>
    | EffectBatchReturnedPayload of BatchId * (RequestId * Result<unit, NativeCallRecord>) list
    | InternalFaultPayload of componentName: string
    | JournalDegradedPayload of since: int64
    | JournalRecoveredPayload of gapStart: int64 * gapEnd: int64
    /// 8.4のGatedEffect迂回経路の記録（R2-6）。
    | SafetyRecoveryActionPayload of description: string
    | PendingOutcomePayload of RequestId * satisfied: bool
    /// PolicyActivatedKind：どの経路で採用されたか（"deploy" | "rollback" | "startup"）。実際に採用
    /// された`ArtifactIdentity`のハッシュは、このPayloadではなく`JournalEntry.PolicyArtifactHash`
    /// （envelope自身が元から持つフィールド）に載る——§14「ファイル自身の申告ではなくHost自身の
    /// 採用記録を信頼の根拠にする」の実体がこのKind＋envelopeのhashの組（v2.148）。
    | PolicyActivatedPayload of source: string
    /// PolicyStaleAnalysisKind：報告専用モードでAnalyzerが検出した問題の要約（人間が読むための
    /// 文字列。`AnalyzerIssue`の型全体をもう一度並行してJournal用DTOへ写すのはこのスライスの範囲を
    /// 大きく超えるため、Journal Inspect（v2.147）と同じ「簡潔な表現で十分」という判断を踏襲する）。
    | PolicyStaleAnalysisPayload of issueSummaries: string list

/// Journal Envelope（§9.2）。
type JournalEntry = {
    SchemaVersion: int
    SessionId: string
    Sequence: int64
    MonotonicTicks: int64
    WallTimeUtc: System.DateTimeOffset
    CorrelationId: string option
    CausationId: string option
    PolicyArtifactHash: string
    CoreBuildId: string
    Kind: JournalKind
    Payload: JournalPayload
}
