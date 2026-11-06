/// architecture.md §4.4/§4.5/§5.4：Core.Windowsの候補条件・Sticky/LatestWinsの信念修正・
/// 分類（AwaitingEvidence→Classified、期限超過での一度だけの再分類）・ユーザー指定・消滅時クリーンアップ。
module Wm.Core.Tests.WindowsTests

open Xunit
open Wm.Contracts.Identifiers
open Wm.Contracts.ErrorTypes
open Wm.Contracts.Observation
open Wm.Contracts.Beliefs
open Wm.Contracts.SharedVocabulary
open Wm.Contracts.Timers
open Wm.Contracts.CoreCommand
open Wm.Core.DomainEvents
open Wm.Core.DecisionTrace
open Wm.Core.Windows
open Wm.Policy.PolicyTypes

let private obs (sequence: int64) (v: 'T) : Observation<'T> =
    { Value = Ok v; SampleSequence = sequence; MonotonicTime = sequence; Source = "test" }

let private failedObs (sequence: int64) : Observation<'T> =
    { Value = Error { Api = "test"; Space = Win32LastError; Code = 0; ElapsedUs = 0L }; SampleSequence = sequence; MonotonicTime = sequence; Source = "test" }

let private emptyAppIdentity: AppIdentity = { Exe = None; HostExe = None; Aumid = None; CoreWindowExe = None }

let private visibleSnapshot (w: WindowId) : WindowSnapshot =
    { Id = w
      Process = None
      AppIdentity = emptyAppIdentity
      Owner = None
      IsDialog = None
      NativeRect = None
      VisibleFrameRect = None
      IsVisible = Some (obs 1L true)
      IsMinimized = None
      IsMaximized = None
      Style = None
      Cloak = None
      Monitor = None
      MinWidthHint = None
      TargetIntegrity = None
      Responsiveness = None
      Presence = None
      DpiAwareness = None
      TitleMatches = None }

let private windowRule (id: string) (priority: int) (pred: WindowPredicate) (apply: Management) : WindowRule =
    { Id = PolicyRuleId id; Priority = priority; When = pred; Apply = apply }

let private basePolicy: PolicyIR = PolicyIR.defaults

let private ctxWith (policy: PolicyIR) (now: int64) : ClassificationCtx =
    { Now = now
      PatternSet = Wm.Policy.PolicyEvaluator.buildTitlePatternSet policy
      Policy = policy
      ActiveWorkspace = Some (WorkspaceId 0)
      ForegroundWindow = None
      ClassificationTimeoutMs = 1_000L
      Movability = Map.empty
      MinWidth = Map.empty }

let private identityInit: IdentityBeliefs =
    { Exe = Unformed []; HostExe = Unformed []; Aumid = Unformed []; CoreWindowExe = Unformed []; Process = Unformed [] }

// ===== isCandidate =====

let private classInputsWithVisible: ClassInputBeliefs =
    { IsDialog = LatestWins.unformed
      Owner = LatestWins.unformed
      IsVisible = LatestWins.revise 1L LatestWins.unformed (obs 1L true)
      Cloak = LatestWins.unformed
      TitleMatches = LatestWins.unformed }

[<Fact>]
let ``isCandidate: 可視かつCloak未観測ならYes(楽観的既定)`` () =
    Assert.True(isCandidate classInputsWithVisible)

[<Fact>]
let ``isCandidate: 不可視ならNo`` () =
    let inputs = { classInputsWithVisible with IsVisible = LatestWins.revise 1L LatestWins.unformed (obs 1L false) }
    Assert.False(isCandidate inputs)

[<Fact>]
let ``isCandidate: 可視だがCloakされていればNo`` () =
    let cloaked = { App = true; Shell = false; Inherited = false }
    let inputs = { classInputsWithVisible with Cloak = LatestWins.revise 1L LatestWins.unformed (obs 1L cloaked) }
    Assert.False(isCandidate inputs)

[<Fact>]
let ``isCandidate: 可視性が未観測ならNo`` () =
    Assert.False(isCandidate { classInputsWithVisible with IsVisible = LatestWins.unformed })

// ===== reviseIdentity（Sticky） =====

[<Fact>]
let ``reviseIdentity: 最初のOk観測でConfirmedに固定する`` () =
    let snapshot = { visibleSnapshot (WindowId 1L) with AppIdentity = { emptyAppIdentity with Exe = Some (obs 1L "foo.exe") } }
    let identity' = reviseIdentity 10L identityInit snapshot
    match identity'.Exe with
    | Believed b ->
        Assert.Equal("foo.exe", b.Value)
        Assert.Equal(Confirmed, b.Confidence)
    | Unformed _ -> Assert.True(false, "expected Believed")

[<Fact>]
let ``reviseIdentity: CoreWindowExeがApplicationFrameHost.exeならOkとして扱わない(R4-13)`` () =
    let snapshot = { visibleSnapshot (WindowId 1L) with AppIdentity = { emptyAppIdentity with CoreWindowExe = Some (obs 1L "ApplicationFrameHost.exe") } }
    let identity' = reviseIdentity 10L identityInit snapshot
    Assert.True(match identity'.CoreWindowExe with Unformed _ -> true | _ -> false)

[<Fact>]
let ``reviseIdentity: 固定後に異なる値が観測されたら値は変えず確度をInferredへ下げる`` () =
    let s1 = { visibleSnapshot (WindowId 1L) with AppIdentity = { emptyAppIdentity with Exe = Some (obs 1L "foo.exe") } }
    let s2 = { visibleSnapshot (WindowId 1L) with AppIdentity = { emptyAppIdentity with Exe = Some (obs 2L "bar.exe") } }
    let afterFirst = reviseIdentity 10L identityInit s1
    let afterSecond = reviseIdentity 20L afterFirst s2
    match afterSecond.Exe with
    | Believed b ->
        Assert.Equal("foo.exe", b.Value) // 値は変わらない
        Assert.True(match b.Confidence with Inferred _ -> true | _ -> false)
    | Unformed _ -> Assert.True(false, "expected Believed")

[<Fact>]
let ``reviseIdentity: Error観測は何もしない`` () =
    let snapshot = { visibleSnapshot (WindowId 1L) with Process = Some (failedObs 1L) }
    let identity' = reviseIdentity 10L identityInit snapshot
    Assert.True(match identity'.Process with Unformed [] -> true | _ -> false)

// ===== onWindowObserved：HWND再利用の検知(§4.5 Sticky規則、v2.47) =====

let private withProcess (w: WindowId) (sequence: int64) (pid: int) : WindowSnapshot =
    { visibleSnapshot w with Process = Some (obs sequence { Pid = pid; CreationTime = 1L }) }

[<Fact>]
let ``Processが異なる値で観測されたらWindowVanishedByReuseを出し、信念は書き換えない`` () =
    let policy = { basePolicy with WindowRules = [ windowRule "r" 10 AnyWindow Tiled ] }
    let ctx = ctxWith policy 0L
    let registry1, _, _, _ = onWindowObserved ctx (withProcess (WindowId 1L) 1L 100) initial
    let registry', events, cmds, _ = onWindowObserved { ctx with Now = 10L } (withProcess (WindowId 1L) 2L 200) registry1
    Assert.Equal<DomainEvent list>([ WindowVanishedByReuse (WindowId 1L) ], events)
    Assert.Empty(cmds)
    // 信念は一切書き換えない(旧Processの値のまま)。実際の除去はWmState.vanishWindowに委ねる。
    match (Map.find (WindowId 1L) registry'.Identity).Process with
    | Believed b -> Assert.Equal({ Pid = 100; CreationTime = 1L }, b.Value)
    | Unformed _ -> Assert.True(false, "expected Believed")

[<Fact>]
let ``同じProcessの繰り返しはWindowVanishedByReuseを出さない`` () =
    let policy = { basePolicy with WindowRules = [ windowRule "r" 10 AnyWindow Tiled ] }
    let ctx = ctxWith policy 0L
    let registry1, _, _, _ = onWindowObserved ctx (withProcess (WindowId 1L) 1L 100) initial
    let _, events, _, _ = onWindowObserved { ctx with Now = 10L } (withProcess (WindowId 1L) 2L 100) registry1
    Assert.DoesNotContain(WindowVanishedByReuse (WindowId 1L), events)

[<Fact>]
let ``Processが初めて観測される(Unformedから)場合はHWND再利用と判定しない`` () =
    let policy = { basePolicy with WindowRules = [ windowRule "r" 10 AnyWindow Tiled ] }
    let ctx = ctxWith policy 0L
    let _, events, _, _ = onWindowObserved ctx (withProcess (WindowId 1L) 1L 100) initial
    Assert.DoesNotContain(WindowVanishedByReuse (WindowId 1L), events)

// ===== classificationView / ctx.MinWidth（v2.48） =====

[<Fact>]
let ``classificationViewはctx.MinWidthに信念があればBelievedとして渡す`` () =
    let w = WindowId 1L
    let belief: Belief<int> = { Value = 500; Confidence = Inferred [ Sample 1L ]; LastRevisedAt = 10L }
    let ctx = { ctxWith basePolicy 0L with MinWidth = Map.ofList [ w, belief ] }
    let view = classificationView ctx w identityInit (visibleSnapshot w |> fun s -> reviseClassInputsFromSnapshot 0L classInputsWithVisible s)
    Assert.Equal(Believed belief, view.MinWidthDip)

[<Fact>]
let ``classificationViewはctx.MinWidthに信念が無ければUnformedを渡す`` () =
    let w = WindowId 1L
    let ctx = ctxWith basePolicy 0L
    let view = classificationView ctx w identityInit (visibleSnapshot w |> fun s -> reviseClassInputsFromSnapshot 0L classInputsWithVisible s)
    Assert.True(match view.MinWidthDip with Unformed [ UnknownMinSize w' ] -> w' = w | _ -> false)

[<Fact>]
let ``MinWidthAtLeastを使うルールは、ctx.MinWidthに十分な信念があれば実際にそのルールへ一致する(v2.48)`` () =
    let w = WindowId 1L
    let policy = { basePolicy with WindowRules = [ windowRule "wide-floats" 10 (MinWidthAtLeast 400) Floating ] }
    let ctx = { ctxWith policy 0L with MinWidth = Map.ofList [ w, { Value = 500; Confidence = Inferred [ Sample 1L ]; LastRevisedAt = 0L } ] }
    let registry', events, _, _ = onWindowObserved ctx (visibleSnapshot w) initial
    match (Map.find w registry'.Records).Classification with
    | Classified (EffFloating, ByPolicy (Some (PolicyRuleId "wide-floats"))) -> ()
    | other -> Assert.True(false, sprintf "unexpected: %A" other)
    Assert.Equal<DomainEvent list>([ WindowClassified (w, EffFloating, Some (WorkspaceId 0)) ], events)

[<Fact>]
let ``MinWidthAtLeastを使うルールは、ctx.MinWidthに信念が無ければUnknownのままAwaitingEvidenceになる(v2.48)`` () =
    // §5.4のUnknown処理：最優先ルールがUnknownな間は、より低優先のAnyWindowへフォールバックせず
    // 証拠を待つ（期限超過後にByTimeoutへ解決する既存の経路、ここでは「即座にwide-floatsへ誤って
    // 一致しない」ことだけを検査する）。
    let w = WindowId 1L
    let policy = { basePolicy with WindowRules = [ windowRule "wide-floats" 10 (MinWidthAtLeast 400) Floating; windowRule "fallback" 0 AnyWindow Tiled ] }
    let ctx = ctxWith policy 0L
    let registry', _, _, _ = onWindowObserved ctx (visibleSnapshot w) initial
    match (Map.find w registry'.Records).Classification with
    | AwaitingEvidence _ -> ()
    | other -> Assert.True(false, sprintf "unexpected: %A" other)

// ===== onWindowObserved / 分類 =====

[<Fact>]
let ``候補条件を満たす窓はAnyWindowルールで即座にClassifiedになる`` () =
    let policy = { basePolicy with WindowRules = [ windowRule "r" 10 AnyWindow Tiled ] }
    let ctx = ctxWith policy 0L
    let registry', events, cmds, _ = onWindowObserved ctx (visibleSnapshot (WindowId 1L)) initial
    Assert.Empty(cmds)
    match (Map.find (WindowId 1L) registry'.Records).Classification with
    | Classified (EffTiled, ByPolicy (Some (PolicyRuleId "r"))) -> ()
    | other -> Assert.True(false, sprintf "unexpected: %A" other)
    Assert.Equal<DomainEvent list>([ WindowClassified (WindowId 1L, EffTiled, Some (WorkspaceId 0)) ], events)

[<Fact>]
let ``Excludedへの分類はWindowUnmanagedを出す`` () =
    let policy = { basePolicy with WindowRules = [ windowRule "r" 10 AnyWindow Excluded ] }
    let ctx = ctxWith policy 0L
    let _, events, _, _ = onWindowObserved ctx (visibleSnapshot (WindowId 1L)) initial
    Assert.Equal<DomainEvent list>([ WindowUnmanaged (WindowId 1L) ], events)

[<Fact>]
let ``信念が無くPolicyがUnknownならAwaitingEvidenceになり期限タイマーを張る`` () =
    let policy = { basePolicy with WindowRules = [ windowRule "needs-dialog" 10 (DialogIs true) Excluded ] }
    let ctx = ctxWith policy 0L
    let registry', events, cmds, _ = onWindowObserved ctx (visibleSnapshot (WindowId 1L)) initial
    Assert.Empty(events)
    Assert.Equal<TimerCommand<TimerId> list>([ StartTimer (TimerId.Windows (ClassificationDeadline (WindowId 1L)), 1_000L) ], cmds)
    match (Map.find (WindowId 1L) registry'.Records).Classification with
    | AwaitingEvidence (deadline, Some ws) ->
        Assert.Equal(1_000L, deadline)
        Assert.Equal(WorkspaceId 0, ws)
    | other -> Assert.True(false, sprintf "unexpected: %A" other)

[<Fact>]
let ``期限超過時はFloating・ByTimeoutへ解決する`` () =
    let policy = { basePolicy with WindowRules = [ windowRule "needs-dialog" 10 (DialogIs true) Excluded ] }
    let ctx0 = ctxWith policy 0L
    let registry1, _, _, _ = onWindowObserved ctx0 (visibleSnapshot (WindowId 1L)) initial
    let ctxLater = { ctx0 with Now = 1_000L }
    let registry2, events, _, _ = onClassificationDeadline ctxLater (WindowId 1L) registry1
    Assert.Equal<DomainEvent list>([ WindowClassified (WindowId 1L, EffFloating, Some (WorkspaceId 0)) ], events)
    match (Map.find (WindowId 1L) registry2.Records).Classification with
    | Classified (EffFloating, ByTimeout) -> ()
    | other -> Assert.True(false, sprintf "unexpected: %A" other)

[<Fact>]
let ``古い発火(期限未到達)のClassificationDeadlineは無視する`` () =
    let policy = { basePolicy with WindowRules = [ windowRule "needs-dialog" 10 (DialogIs true) Excluded ] }
    let ctx0 = ctxWith policy 0L
    let registry1, _, _, _ = onWindowObserved ctx0 (visibleSnapshot (WindowId 1L)) initial
    let _, events, _, _ = onClassificationDeadline ctx0 (WindowId 1L) registry1 // Now=0L、deadline=1000L
    Assert.Empty(events)

[<Fact>]
let ``ByTimeout後の最初の再評価は1回だけ再分類を許す(LateReclassify)`` () =
    // DialogIsが未観測の間はUnknown(AwaitingEvidence)、観測後にTrueならTiled。
    let policy = { basePolicy with WindowRules = [ windowRule "dialog-is-tiled" 10 (DialogIs true) Tiled ] }
    let ctx0 = ctxWith policy 0L
    let registry1, _, _, _ = onWindowObserved ctx0 (visibleSnapshot (WindowId 1L)) initial
    let registry2, _, _, _ = onClassificationDeadline { ctx0 with Now = 1_000L } (WindowId 1L) registry1
    match (Map.find (WindowId 1L) registry2.Records).Classification with
    | Classified (EffFloating, ByTimeout) -> ()
    | other -> Assert.True(false, sprintf "expected Floating/ByTimeout, got %A" other)

    // 遅れてDialogIs trueの証拠が届く → 1回だけTiledへ再分類される。
    let dialogTrueSnapshot = { visibleSnapshot (WindowId 1L) with IsDialog = Some (obs 2L true) }
    let registry3, events3, _, _ = onWindowObserved { ctx0 with Now = 1_500L } dialogTrueSnapshot registry2
    Assert.Equal<DomainEvent list>([ WindowClassified (WindowId 1L, EffTiled, None) ], events3)
    let record3 = Map.find (WindowId 1L) registry3.Records
    Assert.True(record3.LateReclassifyUsed)
    match record3.Classification with
    | Classified (EffTiled, ByPolicy (Some (PolicyRuleId "dialog-is-tiled"))) -> ()
    | other -> Assert.True(false, sprintf "unexpected: %A" other)

    // 再度証拠が変わっても、再分類は既に使用済みなので動かない。
    let dialogFalseSnapshot = { visibleSnapshot (WindowId 1L) with IsDialog = Some (obs 3L false) }
    let _, events4, _, _ = onWindowObserved { ctx0 with Now = 2_000L } dialogFalseSnapshot registry3
    Assert.Empty(events4)

// ===== ユーザー指定 =====

[<Fact>]
let ``ToggleFloatingはPolicy分類を上書きし、以後信念の変化で戻らない`` () =
    let policy = { basePolicy with WindowRules = [ windowRule "r" 10 AnyWindow Tiled ] }
    let ctx = ctxWith policy 0L
    let registry1, _, _, _ = onWindowObserved ctx (visibleSnapshot (WindowId 1L)) initial
    let registry2, events, _, _ = onUserCommand ctx (ToggleFloating (WindowId 1L)) registry1
    Assert.Equal<DomainEvent list>([ WindowClassified (WindowId 1L, EffFloating, None) ], events)
    match (Map.find (WindowId 1L) registry2.Records).Classification with
    | Classified (EffFloating, ByUserOverride) -> ()
    | other -> Assert.True(false, sprintf "unexpected: %A" other)
    // 以後WindowObservedが来ても上書きされない。
    let registry3, events3, _, _ = onWindowObserved ctx (visibleSnapshot (WindowId 1L)) registry2
    Assert.Empty(events3)
    match (Map.find (WindowId 1L) registry3.Records).Classification with
    | Classified (EffFloating, ByUserOverride) -> ()
    | other -> Assert.True(false, sprintf "unexpected: %A" other)

[<Fact>]
let ``SetManagement Excludedへの指定はWindowUnmanagedを出す`` () =
    let policy = { basePolicy with WindowRules = [ windowRule "r" 10 AnyWindow Tiled ] }
    let ctx = ctxWith policy 0L
    let registry1, _, _, _ = onWindowObserved ctx (visibleSnapshot (WindowId 1L)) initial
    let _, events, _, _ = onUserCommand ctx (SetManagement (WindowId 1L, Excluded)) registry1
    Assert.Equal<DomainEvent list>([ WindowUnmanaged (WindowId 1L) ], events)

[<Fact>]
let ``ClearUserOverrideはPolicyの分類に戻す`` () =
    let policy = { basePolicy with WindowRules = [ windowRule "r" 10 AnyWindow Tiled ] }
    let ctx = ctxWith policy 0L
    let registry1, _, _, _ = onWindowObserved ctx (visibleSnapshot (WindowId 1L)) initial
    let registry2, _, _, _ = onUserCommand ctx (ToggleFloating (WindowId 1L)) registry1
    let registry3, events, _, _ = onUserCommand ctx (ClearUserOverride (WindowId 1L)) registry2
    Assert.Equal<DomainEvent list>([ WindowClassified (WindowId 1L, EffTiled, Some (WorkspaceId 0)) ], events)
    Assert.True((Map.find (WindowId 1L) registry3.Records).UserOverride.IsNone)

[<Fact>]
let ``管理下に無い窓へのユーザーコマンドは何もしない`` () =
    let ctx = ctxWith basePolicy 0L
    let registry', events, cmds, _ = onUserCommand ctx (ToggleFloating (WindowId 99L)) initial
    Assert.Equal(initial, registry')
    Assert.Empty(events)
    Assert.Empty(cmds)

// ===== 消滅時クリーンアップ =====

[<Fact>]
let ``onWindowVanishedはRecords・Identity・ClassInputsを除く`` () =
    let policy = { basePolicy with WindowRules = [ windowRule "r" 10 AnyWindow Tiled ] }
    let ctx = ctxWith policy 0L
    let registry1, _, _, _ = onWindowObserved ctx (visibleSnapshot (WindowId 1L)) initial
    Assert.True(Map.containsKey (WindowId 1L) registry1.Records)
    let registry2 = onWindowVanished (WindowId 1L) registry1
    Assert.False(Map.containsKey (WindowId 1L) registry2.Records)
    Assert.False(Map.containsKey (WindowId 1L) registry2.Identity)
    Assert.False(Map.containsKey (WindowId 1L) registry2.ClassInputs)

// ===== Movability → Unplaced（§4.4、v2.23） =====

[<Fact>]
let ``NotMovableな窓はPolicy評価の時点からEffUnplacedになり、WindowUnmanagedを出す`` () =
    let policy = { basePolicy with WindowRules = [ windowRule "r" 10 AnyWindow Tiled ] }
    let w = WindowId 1L
    let ctx = { ctxWith policy 0L with Movability = Map.ofList [ w, { Value = NotMovable; Confidence = Inferred []; LastRevisedAt = 0L } ] }
    let registry', events, _, _ = onWindowObserved ctx (visibleSnapshot w) initial
    Assert.Equal<DomainEvent list>([ WindowUnmanaged w ], events)
    match (Map.find w registry'.Records).Classification with
    | Classified (EffUnplaced, ByPolicy _) -> ()
    | other -> Assert.True(false, sprintf "unexpected: %A" other)

[<Fact>]
let ``onMovabilityBecameNotMovableはTiled/Floatingで確定済みの窓だけをUnplacedへ降格する`` () =
    let policy = { basePolicy with WindowRules = [ windowRule "r" 10 AnyWindow Tiled ] }
    let ctx = ctxWith policy 0L
    let w = WindowId 1L
    let registry1, _, _, _ = onWindowObserved ctx (visibleSnapshot w) initial
    match (Map.find w registry1.Records).Classification with
    | Classified (EffTiled, _) -> ()
    | other -> Assert.True(false, sprintf "expected Tiled first, got %A" other)
    let registry2, events = onMovabilityBecameNotMovable w registry1
    Assert.Equal<DomainEvent list>([ WindowUnmanaged w ], events)
    match (Map.find w registry2.Records).Classification with
    | Classified (EffUnplaced, _) -> ()
    | other -> Assert.True(false, sprintf "unexpected: %A" other)

[<Fact>]
let ``onMovabilityBecameNotMovableはユーザー指定(ByUserOverride)の窓には作用しない`` () =
    let policy = { basePolicy with WindowRules = [ windowRule "r" 10 AnyWindow Tiled ] }
    let ctx = ctxWith policy 0L
    let w = WindowId 1L
    let registry1, _, _, _ = onWindowObserved ctx (visibleSnapshot w) initial
    let registry2, _, _, _ = onUserCommand ctx (ToggleFloating w) registry1 // ByUserOverrideへ
    let registry3, events = onMovabilityBecameNotMovable w registry2
    Assert.Empty(events)
    Assert.Equal<WindowRegistry>(registry2, registry3)

[<Fact>]
let ``onMovabilityBecameNotMovableは管理下に無い窓や既にUnplaced/Excludedの窓には何もしない`` () =
    let state0, _ = onMovabilityBecameNotMovable (WindowId 99L) initial
    Assert.Equal<WindowRegistry>(initial, state0)

// ===== 所属ヒント（§4.5 R4-3、v2.64） =====

[<Fact>]
let ``onMembershipHintsLoadedはMembershipHintsへ登録するだけで、信念・分類は一切変えない`` () =
    let w = WindowId 1L
    let registry' = onMembershipHintsLoaded [ w, WorkspaceId 5, Some Floating ] initial
    Assert.Equal((WorkspaceId 5, Some Floating), Map.find w registry'.MembershipHints)
    Assert.False(Map.containsKey w registry'.Records)

[<Fact>]
let ``ヒントにManagementが有れば、Policyの判断を上書きしてByUserOverride相当で確定し、ヒントのWorkspaceへ入る`` () =
    // Policyは素直にAnyWindow→Tiledへ分類するはずだが、ヒントがFloating/WorkspaceId 5を指定しているので
    // そちらが優先される（§4.5「ヒントのWorkspaceへ入れ、UserOverrideを復元する」）。
    let policy = { basePolicy with WindowRules = [ windowRule "r" 10 AnyWindow Tiled ] }
    let ctx = ctxWith policy 0L
    let w = WindowId 1L
    let withHint = onMembershipHintsLoaded [ w, WorkspaceId 5, Some Floating ] initial
    let registry', events, _, _ = onWindowObserved ctx (visibleSnapshot w) withHint
    Assert.Equal<DomainEvent list>([ WindowClassified (w, EffFloating, Some (WorkspaceId 5)) ], events)
    let record = Map.find w registry'.Records
    Assert.Equal(Some Floating, record.UserOverride)
    match record.Classification with
    | Classified (EffFloating, ByUserOverride) -> ()
    | other -> Assert.True(false, sprintf "unexpected: %A" other)
    // 復元されたUserOverrideは、以後の通常のWindowObservedでも上書きされない（既存のByUserOverride規則どおり）。
    let _, events2, _, _ = onWindowObserved ctx (visibleSnapshot w) registry'
    Assert.Empty(events2)

[<Fact>]
let ``ヒントにManagementが無ければ、分類自体はPolicy任せのままhomeだけヒントのWorkspaceへ差し替わる`` () =
    let policy = { basePolicy with WindowRules = [ windowRule "r" 10 AnyWindow Tiled ] }
    let ctx = ctxWith policy 0L
    let w = WindowId 1L
    let withHint = onMembershipHintsLoaded [ w, WorkspaceId 5, None ] initial
    let registry', events, _, _ = onWindowObserved ctx (visibleSnapshot w) withHint
    Assert.Equal<DomainEvent list>([ WindowClassified (w, EffTiled, Some (WorkspaceId 5)) ], events)
    match (Map.find w registry'.Records).Classification with
    | Classified (EffTiled, ByPolicy (Some (PolicyRuleId "r"))) -> ()
    | other -> Assert.True(false, sprintf "unexpected: %A" other)
    Assert.True((Map.find w registry'.Records).UserOverride.IsNone)

[<Fact>]
let ``AwaitingEvidenceから解決する窓にも、解決した瞬間にヒントが反映される`` () =
    let policy = { basePolicy with WindowRules = [ windowRule "needs-dialog" 10 (DialogIs true) Excluded ] }
    let ctx0 = ctxWith policy 0L
    let w = WindowId 1L
    let withHint = onMembershipHintsLoaded [ w, WorkspaceId 5, Some Floating ] initial
    let registry1, _, _, _ = onWindowObserved ctx0 (visibleSnapshot w) withHint
    match (Map.find w registry1.Records).Classification with
    | AwaitingEvidence _ -> ()
    | other -> Assert.True(false, sprintf "expected AwaitingEvidence, got %A" other)
    let dialogTrueSnapshot = { visibleSnapshot w with IsDialog = Some (obs 2L true) }
    let registry2, events2, _, _ = onWindowObserved { ctx0 with Now = 10L } dialogTrueSnapshot registry1
    // ヒントがFloatingを指定しているので、needs-dialog(Excluded)には一致せず、ヒントが優先される。
    Assert.Equal<DomainEvent list>([ WindowClassified (w, EffFloating, Some (WorkspaceId 5)) ], events2)
    match (Map.find w registry2.Records).Classification with
    | Classified (EffFloating, ByUserOverride) -> ()
    | other -> Assert.True(false, sprintf "unexpected: %A" other)

[<Fact>]
let ``ClassificationDeadline超過(ByTimeout)の経路でもヒントが反映される`` () =
    let policy = { basePolicy with WindowRules = [ windowRule "needs-dialog" 10 (DialogIs true) Excluded ] }
    let ctx0 = ctxWith policy 0L
    let w = WindowId 1L
    let withHint = onMembershipHintsLoaded [ w, WorkspaceId 5, Some Floating ] initial
    let registry1, _, _, _ = onWindowObserved ctx0 (visibleSnapshot w) withHint
    let registry2, events, _, _ = onClassificationDeadline { ctx0 with Now = 1_000L } w registry1
    Assert.Equal<DomainEvent list>([ WindowClassified (w, EffFloating, Some (WorkspaceId 5)) ], events)
    let record = Map.find w registry2.Records
    Assert.Equal(Some Floating, record.UserOverride)
    match record.Classification with
    | Classified (EffFloating, ByUserOverride) -> ()
    | other -> Assert.True(false, sprintf "unexpected: %A" other)

[<Fact>]
let ``ClassificationDeadline超過でヒントにManagementが無ければ、通常のByTimeout/FloatingのままhomeだけヒントWorkspaceへ`` () =
    let policy = { basePolicy with WindowRules = [ windowRule "needs-dialog" 10 (DialogIs true) Excluded ] }
    let ctx0 = ctxWith policy 0L
    let w = WindowId 1L
    let withHint = onMembershipHintsLoaded [ w, WorkspaceId 5, None ] initial
    let registry1, _, _, _ = onWindowObserved ctx0 (visibleSnapshot w) withHint
    let registry2, events, _, _ = onClassificationDeadline { ctx0 with Now = 1_000L } w registry1
    Assert.Equal<DomainEvent list>([ WindowClassified (w, EffFloating, Some (WorkspaceId 5)) ], events)
    match (Map.find w registry2.Records).Classification with
    | Classified (EffFloating, ByTimeout) -> ()
    | other -> Assert.True(false, sprintf "unexpected: %A" other)

[<Fact>]
let ``ヒントが無い窓は通常どおりの規則で分類される(回帰確認)`` () =
    let policy = { basePolicy with WindowRules = [ windowRule "r" 10 AnyWindow Tiled ] }
    let ctx = ctxWith policy 0L
    let w = WindowId 1L
    let registry', events, _, _ = onWindowObserved ctx (visibleSnapshot w) initial
    Assert.Equal<DomainEvent list>([ WindowClassified (w, EffTiled, Some (WorkspaceId 0)) ], events)
    match (Map.find w registry'.Records).Classification with
    | Classified (EffTiled, ByPolicy (Some (PolicyRuleId "r"))) -> ()
    | other -> Assert.True(false, sprintf "unexpected: %A" other)

[<Fact>]
let ``ClearUserOverrideはヒントを再適用しない(Policyの分類へそのまま戻る)`` () =
    // ヒントで復元されたByUserOverrideを、ユーザーが明示的にClearUserOverrideで外した場合、
    // 起動直後のヒントを再び被せると即座に無効化されてしまう——意図的にヒントは一度しか使わない。
    let policy = { basePolicy with WindowRules = [ windowRule "r" 10 AnyWindow Tiled ] }
    let ctx = ctxWith policy 0L
    let w = WindowId 1L
    let withHint = onMembershipHintsLoaded [ w, WorkspaceId 5, Some Floating ] initial
    let registry1, _, _, _ = onWindowObserved ctx (visibleSnapshot w) withHint
    match (Map.find w registry1.Records).Classification with
    | Classified (EffFloating, ByUserOverride) -> ()
    | other -> Assert.True(false, sprintf "expected hint-applied Floating first, got %A" other)
    let registry2, events, _, _ = onUserCommand ctx (ClearUserOverride w) registry1
    Assert.Equal<DomainEvent list>([ WindowClassified (w, EffTiled, Some (WorkspaceId 0)) ], events)
    let record = Map.find w registry2.Records
    Assert.True(record.UserOverride.IsNone)
    match record.Classification with
    | Classified (EffTiled, ByPolicy (Some (PolicyRuleId "r"))) -> ()
    | other -> Assert.True(false, sprintf "unexpected: %A" other)

// ===== DecisionTrace（§7.1「起こり得ない組合せに出会ったら、例外を投げずに入力を捨て、
// DecisionTraceに記録する」、v2.68） =====

[<Fact>]
let ``reconsiderは「起こり得ない組合せ」(UserOverride=NoneなのにClassified(_,ByUserOverride))に遭遇すると、入力を捨てつつDecisionTraceを記録する`` () =
    // 通常のコード経路（applyUserOverride/ClearUserOverride）はこの組合せを絶対に作らない
    // （UserOverride = Some _ ⇔ Classification = Classified (_, ByUserOverride)という対応、
    // Windows.fsファイル冒頭コメント参照）——ここでは意図的にその対応を破った`WindowRecord`を
    // 直接構築し、全域関数としての安全側の振る舞い（入力を捨てる＝信念を書き換えない）に加えて、
    // DecisionTrace自体が実際に記録されることを確認する。
    let policy = { basePolicy with WindowRules = [ windowRule "r" 10 AnyWindow Tiled ] }
    let ctx = ctxWith policy 0L
    let w = WindowId 1L
    let impossibleRecord = { WindowRecord.initial with Classification = Classified (EffFloating, ByUserOverride); UserOverride = None }
    let registry0 = { initial with Records = Map.ofList [ w, impossibleRecord ] }
    let registry', events, cmds, traces = onWindowObserved ctx (visibleSnapshot w) registry0
    Assert.Empty(events)
    Assert.Empty(cmds)
    Assert.Equal(impossibleRecord, Map.find w registry'.Records) // 入力を捨てる(信念は書き換えない)
    match traces with
    | [ InconsistencySuspected (domain, _) ] -> Assert.Equal("Windows", domain)
    | other -> Assert.True(false, sprintf "expected exactly one InconsistencySuspected trace, got %A" other)

// ===== MoveCause.Unattributedラウンド2：Floating劣化（§4.4 R3-4、v2.71、Opus敵対的レビュー
// opus-review-unattributed-movecause.md §Q3） =====

[<Fact>]
let ``onOscillationDegradedはTiled窓をFloating・ByOscillationへ劣化させ、WindowClassifiedを出す`` () =
    let policy = { basePolicy with WindowRules = [ windowRule "r" 10 AnyWindow Tiled ] }
    let ctx = ctxWith policy 0L
    let w = WindowId 1L
    let registry1, _, _, _ = onWindowObserved ctx (visibleSnapshot w) initial
    match (Map.find w registry1.Records).Classification with
    | Classified (EffTiled, ByPolicy _) -> ()
    | other -> Assert.True(false, sprintf "expected Tiled first, got %A" other)
    let registry2, events = onOscillationDegraded w registry1
    Assert.Equal<DomainEvent list>([ WindowClassified (w, EffFloating, None) ], events)
    let record = Map.find w registry2.Records
    match record.Classification with
    | Classified (EffFloating, ByOscillation) -> ()
    | other -> Assert.True(false, sprintf "unexpected: %A" other)
    Assert.True(record.UserOverride.IsNone)

[<Fact>]
let ``onOscillationDegradedは、ByUserOverride(ユーザーが明示的にTiledへ指定した)窓も劣化させ、UserOverrideをNoneへ戻す(プロジェクトオーナー確認済み)`` () =
    let policy = { basePolicy with WindowRules = [ windowRule "r" 10 AnyWindow Tiled ] }
    let ctx = ctxWith policy 0L
    let w = WindowId 1L
    let registry1, _, _, _ = onWindowObserved ctx (visibleSnapshot w) initial
    let registry2, _, _, _ = onUserCommand ctx (ToggleFloating w) registry1
    let registry3, _, _, _ = onUserCommand ctx (ToggleFloating w) registry2 // Floating→Tiled、ByUserOverrideのまま
    match (Map.find w registry3.Records).Classification with
    | Classified (EffTiled, ByUserOverride) -> ()
    | other -> Assert.True(false, sprintf "expected ByUserOverride Tiled, got %A" other)
    let registry4, events = onOscillationDegraded w registry3
    Assert.Equal<DomainEvent list>([ WindowClassified (w, EffFloating, None) ], events)
    let record = Map.find w registry4.Records
    match record.Classification with
    | Classified (EffFloating, ByOscillation) -> ()
    | other -> Assert.True(false, sprintf "unexpected: %A" other)
    Assert.True(record.UserOverride.IsNone) // UserOverride=Some _ ⇔ Classified(_, ByUserOverride)の対応を保つ

[<Fact>]
let ``onOscillationDegradedは既にByOscillationの窓には何もしない(冪等)`` () =
    let policy = { basePolicy with WindowRules = [ windowRule "r" 10 AnyWindow Tiled ] }
    let ctx = ctxWith policy 0L
    let w = WindowId 1L
    let registry1, _, _, _ = onWindowObserved ctx (visibleSnapshot w) initial
    let registry2, _ = onOscillationDegraded w registry1
    let registry3, events = onOscillationDegraded w registry2
    Assert.Empty(events)
    Assert.Equal(registry2, registry3)

[<Fact>]
let ``onOscillationDegradedは管理下に無い窓には何もしない`` () =
    let registry', events = onOscillationDegraded (WindowId 99L) initial
    Assert.Equal<WindowRegistry>(initial, registry')
    Assert.Empty(events)

[<Fact>]
let ``reconsiderはByOscillationになった窓を二度と再分類しない(固定、プロジェクトオーナー確認済み)`` () =
    let policy = { basePolicy with WindowRules = [ windowRule "r" 10 AnyWindow Tiled ] }
    let ctx = ctxWith policy 0L
    let w = WindowId 1L
    let registry1, _, _, _ = onWindowObserved ctx (visibleSnapshot w) initial
    let registry2, _ = onOscillationDegraded w registry1
    // 以後WindowObservedが来ても再分類しない(ByTimeoutの1回きりの再分類とは違い、再分類の機会自体を
    // 一切与えない——劣化→戻る→戦う→劣化という分類の振動を防ぐ、Opusレビュー§Q3(a))。
    let registry3, events3, _, _ = onWindowObserved ctx (visibleSnapshot w) registry2
    Assert.Empty(events3)
    match (Map.find w registry3.Records).Classification with
    | Classified (EffFloating, ByOscillation) -> ()
    | other -> Assert.True(false, sprintf "unexpected: %A" other)

[<Fact>]
let ``ClearUserOverrideはByOscillationの窓にも作用し、Policyの分類へ戻す(ユーザーの明示的な操作は常に優先される)`` () =
    let policy = { basePolicy with WindowRules = [ windowRule "r" 10 AnyWindow Tiled ] }
    let ctx = ctxWith policy 0L
    let w = WindowId 1L
    let registry1, _, _, _ = onWindowObserved ctx (visibleSnapshot w) initial
    let registry2, _ = onOscillationDegraded w registry1
    let registry3, events, _, _ = onUserCommand ctx (ClearUserOverride w) registry2
    Assert.Equal<DomainEvent list>([ WindowClassified (w, EffTiled, Some (WorkspaceId 0)) ], events)
    match (Map.find w registry3.Records).Classification with
    | Classified (EffTiled, ByPolicy (Some (PolicyRuleId "r"))) -> ()
    | other -> Assert.True(false, sprintf "unexpected: %A" other)

// ===== classificationToManagement / registryManagementSnapshot（Phase C Inspect、§10.2） =====
// FCISレビューで、元はWm.Host/ResidentHost.fsに重複して書かれていた純粋な分類マッピングをここへ
// 寄せた(2026-11-05)。Reducer/EffectHarnessを一切立てずにテストできることが、このCore移設の目的。

[<Fact>]
let ``classificationToManagement: NotCandidateはNone,None`` () =
    Assert.Equal((None, None), classificationToManagement NotCandidate)

[<Fact>]
let ``classificationToManagement: AwaitingEvidenceはNone,None`` () =
    Assert.Equal((None, None), classificationToManagement (AwaitingEvidence(100L, Some (WorkspaceId 0))))

[<Fact>]
let ``classificationToManagement: EffTiledはTiled,マッチしたルール`` () =
    let result = classificationToManagement (Classified (EffTiled, ByPolicy (Some (PolicyRuleId "r"))))
    Assert.Equal((Some Tiled, Some (PolicyRuleId "r")), result)

[<Fact>]
let ``classificationToManagement: EffFloatingはFloating`` () =
    Assert.Equal((Some Floating, None), classificationToManagement (Classified (EffFloating, ByTimeout)))

[<Fact>]
let ``classificationToManagement: EffExcludedはExcluded`` () =
    Assert.Equal((Some Excluded, None), classificationToManagement (Classified (EffExcluded, ByUserOverride)))

[<Fact>]
let ``classificationToManagement: EffUnplacedはNone(NotCandidate/AwaitingEvidenceと区別できない既知の簡略化)`` () =
    Assert.Equal((None, None), classificationToManagement (Classified (EffUnplaced, ByOscillation)))

[<Fact>]
let ``classificationToManagement: ByPolicyのruleIdがNoneならDefaultManagement扱いでNone`` () =
    Assert.Equal((Some Tiled, None), classificationToManagement (Classified (EffTiled, ByPolicy None)))

[<Fact>]
let ``registryManagementSnapshot: Recordsの全窓を分類結果へ射影する`` () =
    let policy = { basePolicy with WindowRules = [ windowRule "r" 10 AnyWindow Tiled ] }
    let ctx = ctxWith policy 0L
    let w1 = WindowId 1L
    let w2 = WindowId 2L
    let registry1, _, _, _ = onWindowObserved ctx (visibleSnapshot w1) initial
    let registry2, _, _, _ = onWindowObserved ctx (visibleSnapshot w2) registry1
    let registry3, _ = onOscillationDegraded w2 registry2
    let snapshot = registryManagementSnapshot registry3 |> List.sortBy (fun (WindowId id, _, _) -> id)
    Assert.Equal<(WindowId * Management option * PolicyRuleId option) list>(
        [ w1, Some Tiled, Some (PolicyRuleId "r")
          w2, Some Floating, None ],
        snapshot)
