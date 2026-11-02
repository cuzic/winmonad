/// ADR-019実装順8：PolicyTypes.fs。architecture.md §5.2。
module Wm.Policy.PolicyTypes

open Wm.Contracts.Identifiers
open Wm.Contracts.Observation
open Wm.Contracts.SharedVocabulary
open Wm.Policy.MonitorSelector

type WindowPredicate =
    | AnyWindow
    /// どの導出元（`AppIdentity`の4フィールド）を指すかを明示する（§4.2/§5.2）。
    | AppIs of AppIdSource * string
    | TitleContains of string
    | DialogIs of bool
    /// DIP。比較相手のMinWidthの信念もDIPに正規化して持つので分類はDPIに依存しない（§5.2/§8.3）。
    | MinWidthAtLeast of int
    /// 4.5のMovabilityの信念を読む（ADR-015）。
    | MovableIs of bool
    | All of WindowPredicate list
    | AnyOf of WindowPredicate list
    | Not of WindowPredicate

type WindowRule = {
    Id: PolicyRuleId
    Priority: int
    When: WindowPredicate
    Apply: Management
}

type LayoutPredicate =
    | Always
    | TiledCountAtLeast of int
    /// DIP（§5.2/§8.3。モニター単位の判定なので、そのモニターのDPIで変換する）。
    | MonitorWidthAtLeast of int
    | Both of LayoutPredicate list

type LayoutRule = {
    Id: PolicyRuleId
    Priority: int
    When: LayoutPredicate
    Use: LayoutRef
}

type PolicyIR = {
    SchemaVersion: int
    WindowRules: WindowRule list
    DefaultManagement: Management
    LayoutRules: LayoutRule list
    DefaultLayout: LayoutRef
    /// i3の`workspace X output Y`相当（§4.4）。
    WorkspaceAssignments: (WorkspaceId * MonitorSelector) list
}
