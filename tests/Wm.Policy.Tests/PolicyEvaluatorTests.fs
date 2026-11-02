/// architecture.md §5.4/§6.1：PolicyEvaluatorの三値評価とルール選択を検査する。
module Wm.Policy.Tests.PolicyEvaluatorTests

open Xunit
open FsCheck.Xunit
open Wm.Contracts.Identifiers
open Wm.Contracts.Beliefs
open Wm.Contracts.Observation
open Wm.Contracts.SharedVocabulary
open Wm.Policy.PolicyTypes
open Wm.Policy.PolicyEvaluator
open Wm.Policy.Tests.TestHelpers

let private emptyPatternSet = buildTitlePatternSet basePolicy

[<Fact>]
let ``AnyWindowは常にYesになる`` () =
    match evaluateWindowPredicate emptyPatternSet emptyView AnyWindow with
    | Believed b -> Assert.True(b.Value)
    | Unformed _ -> failwith "expected Believed"

[<Fact>]
let ``AppIsは一致すればYes、不一致ならNo`` () =
    let view = mkView { defaultIdentity with Exe = believed "foo.exe" } unformed unformed unformed unformed
    match evaluateWindowPredicate emptyPatternSet view (AppIs (Exe, "foo.exe")) with
    | Believed b -> Assert.True(b.Value)
    | Unformed _ -> failwith "expected Believed"
    match evaluateWindowPredicate emptyPatternSet view (AppIs (Exe, "bar.exe")) with
    | Believed b -> Assert.False(b.Value)
    | Unformed _ -> failwith "expected Believed"

[<Fact>]
let ``AppIsはIdentityが未観測ならUnknownになる`` () =
    match evaluateWindowPredicate emptyPatternSet emptyView (AppIs (Exe, "foo.exe")) with
    | Unformed _ -> ()
    | Believed _ -> failwith "expected Unformed"

[<Fact>]
let ``DialogIsは信念が無ければUnknown、あれば一致/不一致を返す`` () =
    Assert.True(match evaluateWindowPredicate emptyPatternSet emptyView (DialogIs true) with Unformed _ -> true | _ -> false)
    let view = mkView defaultIdentity (believed true) unformed unformed unformed
    Assert.True(match evaluateWindowPredicate emptyPatternSet view (DialogIs true) with Believed b -> b.Value | _ -> false)
    Assert.True(match evaluateWindowPredicate emptyPatternSet view (DialogIs false) with Believed b -> not b.Value | _ -> false)

[<Property>]
let ``MinWidthAtLeastはDIPの信念と数値比較で判定する`` (believedWidth: int) (threshold: int) =
    let view = mkView defaultIdentity unformed unformed (believed believedWidth) unformed
    match evaluateWindowPredicate emptyPatternSet view (MinWidthAtLeast threshold) with
    | Believed b -> b.Value = (believedWidth >= threshold)
    | Unformed _ -> false

[<Fact>]
let ``MovableIsは信念が無ければUnknown`` () =
    Assert.True(match evaluateWindowPredicate emptyPatternSet emptyView (MovableIs true) with Unformed _ -> true | _ -> false)

[<Fact>]
let ``MovableIsはMovabilityの信念と一致/不一致を返す`` () =
    let view = mkView defaultIdentity unformed unformed unformed (believed NotMovable)
    Assert.True(match evaluateWindowPredicate emptyPatternSet view (MovableIs false) with Believed b -> b.Value | _ -> false)
    Assert.True(match evaluateWindowPredicate emptyPatternSet view (MovableIs true) with Believed b -> not b.Value | _ -> false)

let private policyWithTitlePattern = { basePolicy with WindowRules = [ windowRule "t" 1 (TitleContains "foo") Excluded ] }
let private patternSetWithFoo = buildTitlePatternSet policyWithTitlePattern

[<Fact>]
let ``TitleContainsはPatternSetHashが一致し添字がMatchesに含まれればYes`` () =
    let fooIdx = Map.find "foo" patternSetWithFoo.IndexOf
    let vector: TitleMatchVector = { PatternSetHash = patternSetWithFoo.PatternSetHash; Matches = Set.ofList [ fooIdx ] }
    let view = mkView defaultIdentity unformed (believed vector) unformed unformed
    Assert.True(match evaluateWindowPredicate patternSetWithFoo view (TitleContains "foo") with Believed b -> b.Value | _ -> false)

[<Fact>]
let ``TitleContainsはMatchesに添字が無ければNo`` () =
    let vector: TitleMatchVector = { PatternSetHash = patternSetWithFoo.PatternSetHash; Matches = Set.empty }
    let view = mkView defaultIdentity unformed (believed vector) unformed unformed
    Assert.True(match evaluateWindowPredicate patternSetWithFoo view (TitleContains "foo") with Believed b -> not b.Value | _ -> false)

[<Fact>]
let ``TitleContainsはPatternSetHashが食い違うとUnknownになる(Policy切替直後)`` () =
    let vector: TitleMatchVector = { PatternSetHash = "stale-hash"; Matches = Set.ofList [ 0 ] }
    let view = mkView defaultIdentity unformed (believed vector) unformed unformed
    Assert.True(match evaluateWindowPredicate patternSetWithFoo view (TitleContains "foo") with Unformed _ -> true | _ -> false)

