/// ADR-019実装順17の次段：`WindowBehaviors.DelayedByMs`（§12.1の`ScheduledSimEvent`相当）を使い、
/// 疑似OS側の遅延観測（`VirtualWorld.advanceClock`）とCore側の`PendingDeadline`タイマー
/// （`Reducer.AdvanceToNextTimer`）という、互いに無関係な2つの時計が絡むシナリオを検証する。
/// `ScenarioRunner.fs`冒頭コメントが「タイマー駆動の往復はシナリオ側の責務のまま、ここでは結合しない」
/// と明示していた部分を、このファイルで初めて専用のシナリオとして書く。
module Wm.Simulation.Tests.DelayedWindowScenario

open Xunit
open Wm.Contracts.Identifiers
open Wm.Contracts.Observation
open Wm.Contracts.Beliefs
open Wm.Contracts.SharedVocabulary
open Wm.Contracts.CoreCommand
open Wm.Core.Topology
open Wm.Core.Placement
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
          Style = None
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
    // Grace→Stableへの`ScreenGrace`タイマーも、ここで明示的に汲み取っておく。他のシナリオ
    // （ConstrainedWindowScenario.fs等）はこの後`AdvanceToNextTimer`を二度と呼ばないので残しても無害
    // だが、このファイルはPendingDeadlineをAdvanceToNextTimerで狙って発火させるため、汲み取り忘れた
    // ScreenGraceが先に(最速の期限として)ポップされてしまう——実際にこの取りこぼしでテストが落ちたのを
    // 見つけて直した。
    reducer.AdvanceToNextTimer () |> ignore // ScreenGrace満了 → Stable

[<Fact>]
let ``DelayedByMsの観測がPendingDeadlineより先に届けば、advanceClockの発火でSatisfiedになる`` () =
    let reducer = Reducer(RuntimePolicy.defaults, "scenario-session", "test-build", "test-policy-hash", None)
    settleTopology reducer [ mkMonitor 0 ]
    let w = WindowId 1L
    let initialRect: PixelRect = { X = 100; Y = 100; Width = 400; Height = 300 }
    let world0 = empty |> addWindow w initialRect (DelayedByMs 500L)

    let discovery = reducer.Submit 1000L "virtual-world" (visibleWindowObserved w)
    let worldAfterSetRect = settle 1000L reducer world0 discovery.Effects
    // SetRectの呼び出し自体はOkで即時返るが、観測はまだ届いていないのでPendingは残っている。
    Assert.Equal(1, Map.count reducer.State.Placement.Pending)
    Assert.NotEmpty(worldAfterSetRect.Scheduled)

    // 仮想時計を遅延（500ms後=1500）だけ進める。PendingDeadline（2000ms後=3000）にはまだ遠い。
    let worldObserved, events = advanceClock 1500L worldAfterSetRect
    Assert.Empty(worldObserved.Scheduled)
    match events with
    | [ ev ] -> reducer.Submit 1500L "virtual-world" ev |> ignore
    | other -> Assert.True(false, sprintf "expected exactly 1 scheduled observation to fire, got %A" other)

    Assert.Empty(reducer.State.Placement.Pending)
    match Map.tryFind w reducer.State.Placement.Movability with
    | Some { Value = Movable; Confidence = Confirmed } -> ()
    | other -> Assert.True(false, sprintf "expected Movable/Confirmed after Satisfied, got %A" other)

[<Fact>]
let ``DelayedByMsの観測がPendingDeadlineより遅れ続けると、AdvanceToNextTimerが繰り返しTimeoutを発火し最終的にPendingを打ち切る`` () =
    // v2.28でPendingDeadlineのRetryを実装したので、1回のTimeoutはもう諦めではなく再試行になった
    // （Coreのタイマーキューの前進だけで、疑似OS側のScheduledとは無関係に、新しいPendingDeadlineが
    // 繰り返し発火する——これもこのファイルが検証したい「2つの独立した時計」の一部）。
    // pendingRetryMaxAttempts回続けて発火させると、最後は単発の諦めと同じ終端になる。
    let reducer = Reducer(RuntimePolicy.defaults, "scenario-session", "test-build", "test-policy-hash", None)
    settleTopology reducer [ mkMonitor 0 ]
    let w = WindowId 1L
    let initialRect: PixelRect = { X = 100; Y = 100; Width = 400; Height = 300 }
    // PendingDeadline（2000ms後）よりも遅い5000ms後にしか観測が届かない疑似OS。
    let world0 = empty |> addWindow w initialRect (DelayedByMs 5000L)

    let discovery = reducer.Submit 1000L "virtual-world" (visibleWindowObserved w)
    let worldAfterSetRect = settle 1000L reducer world0 discovery.Effects
    Assert.Equal(1, Map.count reducer.State.Placement.Pending)

    for _ in 1 .. pendingRetryMaxAttempts do
        match reducer.AdvanceToNextTimer () with
        | Some _ -> ()
        | None -> Assert.True(false, "expected a PendingDeadline to be the next due timer")

    Assert.Empty(reducer.State.Placement.Pending)
    Assert.Equal(None, Map.tryFind w reducer.State.Placement.Desired)
    // 疑似OS側にはまだ遅延観測が残っている（5000ms後）が、Pendingはもう無いのでこの先の
    // advanceClockは（Reconcileするまで）何にも結びつかない——意図的に呼ばずここで終える。
    Assert.NotEmpty(worldAfterSetRect.Scheduled)
