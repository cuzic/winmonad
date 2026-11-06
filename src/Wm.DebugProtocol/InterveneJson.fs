module Wm.DebugProtocol.InterveneJson

open System.Text.Json.Nodes
open Wm.Contracts.Identifiers
open Wm.DebugProtocol.SnapshotJson
open Wm.DebugProtocol.Intervene

let interveneCommandDtoToJson (cmd: InterveneCommandDto) : JsonNode =
    let o = JsonObject()
    match cmd with
    | SetManagementToDto(WindowId w, m) ->
        o.["tag"] <- JsonValue.Create("set-management-to")
        o.["windowId"] <- JsonValue.Create(w)
        o.["management"] <- JsonValue.Create(managementTag m)
    | ClearOverrideDto(WindowId w) ->
        o.["tag"] <- JsonValue.Create("clear-override")
        o.["windowId"] <- JsonValue.Create(w)
    o :> JsonNode

let interveneCommandDtoOfJson (node: JsonNode) : InterveneCommandDto =
    match node.["tag"].GetValue<string>() with
    | "set-management-to" ->
        SetManagementToDto(WindowId(node.["windowId"].GetValue<int64>()), managementOfTag (node.["management"].GetValue<string>()))
    | "clear-override" -> ClearOverrideDto(WindowId(node.["windowId"].GetValue<int64>()))
    | other -> failwithf "InterveneJson: unrecognized InterveneCommandDto tag '%s'" other

let interveneRequestDtoToJson (req: InterveneRequestDto) : JsonNode =
    let o = JsonObject()
    o.["correlationId"] <- (req.CorrelationId |> Option.map JsonValue.Create<string> |> Option.defaultValue null)
    o.["command"] <- interveneCommandDtoToJson req.Command
    o :> JsonNode

let interveneRequestDtoOfJson (node: JsonNode) : InterveneRequestDto =
    { CorrelationId = if isNull node.["correlationId"] then None else Some(node.["correlationId"].GetValue<string>())
      Command = interveneCommandDtoOfJson node.["command"] }

let interveneResultDtoToJson (result: InterveneResultDto) : JsonNode =
    let o = JsonObject()
    match result with
    | InterveneAcceptedDto sequence ->
        o.["tag"] <- JsonValue.Create("accepted")
        o.["sequence"] <- JsonValue.Create(sequence)
    | InterveneRejectedDto reason ->
        o.["tag"] <- JsonValue.Create("rejected")
        o.["reason"] <- JsonValue.Create(reason)
    o :> JsonNode

let interveneResultDtoOfJson (node: JsonNode) : InterveneResultDto =
    match node.["tag"].GetValue<string>() with
    | "accepted" -> InterveneAcceptedDto(node.["sequence"].GetValue<int64>())
    | "rejected" -> InterveneRejectedDto(node.["reason"].GetValue<string>())
    | other -> failwithf "InterveneJson: unrecognized InterveneResultDto tag '%s'" other
