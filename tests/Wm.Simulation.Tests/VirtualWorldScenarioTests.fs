/// architecture.md §12.1：VirtualWorld.applyEffectの個別の分岐を、実際のReducerパイプラインが生成した
/// `GatedEffect`を使って検査する。
///
/// `GatedEffect`のコンストラクタは`internal`で`Wm.Core`/`Wm.Core.Tests`だけに構築を許している
/// （§6.2、Opusレビュー2-9：「誰でも偽のGatedEffectを作れると、internalコンストラクタが実質何も
/// 守らなくなる」）。`Wm.Simulation`はGatedEffectを消費するだけの側であり、この制約の対象に
/// 意図的に含まれている（§17「GatedEffectを消費するだけのWm.Windows/Wm.SimulationがWm.Coreの
/// アセンブリ全体を参照することになり依存方向が曖昧になる」）。したがってこのファイルのテストは、
/// 必ず実際の`Reducer.Submit`が返す`StepResult.Effects`（ゲートを実際に通過した本物のGatedEffect）
/// だけを使う——`applyEffect`を手作りのGatedEffectで単体テストすることは、この設計上できない
/// （意図的な制約であり、見落としではない）。
module Wm.Simulation.Tests.VirtualWorldScenarioTests

open Xunit
open Wm.Contracts.Identifiers
open Wm.Contracts.Observation
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
    reducer.AdvanceToNextTimer () |> ignore

[<Fact>]
let ``VirtualWorldに存在しない窓へのSetRectはERROR_INVALID_WINDOW_HANDLEになり、Pendingは消えずに残る`` () =
    // Coreは窓が生きていると思っているが、疑似OS側にその窓が無い（OSで既に消えたのにCoreがまだ
    // 知らない、という競合を模す）。VirtualWorld.addWindowを呼ばないことでこれを表す。
    let reducer = Reducer(RuntimePolicy.defaults, "scenario-session", "test-build", "test-policy-hash", None)
    settleTopology reducer [ mkMonitor 0 ]
    let w = WindowId 1L
    let discovery = reducer.Submit 1000L "virtual-world" (visibleWindowObserved w)
    Assert.Equal(1, Map.count reducer.State.Placement.Pending) // Reconcileが要求を出している

    // applyEffectの戻り値そのもの（callReturns）を直接検査する——`settle`経由ではPlacementが
    // `EffectCallReturned`をまだ消費しない（Placement.fsのファイル冒頭「対象外」参照）ため、
    // Pendingの有無だけを見てもOk/Errorのどちらが返ったかは区別できない。GatedEffectは実際の
    // Reducerパイプラインが生成した本物を使う（このファイルの冒頭コメント参照）。discovery.Effectsには
    // WindowsのTargetIntegrity probe要求（Reprobe）も混ざるので、SetRectだけを取り出す。
    let setRectEffects = discovery.Effects |> List.filter (fun g -> match GatedEffect.effect g with SetRect _ -> true | _ -> false)
    match setRectEffects with
    | [ gated ] ->
        let _, events, callReturns = applyEffect 1000L empty gated
        Assert.Empty(events)
        match callReturns with
        | [ (rid, Error record) ] ->
            Assert.Equal(1400, record.Code)
            Assert.Equal(Wm.Contracts.ErrorTypes.Win32LastError, record.Space)
        | other -> Assert.True(false, sprintf "unexpected callReturns: %A" other)
    | other -> Assert.True(false, sprintf "expected exactly 1 SetRect effect from Reconcile, got %A" other)

    let worldWithoutTheWindow = empty // addWindowを呼ばない
    let finalWorld = settle 1000L reducer worldWithoutTheWindow discovery.Effects

    Assert.Empty(finalWorld.Windows)
    // PlacementはこのラウンドではEffectCallReturnedを消費しないので、PendingはTimeout
    // （PendingDeadline）まで残り続ける。
    Assert.Equal(1, Map.count reducer.State.Placement.Pending)

[<Fact>]
let ``2つ目の窓が現れても、両方がMonocleで全画面に配置され両方Satisfiedになる`` () =
    let reducer = Reducer(RuntimePolicy.defaults, "scenario-session", "test-build", "test-policy-hash", None)
    settleTopology reducer [ mkMonitor 0 ]
    let w1, w2 = WindowId 1L, WindowId 2L
    let world1 = empty |> addWindow w1 { X = 0; Y = 0; Width = 10; Height = 10 } Immediate
    let d1 = reducer.Submit 1000L "virtual-world" (visibleWindowObserved w1)
    let world1' = settle 1000L reducer world1 d1.Effects
    Assert.Empty(reducer.State.Placement.Pending)

    let world2 = world1' |> addWindow w2 { X = 0; Y = 0; Width = 10; Height = 10 } Immediate
    let d2 = reducer.Submit 2000L "virtual-world" (visibleWindowObserved w2)
    let finalWorld = settle 2000L reducer world2 d2.Effects

    let workArea: PixelRect = (mkMonitor 0).WorkArea
    Assert.Equal(workArea, (Map.find w1 finalWorld.Windows).Rect)
    Assert.Equal(workArea, (Map.find w2 finalWorld.Windows).Rect)
    Assert.Empty(reducer.State.Placement.Pending)
