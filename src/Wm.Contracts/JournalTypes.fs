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

/// Phase Aの範囲に絞ったPayload。`PolicyStaleAnalysis`のような、永続化はするがPolicy等の型（AnalyzerReport）を
/// 運ぶ種別の表現は21章の未決事項のまま（本ペイロードには含めない）。
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
