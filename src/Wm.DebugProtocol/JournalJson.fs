/// `JournalDto`のJSON化。`Entries`自体が既に`JsonNode`なので、包むだけ。
module Wm.DebugProtocol.JournalJson

open System.Text.Json.Nodes
open Wm.DebugProtocol.Journal

let journalDtoToJson (j: JournalDto) : JsonNode =
    let o = JsonObject()
    let arr = JsonArray()
    for e in j.Entries do
        arr.Add(e)
    o.["entries"] <- arr
    o :> JsonNode

let journalDtoOfJson (node: JsonNode) : JournalDto =
    { Entries = node.["entries"].AsArray() |> Seq.toList }
