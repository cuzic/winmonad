/// `PolicyDto`のJSON往復。Wm.Journalの手書きJsonNode規約（§9.2、`{"tag": ..., ...}`、タグは安定
/// 文字列コード）を`SnapshotJson.fs`と同じくそのまま踏襲する。
module Wm.DebugProtocol.PolicyJson

open System.Text.Json.Nodes
open Wm.Contracts.Identifiers
open Wm.Contracts.Observation
open Wm.Contracts.SharedVocabulary
open Wm.DebugProtocol.Policy
open Wm.DebugProtocol.SnapshotJson

let private appIdSourceTag (s: AppIdSource) : string =
    match s with
    | Exe -> "exe"
    | HostExe -> "host-exe"
    | Aumid -> "aumid"
    | CoreWindowExe -> "core-window-exe"

let private appIdSourceOfTag (tag: string) : AppIdSource =
    match tag with
    | "exe" -> Exe
    | "host-exe" -> HostExe
    | "aumid" -> Aumid
    | "core-window-exe" -> CoreWindowExe
    | other -> failwithf "PolicyJson: unrecognized AppIdSource tag '%s'" other

let rec private windowPredicateDtoToJson (p: WindowPredicateDto) : JsonNode =
    let o = JsonObject()
    match p with
    | AnyWindowDto -> o.["tag"] <- JsonValue.Create("any")
    | AppIsDto (src, v) ->
        o.["tag"] <- JsonValue.Create("app-is")
        o.["source"] <- JsonValue.Create(appIdSourceTag src)
        o.["value"] <- JsonValue.Create(v)
    | TitleContainsDto v ->
        o.["tag"] <- JsonValue.Create("title-contains")
        o.["value"] <- JsonValue.Create(v)
    | DialogIsDto v ->
        o.["tag"] <- JsonValue.Create("dialog-is")
        o.["value"] <- JsonValue.Create(v)
    | MinWidthAtLeastDto v ->
        o.["tag"] <- JsonValue.Create("min-width-at-least")
        o.["value"] <- JsonValue.Create(v)
    | MovableIsDto v ->
        o.["tag"] <- JsonValue.Create("movable-is")
        o.["value"] <- JsonValue.Create(v)
    | AllDto children ->
        o.["tag"] <- JsonValue.Create("all")
        let arr = JsonArray()
        for c in children do arr.Add(windowPredicateDtoToJson c)
        o.["children"] <- arr
    | AnyOfDto children ->
        o.["tag"] <- JsonValue.Create("any-of")
        let arr = JsonArray()
        for c in children do arr.Add(windowPredicateDtoToJson c)
        o.["children"] <- arr
    | NotDto child ->
        o.["tag"] <- JsonValue.Create("not")
        o.["child"] <- windowPredicateDtoToJson child
    o :> JsonNode

let rec private windowPredicateDtoOfJson (node: JsonNode) : WindowPredicateDto =
    let tag = node.["tag"].GetValue<string>()
    match tag with
    | "any" -> AnyWindowDto
    | "app-is" -> AppIsDto (appIdSourceOfTag (node.["source"].GetValue<string>()), node.["value"].GetValue<string>())
    | "title-contains" -> TitleContainsDto (node.["value"].GetValue<string>())
    | "dialog-is" -> DialogIsDto (node.["value"].GetValue<bool>())
    | "min-width-at-least" -> MinWidthAtLeastDto (node.["value"].GetValue<int>())
    | "movable-is" -> MovableIsDto (node.["value"].GetValue<bool>())
    | "all" -> AllDto (node.["children"].AsArray() |> Seq.map windowPredicateDtoOfJson |> List.ofSeq)
    | "any-of" -> AnyOfDto (node.["children"].AsArray() |> Seq.map windowPredicateDtoOfJson |> List.ofSeq)
    | "not" -> NotDto (windowPredicateDtoOfJson node.["child"])
    | other -> failwithf "PolicyJson: unrecognized WindowPredicateDto tag '%s'" other

let rec private layoutPredicateDtoToJson (p: LayoutPredicateDto) : JsonNode =
    let o = JsonObject()
    match p with
    | AlwaysDto -> o.["tag"] <- JsonValue.Create("always")
    | TiledCountAtLeastDto v ->
        o.["tag"] <- JsonValue.Create("tiled-count-at-least")
        o.["value"] <- JsonValue.Create(v)
    | MonitorWidthAtLeastDto v ->
        o.["tag"] <- JsonValue.Create("monitor-width-at-least")
        o.["value"] <- JsonValue.Create(v)
    | BothDto children ->
        o.["tag"] <- JsonValue.Create("both")
        let arr = JsonArray()
        for c in children do arr.Add(layoutPredicateDtoToJson c)
        o.["children"] <- arr
    o :> JsonNode

let rec private layoutPredicateDtoOfJson (node: JsonNode) : LayoutPredicateDto =
    let tag = node.["tag"].GetValue<string>()
    match tag with
    | "always" -> AlwaysDto
    | "tiled-count-at-least" -> TiledCountAtLeastDto (node.["value"].GetValue<int>())
    | "monitor-width-at-least" -> MonitorWidthAtLeastDto (node.["value"].GetValue<int>())
    | "both" -> BothDto (node.["children"].AsArray() |> Seq.map layoutPredicateDtoOfJson |> List.ofSeq)
    | other -> failwithf "PolicyJson: unrecognized LayoutPredicateDto tag '%s'" other

