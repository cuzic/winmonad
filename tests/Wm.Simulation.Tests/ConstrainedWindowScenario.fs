/// ADR-019実装順18：Tests/ConstrainedWindowScenario.fs。architecture.md §12.1/§19。
///
/// この一連の往復——モニターが現れてトポロジが確定し、新しい窓が現れてPolicyの既定で分類され、
/// Reconcileが矩形を要求し、疑似OSがそれを適用して観測を返し、最終的にPendingがSatisfiedで収束する——
/// こそが、本書の最優先仮説（1章「実Windowsで観測した不具合を、同じCoreと取得した証拠に基づく
/// 疑似OS上に持ち込み、失敗する最小シナリオを作成して修正前後の挙動を比較できる」）の土台である。
/// `Wm.Runtime.Reducer`（実行器）と`Wm.Simulation.VirtualWorld`（疑似OS）を実際に結びつけて検証する、
/// このセッションで最初のEnd-to-Endシナリオテスト。
module Wm.Simulation.Tests.ConstrainedWindowScenario

open Xunit
open Wm.Contracts.Identifiers
open Wm.Contracts.Observation
open Wm.Contracts.SharedVocabulary
open Wm.Contracts.CoreCommand
open Wm.Core.Topology
open Wm.Core.Windows
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
    // 発見時はIsVisibleだけを報告する。矩形はまだ知らない（Reconcileが要求し、疑似OSが適用した結果を
    // 再観測するまで）——これは、VirtualWorldが生成するSetRect後のVisibleFrameRect観測
    // （NextSampleSequence、0から始まる）と、この発見イベント自身のSampleSequenceが衝突して
    // LatestWinsが新しい観測を古いものと誤認する（`isNewer`は厳密な`>`なので、同じ値では更新されない）
    // という不具合を実際に踏んで見つけた——発見イベントはそもそも矩形を運ぶ必要が無い、という
    // 設計上も正しい形に直した。
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

/// モニタートポロジがStableへ確定するまで、`MonitorTopologyObserved`の送信とScreenSettleタイマーの
/// 前進を行う（§7.3「安定待ち」。TopologyScreenSettleTests.fsが検査する遷移そのもの）。
let private settleTopology (reducer: Reducer) (monitors: MonitorSnapshot list) : unit =
    reducer.Submit 0L "virtual-world" (MonitorTopologyObserved monitors) |> ignore
    reducer.AdvanceToNextTimer () |> ignore // ScreenSettle満了 → Grace、Displaysが確定する

[<Fact>]
let ``新しい窓が現れると分類・Reconcile・疑似OSへのSetRect・観測の折り返しを経てPendingがSatisfiedで収束する`` () =
    let reducer = Reducer(RuntimePolicy.defaults, "scenario-session", "test-build", "test-policy-hash", None)
    settleTopology reducer [ mkMonitor 0 ]
    match displays reducer.State.Topology with
    | Displays _ -> ()
    | NoMonitors -> failwith "expected the topology to settle with 1 monitor"

    let w = WindowId 1L
    let initialRect: PixelRect = { X = 100; Y = 100; Width = 400; Height = 300 }
    let world0 = empty |> addWindow w initialRect Immediate

    let discovery = reducer.Submit 1000L "virtual-world" (visibleWindowObserved w)

    match Map.tryFind w reducer.State.Windows.Records |> Option.map (fun r -> r.Classification) with
    | Some (Classified (EffTiled, _)) -> ()
    | other -> failwithf "expected the window to be classified Tiled by DefaultManagement, got %A" other

    let finalWorld = settle 1000L reducer world0 discovery.Effects

    // Reconcileが要求したWorkArea全体(Monocle、DefaultLayout)へ、疑似OSが実際に矩形を合わせている。
    let workArea: PixelRect = (mkMonitor 0).WorkArea
    Assert.Equal(workArea, (Map.find w finalWorld.Windows).Rect)
    // 疑似OSからの観測が目標と一致したので、Pendingは収束してもう残っていない(Satisfied)。
    Assert.Empty(reducer.State.Placement.Pending)
    Assert.Equal(Some (Shown (Some workArea)), Map.tryFind w reducer.State.Placement.Desired)
