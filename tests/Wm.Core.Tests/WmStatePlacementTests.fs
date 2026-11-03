/// architecture.md §7.1：WmState.step経由で、MinimizedStateChanged（これまで配線先が無かった入力）が
/// 実際にPlacement.Geometryへ届くこと、WindowRemovedで消えることを検査する。
module Wm.Core.Tests.WmStatePlacementTests

open Xunit
open Wm.Contracts.Identifiers
open Wm.Contracts.ErrorTypes
open Wm.Contracts.Observation
open Wm.Contracts.Beliefs
open Wm.Contracts.SharedVocabulary
open Wm.Contracts.GatedEffect
open Wm.Contracts.CoreCommand
open Wm.Contracts.Timers
open Wm.Core.Topology
open Wm.Core.Workspaces
open Wm.Core.Windows
open Wm.Core.Placement
open Wm.Core.DecisionTrace
open Wm.Core.WmState
open Wm.Layouts.LayoutRegistry
open Wm.Policy.PolicyTypes
open Wm.Policy.MonitorSelector

let private mkEnvelope (sequence: int64) (input: InputEvent) : EventEnvelope<InputEvent> =
    { Sequence = sequence; MonotonicTime = sequence; Source = "test"; SessionId = "test-session"; Input = input }

let private mkMonitor (id: int) : MonitorSnapshot =
    { Id = MonitorId (int64 id)
      StableKey = sprintf "TEST:%d" id
      Bounds = { X = 0; Y = 0; Width = 1920; Height = 1080 }
      WorkArea = { X = 0; Y = 0; Width = 1920; Height = 1080 }
      Dpi = 96
      IsPrimary = (id = 0) }

let private emptyAppIdentity: AppIdentity = { Exe = None; HostExe = None; Aumid = None; CoreWindowExe = None }

let private visibleWindowObserved (w: WindowId) : InputEvent =
    WindowObserved
        { Id = w
          Process = None
          AppIdentity = emptyAppIdentity
          Owner = None
          IsDialog = None
          NativeRect = None
          VisibleFrameRect = None
          IsVisible = Some { Value = Ok true; SampleSequence = 1L; MonotonicTime = 1L; Source = "test" }
          IsMinimized = None
          IsMaximized = None
          Cloak = None
          Monitor = None
          MinWidthHint = None
          TargetIntegrity = None
          Responsiveness = None
          Presence = None
          DpiAwareness = None
          TitleMatches = None }

/// `visibleWindowObserved`に、観測された矩形(`VisibleFrameRect`)を付け加えたもの。Satisfied判定
/// （`Placement.checkPendingSatisfied`）やOS起因の移動の観測をテストで作るのに使う。
let private visibleWindowObservedAtRect (w: WindowId) (rect: PixelRect) (sequence: int64) : InputEvent =
    match visibleWindowObserved w with
    | WindowObserved s -> WindowObserved { s with VisibleFrameRect = Some { Value = Ok rect; SampleSequence = sequence; MonotonicTime = sequence; Source = "test" } }
    | other -> other

/// `visibleWindowObservedAtRect`に、観測されたモニター(`Geometry.Monitor`)も付け加えたもの。
/// `MoveCause.Unattributed`のクロスモニタードリフト検知（v2.70）をテストするのに使う——
/// `checkUnattributedDrift`は`Geometry.Monitor`信念を見るので、`VisibleFrameRect`だけでは検知しない。
let private visibleWindowObservedAtRectAndMonitor (w: WindowId) (rect: PixelRect) (monitor: MonitorId) (sequence: int64) : InputEvent =
    match visibleWindowObservedAtRect w rect sequence with
    | WindowObserved s -> WindowObserved { s with Monitor = Some { Value = Ok monitor; SampleSequence = sequence; MonotonicTime = sequence; Source = "test" } }
    | other -> other

/// `visibleWindowObserved`に、`IsMaximized = true`の観測を付け加えたもの。`RestoreBeforeMove`の発行と
/// ゲートの`MissingRestoreBeforeMove`拒否を回避できることの結合テスト（v2.74）に使う。
let private visibleWindowObservedMaximized (w: WindowId) : InputEvent =
    match visibleWindowObserved w with
    | WindowObserved s -> WindowObserved { s with IsMaximized = Some { Value = Ok true; SampleSequence = 1L; MonotonicTime = 1L; Source = "test" } }
    | other -> other

/// `visibleWindowObserved`に、`Responsiveness`の観測(`PumpResponded`/`PumpTimedOut`等)を付け加えたもの。
/// Health.applyEvidenceへの中継(v2.84)をstep経由で検証するのに使う。
let private visibleWindowObservedWithResponsiveness (w: WindowId) (sample: ResponsivenessSample) (sequence: int64) : InputEvent =
    match visibleWindowObserved w with
    | WindowObserved s -> WindowObserved { s with Responsiveness = Some { Value = Ok sample; SampleSequence = sequence; MonotonicTime = sequence; Source = "test" } }
    | other -> other

[<Fact>]
let ``新規窓のWindowObservedは、同じstepでWindows.RecordsがAliveになるので、PlacementのReprobeがゲートを通過する`` () =
    let state = initial RuntimePolicy.defaults
    let result = step RuntimePolicy.defaults state (mkEnvelope 1L (visibleWindowObserved (WindowId 1L)))
    let effects = result.Effects |> List.map GatedEffect.effect
    Assert.Contains(Reprobe (WindowId 1L, KTargetIntegrity), effects)

[<Fact>]
let ``最大化状態で発見された窓のReconcileは、RestoreBeforeMoveがNeedsRestoreへ結線されゲートで拒否されず、SetRectの直前に発行される(v2.74)`` () =
    let w = WindowId 1L
    let settleMs = TopologyConfig.defaults.SettleMs
    let afterMonitor0 = step RuntimePolicy.defaults (initial RuntimePolicy.defaults) (mkEnvelope 1L (MonitorTopologyObserved [ mkMonitor 0 ]))
    let afterSettle0 = step RuntimePolicy.defaults afterMonitor0.Next (mkEnvelope (1L + settleMs) (TimerElapsed (TimerId.Topology ScreenSettle)))
    let result = step RuntimePolicy.defaults afterSettle0.Next (mkEnvelope 100L (visibleWindowObservedMaximized w))
    let effects = result.Effects |> List.map GatedEffect.effect
    match effects |> List.filter (function RestoreBeforeMove (_, rw) -> rw = w | SetRect (_, sw, _) -> sw = w | _ -> false) with
    | [ RestoreBeforeMove (_, rw); SetRect (_, sw, _) ] ->
        Assert.Equal(w, rw)
        Assert.Equal(w, sw)
    | other -> Assert.True(false, sprintf "expected [RestoreBeforeMove; SetRect] for the maximized window (gate must not reject), got %A" other)

// ===== §5.2「設定上のモニター割当」(PolicyIR.WorkspaceAssignments、R3-1、v2.80) =====

[<Fact>]
let ``initialはPolicyのWorkspaceAssignmentsをTopologyへ写し、新しいモニターの出現時に効く(v2.80)`` () =
    // 既知のギャップ修正：initialがruntimePolicy引数を受け取っているにもかかわらず、長らく
    // Topology.initial Map.emptyを決め打ちしており、Policyで設定したWorkspace→モニター割当が
    // 新しいモニターの出現時に一度も効かなかった。
    let assignedWorkspace = WorkspaceId 7
    let assignedPolicy: PolicyIR = { PolicyIR.defaults with WorkspaceAssignments = [ (assignedWorkspace, PrimaryMonitor) ] }
    let assignedRuntimePolicy: RuntimePolicy = { RuntimePolicy.defaults with Policy = assignedPolicy }
    let state0 = initial assignedRuntimePolicy
    let settleMs = TopologyConfig.defaults.SettleMs
    let afterMonitor = step assignedRuntimePolicy state0 (mkEnvelope 1L (MonitorTopologyObserved [ mkMonitor 0 ]))
    let afterSettle = step assignedRuntimePolicy afterMonitor.Next (mkEnvelope (1L + settleMs) (TimerElapsed (TimerId.Topology ScreenSettle)))
    match displays afterSettle.Next.Topology with
    | Displays (current, _) -> Assert.Equal(assignedWorkspace, current.Workspace) // 割当が無ければws0(最小未使用)になるはず
    | NoMonitors -> Assert.True(false, "expected a monitor to be connected")

[<Fact>]
let ``WorkspaceAssignmentsが空(既定)なら、initialの挙動はv2.80以前と変わらない(回帰確認)`` () =
    let state0 = initial RuntimePolicy.defaults
    let settleMs = TopologyConfig.defaults.SettleMs
    let afterMonitor = step RuntimePolicy.defaults state0 (mkEnvelope 1L (MonitorTopologyObserved [ mkMonitor 0 ]))
    let afterSettle = step RuntimePolicy.defaults afterMonitor.Next (mkEnvelope (1L + settleMs) (TimerElapsed (TimerId.Topology ScreenSettle)))
    match displays afterSettle.Next.Topology with
    | Displays (current, _) -> Assert.Equal(WorkspaceId 0, current.Workspace) // 割当が無いので最小未使用のws0
    | NoMonitors -> Assert.True(false, "expected a monitor to be connected")

[<Fact>]
let ``MinimizedStateChangedはstep経由でPlacement.Geometry.IsMinimizedへ届く`` () =
    let state = initial RuntimePolicy.defaults
    let obs: Observation<bool> = { Value = Ok true; SampleSequence = 1L; MonotonicTime = 1L; Source = "test" }
    let result = step RuntimePolicy.defaults state (mkEnvelope 1L (MinimizedStateChanged (WindowId 1L, obs)))
    let g = Map.find (WindowId 1L) result.Next.Placement.Geometry
    Assert.Equal(Some true, LatestWins.value g.IsMinimized)

[<Fact>]
let ``新規窓がTiledに分類されると、同じstepでReconcileがSetRectを発行し、観測が一致するとPendingが消える`` () =
    let topology, _ = MonitorLifecycle.reconcile (Wm.Core.Topology.initial Map.empty) [ mkMonitor 0 ]
    let seeded = { initial RuntimePolicy.defaults with Topology = topology }
    let afterObserved = step RuntimePolicy.defaults seeded (mkEnvelope 1L (visibleWindowObserved (WindowId 1L)))
    let effects = afterObserved.Effects |> List.map GatedEffect.effect
    let targetRect: PixelRect = { X = 0; Y = 0; Width = 1920; Height = 1080 }
    Assert.Contains(SetRect (RequestId 0L, WindowId 1L, targetRect), effects)
    Assert.Equal(1, Map.count afterObserved.Next.Placement.Pending)

    // Reprobe応答と同じ形で、目標と一致するVisibleFrameRectの観測が届くとPendingがSatisfiedで消える。
    let snapshotWithRect =
        match visibleWindowObserved (WindowId 1L) with
        | WindowObserved s -> WindowObserved { s with VisibleFrameRect = Some { Value = Ok targetRect; SampleSequence = 2L; MonotonicTime = 2L; Source = "test" } }
        | other -> other
    let afterSatisfied = step RuntimePolicy.defaults afterObserved.Next (mkEnvelope 2L snapshotWithRect)
    Assert.Empty(afterSatisfied.Next.Placement.Pending)

[<Fact>]
let ``EffectCallReturnedのTargetHungは同じstepでHealth.Responsivenessも修正する(ドメインをまたぐ配線)`` () =
    let topology, _ = MonitorLifecycle.reconcile (Wm.Core.Topology.initial Map.empty) [ mkMonitor 0 ]
    let seeded = { initial RuntimePolicy.defaults with Topology = topology }
    let afterObserved = step RuntimePolicy.defaults seeded (mkEnvelope 1L (visibleWindowObserved (WindowId 1L)))
    let rid =
        afterObserved.Next.Placement.Pending
        |> Map.toSeq
        |> Seq.filter (fun (_, (pr: PendingRequest)) -> pr.Window = WindowId 1L)
        |> Seq.map fst
        |> Seq.head
    let hungError: NativeCallRecord = { Api = "SendMessageTimeout"; Space = Win32LastError; Code = 1460; ElapsedUs = 0L }
    let afterHung = step RuntimePolicy.defaults afterObserved.Next (mkEnvelope 2L (EffectCallReturned (rid, Error hungError, None)))
    match Map.tryFind (WindowId 1L) afterHung.Next.Health.Responsiveness with
    | Some { Value = Hung; Confidence = Confirmed } -> ()
    | other -> Assert.True(false, sprintf "unexpected: %A" other)
    Assert.Empty(afterHung.Next.Placement.Pending)

