/// Wm.DebugProtocol：AnalyzerReportDtoのJSON往復を検査する（v2.149、Phase C Inspect「Analyzer」）。
module Wm.DebugProtocol.Tests.AnalyzerJsonTests

open System.Text.Json.Nodes
open Xunit
open Wm.Contracts.Identifiers
open Wm.Contracts.SharedVocabulary
open Wm.DebugProtocol.Analyzer
open Wm.DebugProtocol.AnalyzerJson

let private roundTrip (r: AnalyzerReportDto) : AnalyzerReportDto =
    let json = analyzerReportDtoToJson r
    let parsed = JsonNode.Parse(json.ToJsonString())
    analyzerReportDtoOfJson parsed

[<Fact>]
let ``問題無しのAnalyzerReportDtoが往復する`` () =
    let r = { Errors = [] }
    Assert.Equal<AnalyzerReportDto>(r, roundTrip r)

[<Fact>]
let ``AnalyzerIssueDtoの全6ケース(ruleId=Some/Noneの両方含む)が往復する`` () =
    let r =
        { Errors =
            [ DuplicateWindowRuleIdDto(PolicyRuleId "r1")
              DuplicateLayoutRuleIdDto(PolicyRuleId "r2")
              UnregisteredLayoutDto(LayoutId "does-not-exist", 1, Some(PolicyRuleId "r3"))
              UnregisteredLayoutDto(LayoutId "default-layout-case", 1, None)
              InvalidMasterRatioDto(1.5M, Some(PolicyRuleId "r4"))
              InvalidMasterRatioDto(-0.1M, None)
              InvalidColumnCountDto(0, Some(PolicyRuleId "r5"))
              InvalidColumnCountDto(-3, None)
              DuplicateWorkspaceAssignmentDto(WorkspaceId 2) ] }
    Assert.Equal<AnalyzerReportDto>(r, roundTrip r)
