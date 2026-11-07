module Wm.DebugProtocol.TryPolicyJson

open System.Text.Json.Nodes
open Wm.Contracts.Identifiers
open Wm.DebugProtocol.SnapshotJson
open Wm.DebugProtocol.TryPolicy

let private windowIdToJson (WindowId w: WindowId) : JsonNode = JsonValue.Create(w) :> JsonNode
let private windowIdOfJson (node: JsonNode) : WindowId = WindowId(node.GetValue<int64>())

let private tryPolicyOutcomeDtoToJson (o: TryPolicyOutcomeDto) : JsonNode =
    let n = JsonObject()
    match o with
    | WouldClassifyAsDto(m, ruleId) ->
        n.["tag"] <- JsonValue.Create("would-classify-as")
        n.["management"] <- JsonValue.Create(managementTag m)
        n.["matchedRule"] <- policyRuleIdOptionToJson ruleId
    | WouldAwaitEvidenceDto ruleId ->
        n.["tag"] <- JsonValue.Create("would-await-evidence")
        n.["blockedBy"] <- policyRuleIdOptionToJson (Some ruleId)
    | UndeterminableDto missing ->
        n.["tag"] <- JsonValue.Create("undeterminable")
        let arr = JsonArray()
        for p in missing do arr.Add(JsonValue.Create(p))
        n.["missingTitlePatterns"] <- arr
    n :> JsonNode

let private tryPolicyOutcomeDtoOfJson (node: JsonNode) : TryPolicyOutcomeDto =
    match node.["tag"].GetValue<string>() with
    | "would-classify-as" ->
        WouldClassifyAsDto(managementOfTag (node.["management"].GetValue<string>()), policyRuleIdOptionOfJson node.["matchedRule"])
    | "would-await-evidence" ->
        match policyRuleIdOptionOfJson node.["blockedBy"] with
        | Some ruleId -> WouldAwaitEvidenceDto ruleId
        | None -> failwith "TryPolicyJson: 'would-await-evidence' missing blockedBy"
    | "undeterminable" ->
        let missing = node.["missingTitlePatterns"].AsArray() |> Seq.map (fun v -> v.GetValue<string>()) |> List.ofSeq
        UndeterminableDto missing
    | other -> failwithf "TryPolicyJson: unrecognized TryPolicyOutcomeDto tag '%s'" other

let tryPolicyResultDtoToJson (result: TryPolicyResultDto) : JsonNode =
    let o = JsonObject()
    o.["revision"] <- JsonValue.Create(result.Revision)
    let arr = JsonArray()
    for (w, outcome) in result.Outcomes do
        let entry = JsonObject()
        entry.["window"] <- windowIdToJson w
        entry.["outcome"] <- tryPolicyOutcomeDtoToJson outcome
        arr.Add(entry)
    o.["outcomes"] <- arr
    o :> JsonNode

let tryPolicyResultDtoOfJson (node: JsonNode) : TryPolicyResultDto =
    { Revision = node.["revision"].GetValue<int64>()
      Outcomes =
        node.["outcomes"].AsArray()
        |> Seq.map (fun entry -> windowIdOfJson entry.["window"], tryPolicyOutcomeDtoOfJson entry.["outcome"])
        |> List.ofSeq }