/// v2.84：`WindowObserved`の`Responsiveness`観測が`Health.applyEvidence`へ中継され、`Hung`から
/// `Responding`へ回復できることを`step`越しに確認する。それまで`WindowObserved`からHealthへの経路が
/// 一度も無かったため、`TargetHung`で`Hung`になった窓は原理的に二度と`Responding`へ戻れなかった
/// （Health.fsの型直前のコメント参照）。
[<Fact>]
let ``TargetHungでHungになった窓は、WindowObservedのPumpRespondedでRespondingへ回復する(v2.84)`` () =
    let topology, _ = MonitorLifecycle.reconcile (Wm.Core.Topology.initial Map.empty) [ mkMonitor 0 ]
    let seeded = { initial RuntimePolicy.defaults with Topology = topology }
    let w = WindowId 1L
    let afterObserved = step RuntimePolicy.defaults seeded (mkEnvelope 1L (visibleWindowObserved w))
    let rid =
        afterObserved.Next.Placement.Pending
        |> Map.toSeq
        |> Seq.filter (fun (_, (pr: PendingRequest)) -> pr.Window = w)
        |> Seq.map fst
        |> Seq.head
    let hungError: NativeCallRecord = { Api = "SendMessageTimeout"; Space = Win32LastError; Code = 1460; ElapsedUs = 0L }
    let afterHung = step RuntimePolicy.defaults afterObserved.Next (mkEnvelope 2L (EffectCallReturned (rid, Error hungError, None)))
    match Map.tryFind w afterHung.Next.Health.Responsiveness with
    | Some { Value = Hung } -> ()
    | other -> Assert.True(false, sprintf "precondition failed, unexpected: %A" other)
    // Reprobe(w, KResponsiveness)が実際にゲートを通過して発行されている(ReprobeはRBelievedHungでも
    // 適用されないEffect種別、GatedEffect.fsの表参照)。
    let reprobeEffects = afterHung.Effects |> List.map GatedEffect.effect |> List.filter (function Reprobe (ww, KResponsiveness) -> ww = w | _ -> false)
    Assert.NotEmpty(reprobeEffects)

    let afterRecovered = step RuntimePolicy.defaults afterHung.Next (mkEnvelope 3L (visibleWindowObservedWithResponsiveness w PumpResponded 10L))
    match Map.tryFind w afterRecovered.Next.Health.Responsiveness with
    | Some { Value = Responding; Confidence = Confirmed } -> ()
    | other -> Assert.True(false, sprintf "unexpected after recovery: %A" other)
    Assert.False(Map.containsKey w afterRecovered.Next.Health.Reprobes)

[<Fact>]
let ``EffectCallReturnedのDenied OtherDenialは、同じstepでMovability修正・Unplacedへの降格・Workspaceからの除去までつながる`` () =
    let topology, _ = MonitorLifecycle.reconcile (Wm.Core.Topology.initial Map.empty) [ mkMonitor 0 ]
    let seeded = { initial RuntimePolicy.defaults with Topology = topology }
    let w = WindowId 1L
    let afterObserved = step RuntimePolicy.defaults seeded (mkEnvelope 1L (visibleWindowObserved w))
    Assert.True((WorkspaceSet.workspaceOf w afterObserved.Next.Workspaces).IsSome)
    let rid =
        afterObserved.Next.Placement.Pending
        |> Map.toSeq
        |> Seq.filter (fun (_, (pr: PendingRequest)) -> pr.Window = w)
        |> Seq.map fst
        |> Seq.head
    let deniedError: NativeCallRecord = { Api = "SetWindowPos"; Space = Win32LastError; Code = 5; ElapsedUs = 0L }
    let afterDenied = step RuntimePolicy.defaults afterObserved.Next (mkEnvelope 2L (EffectCallReturned (rid, Error deniedError, None)))

    // Movabilityの修正
    match Map.tryFind w afterDenied.Next.Placement.Movability with
    | Some { Value = NotMovable } -> ()
    | other -> Assert.True(false, sprintf "unexpected Movability: %A" other)
    Assert.Empty(afterDenied.Next.Placement.Pending)

    // Windowsの分類がUnplacedへ降格
    match Map.tryFind w afterDenied.Next.Windows.Records |> Option.map (fun r -> r.Classification) with
    | Some (Classified (EffUnplaced, _)) -> ()
    | other -> Assert.True(false, sprintf "unexpected Classification: %A" other)

    // WorkspacesからもFocus.Historyからも除かれている（§4.4「Unplacedはどのworkspaceにも属さない」）
    Assert.Equal(None, WorkspaceSet.workspaceOf w afterDenied.Next.Workspaces)

    // 2つ目の窓が現れてReconcileが再び走っても、Unplacedになった窓はもはやTiledWindowsに含まれず、
    // 無駄なSetRectが再発行されない（既知のギャップ注記のとおり、ゲート拒否の繰返しにはならない）。
    let w2 = WindowId 2L
    let afterSecondWindow = step RuntimePolicy.defaults afterDenied.Next (mkEnvelope 3L (visibleWindowObserved w2))
    let effects = afterSecondWindow.Effects |> List.map GatedEffect.effect
    let setRectsForW = effects |> List.choose (function SetRect (_, target, _) when target = w -> Some target | _ -> None)
    Assert.Empty(setRectsForW)

/// v2.83：`Movability`が`NotMovable`になった窓(上記のDenied OtherDenialと同じ経路で用意する)に
/// ユーザーが`ToggleFloating`/`SetManagement`を送ると、同じstepでPlacement.onUserCommandが
/// `Movable (Inferred [])`へ1回だけ戻す(architecture.md §4.5「ユーザーの明示操作は古い推定を
/// 捨てる根拠になる」、R4-11)。Windowsドメイン(段①)が同じ入力で分類をFloatingへ変えるのとは
/// 独立に、Placementは自分のMovability信念だけを直す(ADR-018)。
[<Fact>]
let ``NotMovableになった窓にToggleFloatingを送ると、同じstepでMovabilityがMovableへ1回だけ試し直される(v2.83)`` () =
    let topology, _ = MonitorLifecycle.reconcile (Wm.Core.Topology.initial Map.empty) [ mkMonitor 0 ]
    let seeded = { initial RuntimePolicy.defaults with Topology = topology }
    let w = WindowId 1L
    let afterObserved = step RuntimePolicy.defaults seeded (mkEnvelope 1L (visibleWindowObserved w))
    let rid =
        afterObserved.Next.Placement.Pending
        |> Map.toSeq
        |> Seq.filter (fun (_, (pr: PendingRequest)) -> pr.Window = w)
        |> Seq.map fst
        |> Seq.head
    let deniedError: NativeCallRecord = { Api = "SetWindowPos"; Space = Win32LastError; Code = 5; ElapsedUs = 0L }
    let afterDenied = step RuntimePolicy.defaults afterObserved.Next (mkEnvelope 2L (EffectCallReturned (rid, Error deniedError, None)))
    match Map.tryFind w afterDenied.Next.Placement.Movability with
    | Some { Value = NotMovable } -> ()
    | other -> Assert.True(false, sprintf "precondition failed, unexpected Movability: %A" other)

    let afterToggle = step RuntimePolicy.defaults afterDenied.Next (mkEnvelope 3L (UserCommand (ToggleFloating w)))
    match Map.tryFind w afterToggle.Next.Placement.Movability with
    | Some { Value = Movable; Confidence = Inferred [] } -> ()
    | other -> Assert.True(false, sprintf "unexpected Movability after ToggleFloating: %A" other)

[<Fact>]
let ``NotMovableと無関係な窓へのUserCommandは、その窓のMovabilityを変えない(回帰確認、v2.83)`` () =
    let topology, _ = MonitorLifecycle.reconcile (Wm.Core.Topology.initial Map.empty) [ mkMonitor 0 ]
    let seeded = { initial RuntimePolicy.defaults with Topology = topology }
    let w = WindowId 1L
    let afterObserved = step RuntimePolicy.defaults seeded (mkEnvelope 1L (visibleWindowObserved w))
    let before = Map.tryFind w afterObserved.Next.Placement.Movability
    let afterToggle = step RuntimePolicy.defaults afterObserved.Next (mkEnvelope 2L (UserCommand (ToggleFloating w)))
    Assert.Equal(before, Map.tryFind w afterToggle.Next.Placement.Movability)

[<Fact>]
let ``Placement.MinWidthの信念は段⑦のLayoutContext.TiledWindowsへ実際に渡る(v2.48)`` () =
    let captured = ref Option<LayoutContext>.None
    let capturingMonocle: LayoutImplementation =
        { Id = LayoutId "monocle"
          ApiVersion = 1
          ImplementationDigest = "test-capture"
          Calculate = fun args ctx -> captured.Value <- Some ctx; (Wm.Layouts.Monocle.implementation "builtin").Calculate args ctx }
    let rp = { RuntimePolicy.defaults with LayoutRegistry = Registry.register capturingMonocle RuntimePolicy.defaults.LayoutRegistry }
    let topology, _ = MonitorLifecycle.reconcile (Wm.Core.Topology.initial Map.empty) [ mkMonitor 0 ]
    let belief: Belief<int> = { Value = 500; Confidence = Inferred [ Sample 1L ]; LastRevisedAt = 0L }
    let seeded = { initial rp with Topology = topology; Placement = { (initial rp).Placement with MinWidth = Map.ofList [ WindowId 1L, belief ] } }
    step rp seeded (mkEnvelope 1L (visibleWindowObserved (WindowId 1L))) |> ignore
    match captured.Value with
    | Some ctx ->
        match ctx.TiledWindows |> List.tryFind (fun tw -> tw.Id = WindowId 1L) with
        | Some tw -> Assert.Equal(Some belief, tw.MinWidthDip)
        | None -> Assert.True(false, "window not found in TiledWindows")
    | None -> Assert.True(false, "layout implementation was never invoked")

[<Fact>]
let ``2台のモニターそれぞれにWorkspaceが表示されていると、Reconcileはアクティブでない方の窓も配置する`` () =
    let topology, _ = MonitorLifecycle.reconcile (Wm.Core.Topology.initial Map.empty) [ mkMonitor 0; mkMonitor 1 ]
    let current, visible =
        match displays topology with
        | Displays (c, v) -> c, v
        | NoMonitors -> failwith "expected 2 monitors to settle into Displays"
    match visible with
    | [ visibleScreen ] ->
        Assert.NotEqual(current.Workspace, visibleScreen.Workspace)
        let w1, w2 = WindowId 1L, WindowId 2L
        let seeded = { initial RuntimePolicy.defaults with Topology = topology }
        let afterW1 = step RuntimePolicy.defaults seeded (mkEnvelope 1L (visibleWindowObserved w1))
        // w1は新規発見なのでActiveWorkspace（current.Workspace）に分類・配置される。
        Assert.Equal(Some current.Workspace, WorkspaceSet.workspaceOf w1 afterW1.Next.Workspaces)

        // w2は、UserCommand経由の正規の入口（ClassificationのActiveWorkspace割当かMoveFocusedToWorkspace）
        // ではなく、表示中だがアクティブでない方のWorkspaceに属するTiled窓として直接投入する
        // （前回セッションから引き続き存在する窓を模す。このテストの主眼はReconcileのWorkspace横断動作
        // であって窓の配置経路ではないため、最短で状態を作る）。
        let windows2 =
            { afterW1.Next.Windows with
                Records = Map.add w2 { WindowRecord.initial with Classification = Classified (EffTiled, ByUserOverride) } afterW1.Next.Windows.Records }
        let workspaces2 = WorkspaceSet.addWindow visibleScreen.Workspace w2 false afterW1.Next.Workspaces
        let seeded2 = { afterW1.Next with Windows = windows2; Workspaces = workspaces2 }

        // Reconcileの引き金は、shouldReconcileの条件を満たすだけでよい無関係な窓の消滅で十分。
        let afterReconcile = step RuntimePolicy.defaults seeded2 (mkEnvelope 2L (WindowRemoved (WindowId 999L)))
        let effects = afterReconcile.Effects |> List.map GatedEffect.effect
        let rectsForW2 = effects |> List.choose (function SetRect (_, target, rect) when target = w2 -> Some rect | _ -> None)
        Assert.Equal<PixelRect list>([ visibleScreen.Monitor.WorkArea ], rectsForW2)
        // アクティブなWorkspaceのw1はDesiredが既に一致しているので、同じstepで重複SetRectは発行されない。
        let rectsForW1 = effects |> List.choose (function SetRect (_, target, rect) when target = w1 -> Some rect | _ -> None)
        Assert.Empty(rectsForW1)
    | other -> Assert.True(false, sprintf "expected exactly 1 visible (non-active) screen, got %A" other)

[<Fact>]
let ``SwitchWorkspaceで既にvisibleなWorkspaceへ切り替えると、currentが入れ替わりそのzipperのFocusへRequestForegroundする(v2.32)`` () =
    let topology, _ = MonitorLifecycle.reconcile (Wm.Core.Topology.initial Map.empty) [ mkMonitor 0; mkMonitor 1 ]
    let current, visible =
        match displays topology with
        | Displays (c, v) -> c, v
        | NoMonitors -> failwith "expected 2 monitors to settle into Displays"
    match visible with
    | [ visibleScreen ] ->
        let w1, w2 = WindowId 1L, WindowId 2L
        let seeded = { initial RuntimePolicy.defaults with Topology = topology }
        let afterW1 = step RuntimePolicy.defaults seeded (mkEnvelope 1L (visibleWindowObserved w1))
        let windows2 =
            { afterW1.Next.Windows with
                Records = Map.add w2 { WindowRecord.initial with Classification = Classified (EffTiled, ByUserOverride) } afterW1.Next.Windows.Records }
        let workspaces2 = WorkspaceSet.addWindow visibleScreen.Workspace w2 false afterW1.Next.Workspaces
        let seeded2 = { afterW1.Next with Windows = windows2; Workspaces = workspaces2 }

        let afterSwitch = step RuntimePolicy.defaults seeded2 (mkEnvelope 2L (UserCommand (SwitchWorkspace visibleScreen.Workspace)))
        match displays afterSwitch.Next.Topology with
        | Displays (newCurrent, [ newVisible ]) ->
            Assert.Equal(visibleScreen.Workspace, newCurrent.Workspace)
            Assert.Equal(current.Workspace, newVisible.Workspace)
        | other -> failwithf "expected current/visible to swap, got %A" other

        let effects = afterSwitch.Effects |> List.map GatedEffect.effect
        Assert.Contains(effects, function RequestForeground (_, target) -> target = w2 | _ -> false)
    | other -> Assert.True(false, sprintf "expected exactly 1 visible (non-active) screen, got %A" other)

