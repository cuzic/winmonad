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
open Wm.Contracts.GatedEffect
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

/// v2.115・既知のギャップ（Opus敵対的レビュー`opus-review-frame-inset.md`論点3-Bで指摘）：
/// 「既にずれた/既に目標どおりの位置にいる窓へ同じ目標を再発行すると、観測が一切来ずTimeout→Retry→
/// 諦めへ進み、次のReconcileで同じ目標をまた再発行する」という再発行ループ。WM再起動時（前回の実行で
/// 既にタイル済みだった窓）が典型例として指摘されていた——これをVirtualWorldで再現する。
///
/// 根拠（推測ではない）：Phase 0 CIスパイクS22/S23の設計コメントが実機で確認済みの事実を記録していた
/// （`Program.fs`のS22/S23冒頭コメント参照）："SetWindowPosの要求が現在のネイティブ矩形と完全に
/// 同じ場合、WM_WINDOWPOSCHANGED/EVENT_OBJECT_LOCATIONCHANGEは発火しない"——どちらのスパイクも、
/// この無反応を「遅延」と誤認しないよう初期矩形をどのtargetとも異ならせて回避していた。今回
/// `VirtualWorld.applyEffect`に同じ無反応をモデル化した（`Immediate`/`HungAfterMs`、`rect = vw.Rect`
/// の場合は観測を一切生まない、`VirtualWorld.fs`の型直前のコメント参照）。
///
/// **v2.122で実際のCore側対策（`dispatchPlacements`の`alreadyAtTarget`ショートカット、
/// `Placement.fs`の型直前のコメント参照）を入れたが、このテストはそれでも閉じない——意図的に**。
/// このテストの`visibleWindowObserved`は発見イベントから矩形を省く簡略化モデル（型直前の
/// コメント参照、SampleSequence衝突という別の不具合を避けるための設計）であり、実機の
/// `Wm.Windows.RealObservation.observeWindow`（`RawFirstSeen`時点で常にNativeRect/
/// VisibleFrameRectの対を問い合わせる）とは異なる。v2.122の対策は「発見時点で既にTargetと
/// 一致する信念がある」場合に効く——実機では通常これが成立する（`PlacementReconcileTests.fs`の
/// v2.122新規テスト参照）。本テストが表すのは「発見時点でも矩形情報が一切無い」というより狭い
/// 残課題（Win32呼出自体が失敗した等）であり、実機でこれが実際に起こるかは未確認。
[<Fact>]
let ``既に目標どおりの位置にある窓への最初のSetRectはOSの無反応(no-op)のためTimeout→Retry→諦めへ進み、次のReconcileで同じ目標がまた発行される(v2.115、既知のギャップ)`` () =
    let reducer = Reducer(RuntimePolicy.defaults, "scenario-session", "test-build", "test-policy-hash", None)
    settleTopology reducer [ mkMonitor 0 ]
    let w = WindowId 1L
    let workArea: PixelRect = (mkMonitor 0).WorkArea
    // WM再起動後、前回の実行で既にタイル済みだった窓を模す——発見時点で既にネイティブ矩形が
    // 目標(workArea)と一致している。発見イベント自体は矩形を運ばない（`visibleWindowObserved`の
    // 型直前のコメント参照）ので、Coreの`Desired`はまだ空で、最初のReconcileは必ずSetRectを発行する
    // （`dispatchPlacements`の`desiredChanged`判定：`Map.tryFind`が`None`なら常に`true`）。
    let world0 = empty |> addWindow w workArea Immediate

    let discovery = reducer.Submit 1000L "virtual-world" (visibleWindowObserved w)
    let mutable world = settle 1000L reducer world0 discovery.Effects

    // 要求と現在のネイティブ矩形が完全に一致するため、VirtualWorldは観測を一切生まない。
    Assert.Equal(workArea, (Map.find w world.Windows).Rect)
    Assert.NotEmpty(reducer.State.Placement.Pending)

    // PendingDeadline満了を尽きるまで進める：Timeout→Retryを`pendingRetryMaxAttempts`回使い切り、
    // 最終的に諦める（`retryOrGiveUp`、§7.2「Timeout → Retry/Backoff、上限後は配置対象外」、v2.28）。
    // 各リトライのSetRectも同じ理由で無反応なので、何度繰り返しても収束しない。
    let mutable more = true
    while more && not (Map.isEmpty reducer.State.Placement.Pending) do
        match reducer.AdvanceToNextTimer () with
        | Some sr -> world <- settle 1000L reducer world sr.Effects
        | None -> more <- false

    // 諦めた結果、Pendingは空になるが、これはSatisfiedではない——Desiredも消え、この窓は
    // 「配置対象外」になる（`retryOrGiveUp`の上限到達分岐）。
    Assert.Empty(reducer.State.Placement.Pending)
    Assert.Equal(None, Map.tryFind w reducer.State.Placement.Desired)
    match Map.tryFind w reducer.State.Placement.Movability with
    | Some belief ->
        match belief.Confidence with
        | Confirmed -> failwith "known gap closed? Movability became Confirmed even though Satisfied should never have fired here"
        | _ -> ()
    | None -> failwith "expected a Movability belief to exist (set at discovery)"

    // 論点3-Bの核心：次のReconcile（他窓の変化・UserCommand等、何でもよい）が走れば、Layoutは
    // 同じ目標(workArea)を再計算し、`desiredChanged`判定（`Desired`に無い＝`true`）が同じSetRectを
    // また発行する——この窓は二度とSatisfied/Confirmedに至らないまま、Reconcileが走るたびに同じ
    // 無反応のSetRect→Timeout→Retry→諦めを繰り返す。ここでは2枚目の窓を発見させてReconcileを
    // もう一度起こし、1枚目の窓への同じSetRectが実際に再発行されることだけを確認する
    // （堂々巡りそのものの再現は、無限ループになるためこのテストでは1周だけ確認する）。
    let w2 = WindowId 2L
    let world2 = world |> addWindow w2 { X = 0; Y = 0; Width = 10; Height = 10 } Immediate
    let discovery2 = reducer.Submit 2000L "virtual-world" (visibleWindowObserved w2)
    let reissuedForW =
        discovery2.Effects |> List.exists (fun g -> match GatedEffect.effect g with SetRect (_, w', target) -> w' = w && target = workArea | _ -> false)
    Assert.True(reissuedForW, "expected the already-given-up window's original target to be reissued by the next Reconcile")
    ignore world2