[<Fact>]
let ``Notは確定した結果を反転し、Unknownはそのまま伝搬する`` () =
    Assert.True(match evaluateWindowPredicate emptyPatternSet emptyView (Not AnyWindow) with Believed b -> not b.Value | _ -> false)
    Assert.True(match evaluateWindowPredicate emptyPatternSet emptyView (Not (DialogIs true)) with Unformed _ -> true | _ -> false)

[<Fact>]
let ``Allは1つでも確定したNoがあればNo、全てYesならYes`` () =
    let view = mkView defaultIdentity (believed true) unformed unformed unformed
    let allYes = All [ AnyWindow; DialogIs true ]
    let allOneNo = All [ AnyWindow; DialogIs false ]
    Assert.True(match evaluateWindowPredicate emptyPatternSet view allYes with Believed b -> b.Value | _ -> false)
    Assert.True(match evaluateWindowPredicate emptyPatternSet view allOneNo with Believed b -> not b.Value | _ -> false)

[<Fact>]
let ``AnyOfは1つでも確定したYesがあればYes、全てNoならNo`` () =
    let view = mkView defaultIdentity (believed false) unformed unformed unformed
    let anyOneYes = AnyOf [ DialogIs true; DialogIs false ]
    let anyAllNo = AnyOf [ DialogIs true ]
    Assert.True(match evaluateWindowPredicate emptyPatternSet view anyOneYes with Believed b -> b.Value | _ -> false)
    Assert.True(match evaluateWindowPredicate emptyPatternSet view anyAllNo with Believed b -> not b.Value | _ -> false)

// ===== selectManagement =====

[<Fact>]
let ``ルールが無ければDefaultManagementを返す`` () =
    Assert.Equal(Resolved Tiled, selectManagement emptyPatternSet emptyView basePolicy)

[<Fact>]
let ``最初にYesとなった(Priority降順)ルールのApplyを返す`` () =
    let policy =
        { basePolicy with
            WindowRules =
                [ windowRule "low" 10 AnyWindow Floating
                  windowRule "high" 90 AnyWindow Excluded ] }
    Assert.Equal(Resolved Excluded, selectManagement emptyPatternSet emptyView policy)

[<Fact>]
let ``高優先のルールがUnknownなら、低優先が確定Yesでも保留してAwaitingEvidenceを返す`` () =
    let policy =
        { basePolicy with
            WindowRules =
                [ windowRule "low-yes" 10 AnyWindow Floating // 単独なら確定Yes
                  windowRule "high-unknown" 90 (DialogIs true) Excluded ] } // emptyViewではUnknown
    Assert.Equal(AwaitingEvidence (PolicyRuleId "high-unknown"), selectManagement emptyPatternSet emptyView policy)

[<Fact>]
let ``Noのルールはスキップして次を評価する`` () =
    let view = mkView defaultIdentity (believed false) unformed unformed unformed
    let policy =
        { basePolicy with
            WindowRules =
                [ windowRule "no" 90 (DialogIs true) Excluded // Noで確定、スキップされる
                  windowRule "yes" 10 AnyWindow Floating ] }
    Assert.Equal(Resolved Floating, selectManagement emptyPatternSet view policy)

[<Fact>]
let ``同じPriorityはPolicyRuleIdの昇順で評価する`` () =
    // "a"が先に評価されYesで確定するので、同じPriorityの"b"(Excluded)は読まれない。
    let policy =
        { basePolicy with
            WindowRules =
                [ windowRule "b" 50 AnyWindow Excluded
                  windowRule "a" 50 AnyWindow Floating ] }
    Assert.Equal(Resolved Floating, selectManagement emptyPatternSet emptyView policy)

// ===== selectLayout =====

[<Fact>]
let ``LayoutRuleが無ければDefaultLayoutを返す`` () =
    Assert.Equal(basePolicy.DefaultLayout, selectLayout 0 0 basePolicy)

[<Fact>]
let ``最初に真となった(Priority降順)ルールのUseを返す`` () =
    let colLayout = mkLayoutRef "columns" (ColumnCount 3)
    let msLayout = mkLayoutRef "master-stack" (MasterRatio 0.62m)
    let policy =
        { basePolicy with
            LayoutRules =
                [ layoutRule "coding" 10 (TiledCountAtLeast 2) msLayout
                  layoutRule "many" 20 (TiledCountAtLeast 4) colLayout ] }
    Assert.Equal(colLayout, selectLayout 5 1920 policy) // 両方真、優先度の高いcolLayoutが勝つ
    Assert.Equal(msLayout, selectLayout 3 1920 policy) // codingだけ真
    Assert.Equal(policy.DefaultLayout, selectLayout 1 1920 policy) // どちらも偽

[<Fact>]
let ``Bothは全て真のときだけ真になる`` () =
    let layout = mkLayoutRef "wide-and-many" NoArgs
    let policy =
        { basePolicy with
            LayoutRules = [ layoutRule "r" 10 (Both [ TiledCountAtLeast 2; MonitorWidthAtLeast 1200 ]) layout ] }
    Assert.Equal(layout, selectLayout 3 1920 policy)
    Assert.Equal(policy.DefaultLayout, selectLayout 3 800 policy) // 幅が足りない