[<Fact>]
let ``FocusMonitorはアクティブなモニターを切り替え、そのzipperのFocusへRequestForegroundする(v2.36)`` () =
    let topology, _ = MonitorLifecycle.reconcile (Wm.Core.Topology.initial Map.empty) [ mkMonitor 0; mkMonitor 1 ]
    let current, visible =
        match displays topology with
        | Displays (c, v) -> c, v
        | NoMonitors -> failwith "expected 2 monitors to settle into Displays"
    match visible with
    | [ visibleScreen ] ->
        let w2 = WindowId 2L
        let seeded = { initial RuntimePolicy.defaults with Topology = topology }
        let windows2 =
            { seeded.Windows with
                Records = Map.add w2 { WindowRecord.initial with Classification = Classified (EffTiled, ByUserOverride) } seeded.Windows.Records }
        let workspaces2 = WorkspaceSet.addWindow visibleScreen.Workspace w2 false seeded.Workspaces
        let seeded2 = { seeded with Windows = windows2; Workspaces = workspaces2 }

        let afterFocusMonitor = step RuntimePolicy.defaults seeded2 (mkEnvelope 1L (UserCommand (FocusMonitor visibleScreen.Monitor.Id)))
        match displays afterFocusMonitor.Next.Topology with
        | Displays (newCurrent, [ newVisible ]) ->
            Assert.Equal(visibleScreen.Workspace, newCurrent.Workspace) // monitor1はws1を表示したまま
            Assert.Equal(current.Workspace, newVisible.Workspace)
        | other -> failwithf "expected current/visible to swap by monitor, got %A" other

        let effects = afterFocusMonitor.Effects |> List.map GatedEffect.effect
        Assert.Contains(effects, function RequestForeground (_, target) -> target = w2 | _ -> false)
    | other -> Assert.True(false, sprintf "expected exactly 1 visible (non-active) screen, got %A" other)

let private mkForegroundObserved (sequence: int64) (w: WindowId) : InputEvent =
    ForegroundObserved { Value = Ok (Some w); SampleSequence = sequence; MonotonicTime = sequence; Source = "test" }

[<Fact>]
let ``MoveFocusedToWorkspaceは、フォーカス中の窓を別のWorkspaceへ送り、元のWorkspaceは隣の窓へRequestForegroundする`` () =
    let topology, _ = MonitorLifecycle.reconcile (Wm.Core.Topology.initial Map.empty) [ mkMonitor 0 ]
    let seeded = { initial RuntimePolicy.defaults with Topology = topology }
    let w1, w2 = WindowId 1L, WindowId 2L
    let afterW1 = step RuntimePolicy.defaults seeded (mkEnvelope 1L (visibleWindowObserved w1))
    let afterW2 = step RuntimePolicy.defaults afterW1.Next (mkEnvelope 2L (visibleWindowObserved w2))
    let srcWs = WorkspaceSet.workspaceOf w1 afterW2.Next.Workspaces |> Option.get
    Assert.Equal(Some srcWs, WorkspaceSet.workspaceOf w2 afterW2.Next.Workspaces)

    let afterFocused = step RuntimePolicy.defaults afterW2.Next (mkEnvelope 3L (mkForegroundObserved 3L w1))
    let dest = WorkspaceId 99
    let afterMove = step RuntimePolicy.defaults afterFocused.Next (mkEnvelope 4L (UserCommand (MoveFocusedToWorkspace dest)))

    Assert.Equal(Some dest, WorkspaceSet.workspaceOf w1 afterMove.Next.Workspaces)
    // 元のWorkspaceのzipperはw1を除いた隣のw2へFocusが移っている。
    match (WorkspaceSet.stateOf srcWs afterMove.Next.Workspaces).Stack with
    | Some z -> Assert.Equal(w2, z.Focus)
    | None -> Assert.True(false, "expected w2 to remain in the source Workspace")
    let effects = afterMove.Effects |> List.map GatedEffect.effect
    Assert.Contains(effects, function RequestForeground (_, target) -> target = w2 | _ -> false)

[<Fact>]
let ``MoveFocusedToWorkspaceは、移動先が既に所属するWorkspaceと同じ場合は窓の並び順も変えず何もしない`` () =
    let topology, _ = MonitorLifecycle.reconcile (Wm.Core.Topology.initial Map.empty) [ mkMonitor 0 ]
    let seeded = { initial RuntimePolicy.defaults with Topology = topology }
    // 2窓にする——1窓だけだと「取り除いて同じWorkspaceの末尾へ再追加」しても並びが変わらず、
    // ガードが抜けていても区別できない（最初のmutation testingの試行で実際に踏んだ）。
    let w1, w2 = WindowId 1L, WindowId 2L
    let afterW1 = step RuntimePolicy.defaults seeded (mkEnvelope 1L (visibleWindowObserved w1))
    let afterW2 = step RuntimePolicy.defaults afterW1.Next (mkEnvelope 2L (visibleWindowObserved w2))
    let srcWs = WorkspaceSet.workspaceOf w1 afterW2.Next.Workspaces |> Option.get
    let afterFocused = step RuntimePolicy.defaults afterW2.Next (mkEnvelope 3L (mkForegroundObserved 3L w1))
    let afterMove = step RuntimePolicy.defaults afterFocused.Next (mkEnvelope 4L (UserCommand (MoveFocusedToWorkspace srcWs)))
    Assert.Equal(afterFocused.Next.Workspaces, afterMove.Next.Workspaces)

[<Fact>]
let ``MoveFocusedToWorkspaceは、フォーカスが無い(Elsewhere)なら何もしない`` () =
    let seeded = initial RuntimePolicy.defaults
    let afterMove = step RuntimePolicy.defaults seeded (mkEnvelope 1L (UserCommand (MoveFocusedToWorkspace (WorkspaceId 7))))
    Assert.Equal(WorkspaceSet.empty, afterMove.Next.Workspaces)

let private settleTwoMonitors (afterFirstWindow: StepResult) (monitor1: MonitorSnapshot) (now: int64) : StepResult =
    let settleMs = TopologyConfig.defaults.SettleMs
    let afterMonitors = step RuntimePolicy.defaults afterFirstWindow.Next (mkEnvelope now (MonitorTopologyObserved [ mkMonitor 0; monitor1 ]))
    step RuntimePolicy.defaults afterMonitors.Next (mkEnvelope (now + settleMs) (TimerElapsed (TimerId.Topology ScreenSettle)))

let private settleOneMonitorThenDiscoverWindow (w: WindowId) : StepResult =
    let settleMs = TopologyConfig.defaults.SettleMs
    let afterMonitor0 = step RuntimePolicy.defaults (initial RuntimePolicy.defaults) (mkEnvelope 1L (MonitorTopologyObserved [ mkMonitor 0 ]))
    let afterSettle0 = step RuntimePolicy.defaults afterMonitor0.Next (mkEnvelope (1L + settleMs) (TimerElapsed (TimerId.Topology ScreenSettle)))
    match displays afterSettle0.Next.Topology with
    | Displays _ -> ()
    | NoMonitors -> failwith "expected the first monitor to settle"
    step RuntimePolicy.defaults afterSettle0.Next (mkEnvelope 100L (visibleWindowObserved w))

[<Fact>]
let ``TopologySettledは、窓のモニターが変わらない場合は古いPendingを取り消すだけで新しいPendingを作らない(v2.29)`` () =
    let w = WindowId 1L
    let afterW = settleOneMonitorThenDiscoverWindow w
    Assert.Equal(1, Map.count afterW.Next.Placement.Pending)
    let targetBefore = Map.tryFind w afterW.Next.Placement.Desired

    // 2台目のモニターが現れるが、wのモニター（monitor0）自体の構成は変わらない——Reconcileが計算し
    // 直す目標も前回と同じなので、dispatchPlacementsのdesiredChanged判定により再発行の必要が無い
    // （Supersededにした意味はあった：古いPendingが指す観測待ちは、もう当てにできないため取り消す）。
    let afterSettle1 = settleTwoMonitors afterW (mkMonitor 1) 200L
    Assert.Empty(afterSettle1.Next.Placement.Pending) // 古いridは消え、新しいridも作られない
    Assert.Equal(targetBefore, Map.tryFind w afterSettle1.Next.Placement.Desired)
    Assert.Equal(None, Map.tryFind w afterSettle1.Next.Placement.RetryAttempts)

[<Fact>]
let ``TopologySettledは、窓のモニターの配置が変わった場合は新しい目標でPendingを発行し直す(v2.29)`` () =
    let w = WindowId 1L
    let afterW = settleOneMonitorThenDiscoverWindow w
    let rid0 = afterW.Next.Placement.Pending |> Map.toSeq |> Seq.head |> fst

    // monitor0の解像度/WorkAreaが変わった（モニター自体は同じId、構成の変化としてSettlingへ入る）。
    let resizedMonitor0 = { mkMonitor 0 with Bounds = { X = 0; Y = 0; Width = 1280; Height = 720 }; WorkArea = { X = 0; Y = 0; Width = 1280; Height = 720 } }
    let afterMonitorResized = step RuntimePolicy.defaults afterW.Next (mkEnvelope 200L (MonitorTopologyObserved [ resizedMonitor0 ]))
    let afterSettle1 =
        step RuntimePolicy.defaults afterMonitorResized.Next (mkEnvelope (200L + TopologyConfig.defaults.SettleMs) (TimerElapsed (TimerId.Topology ScreenSettle)))

    Assert.Equal(1, Map.count afterSettle1.Next.Placement.Pending)
    let rid1 = afterSettle1.Next.Placement.Pending |> Map.toSeq |> Seq.head |> fst
    Assert.NotEqual(rid0, rid1)
    Assert.Equal(Some (Shown (Some resizedMonitor0.WorkArea)), Map.tryFind w afterSettle1.Next.Placement.Desired)

[<Fact>]
let ``モニターの切断はWorkspaceHiddenを経てHiddenByWm+SetPresence(false)を発行し、再接続はShown Noneから再配置する(v2.31)`` () =
    let w = WindowId 1L
    let afterW = settleOneMonitorThenDiscoverWindow w
    let ws0 = WorkspaceSet.workspaceOf w afterW.Next.Workspaces |> Option.get

    // モニター0が切断される（0台になる）。
    let settleMs = TopologyConfig.defaults.SettleMs
    let afterDisconnect = step RuntimePolicy.defaults afterW.Next (mkEnvelope 200L (MonitorTopologyObserved []))
    let afterDisconnectSettle =
        step RuntimePolicy.defaults afterDisconnect.Next (mkEnvelope (200L + settleMs) (TimerElapsed (TimerId.Topology ScreenSettle)))
    match displays afterDisconnectSettle.Next.Topology with
    | NoMonitors -> ()
    | Displays _ -> failwith "expected 0 monitors after disconnecting the only one"

    Assert.Equal(Some HiddenByWm, Map.tryFind w afterDisconnectSettle.Next.Placement.Desired)
    let hideEffects = afterDisconnectSettle.Effects |> List.map GatedEffect.effect
    Assert.Contains(hideEffects, function SetPresence (_, target, false) -> target = w | _ -> false)
    // Pendingも(TopologySettledの一括Supersededで)取り消されている。
    Assert.Empty(afterDisconnectSettle.Next.Placement.Pending)

    // モニター0が再接続される。切断時に退避したWorkspace(ws0)が同じモニターへ戻る（§4.4のReturns規則）。
    let afterReconnect = step RuntimePolicy.defaults afterDisconnectSettle.Next (mkEnvelope 400L (MonitorTopologyObserved [ mkMonitor 0 ]))
    let afterReconnectSettle =
        step RuntimePolicy.defaults afterReconnect.Next (mkEnvelope (400L + settleMs) (TimerElapsed (TimerId.Topology ScreenSettle)))
    match displays afterReconnectSettle.Next.Topology with
    | Displays (current, _) -> Assert.Equal(ws0, current.Workspace)
    | NoMonitors -> failwith "expected the monitor to settle back"

    // Shown None → Reconcileが新しいSetRectを発行し直す、かつSetPresence(true)も発行されている。
    Assert.Equal(1, Map.count afterReconnectSettle.Next.Placement.Pending)
    Assert.Equal(Some (Shown (Some (mkMonitor 0).WorkArea)), Map.tryFind w afterReconnectSettle.Next.Placement.Desired)
    let showEffects = afterReconnectSettle.Effects |> List.map GatedEffect.effect
    Assert.Contains(showEffects, function SetPresence (_, target, true) -> target = w | _ -> false)
    // モニター再接続による表示（ShownBy = Reconcile）はユーザー操作ではないので、フォーカスは要求しない
    // （§4.4、v2.32。RequestForegroundを要求するのはShownBy = UserView/CurrentChangedだけ）。
    Assert.DoesNotContain(showEffects, function RequestForeground (_, target) -> target = w | _ -> false)

