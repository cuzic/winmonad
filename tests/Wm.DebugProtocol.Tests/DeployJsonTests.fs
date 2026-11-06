module Wm.DebugProtocol.Tests.DeployJsonTests

open System.Text.Json.Nodes
open Xunit
open Wm.Contracts.Identifiers
open Wm.DebugProtocol.Analyzer
open Wm.DebugProtocol.Deploy
open Wm.DebugProtocol.DeployJson

let private roundTrip (r: DeployResultDto) : DeployResultDto =
    let json = deployResultDtoToJson r
    let parsed = JsonNode.Parse(json.ToJsonString())
    deployResultDtoOfJson parsed

[<Fact>]
let ``DeploySucceededDtoが往復する`` () =
    let r = DeploySucceededDto "abc123"
    Assert.Equal<DeployResultDto>(r, roundTrip r)

[<Fact>]
let ``DeployIntegrityFailedDtoが往復する`` () =
    let r = DeployIntegrityFailedDto("expected-hash", "actual-hash")
    Assert.Equal<DeployResultDto>(r, roundTrip r)

[<Fact>]
let ``DeployAnalysisFailedDto(複数AnalyzerIssueDtoを含む)が往復する`` () =
    let r =
        DeployAnalysisFailedDto
            { Errors =
                [ DuplicateWindowRuleIdDto(PolicyRuleId "dup-1")
                  InvalidMasterRatioDto(1.5M, None) ] }
    Assert.Equal<DeployResultDto>(r, roundTrip r)
