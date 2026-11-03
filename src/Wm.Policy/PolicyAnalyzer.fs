/// ADR-019実装順8：PolicyAnalyzer.fs。architecture.md §5.5。
///
/// 初期必須はこの表の上2行のみ（§5.5「初期スコープの縮小」）：
/// (1) 重複RuleId／未登録LayoutId・ApiVersion不一致
/// (2) MasterRatioが0未満または1以上／ColumnCountが0以下
/// `WorkspaceAssignments`重複検査（§5.5の表の3行目、`Error`）はv2.80で追加した——`WmState.initial`
/// がv2.80で`PolicyIR.WorkspaceAssignments`を実際にTopologyへ写すようになったため（型直前の
/// コメント参照）、同じ`WorkspaceId`が複数回現れる設定ミスが`Map.ofList`の「後が勝つ」規則で
/// 黙って解決されてしまう問題が初めて実害を持つようになった。
/// PotentialOverlap検出・不到達ルール検出・EvidenceRequirement推論・
/// `ApplicationFrameHost.exe`直接マッチ警告は依然「できればやる」に位置づけられており、本ファイルでは
/// 実装しない（意図的な範囲外、後続のラウンドで追加してよい——`Warning`severityの導入が必要）。
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
    | DuplicateWorkspaceAssignment of WorkspaceId // v2.80、§5.5の表3行目（R3-1）

/// `Error`のみを持つ（§5.5の初期必須2行、およびv2.80で追加した3行目はいずれも`Error`）。
/// Warning/Unknown/EvidenceRequirementは「できればやる」項目の実装時に追加する。
type AnalyzerReport = { Errors: AnalyzerIssue list }

let private duplicates (ids: 'a list) : 'a list when 'a: equality =
    ids
    |> List.countBy id
    |> List.filter (fun (_, count) -> count > 1)
    |> List.map fst

let private checkDuplicateRuleIds (policy: PolicyIR) : AnalyzerIssue list =
    (policy.WindowRules |> List.map (fun r -> r.Id) |> duplicates |> List.map DuplicateWindowRuleId)
    @ (policy.LayoutRules |> List.map (fun r -> r.Id) |> duplicates |> List.map DuplicateLayoutRuleId)

/// §5.5の表3行目（v2.80、R3-1）：`WorkspaceAssignments`に同じ`WorkspaceId`が2回以上現れないか。
let private checkDuplicateWorkspaceAssignments (policy: PolicyIR) : AnalyzerIssue list =
    policy.WorkspaceAssignments |> List.map fst |> duplicates |> List.map DuplicateWorkspaceAssignment

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
        @ checkDuplicateWorkspaceAssignments policy
        @ checkRegisteredLayouts manifest policy
        @ checkLayoutArgsRanges policy }

let isClean (report: AnalyzerReport) : bool = List.isEmpty report.Errors
