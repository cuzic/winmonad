/// `AnalyzerReportDto`のJSON往復。`SnapshotJson.fs`/`PolicyJson.fs`/`ExplainJson.fs`と同じ
/// `{"tag": ..., ...}`規約。
module Wm.DebugProtocol.AnalyzerJson

open System.Text.Json.Nodes
open Wm.Contracts.Identifiers
open Wm.Contracts.SharedVocabulary
open Wm.DebugProtocol.Analyzer
open Wm.DebugProtocol.SnapshotJson

let private analyzerIssueDtoToJson (issue: AnalyzerIssueDto) : JsonNode =
    let o = JsonObject()
    match issue with
    | DuplicateWindowRuleIdDto(PolicyRuleId rid) ->
        o.["tag"] <- JsonValue.Create("duplicate-window-rule-id")
        o.["ruleId"] <- JsonValue.Create(rid)
    | DuplicateLayoutRuleIdDto(PolicyRuleId rid) ->
        o.["tag"] <- JsonValue.Create("duplicate-layout-rule-id")
        o.["ruleId"] <- JsonValue.Create(rid)
    | UnregisteredLayoutDto(LayoutId lid, apiVersion, ruleId) ->
        o.["tag"] <- JsonValue.Create("unregistered-layout")
        o.["layoutId"] <- JsonValue.Create(lid)
        o.["apiVersion"] <- JsonValue.Create(apiVersion)
        o.["ruleId"] <- policyRuleIdOptionToJson ruleId
    | InvalidMasterRatioDto(ratio, ruleId) ->
        o.["tag"] <- JsonValue.Create("invalid-master-ratio")
        o.["ratio"] <- JsonValue.Create(ratio)
        o.["ruleId"] <- policyRuleIdOptionToJson ruleId
    | InvalidColumnCountDto(count, ruleId) ->
        o.["tag"] <- JsonValue.Create("invalid-column-count")
        o.["count"] <- JsonValue.Create(count)
        o.["ruleId"] <- policyRuleIdOptionToJson ruleId
    | DuplicateWorkspaceAssignmentDto(WorkspaceId wsid) ->
        o.["tag"] <- JsonValue.Create("duplicate-workspace-assignment")
        o.["workspace"] <- JsonValue.Create(wsid)
    o :> JsonNode

let private analyzerIssueDtoOfJson (node: JsonNode) : AnalyzerIssueDto =
    match node.["tag"].GetValue<string>() with
    | "duplicate-window-rule-id" -> DuplicateWindowRuleIdDto(PolicyRuleId(node.["ruleId"].GetValue<string>()))
    | "duplicate-layout-rule-id" -> DuplicateLayoutRuleIdDto(PolicyRuleId(node.["ruleId"].GetValue<string>()))
    | "unregistered-layout" ->
        UnregisteredLayoutDto(
            LayoutId(node.["layoutId"].GetValue<string>()),
            node.["apiVersion"].GetValue<int>(),
            policyRuleIdOptionOfJson node.["ruleId"]
        )
    | "invalid-master-ratio" -> InvalidMasterRatioDto(node.["ratio"].GetValue<decimal>(), policyRuleIdOptionOfJson node.["ruleId"])
    | "invalid-column-count" -> InvalidColumnCountDto(node.["count"].GetValue<int>(), policyRuleIdOptionOfJson node.["ruleId"])
    | "duplicate-workspace-assignment" -> DuplicateWorkspaceAssignmentDto(WorkspaceId(node.["workspace"].GetValue<int>()))
    | other -> failwithf "AnalyzerJson: unrecognized AnalyzerIssueDto tag '%s'" other

let analyzerReportDtoToJson (report: AnalyzerReportDto) : JsonNode =
    let o = JsonObject()
    let arr = JsonArray()
    for issue in report.Errors do
        arr.Add(analyzerIssueDtoToJson issue)
    o.["errors"] <- arr
    o :> JsonNode

let analyzerReportDtoOfJson (node: JsonNode) : AnalyzerReportDto =
    { Errors = node.["errors"].AsArray() |> Seq.map analyzerIssueDtoOfJson |> List.ofSeq }
