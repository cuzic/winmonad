/// Wm.DebugProtocol：ExplainDtoのJSON往復を検査する（v2.147、Phase C Inspect「Explain」）。
/// `DecisionTraceDto`は再帰ではないが複数のケースを持ち、`PlanRejectedDto`はさらに`ValidationErrorDto`
/// のリストを運ぶため、入れ子のケースも必ず含めて検査する。
module Wm.DebugProtocol.Tests.ExplainJsonTests

open System.Text.Json.Nodes
open Xunit
open Wm.Contracts.Identifiers
open Wm.Contracts.SharedVocabulary
open Wm.DebugProtocol.Explain
open Wm.DebugProtocol.ExplainJson

let private roundTrip (e: ExplainDto) : ExplainDto =
    let json = explainDtoToJson e
    let parsed = JsonNode.Parse(json.ToJsonString())
    explainDtoOfJson parsed

[<Fact>]
let ``分類未確定・DecisionTrace無しのExplainが往復する`` () =
    let e: ExplainDto =
        { Window = WindowId 1L
          Management = None
          MatchedRule = None
          RecentTrace = [] }
    Assert.Equal<ExplainDto>(e, roundTrip e)

[<Fact>]
let ``分類済みのExplainが往復する`` () =
    let e: ExplainDto =
        { Window = WindowId 42L
          Management = Some Tiled
          MatchedRule = Some(PolicyRuleId "r1")
          RecentTrace = [] }
    Assert.Equal<ExplainDto>(e, roundTrip e)

[<Fact>]
let ``InconsistencySuspected/LayoutNotRegistered/FrameInsetAppliedのDecisionTraceが往復する`` () =
    let e: ExplainDto =
        { Window = WindowId 1L
          Management = Some Floating
          MatchedRule = None
          RecentTrace =
            [ InconsistencySuspectedDto("placement", "pending without desired")
              LayoutNotRegisteredDto(LayoutId "does-not-exist", 1)
              FrameInsetAppliedDto(
                  RequestId 7L,
                  { X = 0; Y = 0; Width = 800; Height = 600 },
                  { X = -7; Y = 0; Width = 814; Height = 607 }
              ) ] }
    Assert.Equal<ExplainDto>(e, roundTrip e)

[<Fact>]
let ``LayoutInvocationFailed(LayoutErrorの全ケース)とPlanRejected(入れ子のValidationError全ケース)が往復する`` () =
    let e: ExplainDto =
        { Window = WindowId 2L
          Management = Some Tiled
          MatchedRule = Some(PolicyRuleId "wide-master-stack")
          RecentTrace =
            [ LayoutInvocationFailedDto(LayoutId "master-stack", InvalidInputDto "negative ratio")
              LayoutInvocationFailedDto(LayoutId "columns", UnsatisfiableDto "zero columns")
              LayoutInvocationFailedDto(LayoutId "custom", LayoutCrashedDto "System.NullReferenceException")
              PlanRejectedDto(
                  LayoutId "master-stack",
                  [ UnmanagedOrDeadWindowDto(WindowId 3L)
                    DuplicateInPlanDto(WindowId 4L)
                    NonPositiveSizeDto(WindowId 5L, -10, 0)
                    ArithmeticOverflowDto(WindowId 6L)
                    OutsideWorkAreaDto(WindowId 7L, { X = 5000; Y = 0; Width = 100; Height = 100 })
                    ExcludedTargetDto(WindowId 8L)
                    MissingPlacementDto(WindowId 9L) ]
              ) ] }
    Assert.Equal<ExplainDto>(e, roundTrip e)

[<Fact>]
let ``explainOptionToJson/OfJsonはSome/Noneの両方を往復する`` () =
    let e: ExplainDto = { Window = WindowId 1L; Management = Some Tiled; MatchedRule = None; RecentTrace = [] }
    let someJson = JsonNode.Parse((explainOptionToJson (Some e)).ToJsonString())
    Assert.Equal<ExplainDto option>(Some e, explainOptionOfJson someJson)
    let noneJson = JsonNode.Parse((explainOptionToJson None).ToJsonString())
    Assert.Equal<ExplainDto option>(None, explainOptionOfJson noneJson)
