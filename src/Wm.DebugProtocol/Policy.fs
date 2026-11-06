/// Wm.DebugProtocol：Phase CのLive REPL（architecture.md §10.2「Inspect：Policyを見る」）が
/// プロセス境界を越えて運ぶPolicyの型付きDTO。`Snapshot.fs`と同じ理由（§17の依存方向：
/// Wm.DebugProtocolはContractsのみに依存し、`Wm.Policy`の内部型をそのまま晒さない）で、
/// `Wm.Policy.PolicyTypes.PolicyIR`/`WindowPredicate`/`LayoutPredicate`/
/// `Wm.Policy.MonitorSelector.MonitorSelector`の写しを専用に定義する——リーフの値型
/// （`Management`/`LayoutRef`/`AppIdSource`/`WorkspaceId`/`PolicyRuleId`）は元から`Wm.Contracts`に
/// あるのでそのまま再利用する。`PolicyIR -> PolicyDto`の射影自体（`Wm.Policy`に依存できる側）は
/// `Wm.Host/ResidentHost.fs`の`policyDtoOf`に置く（`SnapshotDto`の`snapshotOf`と同じ配置）。
module Wm.DebugProtocol.Policy

open Wm.Contracts.Identifiers
open Wm.Contracts.Observation
open Wm.Contracts.SharedVocabulary

/// `Wm.Policy.MonitorSelector.MonitorSelector`の写し。
type MonitorSelectorDto =
    | PrimaryMonitorDto
    | MonitorKeyDto of string

/// `Wm.Policy.PolicyTypes.WindowPredicate`の写し（再帰ケースも含めて同じ形）。
type WindowPredicateDto =
    | AnyWindowDto
    | AppIsDto of AppIdSource * string
    | TitleContainsDto of string
    | DialogIsDto of bool
    | MinWidthAtLeastDto of int
    | MovableIsDto of bool
    | AllDto of WindowPredicateDto list
    | AnyOfDto of WindowPredicateDto list
    | NotDto of WindowPredicateDto

type WindowRuleDto = {
    Id: PolicyRuleId
    Priority: int
    When: WindowPredicateDto
    Apply: Management
}

/// `Wm.Policy.PolicyTypes.LayoutPredicate`の写し。
type LayoutPredicateDto =
    | AlwaysDto
    | TiledCountAtLeastDto of int
    | MonitorWidthAtLeastDto of int
    | BothDto of LayoutPredicateDto list

type LayoutRuleDto = {
    Id: PolicyRuleId
    Priority: int
    When: LayoutPredicateDto
    Use: LayoutRef
}

/// `Wm.Policy.PolicyTypes.PolicyIR`の写し。
type PolicyDto = {
    SchemaVersion: int
    WindowRules: WindowRuleDto list
    DefaultManagement: Management
    LayoutRules: LayoutRuleDto list
    DefaultLayout: LayoutRef
    WorkspaceAssignments: (WorkspaceId * MonitorSelectorDto) list
}
