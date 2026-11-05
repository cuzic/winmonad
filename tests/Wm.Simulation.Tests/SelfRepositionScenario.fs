/// v2.62：`WindowBehaviors.SelfRepositionAfterMs`を使い、`Mismatch → Reevaluate`（v2.42、Opus敵対的
/// レビューで設計）が実際のReducer+VirtualWorldパイプラインを通して正しく働くことを初めて確認する。
/// `DelayedWindowScenario.fs`と同じ「疑似OS側の遅延観測（`advanceClock`）とCore側の`PendingDeadline`
/// タイマー（`AdvanceToNextTimer`）という独立した2つの時計」の構図を使うが、観測される矩形が要求と
/// 無関係な`selfRect`である点が異なる——これが`Mismatch`（`Timeout`とは別の終端）を引き起こす。
module Wm.Simulation.Tests.SelfRepositionScenario

open Xunit
open Wm.Contracts.Identifiers
open Wm.Contracts.Observation
open Wm.Contracts.SharedVocabulary
open Wm.Contracts.CoreCommand
open Wm.Contracts.GatedEffect
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
    reducer.AdvanceToNextTimer () |> ignore // ScreenGrace満了 → Stable

[<Fact>]
let ``SelfRepositionAfterMsで要求と異なる矩形が観測されても、PendingDeadlineまではPendingは残る(v2.62)`` () =
    let reducer = Reducer(RuntimePolicy.defaults, "scenario-session", "test-build", "test-policy-hash", None)
    settleTopology reducer [ mkMonitor 0 ]
    let w = WindowId 1L
    let selfRect: PixelRect = { X = 500; Y = 500; Width = 300; Height = 300 }
    let world0 = empty |> addWindow w { X = 0; Y = 0; Width = 10; Height = 10 } (SelfRepositionAfterMs (500L, selfRect))

    let discovery = reducer.Submit 1000L "virtual-world" (visibleWindowObserved w)
    let worldAfterSetRect = settle 1000L reducer world0 discovery.Effects
    Assert.Equal(1, Map.count reducer.State.Placement.Pending)
    Assert.Equal(selfRect, (Map.find w worldAfterSetRect.Windows).Rect) // DelayedByMsと同じ前例：worldの内部状態は即座にselfRectへ

    // 仮想時計を500ms進め、自己移動の観測を発火させる。PendingDeadline（2000ms後=3000）にはまだ遠い。
    let worldObserved, events = advanceClock 1500L worldAfterSetRect
    Assert.Empty(worldObserved.Scheduled)
    match events with
    | [ ev ] -> reducer.Submit 1500L "virtual-world" ev |> ignore
    | other -> Assert.True(false, sprintf "expected exactly 1 scheduled observation to fire, got %A" other)

    // 要求(WorkArea)と異なるselfRectが観測されても、「ちょうど一致した」わけではないのでSatisfiedには
    // ならない——`Mismatch`の判定自体はPendingDeadline満了時点でだけ行う（段階1、v2.42）ので、
    // この時点ではまだPendingが残っている。
    Assert.Equal(1, Map.count reducer.State.Placement.Pending)

[<Fact>]
let ``SelfRepositionAfterMsのMismatchはPendingDeadline一度で終端し、Timeoutと違って再試行しない(v2.62)`` () =
    let reducer = Reducer(RuntimePolicy.defaults, "scenario-session", "test-build", "test-policy-hash", None)
    settleTopology reducer [ mkMonitor 0 ]
    let w = WindowId 1L
    let selfRect: PixelRect = { X = 500; Y = 500; Width = 300; Height = 300 }
    let world0 = empty |> addWindow w { X = 0; Y = 0; Width = 10; Height = 10 } (SelfRepositionAfterMs (500L, selfRect))

    let discovery = reducer.Submit 1000L "virtual-world" (visibleWindowObserved w)
    let worldAfterSetRect = settle 1000L reducer world0 discovery.Effects
    let worldObserved, events = advanceClock 1500L worldAfterSetRect
    match events with
    | [ ev ] -> reducer.Submit 1500L "virtual-world" ev |> ignore
    | other -> Assert.True(false, sprintf "expected exactly 1 scheduled observation to fire, got %A" other)

    // PendingDeadline（3000ms）を発火させる。Mismatchと判定され、Pendingは即座に終端する。
    let afterDeadline =
        match reducer.AdvanceToNextTimer () with
        | Some r -> r
        | None -> failwith "expected a pending PendingDeadline"
    Assert.Empty(afterDeadline.Next.Placement.Pending)

    // `Reevaluate`の意味論「アプリと戦わない」（v2.42、AskUserQuestionでプロジェクトオーナー確認済み）：
    // `DelayedWindowScenario.fs`のTimeoutケース（`pendingRetryMaxAttempts`回まで同じ目標へSetRectを
    // 再発行する）と対照的に、Mismatchは一度の終端で同じ目標へのRetryを一切しない。
    let newSetRects = afterDeadline.Effects |> List.filter (fun g -> match GatedEffect.effect g with SetRect _ -> true | _ -> false)
    Assert.Empty(newSetRects)
    // 再試行用のPendingDeadlineを新たに張っていないので、次に発火すべきタイマーも無い。
    Assert.Equal(None, reducer.Timers.NextDeadline)
