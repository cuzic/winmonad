/// architecture.md §4.4/§4.5：Core.PlacementのGeometry信念修正（LatestWins規則）と消滅時クリーンアップ。
module Wm.Core.Tests.PlacementTests

open Xunit
open Wm.Contracts.Identifiers
open Wm.Contracts.Observation
open Wm.Contracts.Beliefs
open Wm.Core.Placement

let private obs (sequence: int64) (v: 'T) : Observation<'T> =
    { Value = Ok v; SampleSequence = sequence; MonotonicTime = sequence; Source = "test" }

let private emptyAppIdentity: AppIdentity = { Exe = None; HostExe = None; Aumid = None; CoreWindowExe = None }

let private snapshotWith
    (visibleFrameRect: Observation<PixelRect> option)
    (monitor: Observation<MonitorId> option)
    (isMinimized: Observation<bool> option)
    (presence: Observation<ObservedPresence> option)
    : WindowSnapshot =
    { Id = WindowId 1L
      Process = None
      AppIdentity = emptyAppIdentity
      Owner = None
      IsDialog = None
      NativeRect = None
      VisibleFrameRect = visibleFrameRect
      IsVisible = None
      IsMinimized = isMinimized
      Cloak = None
      Monitor = monitor
      MinWidthHint = None
      TargetIntegrity = None
      Responsiveness = None
      Presence = presence
      DpiAwareness = None
      TitleMatches = None }

let private rect: PixelRect = { X = 0; Y = 0; Width = 800; Height = 600 }

[<Fact>]
let ``onWindowObservedはVisibleFrameRect・Monitor・IsMinimized・Presenceを修正する`` () =
    let snapshot =
        snapshotWith (Some (obs 1L rect)) (Some (obs 1L (MonitorId 0L))) (Some (obs 1L false)) (Some (obs 1L VisibleToUser))
    let state' = onWindowObserved 10L snapshot initial
    let g = Map.find (WindowId 1L) state'.Geometry
    Assert.Equal(Some rect, LatestWins.value g.VisibleFrameRect)
    Assert.Equal(Some (MonitorId 0L), LatestWins.value g.Monitor)
    Assert.Equal(Some false, LatestWins.value g.IsMinimized)
    Assert.Equal(Some VisibleToUser, LatestWins.value g.Presence)
    // NativeRectはこのラウンドでは修正しない(既知のギャップ)。
    Assert.True(match g.NativeRect with Unformed [] -> true | _ -> false)

[<Fact>]
let ``onWindowObservedは観測が無いフィールドを変更しない`` () =
    let snapshot = snapshotWith (Some (obs 1L rect)) None None None
    let state' = onWindowObserved 10L snapshot initial
    let g = Map.find (WindowId 1L) state'.Geometry
    Assert.Equal(Some rect, LatestWins.value g.VisibleFrameRect)
    Assert.Equal(None, LatestWins.value g.Monitor)
    Assert.Equal(None, LatestWins.value g.IsMinimized)
    Assert.Equal(None, LatestWins.value g.Presence)

[<Fact>]
let ``onWindowObservedを2回続けて呼ぶと、より新しいSampleSequenceの観測が採用される`` () =
    let s1 = snapshotWith (Some (obs 1L rect)) None None None
    let largerRect = { rect with Width = 1200 }
    let s2 = snapshotWith (Some (obs 2L largerRect)) None None None
    let state1 = onWindowObserved 10L s1 initial
    let state2 = onWindowObserved 20L s2 state1
    let g = Map.find (WindowId 1L) state2.Geometry
    Assert.Equal(Some largerRect, LatestWins.value g.VisibleFrameRect)

[<Fact>]
let ``onMinimizedStateChangedはIsMinimizedだけを修正する`` () =
    let snapshot = snapshotWith (Some (obs 1L rect)) None None None
    let afterObserved = onWindowObserved 10L snapshot initial
    let afterMinimized = onMinimizedStateChanged 20L (WindowId 1L) (obs 2L true) afterObserved
    let g = Map.find (WindowId 1L) afterMinimized.Geometry
    Assert.Equal(Some true, LatestWins.value g.IsMinimized)
    Assert.Equal(Some rect, LatestWins.value g.VisibleFrameRect) // 他のフィールドは変わらない

[<Fact>]
let ``onWindowVanishedはGeometryを除く`` () =
    let snapshot = snapshotWith (Some (obs 1L rect)) None None None
    let afterObserved = onWindowObserved 10L snapshot initial
    Assert.True(Map.containsKey (WindowId 1L) afterObserved.Geometry)
    let afterVanished = onWindowVanished (WindowId 1L) afterObserved
    Assert.False(Map.containsKey (WindowId 1L) afterVanished.Geometry)
