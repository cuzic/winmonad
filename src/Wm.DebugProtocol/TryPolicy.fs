/// `op=trypolicy`（architecture.md §10.2/§10.3、v2.152）の応答DTO。Opus敵対的相談
/// `opus-review-fork-design.md`の結論：本格的なFork（Journal replay経由のCounterfactual
/// Simulation、ADR-006でPhase Dまで意図的に先送り）ではなく、§10.3が実際に必要とする唯一の具体例
/// ——候補Policyの下で現在の各窓がどう分類されるかの差分——だけを実装する。クライアントは候補
/// `PolicyDto`だけを送り、Hostが自分の現在の`reducer.State`に対して実際に評価し直す（§14/Analyzerと
/// 同じ「クライアントの自己申告を信用しない」原則）。「Fork」ではなく正直に「TryPolicy」と呼ぶ
/// ——`ForkAsync`/`TryPolicyAsync`風の§10.3のAPI例が想定する「Snapshotを疑似OSへコピーする」
/// 時間軸分離や、Revision不一致検出による拒否（§10.4）は実装しない。
module Wm.DebugProtocol.TryPolicy

open Wm.Contracts.Identifiers
open Wm.Contracts.SharedVocabulary

/// `Wm.Core.WmState.ClassifyOutcome`の写し。
type TryPolicyOutcomeDto =
    | WouldClassifyAsDto of Management * matchedRule: PolicyRuleId option
    | WouldAwaitEvidenceDto of blockedBy: PolicyRuleId
    /// タイトル証拠の付け替えができなかった（Opusレビュー F-a、`Wm.Core.WmState.remapTitleMatches`
    /// 型直前のコメント参照）：候補のPolicyが参照する`TitleContains`パターンのうち、現在のPolicyの
    /// 下では一度も測定されていないものがある。生タイトルはどこにも保存されないため
    /// （E-6）、原理的にこれ以上は分からない。
    | UndeterminableDto of missingTitlePatterns: string list

/// 1回の`op=trypolicy`応答。`Revision`はこの結果を計算した時点の`reducer.Sequence`
/// （`SnapshotDto.Revision`と同じ意味、§10.4「楽観的排他の起点」）——クライアントはこの値と
/// 自分が見たSnapshotのRevisionを比較することで、計算対象が古くなっていないかを自分で判断できる
/// （Hostは拒否しない——Fork本体のRevision不一致検出はこのスライスの対象外）。
type TryPolicyResultDto = {
    Revision: int64
    Outcomes: (WindowId * TryPolicyOutcomeDto) list
}