[<Fact>]
let ``SwitchWorkspaceでhiddenなWorkspaceへ切り替えると、新しくcurrentになったWorkspaceのzipperのFocusへRequestForegroundする(v2.32)`` () =
    let w1 = WindowId 1L
    let afterW1 = settleOneMonitorThenDiscoverWindow w1
    let ws0 = WorkspaceSet.workspaceOf w1 afterW1.Next.Workspaces |> Option.get

    // w2を、まだ表示されたことの無いWorkspace(hiddenWs)に直接投入する（前回セッションから引き続き
    // 存在する窓を模す。このテストの主眼はSwitchWorkspace後のRequestForeground配線であって
    // 窓の配置経路ではないため、最短で状態を作る——他のテストと同じ理由）。
    let hiddenWs = WorkspaceId 999
    let w2 = WindowId 2L
    let windows2 =
        { afterW1.Next.Windows with
            Records = Map.add w2 { WindowRecord.initial with Classification = Classified (EffTiled, ByUserOverride) } afterW1.Next.Windows.Records }
    let workspaces2 = WorkspaceSet.addWindow hiddenWs w2 false afterW1.Next.Workspaces
    let seeded2 = { afterW1.Next with Windows = windows2; Workspaces = workspaces2 }

    let afterSwitch = step RuntimePolicy.defaults seeded2 (mkEnvelope 200L (UserCommand (SwitchWorkspace hiddenWs)))
    match displays afterSwitch.Next.Topology with
    | Displays (current, []) -> Assert.Equal(hiddenWs, current.Workspace)
    | other -> failwithf "expected the previously-hidden workspace to become current, got %A" other
    Assert.Equal(Some ws0, WorkspaceSet.workspaceOf w1 afterSwitch.Next.Workspaces) // w1はws0に残る(非表示化)

    let effects = afterSwitch.Effects |> List.map GatedEffect.effect
    Assert.Contains(effects, function RequestForeground (_, target) -> target = w2 | _ -> false)

/// v2.86：`SwitchWorkspace`で窓が隠れても(`onWorkspaceHidden`、v2.31)、その窓宛ての既存の`Pending`
/// （`SetRect`、隠れる直前にReconcileが発行した分）はここでは一切取り消されない（`onTopologySettled`の
/// 一括Supersededはモニター構成が確定した時だけ発火し、`SwitchWorkspace`は踏まない）——このPendingの
/// `PendingDeadline`がその後満了すると、`retryOrGiveUp`（Placement.fs）が同じ窓へ新しい`SetRect`を
/// 無条件に再発行していた。`GateInput.HiddenByWm`（v2.86でようやく`Placement.Desired`から実際に
/// 組み立てられるようになった、それまで`Set.empty`の決め打ち）がこれを捕まえ、ゲートで拒否する
/// ことを確認する。
[<Fact>]
let ``SwitchWorkspaceで隠れた窓の、隠れる前からの既存Pendingが満了して再試行されても、HiddenByWmのゲートで拒否される(v2.86)`` () =
    let w1 = WindowId 1L
    let afterW1 = settleOneMonitorThenDiscoverWindow w1
    let ws0 = WorkspaceSet.workspaceOf w1 afterW1.Next.Workspaces |> Option.get
    let rid = afterW1.Next.Placement.Pending |> Map.toSeq |> Seq.map fst |> Seq.exactlyOne

    let hiddenWs = WorkspaceId 999
    let w2 = WindowId 2L
    let windows2 =
        { afterW1.Next.Windows with
            Records = Map.add w2 { WindowRecord.initial with Classification = Classified (EffTiled, ByUserOverride) } afterW1.Next.Windows.Records }
    let workspaces2 = WorkspaceSet.addWindow hiddenWs w2 false afterW1.Next.Workspaces
    let seeded2 = { afterW1.Next with Windows = windows2; Workspaces = workspaces2 }
    let afterSwitch = step RuntimePolicy.defaults seeded2 (mkEnvelope 200L (UserCommand (SwitchWorkspace hiddenWs)))

    // 前提：切替後もw1宛ての古いPendingがまだ生きている(onTopologySettledの一括Supersededを踏まない)。
    Assert.Equal(Some ws0, WorkspaceSet.workspaceOf w1 afterSwitch.Next.Workspaces)
    Assert.True(Map.containsKey rid afterSwitch.Next.Placement.Pending)
    Assert.Equal(Some HiddenByWm, Map.tryFind w1 afterSwitch.Next.Placement.Desired)

    // 古いPendingのPendingDeadlineが満了する。観測が一度も来ていないので`retryOrGiveUp`が新しいSetRectを
    // 再発行しようとするが、w1は`HiddenByWm`なのでゲートが拒否するはず。
    let afterDeadline = step RuntimePolicy.defaults afterSwitch.Next (mkEnvelope (100L + pendingDeadlineMs) (TimerElapsed (TimerId.Placement (PendingDeadline rid))))
    let effects = afterDeadline.Effects |> List.map GatedEffect.effect
    Assert.DoesNotContain(effects, function SetRect (_, target, _) -> target = w1 | _ -> false)

    // ゲートに拒否されても(onGateRejected、Placement.fs)、Desiredの`HiddenByWm`マーカーは取り消されない
    // （PendingPresence拒否・PresenceDeadline満了と同じ「Desiredは取り消さない」規則、型直前のコメント参照）。
    Assert.Equal(Some HiddenByWm, Map.tryFind w1 afterDeadline.Next.Placement.Desired)
    Assert.False(afterDeadline.Next.Placement.Pending |> Map.toSeq |> Seq.exists (fun (_, (pr: PendingRequest)) -> pr.Window = w1))

[<Fact>]
let ``SwitchWorkspaceはSwitchSettleを開始し、揺れ(旧Workspaceの窓への一時的なフォアグラウンド化)でWorkspaceを戻さない(R4-5, v2.40)`` () =
    let w1 = WindowId 1L
    let afterW1 = settleOneMonitorThenDiscoverWindow w1
    let ws0 = WorkspaceSet.workspaceOf w1 afterW1.Next.Workspaces |> Option.get

    let hiddenWs = WorkspaceId 999
    let w2 = WindowId 2L
    let windows2 =
        { afterW1.Next.Windows with
            Records = Map.add w2 { WindowRecord.initial with Classification = Classified (EffTiled, ByUserOverride) } afterW1.Next.Windows.Records }
    let workspaces2 = WorkspaceSet.addWindow hiddenWs w2 false afterW1.Next.Workspaces
    let seeded2 = { afterW1.Next with Windows = windows2; Workspaces = workspaces2 }

    let afterSwitch = step RuntimePolicy.defaults seeded2 (mkEnvelope 200L (UserCommand (SwitchWorkspace hiddenWs)))
    match afterSwitch.Next.Focus.SwitchSettle with
    | Some (Some target, deadline) ->
        Assert.Equal(w2, target)
        Assert.Equal(200L + RuntimePolicy.defaults.SwitchSettleMs, deadline)
    | other -> failwithf "expected SwitchSettle to start with focusTarget=Some w2, got %A" other

    // OSが隠しつつあるws0側のw1を一時的にフォアグラウンドにする「揺れ」を模す。SwitchSettleが
    // 待ち中なので、R5-1のhiddenなWorkspaceへのForegroundFollow分岐は止まり、currentはhiddenWsの
    // ままになる(戻らない)——戻ってしまうと切替そのものが巻き戻る。
    let afterWobble = step RuntimePolicy.defaults afterSwitch.Next (mkEnvelope 250L (mkForegroundObserved 250L w1))
    match displays afterWobble.Next.Topology with
    | Displays (current, _) -> Assert.Equal(hiddenWs, current.Workspace)
    | NoMonitors -> Assert.True(false, "expected a monitor")

[<Fact>]
let ``SwitchSettleタイマー発火時、まだfocusedが目的の窓でなければRequestForegroundを1回だけ再発行する(R4-5, v2.40)`` () =
    let w1 = WindowId 1L
    let afterW1 = settleOneMonitorThenDiscoverWindow w1
    let hiddenWs = WorkspaceId 999
    let w2 = WindowId 2L
    let windows2 =
        { afterW1.Next.Windows with
            Records = Map.add w2 { WindowRecord.initial with Classification = Classified (EffTiled, ByUserOverride) } afterW1.Next.Windows.Records }
    let workspaces2 = WorkspaceSet.addWindow hiddenWs w2 false afterW1.Next.Workspaces
    let seeded2 = { afterW1.Next with Windows = windows2; Workspaces = workspaces2 }
    let afterSwitch = step RuntimePolicy.defaults seeded2 (mkEnvelope 200L (UserCommand (SwitchWorkspace hiddenWs)))

    // 実際のOSからのForegroundObservedがまだ届いていない(focused = Elsewhere)まま、期限が来る。
    let deadline = 200L + RuntimePolicy.defaults.SwitchSettleMs
    let afterTimer = step RuntimePolicy.defaults afterSwitch.Next (mkEnvelope deadline (TimerElapsed (TimerId.Focus WorkspaceSwitchSettle)))
    Assert.Equal(None, afterTimer.Next.Focus.SwitchSettle)
    let effects = afterTimer.Effects |> List.map GatedEffect.effect
    Assert.Contains(effects, function RequestForeground (_, target) -> target = w2 | _ -> false)

[<Fact>]
let ``SwitchSettleタイマー発火時、既にfocusedが目的の窓なら再発行しない(R4-5, v2.40)`` () =
    let w1 = WindowId 1L
    let afterW1 = settleOneMonitorThenDiscoverWindow w1
    let hiddenWs = WorkspaceId 999
    let w2 = WindowId 2L
    let windows2 =
        { afterW1.Next.Windows with
            Records = Map.add w2 { WindowRecord.initial with Classification = Classified (EffTiled, ByUserOverride) } afterW1.Next.Windows.Records }
    let workspaces2 = WorkspaceSet.addWindow hiddenWs w2 false afterW1.Next.Workspaces
    let seeded2 = { afterW1.Next with Windows = windows2; Workspaces = workspaces2 }
    let afterSwitch = step RuntimePolicy.defaults seeded2 (mkEnvelope 200L (UserCommand (SwitchWorkspace hiddenWs)))

    // 実際のOSからのForegroundObservedが先に届き、正しくw2がfocusedになった後に期限が来る。
    let afterReallyFocused = step RuntimePolicy.defaults afterSwitch.Next (mkEnvelope 210L (mkForegroundObserved 210L w2))
    match focused afterReallyFocused.Next with
    | ManagedWindow target -> Assert.Equal(w2, target)
    | Elsewhere -> Assert.True(false, "expected w2 to already be focused")

    let deadline = 200L + RuntimePolicy.defaults.SwitchSettleMs
    let afterTimer = step RuntimePolicy.defaults afterReallyFocused.Next (mkEnvelope deadline (TimerElapsed (TimerId.Focus WorkspaceSwitchSettle)))
    Assert.Equal(None, afterTimer.Next.Focus.SwitchSettle)
    let effects = afterTimer.Effects |> List.map GatedEffect.effect
    Assert.DoesNotContain(effects, function RequestForeground _ -> true | _ -> false)

[<Fact>]
let ``FocusNext/FocusPreviousはアクティブなWorkspaceのzipperを回し、新しいFocusへRequestForegroundする(v2.33)`` () =
    // 3窓にする——2窓だと「次」と「前」が同じ結果になり、方向を取り違えるmutationを区別できない
    // （最初のmutation testingの試行で実際に踏んだ）。
    let w1, w2, w3 = WindowId 1L, WindowId 2L, WindowId 3L
    let afterW1 = settleOneMonitorThenDiscoverWindow w1
    let afterW2 = step RuntimePolicy.defaults afterW1.Next (mkEnvelope 101L (visibleWindowObserved w2))
    let afterW3 = step RuntimePolicy.defaults afterW2.Next (mkEnvelope 102L (visibleWindowObserved w3))
    let ws0 = WorkspaceSet.workspaceOf w1 afterW3.Next.Workspaces |> Option.get
    match (WorkspaceSet.stateOf ws0 afterW3.Next.Workspaces).Stack with
    | Some z -> Assert.Equal(w1, z.Focus) // 新規windowの追加はFocusを動かさない（Stack = [w1(Focus); w2; w3]）
    | None -> Assert.True(false, "expected a non-empty stack")

    let afterNext = step RuntimePolicy.defaults afterW3.Next (mkEnvelope 103L (UserCommand FocusNext))
    match (WorkspaceSet.stateOf ws0 afterNext.Next.Workspaces).Stack with
    | Some z -> Assert.Equal(w2, z.Focus) // w1 → w2（Afterの先頭）
    | None -> Assert.True(false, "expected a non-empty stack")
    let nextEffects = afterNext.Effects |> List.map GatedEffect.effect
    Assert.Contains(nextEffects, function RequestForeground (_, target) -> target = w2 | _ -> false)

    let afterPrevious = step RuntimePolicy.defaults afterNext.Next (mkEnvelope 104L (UserCommand FocusPrevious))
    match (WorkspaceSet.stateOf ws0 afterPrevious.Next.Workspaces).Stack with
    | Some z -> Assert.Equal(w1, z.Focus) // w2 → w1（Beforeの先頭。w3へではない——方向の確認）
    | None -> Assert.True(false, "expected a non-empty stack")
    let previousEffects = afterPrevious.Effects |> List.map GatedEffect.effect
    Assert.Contains(previousEffects, function RequestForeground (_, target) -> target = w1 | _ -> false)

