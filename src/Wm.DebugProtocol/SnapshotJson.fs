/// `SnapshotDto`のJSON往復。Wm.Journalの手書きJsonNode規約（タグは安定文字列コード、
/// Option無しはCLR nullなJsonNodeとして表す、§9.2）をそのまま踏襲する——Reflectionベースの
/// シリアライザを使わないのはADR-018の依存方向と同じ理由（決定的・監査可能な配線を優先）。
module Wm.DebugProtocol.SnapshotJson

open System.Text.Json.Nodes
open Wm.Contracts.Identifiers
open Wm.Contracts.SharedVocabulary
open Wm.DebugProtocol.Snapshot

/// `PolicyJson.fs`も同じ`Management`の符号化に合意する必要があるため公開する（v2.145）。
let managementTag (m: Management) : string =
    match m with
    | Tiled -> "tiled"
    | Floating -> "floating"
    | Excluded -> "excluded"

let managementOfTag (tag: string) : Management =
    match tag with
    | "tiled" -> Tiled
    | "floating" -> Floating
    | "excluded" -> Excluded
    | other -> failwithf "SnapshotJson: unrecognized Management tag '%s'" other

let private managementOptionToJson (m: Management option) : JsonNode =
    match m with
    | Some v -> JsonValue.Create(managementTag v) :> JsonNode
    | None -> null

let private managementOptionOfJson (node: JsonNode) : Management option =
    if isNull node then None else Some (managementOfTag (node.GetValue<string>()))

let private policyRuleIdOptionToJson (r: PolicyRuleId option) : JsonNode =
    match r with
    | Some (PolicyRuleId rid) -> JsonValue.Create(rid) :> JsonNode
    | None -> null

let private policyRuleIdOptionOfJson (node: JsonNode) : PolicyRuleId option =
    if isNull node then None else Some (PolicyRuleId (node.GetValue<string>()))

let windowSnapshotDtoToJson (w: WindowSnapshotDto) : JsonNode =
    let o = JsonObject()
    let (WindowId wid) = w.Window
    o.["window"] <- JsonValue.Create(wid)
    o.["management"] <- managementOptionToJson w.Management
    o.["matchedRule"] <- policyRuleIdOptionToJson w.MatchedRule
    o :> JsonNode

let windowSnapshotDtoOfJson (node: JsonNode) : WindowSnapshotDto =
    { Window = WindowId (node.["window"].GetValue<int64>())
      Management = managementOptionOfJson node.["management"]
      MatchedRule = policyRuleIdOptionOfJson node.["matchedRule"] }

let snapshotDtoToJson (s: SnapshotDto) : JsonNode =
    let o = JsonObject()
    o.["revision"] <- JsonValue.Create(s.Revision)
    let arr = JsonArray()
    for w in s.Windows do
        arr.Add(windowSnapshotDtoToJson w)
    o.["windows"] <- arr
    o :> JsonNode

let snapshotDtoOfJson (node: JsonNode) : SnapshotDto =
    { Revision = node.["revision"].GetValue<int64>()
      Windows =
        node.["windows"].AsArray()
        |> Seq.map windowSnapshotDtoOfJson
        |> List.ofSeq }
