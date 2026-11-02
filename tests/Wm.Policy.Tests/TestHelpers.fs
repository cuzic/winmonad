/// テスト共通のヘルパー：信念・ClassificationView・最小限のPolicyIRを組み立てる。
module Wm.Policy.Tests.TestHelpers

open Wm.Contracts.Beliefs
open Wm.Contracts.Observation
open Wm.Contracts.Identifiers
open Wm.Contracts.SharedVocabulary
open Wm.Policy.PolicyTypes
open Wm.Policy.PolicyEvaluator

let believed (v: 'T) : BeliefSlot<'T> =
    Believed { Value = v; Confidence = Confirmed; LastRevisedAt = 0L }

let unformed<'T> : BeliefSlot<'T> = Unformed []

let defaultIdentity: IdentityBeliefs =
    { Exe = unformed; HostExe = unformed; Aumid = unformed; CoreWindowExe = unformed; Process = unformed }

let mkView
    (identity: IdentityBeliefs)
    (isDialog: BeliefSlot<bool>)
    (titleMatches: BeliefSlot<TitleMatchVector>)
    (minWidth: BeliefSlot<int>)
    (movability: BeliefSlot<Movability>)
    : ClassificationView =
    { Identity = identity
      IsDialog = isDialog
      TitleMatches = titleMatches
      MinWidthDip = minWidth
      Movability = movability }

let emptyView: ClassificationView = mkView defaultIdentity unformed unformed unformed unformed

let windowRule (id: string) (priority: int) (pred: WindowPredicate) (apply: Management) : WindowRule =
    { Id = PolicyRuleId id; Priority = priority; When = pred; Apply = apply }

let layoutRule (id: string) (priority: int) (pred: LayoutPredicate) (useRef: LayoutRef) : LayoutRule =
    { Id = PolicyRuleId id; Priority = priority; When = pred; Use = useRef }

let mkLayoutRef (id: string) (args: LayoutArgs) : LayoutRef =
    { Id = LayoutId id; ApiVersion = 1; Args = args }

let basePolicy: PolicyIR =
    { SchemaVersion = 1
      WindowRules = []
      DefaultManagement = Tiled
      LayoutRules = []
      DefaultLayout = mkLayoutRef "monocle" NoArgs
      WorkspaceAssignments = [] }