[<Fact>]
let ``FocusNextは窓が1つしか無くFocusが変わらない場合、無意味なRequestForegroundを発行しない(v2.33)`` () =
    let w1 = WindowId 1L
    let afterW1 = settleOneMonitorThenDiscoverWindow w1
    let afterNext = step RuntimePolicy.defaults afterW1.Next (mkEnvelope 102L (UserCommand FocusNext))
    let effects = afterNext.Effects |> List.map GatedEffect.effect
    Assert.DoesNotContain(effects, function RequestForeground _ -> true | _ -> false)

[<Fact>]
let ``FocusNextはアクティブなモニターが無い(NoMonitors)場合、何もしない`` () =
    let state = initial RuntimePolicy.defaults
    let afterNext = step RuntimePolicy.defaults state (mkEnvelope 1L (UserCommand FocusNext))
    Assert.Equal(WorkspaceSet.empty, afterNext.Next.Workspaces)
    Assert.Empty(afterNext.Effects)

[<Fact>]
let ``CloseFocusedはfocusedの窓へRequestCloseを発行する(v2.34)`` () =
    let w = WindowId 1L
    let afterW = settleOneMonitorThenDiscoverWindow w
    let afterFocused = step RuntimePolicy.defaults afterW.Next (mkEnvelope 101L (mkForegroundObserved 101L w))
    match focused afterFocused.Next with
    | ManagedWindow target -> Assert.Equal(w, target)
    | Elsewhere -> Assert.True(false, "expected w to be focused")

    let afterClose = step RuntimePolicy.defaults afterFocused.Next (mkEnvelope 102L (UserCommand CloseFocused))
    let effects = afterClose.Effects |> List.map GatedEffect.effect
    Assert.Contains(effects, function RequestClose (_, target) -> target = w | _ -> false)

[<Fact>]
let ``CloseFocusedは、窓が存在してもフォーカスが無い(Elsewhere)なら何もしない`` () =
    // 窓が1つも無い状態だと、「focusedを見ずに何か適当な窓を閉じようとする」という壊れたガードでも
    // 偶然「対象が無いので何もしない」になってしまい区別できない（最初のmutation testingの試行で
    // 実際に踏んだ）。窓は存在するがForegroundObservedを一度も受けていない(focused=Elsewhere)、
    // という状態で検査する。
    let w = WindowId 1L
    let afterW = settleOneMonitorThenDiscoverWindow w
    match focused afterW.Next with
    | Elsewhere -> ()
    | ManagedWindow _ -> Assert.True(false, "expected Elsewhere before any ForegroundObserved")
    let afterClose = step RuntimePolicy.defaults afterW.Next (mkEnvelope 101L (UserCommand CloseFocused))
    let effects = afterClose.Effects |> List.map GatedEffect.effect
    Assert.DoesNotContain(effects, function RequestClose _ -> true | _ -> false)

let private masterStackPolicy : RuntimePolicy =
    { RuntimePolicy.defaults with
        Policy = { RuntimePolicy.defaults.Policy with DefaultLayout = { Id = LayoutId "master-stack"; ApiVersion = 1; Args = NoArgs } } }

/// stack側2窓の縦分割。masterStackPolicyの両テストで使う。
let private masterStackRects (workArea: PixelRect) : PixelRect * PixelRect * PixelRect =
    let masterRect = { workArea with Width = workArea.Width / 2 }
    let stackArea = { workArea with X = masterRect.Width; Width = workArea.Width - masterRect.Width }
    let stackTop = { stackArea with Height = stackArea.Height / 2 }
    let stackBottom = { stackArea with Y = stackTop.Height; Height = stackArea.Height - stackTop.Height }
    masterRect, stackTop, stackBottom

/// `Desired`から現在の目標矩形を読む。`dispatchPlacements`は目標が変わらない窓にはSetRectを
/// 再発行しないため（§7.3重複要求の禁止）、「このstepのEffects」ではなく「現在のDesired」で
/// 各窓の実際の位置を確認する——変化が無い窓を`Effects`だけで確認しようとすると、何も起きていない
/// ように見えてしまう（最初のテスト草稿がこれで落ちた。SwapWithMasterはw2を動かさないので、
/// w2についてこの取り違えを実際に踏んだ）。
let private desiredRectOf (state: PlacementState) (w: WindowId) : PixelRect option =
    match Map.tryFind w state.Desired with
    | Some (Shown (Some r)) -> Some r
    | _ -> None

/// 3窓にする——2窓だとPromoteToMaster（シフト、他の要素を押し出す）とSwapWithMaster（2要素だけの
/// 交換）が同じ結果になり、取り違えるmutationを区別できない（FocusNext/FocusPreviousの方向違いと
/// 同じ理由で、最初のmutation testingの試行で実際に踏んだ）。
[<Fact>]
let ``SwapWithMasterはfocusedとmasterの2要素だけを入れ替え、他の窓の相対順序は変えない(v2.35)`` () =
    let w1, w2, w3 = WindowId 1L, WindowId 2L, WindowId 3L
    let topology, _ = MonitorLifecycle.reconcile (Wm.Core.Topology.initial Map.empty) [ mkMonitor 0 ]
    let seeded = { initial masterStackPolicy with Topology = topology }
    let afterW1 = step masterStackPolicy seeded (mkEnvelope 1L (visibleWindowObserved w1))
    let afterW2 = step masterStackPolicy afterW1.Next (mkEnvelope 2L (visibleWindowObserved w2))
    let afterW3 = step masterStackPolicy afterW2.Next (mkEnvelope 3L (visibleWindowObserved w3))
    // 並び：master=w1、stack=[w2(top); w3(bottom)]。
    let masterRect, stackTop, stackBottom = masterStackRects (mkMonitor 0).WorkArea
    Assert.Equal(Some masterRect, desiredRectOf afterW3.Next.Placement w1)
    Assert.Equal(Some stackTop, desiredRectOf afterW3.Next.Placement w2)
    Assert.Equal(Some stackBottom, desiredRectOf afterW3.Next.Placement w3)

    let afterFocusW3 = step masterStackPolicy afterW3.Next (mkEnvelope 4L (mkForegroundObserved 4L w3))
    let afterSwap = step masterStackPolicy afterFocusW3.Next (mkEnvelope 5L (UserCommand SwapWithMaster))
    // w3(focused)とw1(master)だけが入れ替わる。w2はstackのtopのまま(相対順序を変えない)。
    Assert.Equal(Some masterRect, desiredRectOf afterSwap.Next.Placement w3)
    Assert.Equal(Some stackTop, desiredRectOf afterSwap.Next.Placement w2)
    Assert.Equal(Some stackBottom, desiredRectOf afterSwap.Next.Placement w1)
    // フォーカス中の窓自体は変わらないので、RequestForegroundは発行しない。
    let swapEffects = afterSwap.Effects |> List.map GatedEffect.effect
    Assert.DoesNotContain(swapEffects, function RequestForeground _ -> true | _ -> false)

[<Fact>]
let ``PromoteToMasterはfocusedを先頭へ移し、他の窓は1つずつ後ろへずれる(v2.35)`` () =
    let w1, w2, w3 = WindowId 1L, WindowId 2L, WindowId 3L
    let topology, _ = MonitorLifecycle.reconcile (Wm.Core.Topology.initial Map.empty) [ mkMonitor 0 ]
    let seeded = { initial masterStackPolicy with Topology = topology }
    let afterW1 = step masterStackPolicy seeded (mkEnvelope 1L (visibleWindowObserved w1))
    let afterW2 = step masterStackPolicy afterW1.Next (mkEnvelope 2L (visibleWindowObserved w2))
    let afterW3 = step masterStackPolicy afterW2.Next (mkEnvelope 3L (visibleWindowObserved w3))
    // 並び：master=w1、stack=[w2(top); w3(bottom)]。

    let afterFocusW3 = step masterStackPolicy afterW3.Next (mkEnvelope 4L (mkForegroundObserved 4L w3))
    let afterPromote = step masterStackPolicy afterFocusW3.Next (mkEnvelope 5L (UserCommand PromoteToMaster))
    let masterRect, stackTop, stackBottom = masterStackRects (mkMonitor 0).WorkArea
    // w3(focused)がmasterへ。w1・w2は元の相対順序(w1,w2)を保ったままstackへ1つずれる
    // （SwapWithMasterの結果（w1だけがstackへ移りw2はtopのまま）とはここで区別できる）。
    Assert.Equal(Some masterRect, desiredRectOf afterPromote.Next.Placement w3)
    Assert.Equal(Some stackTop, desiredRectOf afterPromote.Next.Placement w1)
    Assert.Equal(Some stackBottom, desiredRectOf afterPromote.Next.Placement w2)
    let promoteEffects = afterPromote.Effects |> List.map GatedEffect.effect
    Assert.DoesNotContain(promoteEffects, function RequestForeground _ -> true | _ -> false)

    // 続けてPromoteToMaster(今度はfocusedはまだw3=master)を呼んでも、既にmasterなので何も変わらない。
    let afterPromoteAgain = step masterStackPolicy afterPromote.Next (mkEnvelope 6L (UserCommand PromoteToMaster))
    Assert.Equal(afterPromote.Next.Workspaces, afterPromoteAgain.Next.Workspaces)

[<Fact>]
let ``ChangeLayoutはアクティブなWorkspaceのLayoutOverrideへ記録し、Reconcileが新しいLayoutで計算し直す(v2.37)`` () =
    // RuntimePolicy.defaults（DefaultLayout=Monocle）から始め、ChangeLayoutでMasterStackへ切り替える。
    // focusedを一切要求しない（ChangeLayoutはWorkspace全体の設定であり、特定の窓とは無関係）。
    let w1, w2 = WindowId 1L, WindowId 2L
    let afterW1 = settleOneMonitorThenDiscoverWindow w1
    let afterW2 = step RuntimePolicy.defaults afterW1.Next (mkEnvelope 101L (visibleWindowObserved w2))
    let ws0 = WorkspaceSet.workspaceOf w1 afterW2.Next.Workspaces |> Option.get
    let workArea = (mkMonitor 0).WorkArea
    // Monocleでは両方ともWorkArea全体。
    Assert.Equal(Some workArea, desiredRectOf afterW2.Next.Placement w1)
    Assert.Equal(Some workArea, desiredRectOf afterW2.Next.Placement w2)
    Assert.Equal(None, (WorkspaceSet.stateOf ws0 afterW2.Next.Workspaces).LayoutOverride)

    let masterStackRef : LayoutRef = { Id = LayoutId "master-stack"; ApiVersion = 1; Args = NoArgs }
    let afterChange = step RuntimePolicy.defaults afterW2.Next (mkEnvelope 102L (UserCommand (ChangeLayout masterStackRef)))
    Assert.Equal(Some masterStackRef, (WorkspaceSet.stateOf ws0 afterChange.Next.Workspaces).LayoutOverride)
    let masterRect, _, _ = masterStackRects workArea
    // 窓は2つしか無く片方がmaster・片方がstackなので、stackは1窓分=stackArea全体になる
    // （masterStackRectsは2窓stackの前提で作った。stack1窓は`stackArea`全体になる——下で検算する）。
    let stackArea = { workArea with X = masterRect.Width; Width = workArea.Width - masterRect.Width }
    Assert.Equal(Some masterRect, desiredRectOf afterChange.Next.Placement w1)
    Assert.Equal(Some stackArea, desiredRectOf afterChange.Next.Placement w2)

[<Fact>]
let ``ChangeLayoutでColumns(v2.67)へ切り替えると、RuntimePolicy.defaultsのLayoutRegistryから解決されて2列に分割する`` () =
    let w1, w2 = WindowId 1L, WindowId 2L
    let afterW1 = settleOneMonitorThenDiscoverWindow w1
    let afterW2 = step RuntimePolicy.defaults afterW1.Next (mkEnvelope 101L (visibleWindowObserved w2))
    let ws0 = WorkspaceSet.workspaceOf w1 afterW2.Next.Workspaces |> Option.get
    let workArea = (mkMonitor 0).WorkArea

    let columnsRef : LayoutRef = { Id = LayoutId "columns"; ApiVersion = 1; Args = ColumnCount 2 }
    let afterChange = step RuntimePolicy.defaults afterW2.Next (mkEnvelope 102L (UserCommand (ChangeLayout columnsRef)))
    Assert.Equal(Some columnsRef, (WorkspaceSet.stateOf ws0 afterChange.Next.Workspaces).LayoutOverride)
    // 2窓・2列なので、各窓がちょうど半分の幅を縦いっぱいに占める列になる。
    let col0 = { workArea with Width = workArea.Width / 2 }
    let col1 = { workArea with X = col0.Width; Width = workArea.Width - col0.Width }
    Assert.Equal(Some col0, desiredRectOf afterChange.Next.Placement w1)
    Assert.Equal(Some col1, desiredRectOf afterChange.Next.Placement w2)

[<Fact>]
let ``ChangeLayoutはアクティブなモニターが無い(NoMonitors)場合、何もしない`` () =
    let state = initial RuntimePolicy.defaults
    let someRef : LayoutRef = { Id = LayoutId "master-stack"; ApiVersion = 1; Args = NoArgs }
    let afterChange = step RuntimePolicy.defaults state (mkEnvelope 1L (UserCommand (ChangeLayout someRef)))
    Assert.Equal(WorkspaceSet.empty, afterChange.Next.Workspaces)

