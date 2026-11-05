/// architecture.md §4.4/§7.1：WmState.step経由でWindowsの分類結果が実際にWorkspacesへ反映されることを、
/// 決定的なシナリオで検査する（CrossDomainInvariantTests.fsのX2プロパティテストの健全性チェックに相当）。
module Wm.Core.Tests.WmStateWindowsTests

open Xunit
open Wm.Contracts.Identifiers
open Wm.Contracts.Beliefs
open Wm.Contracts.Observation
open Wm.Contracts.SharedVocabulary
open Wm.Contracts.Timers
open Wm.Contracts.CoreCommand
open Wm.Contracts.GatedEffect
open Wm.Core.Windows
open Wm.Core.Workspaces
open Wm.Core.Topology
open Wm.Core.WmState
open Wm.Policy.PolicyTypes

let private mkMonitor (id: int) : MonitorSnapshot =
    { Id = MonitorId (int64 id)
      StableKey = sprintf "TEST:%d" id
      Bounds = { X = 0; Y = 0; Width = 1920; Height = 1080 }
      WorkArea = { X = 0; Y = 0; Width = 1920; Height = 1040 }
      Dpi = 96
      IsPrimary = (id = 0) }

let private policy: PolicyIR =
    { PolicyIR.defaults with WindowRules = [ { Id = PolicyRuleId "any-tiled"; Priority = 10; When = AnyWindow; Apply = Tiled } ] }

let private runtimePolicy: RuntimePolicy =
    { RuntimePolicy.defaults with Policy = policy; PatternSet = Wm.Policy.PolicyEvaluator.buildTitlePatternSet policy }

let private seedState () : WmState =
    let topology, _ = MonitorLifecycle.reconcile (Wm.Core.Topology.initial Map.empty) [ mkMonitor 0 ]
    { initial runtimePolicy with Topology = topology }

let private mkEnvelope (sequence: int64) (input: InputEvent) : EventEnvelope<InputEvent> =
    { Sequence = sequence; MonotonicTime = sequence; Source = "test"; SessionId = "test-session"; Input = input }

