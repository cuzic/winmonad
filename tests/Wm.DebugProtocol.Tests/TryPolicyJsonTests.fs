module Wm.DebugProtocol.Tests.TryPolicyJsonTests

open System.Text.Json.Nodes
open Xunit
open Wm.Contracts.Identifiers
open Wm.Contracts.SharedVocabulary
open Wm.DebugProtocol.TryPolicy
open Wm.DebugProtocol.TryPolicyJson

let private roundTrip (r: TryPolicyResultDto) : TryPolicyResultDto =
    let json = tryPolicyResultDtoToJson r
    let parsed = JsonNode.Parse(json.ToJsonString())
    tryPolicyResultDtoOfJson parsed

[<Fact>]
let ``空のOutcomesでも往復する`` () =
    let r = { Revision = 0L; Outcomes = [] }
    Assert.Equal<TryPolicyResultDto>(r, roundTrip r)

[<Fact>]
let ``WouldClassifyAsDto(MatchedRuleあり/無し両方)が往復する`` () =
    let r =
        { Revision = 42L
          Outcomes =
            [ WindowId 1L, WouldClassifyAsDto(Floating, Some(PolicyRuleId "rule-1"))
              WindowId 2L, WouldClassifyAsDto(Tiled, None) ] }
    Assert.Equal<TryPolicyResultDto>(r, roundTrip r)

[<Fact>]
let ``WouldAwaitEvidenceDtoが往復する`` () =
    let r = { Revision = 7L; Outcomes = [ WindowId 3L, WouldAwaitEvidenceDto(PolicyRuleId "blocked-rule") ] }
    Assert.Equal<TryPolicyResultDto>(r, roundTrip r)

[<Fact>]
let ``UndeterminableDto(複数パターン)が往復する`` () =
    let r = { Revision = 7L; Outcomes = [ WindowId 4L, UndeterminableDto [ "新規パターン1"; "新規パターン2" ] ] }
    Assert.Equal<TryPolicyResultDto>(r, roundTrip r)