[<Fact>]
let ``WindowRemovedは同じstepでPlacement.Geometryも除く`` () =
    let state = initial RuntimePolicy.defaults
    let obs: Observation<bool> = { Value = Ok true; SampleSequence = 1L; MonotonicTime = 1L; Source = "test" }
    let afterMinimized = (step RuntimePolicy.defaults state (mkEnvelope 1L (MinimizedStateChanged (WindowId 1L, obs)))).Next
    Assert.True(Map.containsKey (WindowId 1L) afterMinimized.Placement.Geometry)
    let afterRemoved = (step RuntimePolicy.defaults afterMinimized (mkEnvelope 2L (WindowRemoved (WindowId 1L)))).Next
    Assert.False(Map.containsKey (WindowId 1L) afterRemoved.Placement.Geometry)

[<Fact>]
let ``FocusMostRecentは、focused=ManagedWindowなら同じWorkspace内のHistoryの2番目へ直接RequestForegroundする(v2.38)`` () =
    // 3窓にする——2窓だと「2番目」が常に「唯一の他の窓」になり、本当にHistoryの順序（2番目≠先頭）を
    // 見ているのか、単に「focused以外の窓」を見ているだけなのかを区別できない
    // （FocusNext/FocusPreviousの方向違いと同じ理由で、最初のmutation testingの試行で実際に踏んだ）。
    let w1, w2, w3 = WindowId 1L, WindowId 2L, WindowId 3L
    let afterW1 = settleOneMonitorThenDiscoverWindow w1
    let afterW2 = step RuntimePolicy.defaults afterW1.Next (mkEnvelope 101L (visibleWindowObserved w2))
    let afterW3 = step RuntimePolicy.defaults afterW2.Next (mkEnvelope 102L (visibleWindowObserved w3))
    let ws0 = WorkspaceSet.workspaceOf w1 afterW3.Next.Workspaces |> Option.get

    // Historyを[w1;w2;w3]の順に積む：w1→w2→w3の順でフォーカスし、最後にw1へ戻る
    // （focused=ManagedWindow w1、History=[w1;w3;w2]）。2番目=w3。
    let afterFocusW1 = step RuntimePolicy.defaults afterW3.Next (mkEnvelope 103L (mkForegroundObserved 103L w1))
    let afterFocusW2 = step RuntimePolicy.defaults afterFocusW1.Next (mkEnvelope 104L (mkForegroundObserved 104L w2))
    let afterFocusW3 = step RuntimePolicy.defaults afterFocusW2.Next (mkEnvelope 105L (mkForegroundObserved 105L w3))
    let afterFocusW1Again = step RuntimePolicy.defaults afterFocusW3.Next (mkEnvelope 106L (mkForegroundObserved 106L w1))
    Assert.Equal<WindowId list>([ w1; w3; w2 ], afterFocusW1Again.Next.Focus.History)

    let afterFmr = step RuntimePolicy.defaults afterFocusW1Again.Next (mkEnvelope 107L (UserCommand FocusMostRecent))
    // w3はws0に既にcurrentなので、Topologyのviewは変わらず直接RequestForegroundする。
    match displays afterFmr.Next.Topology with
    | Displays (current, []) -> Assert.Equal(ws0, current.Workspace)
    | other -> failwithf "expected the single monitor to stay on ws0, got %A" other
    match (WorkspaceSet.stateOf ws0 afterFmr.Next.Workspaces).Stack with
    | Some z -> Assert.Equal(w3, z.Focus)
    | None -> Assert.True(false, "expected ws0's zipper to keep a Stack")
    let effects = afterFmr.Effects |> List.map GatedEffect.effect
    Assert.Contains(effects, function RequestForeground (_, target) -> target = w3 | _ -> false)

[<Fact>]
let ``FocusMostRecentは、focused=Elsewhereなら(2番目ではなく)Historyの先頭へ移る(v2.38)`` () =
    let w1, w2 = WindowId 1L, WindowId 2L
    let afterW1 = settleOneMonitorThenDiscoverWindow w1
    let afterW2 = step RuntimePolicy.defaults afterW1.Next (mkEnvelope 101L (visibleWindowObserved w2))
    let afterFocusW1 = step RuntimePolicy.defaults afterW2.Next (mkEnvelope 102L (mkForegroundObserved 102L w1))
    // フォアグラウンドがどの窓も指さなくなる(Elsewhereへ)。Historyは[w1]のまま変わらない
    // （updateHistoryはManagedWindowへ変わったときだけ書くので、Elsewhereへの変化では削らない）。
    let elsewhereObs: Observation<WindowId option> = { Value = Ok None; SampleSequence = 103L; MonotonicTime = 103L; Source = "test" }
    let afterElsewhere = step RuntimePolicy.defaults afterFocusW1.Next (mkEnvelope 103L (ForegroundObserved elsewhereObs))
    Assert.Equal<WindowId list>([ w1 ], afterElsewhere.Next.Focus.History)

    let afterFmr = step RuntimePolicy.defaults afterElsewhere.Next (mkEnvelope 104L (UserCommand FocusMostRecent))
    let effects = afterFmr.Effects |> List.map GatedEffect.effect
    Assert.Contains(effects, function RequestForeground (_, target) -> target = w1 | _ -> false)

[<Fact>]
let ``FocusMostRecentは、行き先の窓のWorkspaceがhiddenならSwitchWorkspaceと同じview遷移で表示してからRequestForegroundし、同じstepでReconcileも新しいcurrentを計算し直す(v2.38)`` () =
    let w1, w2 = WindowId 1L, WindowId 2L
    let afterW1 = settleOneMonitorThenDiscoverWindow w1
    let afterFocusW1 = step RuntimePolicy.defaults afterW1.Next (mkEnvelope 101L (mkForegroundObserved 101L w1))

    // w2を、まだ表示されたことの無いWorkspace(hiddenWs)へ直接投入する（既存のSwitchWorkspaceの
    // hiddenテストと同じ理由で最短構成を使う：このテストの主眼はFocusMostRecentのview遷移配線と
    // Reconcileの引き金であって、w2がどう分類されたかではない）。Focus.Historyも直接
    // [w1(現focused); w2]へ設定する——実際のシーケンス（w2を表示していた時にフォーカス→別へ切替で
    // hiddenに→w1をフォーカス）を全部再現せずに、その結果の状態だけを作る。
    let hiddenWs = WorkspaceId 999
    let windows2 =
        { afterFocusW1.Next.Windows with
            Records = Map.add w2 { WindowRecord.initial with Classification = Classified (EffTiled, ByUserOverride) } afterFocusW1.Next.Windows.Records }
    let workspaces2 = WorkspaceSet.addWindow hiddenWs w2 false afterFocusW1.Next.Workspaces
    let seeded2 =
        { afterFocusW1.Next with
            Windows = windows2
            Workspaces = workspaces2
            Focus = { afterFocusW1.Next.Focus with History = [ w1; w2 ] } }
    // 直接構成した直後はhiddenWsを一度もReconcileしていないので、w2のDesiredはまだ無い。
    Assert.Equal(None, desiredRectOf seeded2.Placement w2)

    let afterFmr = step RuntimePolicy.defaults seeded2 (mkEnvelope 200L (UserCommand FocusMostRecent))
    match displays afterFmr.Next.Topology with
    | Displays (current, []) -> Assert.Equal(hiddenWs, current.Workspace)
    | other -> failwithf "expected hiddenWs to become current, got %A" other
    match (WorkspaceSet.stateOf hiddenWs afterFmr.Next.Workspaces).Stack with
    | Some z -> Assert.Equal(w2, z.Focus)
    | None -> Assert.True(false, "expected hiddenWs's zipper to keep a Stack")
    let effects = afterFmr.Effects |> List.map GatedEffect.effect
    Assert.Contains(effects, function RequestForeground (_, target) -> target = w2 | _ -> false)
    // shouldReconcileがWorkspaceShown経由でも引き金になる(v2.38のバグ修正)ので、新しくcurrentへ
    // 入ってきたw2は同じstepで実際の矩形を得る(Shown Noneのままではない)。
    Assert.NotEqual(None, desiredRectOf afterFmr.Next.Placement w2)

[<Fact>]
let ``FocusMostRecentは、Historyに行き先が無ければ何もしない(v2.38)`` () =
    let w1 = WindowId 1L
    let afterW1 = settleOneMonitorThenDiscoverWindow w1
    let afterFocusW1 = step RuntimePolicy.defaults afterW1.Next (mkEnvelope 101L (mkForegroundObserved 101L w1))
    // focused=ManagedWindow w1、History=[w1]だけ——2番目が存在しない。
    Assert.Equal<WindowId list>([ w1 ], afterFocusW1.Next.Focus.History)
    let afterFmr = step RuntimePolicy.defaults afterFocusW1.Next (mkEnvelope 102L (UserCommand FocusMostRecent))
    Assert.Equal(afterFocusW1.Next.Workspaces, afterFmr.Next.Workspaces)
    Assert.Equal(afterFocusW1.Next.Topology, afterFmr.Next.Topology)
    let effects = afterFmr.Effects |> List.map GatedEffect.effect
    Assert.DoesNotContain(effects, function RequestForeground _ -> true | _ -> false)

[<Fact>]
let ``ForegroundChangedは、管理下の窓が別のvisible(非current)モニターへ移ったら、RequestForegroundせずにcurrentだけ追従する(R5-1、v2.39)`` () =
    let topology, _ = MonitorLifecycle.reconcile (Wm.Core.Topology.initial Map.empty) [ mkMonitor 0; mkMonitor 1 ]
    let current, visible =
        match displays topology with
        | Displays (c, v) -> c, v
        | NoMonitors -> failwith "expected 2 monitors to settle into Displays"
    match visible with
    | [ visibleScreen ] ->
        let w2 = WindowId 2L
        let seeded = { initial RuntimePolicy.defaults with Topology = topology }
        let windows2 =
            { seeded.Windows with
                Records = Map.add w2 { WindowRecord.initial with Classification = Classified (EffTiled, ByUserOverride) } seeded.Windows.Records }
        let workspaces2 = WorkspaceSet.addWindow visibleScreen.Workspace w2 false seeded.Workspaces
        let seeded2 = { seeded with Windows = windows2; Workspaces = workspaces2 }

        // w2は既にvisibleScreen（非current）のWorkspaceに属する管理下の窓。これが前面になった
        // （ユーザーが別モニターの窓を直接クリックした等）ことをForegroundObservedで届ける。
        let afterForeground = step RuntimePolicy.defaults seeded2 (mkEnvelope 1L (mkForegroundObserved 1L w2))
        match displays afterForeground.Next.Topology with
        | Displays (newCurrent, [ newVisible ]) ->
            Assert.Equal(visibleScreen.Workspace, newCurrent.Workspace) // currentがw2のWorkspaceへ追従
            Assert.Equal(current.Workspace, newVisible.Workspace)
        | other -> failwithf "expected current to follow to w2's monitor, got %A" other

        // OS側で既にw2にフォーカスがあるので、RequestForegroundは発行しない
        // （SwitchWorkspace/FocusMonitorの明示操作とは違い、二重要求になる）。
        let effects = afterForeground.Effects |> List.map GatedEffect.effect
        Assert.DoesNotContain(effects, function RequestForeground _ -> true | _ -> false)
    | other -> Assert.True(false, sprintf "expected exactly 1 visible (non-active) screen, got %A" other)

[<Fact>]
let ``ForegroundChangedは、hiddenなWorkspaceの窓が前面になったらForegroundFollowで表示し、ReturnMonitorを保持したままRequestForegroundしない(R5-1、v2.39)`` () =
    // `TopologyState`はprivateなので`Returns`を直接構成できない——モニター切断の実際の経路
    // （`MonitorLifecycle.reconcile`、visibleScreenが切断されてhiddenになりReturnsへ記録される）で
    // 本物のReturn予約を作る。
    let topology0, _ = MonitorLifecycle.reconcile (Wm.Core.Topology.initial Map.empty) [ mkMonitor 0; mkMonitor 1 ]
    let current0, visible0 =
        match displays topology0 with
        | Displays (c, v) -> c, v
        | NoMonitors -> failwith "expected 2 monitors to settle into Displays"
    match visible0 with
    | [ visibleScreen ] ->
        let w2 = WindowId 2L
        let seeded = { initial RuntimePolicy.defaults with Topology = topology0 }
        let windows2 =
            { seeded.Windows with
                Records = Map.add w2 { WindowRecord.initial with Classification = Classified (EffTiled, ByUserOverride) } seeded.Windows.Records }
        let workspaces2 = WorkspaceSet.addWindow visibleScreen.Workspace w2 false seeded.Workspaces
        let seeded2 = { seeded with Windows = windows2; Workspaces = workspaces2 }

        // monitor1を切断する。visibleScreen.WorkspaceがhiddenになりReturnsへ記録される（実際の経路）。
        let settleMs = TopologyConfig.defaults.SettleMs
        let afterDisconnect = step RuntimePolicy.defaults seeded2 (mkEnvelope 100L (MonitorTopologyObserved [ mkMonitor 0 ]))
        let afterSettle = step RuntimePolicy.defaults afterDisconnect.Next (mkEnvelope (100L + settleMs) (TimerElapsed (TimerId.Topology ScreenSettle)))
        match displays afterSettle.Next.Topology with
        | Displays (c, []) -> Assert.Equal(current0.Workspace, c.Workspace)
        | other -> failwithf "expected monitor1 to disconnect, got %A" other
        Assert.Equal(Some visibleScreen.Monitor.Id, returns afterSettle.Next.Topology |> Map.tryFind visibleScreen.Workspace)

        // w2のWorkspace（今はhidden）の窓が前面になった（タスクバーからの選択等）。
        let afterForeground = step RuntimePolicy.defaults afterSettle.Next (mkEnvelope 200L (mkForegroundObserved 200L w2))
        match displays afterForeground.Next.Topology with
        | Displays (newCurrent, []) -> Assert.Equal(visibleScreen.Workspace, newCurrent.Workspace)
        | other -> failwithf "expected visibleScreen.Workspace to become current via ForegroundFollow, got %A" other
        // ユーザーの明示操作ではないので、ReturnMonitorはSwitchWorkspace/FocusMostRecentとは違い解除しない。
        Assert.Equal(Some visibleScreen.Monitor.Id, returns afterForeground.Next.Topology |> Map.tryFind visibleScreen.Workspace)
        // OS側で既にw2にフォーカスがあるのでRequestForegroundは発行しない。
        let effects = afterForeground.Effects |> List.map GatedEffect.effect
        Assert.DoesNotContain(effects, function RequestForeground _ -> true | _ -> false)
        // shouldReconcileはWorkspaceShown(ShownByの値を問わない)で引き金になる(v2.38)ので、新しくcurrentへ
        // 入ってきたw2は同じstepで実際の矩形を得る。
        Assert.NotEqual(None, desiredRectOf afterForeground.Next.Placement w2)
    | other -> Assert.True(false, sprintf "expected exactly 1 visible (non-active) screen, got %A" other)

