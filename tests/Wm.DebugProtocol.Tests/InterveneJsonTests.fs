module Wm.DebugProtocol.Tests.InterveneJsonTests

open System.Text.Json.Nodes
open Xunit
open Wm.Contracts.Identifiers
open Wm.Contracts.SharedVocabulary
open Wm.DebugProtocol.Intervene
open Wm.DebugProtocol.InterveneJson

let private roundTripRequest (r: InterveneRequestDto) : InterveneRequestDto =
    let json = interveneRequestDtoToJson r
    let parsed = JsonNode.Parse(json.ToJsonString())
    interveneRequestDtoOfJson parsed

let private roundTripResult (r: InterveneResultDto) : InterveneResultDto =
    let json = interveneResultDtoToJson r
    let parsed = JsonNode.Parse(json.ToJsonString())
    interveneResultDtoOfJson parsed

[<Fact>]
let ``SetManagementToDto(CorrelationId=Some)が往復する`` () =
    let r = { CorrelationId = Some "corr-1"; Command = SetManagementToDto(WindowId 42L, Floating) }
    Assert.Equal<InterveneRequestDto>(r, roundTripRequest r)

[<Fact>]
let ``ClearOverrideDto(CorrelationId=None)が往復する`` () =
    let r = { CorrelationId = None; Command = ClearOverrideDto(WindowId 7L) }
    Assert.Equal<InterveneRequestDto>(r, roundTripRequest r)

[<Fact>]
let ``InterveneAcceptedDtoが往復する`` () =
    let r = InterveneAcceptedDto 123L
    Assert.Equal<InterveneResultDto>(r, roundTripResult r)

[<Fact>]
let ``InterveneRejectedDtoが往復する`` () =
    let r = InterveneRejectedDto "window is not known/managed"
    Assert.Equal<InterveneResultDto>(r, roundTripResult r)
