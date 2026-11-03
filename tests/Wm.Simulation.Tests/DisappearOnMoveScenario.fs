/// v2.63：`WindowBehaviors.DisappearOnMove`を使い、「`SetRect`を発行してPendingが生きている最中に、
/// その応答として窓が本当に消滅する」という競合を初めて実パイプラインで再現する。`Placement.onWindowVanished`
/// の`Pending`/`PendingDeadline`後始末（Opusレビュー4-3、v2.16で一度は取りこぼしが実際に見つかった項目）は
/// これまで`WindowRemoved`を外部から直接`reducer.Submit`するテストでしか確かめられておらず、`SetRect`の
/// `GatedEffect`応答として`WindowRemoved`が生まれる経路（`VirtualWorld.applyEffect` → `ScenarioRunner.settle`）
/// を通したことは一度もなかった。
module Wm.Simulation.Tests.DisappearOnMoveScenario

open Xunit
open Wm.Contracts.Identifiers
open Wm.Contracts.Observation
open Wm.Contracts.SharedVocabulary
open Wm.Contracts.CoreCommand
open Wm.Core.Topology
open Wm.Core.WmState
open Wm.Runtime.Reducer
open Wm.Simulation.WindowBehaviors
open Wm.Simulation.VirtualWorld
open Wm.Simulation.ScenarioRunner

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
          IsVisible = Some { Value = Ok true; SampleSequence = 0L; MonotonicTime = 0L; Source = "virtual-world" }
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

let private settleTopology (reducer: Reducer) (monitors: MonitorSnapshot list) : unit =
    reducer.Submit 0L "virtual-world" (MonitorTopologyObserved monitors) |> ignore
    reducer.AdvanceToNextTimer () |> ignore // ScreenSettle満了 → Grace（§7.3）
    reducer.AdvanceToNextTimer () |> ignore // ScreenGrace満了 → Stable

[<Fact>]
let ``DisappearOnMoveはSetRect応答のWindowRemovedでPendingとPendingDeadlineタイマーを即座に後始末する(v2.63)`` () =
    let reducer = Reducer(RuntimePolicy.defaults, "scenario-session", "test-build", "test-policy-hash", None)
    settleTopology reducer [ mkMonitor 0 ]
    let w = WindowId 1L
    let world0 = empty |> addWindow w { X = 0; Y = 0; Width = 10; Height = 10 } DisappearOnMove

    // 発見観測によりLayoutがSetRectを発行する——これがPendingを作る（PendingDeadlineタイマーも張る）。
    let discovery = reducer.Submit 1000L "virtual-world" (visibleWindowObserved w)

    // settleの中で、VirtualWorldがSetRectへの応答としてWindowRemovedを生み（要求どおりの観測には
    // 一度も応えない）、それがreducerへ投入されて即座にvanishWindow→Placement.onWindowVanishedへ届く。
    let worldAfter = settle 1000L reducer world0 discovery.Effects

    Assert.DoesNotContain(w, worldAfter.Windows |> Map.toSeq |> Seq.map fst)
    Assert.Empty(reducer.State.Placement.Pending)
    Assert.Equal(None, reducer.Timers.NextDeadline)
