/// architecture.md §5.5：PolicyAnalyzerの初期必須2行(重複RuleId/未登録Layout、比率・列数)を検査する。
module Wm.Policy.Tests.PolicyAnalyzerTests

open Xunit
open Wm.Contracts.Identifiers
open Wm.Contracts.SharedVocabulary
open Wm.Policy.PolicyTypes
open Wm.Policy.PolicyAnalyzer
open Wm.Policy.Tests.TestHelpers

let private registeredManifest: RegistryManifest =
    [ { LayoutId = LayoutId "monocle"; ApiVersion = 1; ImplementationDigest = "d1" }
      { LayoutId = LayoutId "master-stack"; ApiVersion = 1; ImplementationDigest = "d2" } ]

[<Fact>]
let ``条件を全て満たすPolicyはクリーン`` () =
    let policy =
        { basePolicy with
            WindowRules = [ windowRule "a" 10 AnyWindow Tiled ]
            LayoutRules = [ layoutRule "l" 10 Always (mkLayoutRef "master-stack" (MasterRatio 0.5m)) ] }
    Assert.True(isClean (analyze registeredManifest policy))

[<Fact>]
let ``WindowRuleのId重複はDuplicateWindowRuleIdになる`` () =
    let policy = { basePolicy with WindowRules = [ windowRule "a" 10 AnyWindow Tiled; windowRule "a" 20 AnyWindow Excluded ] }
    Assert.Contains(DuplicateWindowRuleId (PolicyRuleId "a"), (analyze registeredManifest policy).Errors)

[<Fact>]
let ``LayoutRuleのId重複はDuplicateLayoutRuleIdになる`` () =
    let l = mkLayoutRef "monocle" NoArgs
    let policy = { basePolicy with LayoutRules = [ layoutRule "x" 10 Always l; layoutRule "x" 20 Always l ] }
    Assert.Contains(DuplicateLayoutRuleId (PolicyRuleId "x"), (analyze registeredManifest policy).Errors)

[<Fact>]
let ``WindowRuleとLayoutRuleのId重複は別名前空間として許す(衝突しない)`` () =
    let policy =
        { basePolicy with
            WindowRules = [ windowRule "shared" 10 AnyWindow Tiled ]
            LayoutRules = [ layoutRule "shared" 10 Always (mkLayoutRef "monocle" NoArgs) ] }
    Assert.True(isClean (analyze registeredManifest policy))

[<Fact>]
let ``Registryに無いLayoutIdはUnregisteredLayoutになる(LayoutRule経由)`` () =
    let unregistered = mkLayoutRef "nope" NoArgs
    let policy = { basePolicy with LayoutRules = [ layoutRule "l" 10 Always unregistered ] }
    Assert.Contains(UnregisteredLayout (LayoutId "nope", 1, Some (PolicyRuleId "l")), (analyze registeredManifest policy).Errors)

[<Fact>]
let ``DefaultLayoutがRegistryに無ければUnregisteredLayoutになる(ruleId=None)`` () =
    let policy = { basePolicy with DefaultLayout = mkLayoutRef "nope" NoArgs }
    Assert.Contains(UnregisteredLayout (LayoutId "nope", 1, None), (analyze registeredManifest policy).Errors)

[<Fact>]
let ``ApiVersionが一致しないLayoutもUnregisteredLayoutになる`` () =
    let wrongVersion = { Id = LayoutId "monocle"; ApiVersion = 99; Args = NoArgs }
    let policy = { basePolicy with DefaultLayout = wrongVersion }
    Assert.Contains(UnregisteredLayout (LayoutId "monocle", 99, None), (analyze registeredManifest policy).Errors)

[<Fact>]
let ``MasterRatioが0未満または1以上はInvalidMasterRatioになる`` () =
    let tooLow = { basePolicy with DefaultLayout = mkLayoutRef "monocle" (MasterRatio -0.1m) }
    let tooHigh = { basePolicy with DefaultLayout = mkLayoutRef "monocle" (MasterRatio 1.0m) }
    Assert.Contains(InvalidMasterRatio (-0.1m, None), (analyze registeredManifest tooLow).Errors)
    Assert.Contains(InvalidMasterRatio (1.0m, None), (analyze registeredManifest tooHigh).Errors)

[<Fact>]
let ``MasterRatioが0は許容する(境界は0未満だけがError)`` () =
    let policy = { basePolicy with DefaultLayout = mkLayoutRef "monocle" (MasterRatio 0.0m) }
    Assert.True(isClean (analyze registeredManifest policy))

[<Fact>]
let ``ColumnCountが0以下はInvalidColumnCountになる`` () =
    let policy = { basePolicy with DefaultLayout = mkLayoutRef "monocle" (ColumnCount 0) }
    Assert.Contains(InvalidColumnCount (0, None), (analyze registeredManifest policy).Errors)

[<Fact>]
let ``全違反を1度に蓄積する(fail-fastしない)`` () =
    let policy =
        { basePolicy with
            WindowRules = [ windowRule "dup" 10 AnyWindow Tiled; windowRule "dup" 20 AnyWindow Excluded ]
            DefaultLayout = mkLayoutRef "nope" (MasterRatio 2.0m) }
    let errors = (analyze registeredManifest policy).Errors
    Assert.Contains(DuplicateWindowRuleId (PolicyRuleId "dup"), errors)
    Assert.Contains(UnregisteredLayout (LayoutId "nope", 1, None), errors)
    Assert.Contains(InvalidMasterRatio (2.0m, None), errors)
