/// architecture.md §7.1：WmState.step経由で、MinimizedStateChanged（これまで配線先が無かった入力）が
/// 実際にPlacement.Geometryへ届くこと、WindowRemovedで消えることを検査する。
module Wm.Core.Tests.WmStatePlacementTests

open Xunit
open Wm.Contracts.Identifiers
open Wm.Contracts.Observation
open Wm.Contracts.Beliefs
open Wm.Contracts.GatedEffect
open Wm.Contracts.CoreCommand
open Wm.Core.WmState

let private mkEnvelope (sequence: int64) (input: InputEvent) : EventEnvelope<InputEvent> =
    { Sequence = sequence; MonotonicTime = sequence; Source = "test"; SessionId = "test-session"; Input = input }

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
let ``WindowRemovedは同じstepでPlacement.Geometryも除く`` () =
    let state = initial RuntimePolicy.defaults
    let obs: Observation<bool> = { Value = Ok true; SampleSequence = 1L; MonotonicTime = 1L; Source = "test" }
    let afterMinimized = (step RuntimePolicy.defaults state (mkEnvelope 1L (MinimizedStateChanged (WindowId 1L, obs)))).Next
    Assert.True(Map.containsKey (WindowId 1L) afterMinimized.Placement.Geometry)
    let afterRemoved = (step RuntimePolicy.defaults afterMinimized (mkEnvelope 2L (WindowRemoved (WindowId 1L)))).Next
    Assert.False(Map.containsKey (WindowId 1L) afterRemoved.Placement.Geometry)