[<Fact>]
let ``ForegroundChangedは、step開始時点でどのWorkspaceにも属さない窓では追従しない(v2.39)`` () =
    // architecture.md §7.1補足：観測の到着順は保証されないため、ForegroundObservedが窓の発見より
    // 先に届く経路がある。その窓はstep開始時点ではどのWorkspaceにも属していないので追従しない
    // （新しい窓は常にアクティブなWSに入るので、追従自体が無意味、§7.1補足）。
    let topology, _ = MonitorLifecycle.reconcile (Wm.Core.Topology.initial Map.empty) [ mkMonitor 0 ]
    let seeded = { initial RuntimePolicy.defaults with Topology = topology }
    let w = WindowId 1L
    let afterForegroundFirst = step RuntimePolicy.defaults seeded (mkEnvelope 1L (mkForegroundObserved 1L w))
    Assert.Equal(seeded.Topology, afterForegroundFirst.Next.Topology)

// ===== MoveSizeStarted/MoveSizeEnded（§4.4 Suspended、reconcileOneScreenのAlive/TiledWindows除外、v2.65） =====

[<Fact>]
let ``Dragging中の窓は、他の窓の出現でレイアウトが変わる局面でも除外されSetRectを受け取らない(v2.65)`` () =
    let w1, w2 = WindowId 1L, WindowId 2L
    let topology, _ = MonitorLifecycle.reconcile (Wm.Core.Topology.initial Map.empty) [ mkMonitor 0 ]
    let seeded = { initial masterStackPolicy with Topology = topology }
    let afterW1 = step masterStackPolicy seeded (mkEnvelope 1L (visibleWindowObserved w1))
    let workArea = (mkMonitor 0).WorkArea
    Assert.Equal(Some workArea, desiredRectOf afterW1.Next.Placement w1) // 唯一の窓なのでmaster=全画面

    let afterDragStart = step masterStackPolicy afterW1.Next (mkEnvelope 2L (MoveSizeStarted w1))
    Assert.True(Set.contains w1 afterDragStart.Next.Placement.Dragging)

    // ドラッグ中にw2が加わる。通常ならMasterStackが2窓構成に切り替わりw1もstackへ動くはずだが、
    // w1はDragging中なのでこのstepのTiledWindows/Aliveから除かれる——w2だけが「唯一の対象窓」として
    // 全画面を与えられ、w1のDesired/Pendingは一切変化しない。
    let afterW2 = step masterStackPolicy afterDragStart.Next (mkEnvelope 3L (visibleWindowObserved w2))
    Assert.Equal(Some workArea, desiredRectOf afterW2.Next.Placement w1) // 変化無し(新しいSetRectが無い証拠)
    Assert.Equal(Some workArea, desiredRectOf afterW2.Next.Placement w2) // w1を除いた「唯一の窓」として全画面
    let w2Effects = afterW2.Effects |> List.map GatedEffect.effect
    Assert.DoesNotContain(w2Effects, function SetRect (_, w, _) -> w = w1 | _ -> false)

[<Fact>]
let ``MoveSizeEndedはDraggingから外し、以後は通常どおりReconcileの対象に戻る(v2.65)`` () =
    let w1, w2 = WindowId 1L, WindowId 2L
    let topology, _ = MonitorLifecycle.reconcile (Wm.Core.Topology.initial Map.empty) [ mkMonitor 0 ]
    let seeded = { initial masterStackPolicy with Topology = topology }
    let afterW1 = step masterStackPolicy seeded (mkEnvelope 1L (visibleWindowObserved w1))
    let afterDragStart = step masterStackPolicy afterW1.Next (mkEnvelope 2L (MoveSizeStarted w1))
    let rectObs: Observation<PixelRect> = { Value = Ok (mkMonitor 0).WorkArea; SampleSequence = 1L; MonotonicTime = 3L; Source = "test" }
    let monitorObs: Observation<MonitorId> = { Value = Ok (MonitorId 0L); SampleSequence = 1L; MonotonicTime = 3L; Source = "test" }
    let afterDragEnd = step masterStackPolicy afterDragStart.Next (mkEnvelope 3L (MoveSizeEnded (w1, rectObs, monitorObs)))
    Assert.False(Set.contains w1 afterDragEnd.Next.Placement.Dragging)

    // ドラッグが終わっているので、w2が加わると通常どおりmaster(w1)/stack(w2、1窓なのでstackArea全体)へ
    // 分割される（`ChangeLayoutはアクティブなWorkspaceの...`テストと同じ計算）。
    let workArea = (mkMonitor 0).WorkArea
    let masterRect = { workArea with Width = workArea.Width / 2 }
    let stackArea = { workArea with X = masterRect.Width; Width = workArea.Width - masterRect.Width }
    let afterW2 = step masterStackPolicy afterDragEnd.Next (mkEnvelope 4L (visibleWindowObserved w2))
    Assert.Equal(Some masterRect, desiredRectOf afterW2.Next.Placement w1)
    Assert.Equal(Some stackArea, desiredRectOf afterW2.Next.Placement w2)

[<Fact>]
let ``WindowRemovedは同じstepでPlacement.Draggingも除く(v2.65)`` () =
    let w = WindowId 1L
    let topology, _ = MonitorLifecycle.reconcile (Wm.Core.Topology.initial Map.empty) [ mkMonitor 0 ]
    let seeded = { initial RuntimePolicy.defaults with Topology = topology }
    let afterW = step RuntimePolicy.defaults seeded (mkEnvelope 1L (visibleWindowObserved w))
    let afterDragStart = step RuntimePolicy.defaults afterW.Next (mkEnvelope 2L (MoveSizeStarted w))
    Assert.True(Set.contains w afterDragStart.Next.Placement.Dragging)
    let afterRemoved = step RuntimePolicy.defaults afterDragStart.Next (mkEnvelope 3L (WindowRemoved w))
    Assert.False(Set.contains w afterRemoved.Next.Placement.Dragging)

// ===== MoveAttributed(w, UserDrag)（§4.4「観測されたモニターと意図されたモニターが食い違うとき」の
// UserDrag分岐、R3-4、v2.66） =====

// `mkMonitor`はどのIDでも同じWorkArea({X=0;Y=0;Width=1920;Height=1080})を返すため、2台とも素の
// `mkMonitor`を使うと両モニターのDesired矩形が数値として一致してしまい、`dispatchPlacements`の
// `desiredChanged`判定（§7.3重複要求の禁止）が「変化無し」としてSetRectの再発行を抑止する
// ——Reconcileが実際に新しいモニターで計算し直したことを`Effects`で確認できなくなる
// （このファイル冒頭の`desiredRectOf`の注釈と同じ「取り違え」の一種）。2台目のWorkAreaを明示的に
// ずらし、矩形の値そのもので区別できるようにする。
let private twoMonitorTopologyAndScreens () : Wm.Core.Topology.TopologyState * Screen * Screen =
    let monitor1 = { mkMonitor 1 with Bounds = { X = 1920; Y = 0; Width = 1280; Height = 720 }; WorkArea = { X = 1920; Y = 0; Width = 1280; Height = 720 } }
    let topology, _ = MonitorLifecycle.reconcile (Wm.Core.Topology.initial Map.empty) [ mkMonitor 0; monitor1 ]
    match displays topology with
    | Displays (current, [ visibleScreen ]) -> topology, current, visibleScreen
    | other -> failwithf "expected exactly 1 visible (non-active) screen, got %A" other

[<Fact>]
let ``MoveSizeEndedで別モニターへ落とされた窓は、そのモニターに表示中のWorkspaceへ移り、Reconcileが新しい矩形を発行する(v2.66)`` () =
    let topology, current, visibleScreen = twoMonitorTopologyAndScreens ()
    let seeded = { initial RuntimePolicy.defaults with Topology = topology }
    let w = WindowId 1L
    let afterW = step RuntimePolicy.defaults seeded (mkEnvelope 1L (visibleWindowObserved w))
    Assert.Equal(Some current.Workspace, WorkspaceSet.workspaceOf w afterW.Next.Workspaces) // 新規発見はcurrentへ

    let rectObs: Observation<PixelRect> = { Value = Ok { X = 2000; Y = 0; Width = 300; Height = 300 }; SampleSequence = 1L; MonotonicTime = 2L; Source = "test" }
    let monitorObs: Observation<MonitorId> = { Value = Ok visibleScreen.Monitor.Id; SampleSequence = 1L; MonotonicTime = 2L; Source = "test" }
    let afterDragEnd = step RuntimePolicy.defaults afterW.Next (mkEnvelope 2L (MoveSizeEnded (w, rectObs, monitorObs)))

    Assert.Equal(Some visibleScreen.Workspace, WorkspaceSet.workspaceOf w afterDragEnd.Next.Workspaces)
    let effects = afterDragEnd.Effects |> List.map GatedEffect.effect
    Assert.Contains(effects, function SetRect (_, target, rect) -> target = w && rect = visibleScreen.Monitor.WorkArea | _ -> false)

[<Fact>]
let ``MoveSizeEndedで同じモニター内に落とされた窓はWorkspaceを変えない(v2.66)`` () =
    let topology, current, _ = twoMonitorTopologyAndScreens ()
    let seeded = { initial RuntimePolicy.defaults with Topology = topology }
    let w = WindowId 1L
    let afterW = step RuntimePolicy.defaults seeded (mkEnvelope 1L (visibleWindowObserved w))
    let rectObs: Observation<PixelRect> = { Value = Ok { X = 500; Y = 0; Width = 300; Height = 300 }; SampleSequence = 1L; MonotonicTime = 2L; Source = "test" }
    let monitorObs: Observation<MonitorId> = { Value = Ok current.Monitor.Id; SampleSequence = 1L; MonotonicTime = 2L; Source = "test" }
    let afterDragEnd = step RuntimePolicy.defaults afterW.Next (mkEnvelope 2L (MoveSizeEnded (w, rectObs, monitorObs)))
    Assert.Equal(afterW.Next.Workspaces, afterDragEnd.Next.Workspaces)

[<Fact>]
let ``MoveSizeEndedのモニター観測がErrorなら何もしない(v2.66)`` () =
    let seeded = { initial RuntimePolicy.defaults with Topology = (MonitorLifecycle.reconcile (Wm.Core.Topology.initial Map.empty) [ mkMonitor 0; mkMonitor 1 ] |> fst) }
    let w = WindowId 1L
    let afterW = step RuntimePolicy.defaults seeded (mkEnvelope 1L (visibleWindowObserved w))
    let rectObs: Observation<PixelRect> = { Value = Ok { X = 2000; Y = 0; Width = 300; Height = 300 }; SampleSequence = 1L; MonotonicTime = 2L; Source = "test" }
    let failedMonitorObs: Observation<MonitorId> =
        { Value = Error { Api = "test"; Space = Win32LastError; Code = 0; ElapsedUs = 0L }; SampleSequence = 1L; MonotonicTime = 2L; Source = "test" }
    let afterDragEnd = step RuntimePolicy.defaults afterW.Next (mkEnvelope 2L (MoveSizeEnded (w, rectObs, failedMonitorObs)))
    Assert.Equal(afterW.Next.Workspaces, afterDragEnd.Next.Workspaces)

[<Fact>]
let ``MoveSizeEndedで落とされたモニターが現在表示されていなければ何もしない(v2.66)`` () =
    let topology, _ = MonitorLifecycle.reconcile (Wm.Core.Topology.initial Map.empty) [ mkMonitor 0 ]
    let seeded = { initial RuntimePolicy.defaults with Topology = topology }
    let w = WindowId 1L
    let afterW = step RuntimePolicy.defaults seeded (mkEnvelope 1L (visibleWindowObserved w))
    let rectObs: Observation<PixelRect> = { Value = Ok { X = 2000; Y = 0; Width = 300; Height = 300 }; SampleSequence = 1L; MonotonicTime = 2L; Source = "test" }
    let unknownMonitorObs: Observation<MonitorId> = { Value = Ok (MonitorId 99L); SampleSequence = 1L; MonotonicTime = 2L; Source = "test" }
    let afterDragEnd = step RuntimePolicy.defaults afterW.Next (mkEnvelope 2L (MoveSizeEnded (w, rectObs, unknownMonitorObs)))
    Assert.Equal(afterW.Next.Workspaces, afterDragEnd.Next.Workspaces)

