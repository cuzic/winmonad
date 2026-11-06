/// Wm.DebugProtocol：JournalDtoのJSON往復を検査する（v2.147、Phase C Inspect「Journalを見る」）。
/// `Entries`自体が既に`JsonNode`（`Wm.Journal.JournalWriter.entryToJson`が作る形をそのまま運ぶ、
/// `Journal.fs`の型直前のコメント参照）なので、`JsonNode`は値の構造的等価性を持たない——
/// `.ToJsonString()`で文字列化して比較する。
module Wm.DebugProtocol.Tests.JournalJsonTests

open System.Text.Json.Nodes
open Xunit
open Wm.DebugProtocol.Journal
open Wm.DebugProtocol.JournalJson

let private mkEntry (sequence: int64) (kind: string) : JsonNode =
    let o = JsonObject()
    o.["sequence"] <- JsonValue.Create(sequence)
    o.["kind"] <- JsonValue.Create(kind)
    o :> JsonNode

[<Fact>]
let ``空のJournalDtoが往復する`` () =
    let original = { Entries = [] }
    let json = journalDtoToJson original
    let parsed = JsonNode.Parse(json.ToJsonString())
    let roundTripped = journalDtoOfJson parsed
    Assert.Empty(roundTripped.Entries)

[<Fact>]
let ``複数のJournalEntry(任意のJsonNode)が順序を保って往復する`` () =
    let e1 = mkEntry 1L "input-received"
    let e2 = mkEntry 2L "effect-call-returned-kind"
    let e3 = mkEntry 3L "pending-satisfied"
    let original = { Entries = [ e1; e2; e3 ] }
    let json = journalDtoToJson original
    let parsed = JsonNode.Parse(json.ToJsonString())
    let roundTripped = journalDtoOfJson parsed
    Assert.Equal(3, List.length roundTripped.Entries)
    let originalTexts = original.Entries |> List.map (fun n -> n.ToJsonString())
    let roundTrippedTexts = roundTripped.Entries |> List.map (fun n -> n.ToJsonString())
    Assert.Equal<string list>(originalTexts, roundTrippedTexts)