let private visibleWindowObserved (w: WindowId) : InputEvent =
    WindowObserved
        { Id = w
          Process = None
          AppIdentity = { Exe = None; HostExe = None; Aumid = None; CoreWindowExe = None }
          Owner = None
          IsDialog = None
          NativeRect = None
          VisibleFrameRect = None
          IsVisible = Some { Value = Ok true; SampleSequence = 1L; MonotonicTime = 1L; Source = "test" }
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

[<Fact>]
let ``WindowObservedで即座に分類確定した窓は、同じstepでアクティブなWorkspaceのStackへ入る`` () =
    let state = seedState ()
    let result = step runtimePolicy state (mkEnvelope 1L (visibleWindowObserved (WindowId 1L)))
    let active = activeWorkspace result.Next
    Assert.Equal(Some (WorkspaceId 0), active)
    match (WorkspaceSet.stateOf (WorkspaceId 0) result.Next.Workspaces).Stack with
    | Some z -> Assert.Contains(WindowId 1L, Zipper.toList z)
    | None -> Assert.True(false, "expected the window to be in the workspace's stack")
    match (Map.find (WindowId 1L) result.Next.Windows.Records).Classification with
    | Classified (EffTiled, ByPolicy (Some (PolicyRuleId "any-tiled"))) -> ()
    | other -> Assert.True(false, sprintf "unexpected: %A" other)

[<Fact>]
let ``WindowRemovedは同じstepでWorkspaces・Windows.Recordsの双方から除く`` () =
    let state = seedState ()
    let afterObserved = (step runtimePolicy state (mkEnvelope 1L (visibleWindowObserved (WindowId 1L)))).Next
    Assert.True(Map.containsKey (WindowId 1L) afterObserved.Windows.Records)
    let afterRemoved = (step runtimePolicy afterObserved (mkEnvelope 2L (WindowRemoved (WindowId 1L)))).Next
    Assert.False(Map.containsKey (WindowId 1L) afterRemoved.Windows.Records)
    Assert.Equal(None, WorkspaceSet.workspaceOf (WindowId 1L) afterRemoved.Workspaces)

let private visibleWindowObservedWithProcess (w: WindowId) (sequence: int64) (pid: int) : InputEvent =
    WindowObserved
        { (match visibleWindowObserved w with WindowObserved s -> s | _ -> failwith "unreachable") with
            Process = Some { Value = Ok { Pid = pid; CreationTime = 1L }; SampleSequence = sequence; MonotonicTime = sequence; Source = "test" } }

[<Fact>]
let ``HWND再利用(Processの不一致)は旧WindowIdをWindowRemovedと同じく全除去し、ForgetWindowを出す(v2.47)`` () =
    let state = seedState ()
    let afterFirst = (step runtimePolicy state (mkEnvelope 1L (visibleWindowObservedWithProcess (WindowId 1L) 1L 100))).Next
    Assert.True(Map.containsKey (WindowId 1L) afterFirst.Windows.Records)
    Assert.Equal(Some (WorkspaceId 0), WorkspaceSet.workspaceOf (WindowId 1L) afterFirst.Workspaces)
    let result = step runtimePolicy afterFirst (mkEnvelope 2L (visibleWindowObservedWithProcess (WindowId 1L) 2L 200))
    // 消滅と同じ全除去：Windows.Records、Workspacesの所属の双方から除かれる（WindowRemovedと同じ経路）。
    Assert.False(Map.containsKey (WindowId 1L) result.Next.Windows.Records)
    Assert.Equal(None, WorkspaceSet.workspaceOf (WindowId 1L) result.Next.Workspaces)
    // ShellのHWND→WindowId対応表から外させるForgetWindowが出る。
    Assert.Contains(ForgetWindow (WindowId 1L), result.Effects |> List.map GatedEffect.effect)

[<Fact>]
let ``同じProcessが繰り返し観測されてもHWND再利用とは判定されない`` () =
    let state = seedState ()
    let afterFirst = (step runtimePolicy state (mkEnvelope 1L (visibleWindowObservedWithProcess (WindowId 1L) 1L 100))).Next
    let result = step runtimePolicy afterFirst (mkEnvelope 2L (visibleWindowObservedWithProcess (WindowId 1L) 2L 100))
    Assert.True(Map.containsKey (WindowId 1L) result.Next.Windows.Records)
    Assert.Equal(Some (WorkspaceId 0), WorkspaceSet.workspaceOf (WindowId 1L) result.Next.Workspaces)
    Assert.DoesNotContain(ForgetWindow (WindowId 1L), result.Effects |> List.map GatedEffect.effect)

[<Fact>]
let ``Placement.MinWidthの信念は段⑤のClassificationViewへ実際に渡り、MinWidthAtLeastルールが機能する(v2.48)`` () =
    let widePolicy = { policy with WindowRules = [ { Id = PolicyRuleId "wide-floats"; Priority = 10; When = MinWidthAtLeast 400; Apply = Floating } ] }
    let rp = { runtimePolicy with Policy = widePolicy; PatternSet = Wm.Policy.PolicyEvaluator.buildTitlePatternSet widePolicy }
    let state = seedState ()
    let seeded = { state with Placement = { state.Placement with MinWidth = Map.ofList [ WindowId 1L, { Value = 500; Confidence = Inferred [ Sample 1L ]; LastRevisedAt = 0L } ] } }
    let result = step rp seeded (mkEnvelope 1L (visibleWindowObserved (WindowId 1L)))
    match (Map.find (WindowId 1L) result.Next.Windows.Records).Classification with
    | Classified (EffFloating, ByPolicy (Some (PolicyRuleId "wide-floats"))) -> ()
    | other -> Assert.True(false, sprintf "unexpected: %A" other)

[<Fact>]
let ``SetManagement Excludedは同じstepでWorkspacesから除くが、Windows.Recordsの信念は保持する(管理外化)`` () =
    let state = seedState ()
    let afterObserved = (step runtimePolicy state (mkEnvelope 1L (visibleWindowObserved (WindowId 1L)))).Next
    let afterExcluded = (step runtimePolicy afterObserved (mkEnvelope 2L (UserCommand (SetManagement (WindowId 1L, Excluded))))).Next
    Assert.Equal(None, WorkspaceSet.workspaceOf (WindowId 1L) afterExcluded.Workspaces)
    // 管理外化では信念(Records)は消えない——消滅(WindowRemoved)との違い。
    Assert.True(Map.containsKey (WindowId 1L) afterExcluded.Windows.Records)
    match (Map.find (WindowId 1L) afterExcluded.Windows.Records).Classification with
    | Classified (EffExcluded, ByUserOverride) -> ()
    | other -> Assert.True(false, sprintf "unexpected: %A" other)

// ===== Opusレビュー4-3（docs/review-phase-a-round1.md）：消滅時、動いている待ちのタイマーをCancelする =====

[<Fact>]
let ``WindowRemovedはHealthのReprobe待ちがあればCancelTimerを出す`` () =
    let state = seedState ()
    let withReprobe =
        { state with
            Health = { state.Health with Reprobes = Map.ofList [ WindowId 1L, ({ AttemptCount = 1; LastAttemptAt = 0L }, 1_000L) ] } }
    let result = step runtimePolicy withReprobe (mkEnvelope 1L (WindowRemoved (WindowId 1L)))
    Assert.Contains(CancelTimer (TimerId.Health (ResponsivenessReprobe (WindowId 1L))), result.Timers)

let private awaitingDialogPolicy: PolicyIR =
    { PolicyIR.defaults with WindowRules = [ { Id = PolicyRuleId "needs-dialog"; Priority = 10; When = DialogIs true; Apply = Tiled } ] }

let private awaitingRuntimePolicy: RuntimePolicy =
    { RuntimePolicy.defaults with Policy = awaitingDialogPolicy; PatternSet = Wm.Policy.PolicyEvaluator.buildTitlePatternSet awaitingDialogPolicy }

[<Fact>]
let ``WindowRemovedはAwaitingEvidence中のClassificationDeadlineタイマーをCancelする`` () =
    let topology, _ = MonitorLifecycle.reconcile (Wm.Core.Topology.initial Map.empty) [ mkMonitor 0 ]
    let state = { initial awaitingRuntimePolicy with Topology = topology }
    let afterObserved = (step awaitingRuntimePolicy state (mkEnvelope 1L (visibleWindowObserved (WindowId 1L)))).Next
    match (Map.find (WindowId 1L) afterObserved.Windows.Records).Classification with
    | AwaitingEvidence _ -> ()
    | other -> Assert.True(false, sprintf "expected AwaitingEvidence, got %A" other)
    let result = step awaitingRuntimePolicy afterObserved (mkEnvelope 2L (WindowRemoved (WindowId 1L)))
    Assert.Contains(CancelTimer (TimerId.Windows (ClassificationDeadline (WindowId 1L))), result.Timers)
