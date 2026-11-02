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
open Wm.Core.Topology
open Wm.Core.Workspaces
open Wm.Core.Windows
open Wm.Core.Placement
open Wm.Core.WmState

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
          Cloak = None
          Monitor = None
          MinWidthHint = None
          TargetIntegrity = None
          Responsiveness = None
          Presence = None
          DpiAwareness = None
          TitleMatches = None }

[<Fact>]
let ``新規窓のWindowObservedは、同じstepでWindows.RecordsがAliveになるので、PlacementのReprobeがゲートを通過する`` () =
    let state = initial RuntimePolicy.defaults
    let result = step RuntimePolicy.defaults state (mkEnvelope 1L (visibleWindowObserved (WindowId 1L)))
    let effects = result.Effects |> List.map GatedEffect.effect
    Assert.Contains(Reprobe (WindowId 1L, KTargetIntegrity), effects)

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
    let afterHung = step RuntimePolicy.defaults afterObserved.Next (mkEnvelope 2L (EffectCallReturned (rid, Error hungError)))
    match Map.tryFind (WindowId 1L) afterHung.Next.Health.Responsiveness with
    | Some { Value = Hung; Confidence = Confirmed } -> ()
    | other -> Assert.True(false, sprintf "unexpected: %A" other)
    Assert.Empty(afterHung.Next.Placement.Pending)

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
    let afterDenied = step RuntimePolicy.defaults afterObserved.Next (mkEnvelope 2L (EffectCallReturned (rid, Error deniedError)))

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

[<Fact>]
let ``WindowRemovedは同じstepでPlacement.Geometryも除く`` () =
    let state = initial RuntimePolicy.defaults
    let obs: Observation<bool> = { Value = Ok true; SampleSequence = 1L; MonotonicTime = 1L; Source = "test" }
    let afterMinimized = (step RuntimePolicy.defaults state (mkEnvelope 1L (MinimizedStateChanged (WindowId 1L, obs)))).Next
    Assert.True(Map.containsKey (WindowId 1L) afterMinimized.Placement.Geometry)
    let afterRemoved = (step RuntimePolicy.defaults afterMinimized (mkEnvelope 2L (WindowRemoved (WindowId 1L)))).Next
    Assert.False(Map.containsKey (WindowId 1L) afterRemoved.Placement.Geometry)
