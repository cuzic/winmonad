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
open Wm.Contracts.Beliefs
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

/// v2.113・既知のギャップ（pad2py-pad-host実機、v2.112で発見・Opus敵対的レビュー
/// `opus-review-frame-inset.md`で検証）：上のテストが示す「きれいに収束する」基準シナリオに、実機で
/// 実測した不可視リサイズ枠（architecture.md §4.2/§13）だけを加えると、Satisfiedに一度も至らず
/// Mismatch → Reevaluate（v2.42）で「戦わない」終端をしてしまう——Coreは`VisibleFrameRect`で
/// 比較するが、`SetRect`はネイティブ矩形を要求するだけで補正しないため。この修正自体
/// （Core側での補正）は意図的にこのラウンドの対象外——VirtualWorld側で先に再現することが目的
/// （Opusレビュー論点5「ラウンド1：VirtualWorldに不可視枠をモデル化する」）。Core側の補正が入れば
/// このテストの「Movabilityが確定しない」という結末は変わるはずで、そのときはこのテストを
/// 更新する（削除ではなく、「補正が効いてSatisfiedに至る」ことを検査する形へ）。
[<Fact>]
let ``不可視リサイズ枠を持つ窓は、要求どおりの矩形が返らずMismatch→Reevaluateで終端し、Satisfied/Movable確定に至らない(v2.113、既知のギャップ)`` () =
    let reducer = Reducer(RuntimePolicy.defaults, "scenario-session", "test-build", "test-policy-hash", None)
    settleTopology reducer [ mkMonitor 0 ]
    let w = WindowId 1L
    let initialRect: PixelRect = { X = 100; Y = 100; Width = 400; Height = 300 }
    // pad2py-pad-host実機（v2.112）で実測した不可視リサイズ枠：X+7/Width-14/Height-7/Y不変。
    let realisticInset: FrameInset = { Left = 7; Top = 0; Right = 7; Bottom = 7 }
    let world0 = empty |> addWindow w initialRect Immediate |> withFrameInset w realisticInset

    let discovery = reducer.Submit 1000L "virtual-world" (visibleWindowObserved w)
    let finalWorld = settle 1000L reducer world0 discovery.Effects

    let workArea: PixelRect = (mkMonitor 0).WorkArea
    // 疑似OSが実際に設定したネイティブ矩形は要求どおり(workArea)だが、不可視リサイズ枠のため
    // VisibleFrameRectはそれより縮んで観測される——Coreが比較するのはVisibleFrameRectなので、
    // settle直後の時点ではまだSatisfiedに至らずPendingが残る。
    Assert.Equal(workArea, (Map.find w finalWorld.Windows).Rect)
    Assert.NotEmpty(reducer.State.Placement.Pending)

    // PendingDeadline満了（§7.2「Mismatch → Reevaluate」、v2.42）：不一致のまま「戦わない」終端をする。
    // ScreenGrace等、Placement以外のタイマーが先に発火することもあるため、Pendingが尽きるまで
    // 全てのタイマーを進める（`CalibrateTests.fs`/`ClassifyTests.fs`と同じ要領）。
    let mutable more = true
    while more && not (Map.isEmpty reducer.State.Placement.Pending) do
        match reducer.AdvanceToNextTimer () with
        | Some _ -> ()
        | None -> more <- false

    // Mismatch→Reevaluateで終端したのでPendingは消える——が、これはSatisfiedではない。
    Assert.Empty(reducer.State.Placement.Pending)
    // 本来の目標(workArea)はSatisfiedで一度も確認されなかったので、Movabilityは発見時の楽観的な
    // 既定値(Inferred)のままで、Confirmedへは進まない——実機で見つかった既知のギャップそのもの。
    match Map.tryFind w reducer.State.Placement.Movability with
    | Some belief ->
        match belief.Confidence with
        | Confirmed ->
            failwith
                "known gap closed? Movability became Confirmed even though Satisfied should never have fired here -- if a real fix landed, update this test to assert Satisfied/Confirmed instead of this known-gap shape"
        | _ -> ()
    | None -> failwith "expected a Movability belief to exist (set at discovery)"
