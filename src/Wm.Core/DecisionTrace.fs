/// ADR-019決定7：`StepResult.Trace`の要素。§4.4の`update`の戻り値、§7.1の「導出不整合の疑い」の記録先。
/// Journalに永続化しない揮発性の値で、ExplainはReplayで再構成する（§18）。
module Wm.Core.DecisionTrace

/// 現時点ではCoreが自力で全域関数として検出できる「導出不整合の疑い」だけを持つ
/// （§7.1「起こり得ない組合せに出会ったら、例外を投げずに入力を捨て、DecisionTraceに記録する」）。
/// `ValidationError`（Wm.Layouts）や`PolicyRuleId`（Wm.Policy）を運ぶケースは、それらのドメインの
/// 実装が揃った時点で追加する（本ファイルはそのための拡張点として残す）。
type DecisionTrace =
    | InconsistencySuspected of domain: string * detail: string