// ===== DecisionTrace（§7.1、v2.68）：WmState.step経由でWindowsが出すInconsistencySuspectedが
// StepResult.Traceまで届くこと（Windows.fsの直接テストに加え、結線自体の確認）。 =====

[<Fact>]
let ``Windowsが出すInconsistencySuspectedは、WmState.step経由でStepResult.Traceまで届く(v2.68)`` () =
    let w = WindowId 1L
    let impossibleRecord = { WindowRecord.initial with Classification = Classified (EffFloating, ByUserOverride); UserOverride = None }
    let seeded = { initial RuntimePolicy.defaults with Windows = { (initial RuntimePolicy.defaults).Windows with Records = Map.ofList [ w, impossibleRecord ] } }
    let result = step RuntimePolicy.defaults seeded (mkEnvelope 1L (visibleWindowObserved w))
    match result.Trace with
    | [ InconsistencySuspected (domain, _) ] -> Assert.Equal("Windows", domain)
    | other -> Assert.True(false, sprintf "expected exactly one InconsistencySuspected trace, got %A" other)

// ===== MoveCause.OsInduced（§4.4 R3-4、v2.69で確認した既知のギャップをv2.72（ラウンド3）で解消。
// opus-review-unattributed-movecause.md §A・§Q5参照） =====

[<Fact>]
let ``OsInduced：Reconfiguring中にOSが別モニターへ寄せた窓は、復帰後のgrace満了で元のモニターへ戻るSetRectが出る(v2.72、既知のギャップv2.69の解消)`` () =
    // §Aの失敗シナリオそのまま：モニター2台、Tiled窓がモニターAに配置済み。抑止区間中にOSがモニターBへ
    // 寄せ、復帰後もモニター構成自体（2台とも存在）は変わらない——にもかかわらず窓は置き去りになっていた。
    let topology, current, visibleScreen = twoMonitorTopologyAndScreens ()
    let seeded = { initial RuntimePolicy.defaults with Topology = topology }
    let w = WindowId 1L
    let afterW = step RuntimePolicy.defaults seeded (mkEnvelope 1L (visibleWindowObserved w))
    Assert.Equal(Some current.Workspace, WorkspaceSet.workspaceOf w afterW.Next.Workspaces)
    // Satisfiedにする(Acceptedを作る)。
    let afterSatisfied = step RuntimePolicy.defaults afterW.Next (mkEnvelope 2L (visibleWindowObservedAtRectAndMonitor w current.Monitor.WorkArea current.Monitor.Id 2L))
    Assert.Empty(afterSatisfied.Next.Placement.Pending)
    Assert.Equal(Some current.Monitor.WorkArea, Map.tryFind w afterSatisfied.Next.Placement.Accepted)

    // スリープ/ロック等でReconfiguringへ入る(§7.3)。
    let afterReconfiguring = step RuntimePolicy.defaults afterSatisfied.Next (mkEnvelope 200L (ScreenReconfigurationInProgress true))
    match phase afterReconfiguring.Next.Topology with
    | Reconfiguring _ -> ()
    | other -> failwithf "expected Reconfiguring, got %A" other

    // 抑止区間中、OSが窓を別モニターへ寄せる(段①は無条件に信念へ反映する、ScreenPhaseを問わない)。
    // この観測自体はScreenPhaseがStableでないので検知も補正もされない(Q4-1、必須)。
    let afterMoved = step RuntimePolicy.defaults afterReconfiguring.Next (mkEnvelope 210L (visibleWindowObservedAtRectAndMonitor w visibleScreen.Monitor.WorkArea visibleScreen.Monitor.Id 3L))
    Assert.Equal(Some visibleScreen.Monitor.Id, LatestWins.value (Map.find w afterMoved.Next.Placement.Geometry).Monitor)

    // 復帰(モニター構成自体は変わらない)。Settling→(ScreenSettle満了)→Grace→(ScreenGrace満了)→Stable。
    let afterReconfiguringEnds = step RuntimePolicy.defaults afterMoved.Next (mkEnvelope 300L (ScreenReconfigurationInProgress false))
    let settleMs = TopologyConfig.defaults.SettleMs
    let graceMs = TopologyConfig.defaults.GraceMs
    let afterSettle = step RuntimePolicy.defaults afterReconfiguringEnds.Next (mkEnvelope (300L + settleMs) (TimerElapsed (TimerId.Topology ScreenSettle)))
    let afterGrace = step RuntimePolicy.defaults afterSettle.Next (mkEnvelope (300L + settleMs + graceMs) (TimerElapsed (TimerId.Topology ScreenGrace)))

    // Workspace所属は変わらない(UserDragとは違い、OsInducedも所属を変えない——Unattributedと同じ)。
    Assert.Equal(Some current.Workspace, WorkspaceSet.workspaceOf w afterGrace.Next.Workspaces)
    // grace満了までに、元のモニターのWorkAreaへ戻すSetRectが出ている
    // （architecture.md「grace window終了後に意図した配置(Desired)を再適用する」、v2.72で実装）。
    let effects = afterSettle.Effects @ afterGrace.Effects |> List.map GatedEffect.effect
    Assert.Contains(effects, function SetRect (_, target, rect) -> target = w && rect = current.Monitor.WorkArea | _ -> false)

// ===== MoveCause.Unattributed（§4.4 R3-4、ラウンド1、v2.70、Opus敵対的レビュー
// opus-review-unattributed-movecause.md） =====

[<Fact>]
let ``Stable中にMoveSizeイベントを伴わず別モニターへ漂った窓は、Workspaceは変えずに元のモニターへ戻すSetRectを発行する(v2.70)`` () =
    let topology, current, visibleScreen = twoMonitorTopologyAndScreens ()
    let seeded = { initial RuntimePolicy.defaults with Topology = topology }
    let w = WindowId 1L
    let afterW = step RuntimePolicy.defaults seeded (mkEnvelope 1L (visibleWindowObserved w))
    Assert.Equal(Some current.Workspace, WorkspaceSet.workspaceOf w afterW.Next.Workspaces)
    // Satisfiedにする(Acceptedを作る)。
    let afterSatisfied = step RuntimePolicy.defaults afterW.Next (mkEnvelope 2L (visibleWindowObservedAtRectAndMonitor w current.Monitor.WorkArea current.Monitor.Id 2L))
    Assert.Empty(afterSatisfied.Next.Placement.Pending)
    Assert.Equal(Some current.Monitor.WorkArea, Map.tryFind w afterSatisfied.Next.Placement.Accepted)

    // MoveSizeStarted/Endedを一切経由せず、別モニターの矩形がそのまま観測される
    // （アプリ自身の位置変更やWin+Shift+矢印を模す）。
    let afterDrifted = step RuntimePolicy.defaults afterSatisfied.Next (mkEnvelope 3L (visibleWindowObservedAtRectAndMonitor w visibleScreen.Monitor.WorkArea visibleScreen.Monitor.Id 3L))

    // 所属Workspaceは変わらない(UserDragとは違い、Unattributedは所属を変えない)。
    Assert.Equal(Some current.Workspace, WorkspaceSet.workspaceOf w afterDrifted.Next.Workspaces)
    // 元のモニターのWorkAreaへ戻すSetRectが発行される。
    let effects = afterDrifted.Effects |> List.map GatedEffect.effect
    Assert.Contains(effects, function SetRect (_, target, rect) -> target = w && rect = current.Monitor.WorkArea | _ -> false)

[<Fact>]
let ``MoveSizeEndedを経由した(UserDragの)別モニターへの移動はUnattributedのドリフト補正を誘発しない(回帰確認、v2.70)`` () =
    let topology, current, visibleScreen = twoMonitorTopologyAndScreens ()
    let seeded = { initial RuntimePolicy.defaults with Topology = topology }
    let w = WindowId 1L
    let afterW = step RuntimePolicy.defaults seeded (mkEnvelope 1L (visibleWindowObserved w))
    let afterSatisfied = step RuntimePolicy.defaults afterW.Next (mkEnvelope 2L (visibleWindowObservedAtRect w current.Monitor.WorkArea 2L))

    let rectObs: Observation<PixelRect> = { Value = Ok visibleScreen.Monitor.WorkArea; SampleSequence = 1L; MonotonicTime = 3L; Source = "test" }
    let monitorObs: Observation<MonitorId> = { Value = Ok visibleScreen.Monitor.Id; SampleSequence = 1L; MonotonicTime = 3L; Source = "test" }
    let afterDragEnd = step RuntimePolicy.defaults afterSatisfied.Next (mkEnvelope 3L (MoveSizeEnded (w, rectObs, monitorObs)))
    // UserDrag経由でWorkspace自体がvisibleScreenへ移るので、新しい所属での全画面が目標になり、
    // 「元のモニターへ戻す」補正にはならない。
    Assert.Equal(Some visibleScreen.Workspace, WorkspaceSet.workspaceOf w afterDragEnd.Next.Workspaces)
    let effects = afterDragEnd.Effects |> List.map GatedEffect.effect
    Assert.DoesNotContain(effects, function SetRect (_, target, rect) -> target = w && rect = current.Monitor.WorkArea | _ -> false)

[<Fact>]
let ``繰り返しクロスモニターへ漂う窓は、補正の打ち止め後にFloating・ByOscillationへ劣化する(v2.71、Opusレビュー§Q3・§Q5「ラウンド2」)`` () =
    // masterStackPolicyを使う：劣化によってTiledが1窓だけ残ったとき、Reconcileが実際に再計算して
    // 残りの窓を全画面へ広げることまで確認する(劣化がshouldReconcileを引き金にすることのテスト、
    // mutation testingで「driftDegradedをshouldReconcileへ足し忘れる」変更を検出するのに必要)。
    let topology, current, visibleScreen = twoMonitorTopologyAndScreens ()
    let seeded = { initial masterStackPolicy with Topology = topology }
    let w1, w2 = WindowId 1L, WindowId 2L
    let afterW1 = step masterStackPolicy seeded (mkEnvelope 1L (visibleWindowObserved w1))
    let afterW2 = step masterStackPolicy afterW1.Next (mkEnvelope 2L (visibleWindowObserved w2))
    // w1=master(半分)、w2=stack(半分、1窓なのでstackArea全体)。
    let masterRect = { current.Monitor.WorkArea with Width = current.Monitor.WorkArea.Width / 2 }
    let stackArea = { current.Monitor.WorkArea with X = masterRect.Width; Width = current.Monitor.WorkArea.Width - masterRect.Width }
    Assert.Equal(Some masterRect, desiredRectOf afterW2.Next.Placement w1)
    Assert.Equal(Some stackArea, desiredRectOf afterW2.Next.Placement w2)
    let afterSatisfied0 = step masterStackPolicy afterW2.Next (mkEnvelope 3L (visibleWindowObservedAtRectAndMonitor w1 masterRect current.Monitor.Id 3L))

    // unattributedCorrectionLimit(3)回、w1が漂う→補正→再び受け入れる、を繰り返す。
    let drift (prev: StepResult) (seq: int64) : StepResult =
        let afterDrifted = step masterStackPolicy prev.Next (mkEnvelope seq (visibleWindowObservedAtRectAndMonitor w1 visibleScreen.Monitor.WorkArea visibleScreen.Monitor.Id seq))
        step masterStackPolicy afterDrifted.Next (mkEnvelope (seq + 1L) (visibleWindowObservedAtRectAndMonitor w1 masterRect current.Monitor.Id (seq + 1L)))
    let afterSatisfied1 = drift afterSatisfied0 4L
    let afterSatisfied2 = drift afterSatisfied1 6L
    let afterSatisfied3 = drift afterSatisfied2 8L
    // まだTiledのまま(3回目までは補正されるだけ)、w2は依然stackArea(w1がmasterのまま)。
    match (Map.find w1 afterSatisfied3.Next.Windows.Records).Classification with
    | Classified (EffTiled, _) -> ()
    | other -> Assert.True(false, sprintf "expected still Tiled after 3 corrections, got %A" other)
    Assert.Equal(Some stackArea, desiredRectOf afterSatisfied3.Next.Placement w2)

    // 4回目の漂い：打ち止めに達し、補正ではなくFloating化が起きる。
    let afterDegraded = step masterStackPolicy afterSatisfied3.Next (mkEnvelope 10L (visibleWindowObservedAtRectAndMonitor w1 visibleScreen.Monitor.WorkArea visibleScreen.Monitor.Id 10L))
    match (Map.find w1 afterDegraded.Next.Windows.Records).Classification with
    | Classified (EffFloating, ByOscillation) -> ()
    | other -> Assert.True(false, sprintf "unexpected: %A" other)
    // Floatingになった窓はWorkspaceのFloating集合に入る(所属Workspace自体は変わらない)。
    Assert.Equal(Some current.Workspace, WorkspaceSet.workspaceOf w1 afterDegraded.Next.Workspaces)
    Assert.True(Set.contains w1 (WorkspaceSet.stateOf current.Workspace afterDegraded.Next.Workspaces).Floating)
    // w1が抜けたので、w2が唯一のTiled窓として全画面へ広がる——劣化が同じstepでshouldReconcileを
    // 引き金にしていることの確認(引き金にし忘れると、w2はstackAreaのまま変化しない)。
    Assert.Equal(Some current.Monitor.WorkArea, desiredRectOf afterDegraded.Next.Placement w2)
