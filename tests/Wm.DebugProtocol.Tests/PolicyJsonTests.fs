/// Wm.DebugProtocol：PolicyDtoのJSON往復を検査する（v2.145、Phase C Inspect「Policyを見る」）。
/// `WindowPredicateDto`/`LayoutPredicateDto`は再帰的なケース(`All`/`AnyOf`/`Not`/`Both`)を持つため、
/// 入れ子のケースも必ず含めて検査する——葉のケースだけでは再帰の符号化が本当に正しいか確認できない。
module Wm.DebugProtocol.Tests.PolicyJsonTests

open System.Text.Json.Nodes
open Xunit
open Wm.Contracts.Identifiers
open Wm.Contracts.Observation
open Wm.Contracts.SharedVocabulary
open Wm.DebugProtocol.Policy
open Wm.DebugProtocol.PolicyJson

let private roundTrip (p: PolicyDto) : PolicyDto =
    let json = policyDtoToJson p
    let text = json.ToJsonString()
    let parsed = JsonNode.Parse(text)
    policyDtoOfJson parsed

[<Fact>]
let ``ルールの無い既定のPolicyが往復する`` () =
    let p: PolicyDto =
        { SchemaVersion = 1
          WindowRules = []
          DefaultManagement = Tiled
          LayoutRules = []
          DefaultLayout = { Id = LayoutId "monocle"; ApiVersion = 1; Args = NoArgs }
          WorkspaceAssignments = [] }
    Assert.Equal<PolicyDto>(p, roundTrip p)

[<Fact>]
let ``葉のWindowPredicateを使うWindowRule(AppIs/DialogIs)が往復する(recommendedWindowRules相当)`` () =
    let p: PolicyDto =
        { SchemaVersion = 1
          WindowRules =
            [ { Id = PolicyRuleId "exclude-desktop-shell"
                Priority = 100
                When = AppIsDto (Exe, @"C:\Windows\explorer.exe")
                Apply = Excluded }
              { Id = PolicyRuleId "float-dialogs"
                Priority = 50
                When = DialogIsDto true
                Apply = Floating } ]
          DefaultManagement = Tiled
          LayoutRules = []
          DefaultLayout = { Id = LayoutId "monocle"; ApiVersion = 1; Args = NoArgs }
          WorkspaceAssignments = [] }
    Assert.Equal<PolicyDto>(p, roundTrip p)

[<Fact>]
let ``入れ子のWindowPredicate(All/AnyOf/Not)が往復する`` () =
    let nested =
        AllDto
            [ NotDto (TitleContainsDto "ignore-me")
              AnyOfDto [ DialogIsDto true; MinWidthAtLeastDto 700 ]
              MovableIsDto true ]
    let p: PolicyDto =
        { SchemaVersion = 1
          WindowRules = [ { Id = PolicyRuleId "nested-rule"; Priority = 10; When = nested; Apply = Floating } ]
          DefaultManagement = Tiled
          LayoutRules = []
          DefaultLayout = { Id = LayoutId "monocle"; ApiVersion = 1; Args = NoArgs }
          WorkspaceAssignments = [] }
    Assert.Equal<PolicyDto>(p, roundTrip p)

[<Fact>]
let ``LayoutRule(入れ子のLayoutPredicate Both含む)とLayoutArgsの全ケースが往復する`` () =
    let p: PolicyDto =
        { SchemaVersion = 1
          WindowRules = []
          DefaultManagement = Tiled
          LayoutRules =
            [ { Id = PolicyRuleId "wide-master-stack"
                Priority = 10
                When = BothDto [ TiledCountAtLeastDto 2; MonitorWidthAtLeastDto 1600 ]
                Use = { Id = LayoutId "master-stack"; ApiVersion = 1; Args = MasterRatio 0.6M } }
              { Id = PolicyRuleId "columns-always"
                Priority = 1
                When = AlwaysDto
                Use = { Id = LayoutId "columns"; ApiVersion = 1; Args = ColumnCount 3 } } ]
          DefaultLayout = { Id = LayoutId "monocle"; ApiVersion = 1; Args = NoArgs }
          WorkspaceAssignments = [] }
    Assert.Equal<PolicyDto>(p, roundTrip p)

[<Fact>]
let ``WorkspaceAssignments(PrimaryMonitor/MonitorKeyの両方)が往復する`` () =
    let p: PolicyDto =
        { SchemaVersion = 1
          WindowRules = []
          DefaultManagement = Tiled
          LayoutRules = []
          DefaultLayout = { Id = LayoutId "monocle"; ApiVersion = 1; Args = NoArgs }
          WorkspaceAssignments = [ WorkspaceId 0, PrimaryMonitorDto; WorkspaceId 1, MonitorKeyDto "DISPLAY2" ] }
    Assert.Equal<PolicyDto>(p, roundTrip p)
