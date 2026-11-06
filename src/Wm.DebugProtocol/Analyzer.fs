/// Wm.DebugProtocol：Phase CのLive REPL（architecture.md §10.2「Inspect：Analyzerを見る」、v2.149）が
/// プロセス境界を越えて運ぶAnalyzerの型。`Policy.fs`と同じ方針（§17の依存方向、`Wm.Policy`の内部型を
/// そのまま晒さない）で、`Wm.Policy.PolicyAnalyzer.AnalyzerIssue`/`AnalyzerReport`の写しを専用に
/// 定義する。
module Wm.DebugProtocol.Analyzer

open Wm.Contracts.Identifiers
open Wm.Contracts.SharedVocabulary

/// `Wm.Policy.PolicyAnalyzer.AnalyzerIssue`の写し。
type AnalyzerIssueDto =
    | DuplicateWindowRuleIdDto of PolicyRuleId
    | DuplicateLayoutRuleIdDto of PolicyRuleId
    | UnregisteredLayoutDto of LayoutId * apiVersion: int * ruleId: PolicyRuleId option
    | InvalidMasterRatioDto of decimal * ruleId: PolicyRuleId option
    | InvalidColumnCountDto of int * ruleId: PolicyRuleId option
    | DuplicateWorkspaceAssignmentDto of WorkspaceId

/// `Wm.Policy.PolicyAnalyzer.AnalyzerReport`の写し。
type AnalyzerReportDto = { Errors: AnalyzerIssueDto list }
