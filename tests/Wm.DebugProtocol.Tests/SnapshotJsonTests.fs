/// Wm.DebugProtocol：SnapshotDtoのJSON往復を検査する。architecture.md §10.1「REPLが得る
/// Snapshotはコピーであり、内部の現在状態へ書込みアクセスできない」——プロセス境界を越えて
/// 運ばれる実際のワイヤ形式がここでの対象。
module Wm.DebugProtocol.Tests.SnapshotJsonTests

open System.Text.Json.Nodes
open Xunit
open Wm.Contracts.Identifiers
open Wm.Contracts.SharedVocabulary
open Wm.DebugProtocol.Snapshot
open Wm.DebugProtocol.SnapshotJson

let private roundTrip (s: SnapshotDto) : SnapshotDto =
    let json = snapshotDtoToJson s
    let text = json.ToJsonString()
    let parsed = JsonNode.Parse(text)
    snapshotDtoOfJson parsed

[<Fact>]
let ``空のSnapshotが往復する`` () =
    let s = { Revision = 0L; Windows = [] }
    Assert.Equal<SnapshotDto>(s, roundTrip s)

[<Fact>]
let ``分類済み(Management/MatchedRule両方Some)の窓が往復する`` () =
    let s =
        { Revision = 42L
          Windows =
            [ { Window = WindowId 7L
                Management = Some Tiled
                MatchedRule = Some (PolicyRuleId "exclude-desktop-shell") } ] }
    Assert.Equal<SnapshotDto>(s, roundTrip s)

[<Fact>]
let ``分類未確定(Management/MatchedRule両方None)の窓が往復する`` () =
    let s =
        { Revision = 1L
          Windows = [ { Window = WindowId 99L; Management = None; MatchedRule = None } ] }
    Assert.Equal<SnapshotDto>(s, roundTrip s)

[<Fact>]
let ``DefaultManagementで確定(ManagementはSome、MatchedRuleはNone)の窓が往復する`` () =
    let s =
        { Revision = 2L
          Windows = [ { Window = WindowId 5L; Management = Some Floating; MatchedRule = None } ] }
    Assert.Equal<SnapshotDto>(s, roundTrip s)

[<Fact>]
let ``全てのManagementケースがタグとして往復する`` () =
    for m in [ Tiled; Floating; Excluded ] do
        let s = { Revision = 0L; Windows = [ { Window = WindowId 1L; Management = Some m; MatchedRule = None } ] }
        Assert.Equal<SnapshotDto>(s, roundTrip s)

[<Fact>]
let ``複数窓の順序が往復後も保たれる`` () =
    let s =
        { Revision = 10L
          Windows =
            [ { Window = WindowId 1L; Management = Some Tiled; MatchedRule = None }
              { Window = WindowId 2L; Management = Some Floating; MatchedRule = Some (PolicyRuleId "float-dialogs") }
              { Window = WindowId 3L; Management = None; MatchedRule = None } ] }
    Assert.Equal<SnapshotDto>(s, roundTrip s)
