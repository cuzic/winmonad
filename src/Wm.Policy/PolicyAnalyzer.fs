/// ADR-019実装順8：PolicyAnalyzer.fs。architecture.md §5.5。
///
/// 初期必須はこの表の上2行のみ（§5.5「初期スコープの縮小」）：
/// (1) 重複RuleId／未登録LayoutId・ApiVersion不一致
/// (2) MasterRatioが0未満または1以上／ColumnCountが0以下
/// PotentialOverlap検出・不到達ルール検出・EvidenceRequirement推論・
/// `ApplicationFrameHost.exe`直接マッチ警告・`WorkspaceAssignments`重複検査は「できればやる」に
/// 位置づけられており、本ファイルでは実装しない（意図的な範囲外、後続のラウンドで追加してよい）。
module Wm.Policy.PolicyAnalyzer

open Wm.Contracts.Identifiers
open Wm.Contracts.SharedVocabulary
open Wm.Policy.PolicyTypes

type AnalyzerIssue =
    | DuplicateWindowRuleId of PolicyRuleId
    | DuplicateLayoutRuleId of PolicyRuleId
    | UnregisteredLayout of LayoutId * apiVersion: int * ruleId: PolicyRuleId option // None = DefaultLayout
    | InvalidMasterRatio of decimal * ruleId: PolicyRuleId option
    | InvalidColumnCount of int * ruleId: PolicyRuleId option

/// `Error`のみを持つ（§5.5の初期必須2行はいずれも`Error`）。Warning/Unknown/EvidenceRequirementは
/// 「できればやる」項目の実装時に追加する。
type AnalyzerReport = { Errors: AnalyzerIssue list }

let private duplicateIds (ids: PolicyRuleId list) : PolicyRuleId list =
    ids
    |> List.countBy id
    |> List.filter (fun (_, count) -> count > 1)
    |> List.map fst

let private checkDuplicateRuleIds (policy: PolicyIR) : AnalyzerIssue list =
    (policy.WindowRules |> List.map (fun r -> r.Id) |> duplicateIds |> List.map DuplicateWindowRuleId)
    @ (policy.LayoutRules |> List.map (fun r -> r.Id) |> duplicateIds |> List.map DuplicateLayoutRuleId)

let private allLayoutRefs (policy: PolicyIR) : (LayoutRef * PolicyRuleId option) list =
    (policy.LayoutRules |> List.map (fun r -> r.Use, Some r.Id)) @ [ policy.DefaultLayout, None ]

let private checkRegisteredLayouts (manifest: RegistryManifest) (policy: PolicyIR) : AnalyzerIssue list =
    let registered = manifest |> List.map (fun e -> e.LayoutId, e.ApiVersion) |> Set.ofList
    allLayoutRefs policy
    |> List.choose (fun (layoutRef, ruleId) ->
        if Set.contains (layoutRef.Id, layoutRef.ApiVersion) registered then
            None
        else
            Some (UnregisteredLayout (layoutRef.Id, layoutRef.ApiVersion, ruleId)))

let private checkLayoutArgsRanges (policy: PolicyIR) : AnalyzerIssue list =
    allLayoutRefs policy
    |> List.choose (fun (layoutRef, ruleId) ->
        match layoutRef.Args with
        | MasterRatio r when r < 0.0m || r >= 1.0m -> Some (InvalidMasterRatio (r, ruleId))
        | ColumnCount c when c <= 0 -> Some (InvalidColumnCount (c, ruleId))
        | _ -> None)

let analyze (manifest: RegistryManifest) (policy: PolicyIR) : AnalyzerReport =
    { Errors =
        checkDuplicateRuleIds policy
        @ checkRegisteredLayouts manifest policy
        @ checkLayoutArgsRanges policy }

let isClean (report: AnalyzerReport) : bool = List.isEmpty report.Errors