let private layoutArgsToJson (a: LayoutArgs) : JsonNode =
    let o = JsonObject()
    match a with
    | NoArgs -> o.["tag"] <- JsonValue.Create("no-args")
    | MasterRatio v ->
        o.["tag"] <- JsonValue.Create("master-ratio")
        o.["value"] <- JsonValue.Create(v)
    | ColumnCount v ->
        o.["tag"] <- JsonValue.Create("column-count")
        o.["value"] <- JsonValue.Create(v)
    o :> JsonNode

let private layoutArgsOfJson (node: JsonNode) : LayoutArgs =
    match node.["tag"].GetValue<string>() with
    | "no-args" -> NoArgs
    | "master-ratio" -> MasterRatio (node.["value"].GetValue<decimal>())
    | "column-count" -> ColumnCount (node.["value"].GetValue<int>())
    | other -> failwithf "PolicyJson: unrecognized LayoutArgs tag '%s'" other

let private layoutRefToJson (r: LayoutRef) : JsonNode =
    let (LayoutId lid) = r.Id
    let o = JsonObject()
    o.["id"] <- JsonValue.Create(lid)
    o.["apiVersion"] <- JsonValue.Create(r.ApiVersion)
    o.["args"] <- layoutArgsToJson r.Args
    o :> JsonNode

let private layoutRefOfJson (node: JsonNode) : LayoutRef =
    { Id = LayoutId (node.["id"].GetValue<string>())
      ApiVersion = node.["apiVersion"].GetValue<int>()
      Args = layoutArgsOfJson node.["args"] }

let private monitorSelectorDtoToJson (s: MonitorSelectorDto) : JsonNode =
    let o = JsonObject()
    match s with
    | PrimaryMonitorDto -> o.["tag"] <- JsonValue.Create("primary")
    | MonitorKeyDto k ->
        o.["tag"] <- JsonValue.Create("key")
        o.["value"] <- JsonValue.Create(k)
    o :> JsonNode

let private monitorSelectorDtoOfJson (node: JsonNode) : MonitorSelectorDto =
    match node.["tag"].GetValue<string>() with
    | "primary" -> PrimaryMonitorDto
    | "key" -> MonitorKeyDto (node.["value"].GetValue<string>())
    | other -> failwithf "PolicyJson: unrecognized MonitorSelectorDto tag '%s'" other

let private windowRuleDtoToJson (r: WindowRuleDto) : JsonNode =
    let (PolicyRuleId rid) = r.Id
    let o = JsonObject()
    o.["id"] <- JsonValue.Create(rid)
    o.["priority"] <- JsonValue.Create(r.Priority)
    o.["when"] <- windowPredicateDtoToJson r.When
    o.["apply"] <- JsonValue.Create(managementTag r.Apply)
    o :> JsonNode

let private windowRuleDtoOfJson (node: JsonNode) : WindowRuleDto =
    { Id = PolicyRuleId (node.["id"].GetValue<string>())
      Priority = node.["priority"].GetValue<int>()
      When = windowPredicateDtoOfJson node.["when"]
      Apply = managementOfTag (node.["apply"].GetValue<string>()) }

let private layoutRuleDtoToJson (r: LayoutRuleDto) : JsonNode =
    let (PolicyRuleId rid) = r.Id
    let o = JsonObject()
    o.["id"] <- JsonValue.Create(rid)
    o.["priority"] <- JsonValue.Create(r.Priority)
    o.["when"] <- layoutPredicateDtoToJson r.When
    o.["use"] <- layoutRefToJson r.Use
    o :> JsonNode

let private layoutRuleDtoOfJson (node: JsonNode) : LayoutRuleDto =
    { Id = PolicyRuleId (node.["id"].GetValue<string>())
      Priority = node.["priority"].GetValue<int>()
      When = layoutPredicateDtoOfJson node.["when"]
      Use = layoutRefOfJson node.["use"] }

let policyDtoToJson (p: PolicyDto) : JsonNode =
    let o = JsonObject()
    o.["schemaVersion"] <- JsonValue.Create(p.SchemaVersion)
    let windowRulesArr = JsonArray()
    for r in p.WindowRules do windowRulesArr.Add(windowRuleDtoToJson r)
    o.["windowRules"] <- windowRulesArr
    o.["defaultManagement"] <- JsonValue.Create(managementTag p.DefaultManagement)
    let layoutRulesArr = JsonArray()
    for r in p.LayoutRules do layoutRulesArr.Add(layoutRuleDtoToJson r)
    o.["layoutRules"] <- layoutRulesArr
    o.["defaultLayout"] <- layoutRefToJson p.DefaultLayout
    let assignmentsArr = JsonArray()
    for (WorkspaceId wid, sel) in p.WorkspaceAssignments do
        let a = JsonObject()
        a.["workspace"] <- JsonValue.Create(wid)
        a.["monitor"] <- monitorSelectorDtoToJson sel
        assignmentsArr.Add(a)
    o.["workspaceAssignments"] <- assignmentsArr
    o :> JsonNode

let policyDtoOfJson (node: JsonNode) : PolicyDto =
    { SchemaVersion = node.["schemaVersion"].GetValue<int>()
      WindowRules = node.["windowRules"].AsArray() |> Seq.map windowRuleDtoOfJson |> List.ofSeq
      DefaultManagement = managementOfTag (node.["defaultManagement"].GetValue<string>())
      LayoutRules = node.["layoutRules"].AsArray() |> Seq.map layoutRuleDtoOfJson |> List.ofSeq
      DefaultLayout = layoutRefOfJson node.["defaultLayout"]
      WorkspaceAssignments =
        node.["workspaceAssignments"].AsArray()
        |> Seq.map (fun a -> WorkspaceId (a.["workspace"].GetValue<int>()), monitorSelectorDtoOfJson a.["monitor"])
        |> List.ofSeq }
