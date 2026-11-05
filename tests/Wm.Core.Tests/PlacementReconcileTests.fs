/// architecture.md §6.1/§7.1段⑦/§7.2：Core.PlacementのReconcile（Layout呼出→PlanValidator→SetRect発行）
/// とPendingの往復（Satisfied/Timeout/GateRejected）をラウンド1の範囲で検査する。
module Wm.Core.Tests.PlacementReconcileTests

open Xunit
open Wm.Contracts.Identifiers
open Wm.Contracts.ErrorTypes
open Wm.Contracts.Observation
open Wm.Contracts.Beliefs
open Wm.Contracts.SharedVocabulary
open Wm.Contracts.Timers
open Wm.Contracts.CoreCommand
open Wm.Layouts.LayoutRegistry
open Wm.Core.DecisionTrace
open Wm.Core.Placement

let private workArea: PixelRect = { X = 0; Y = 0; Width = 1920; Height = 1080 }

let private registry: Registry =
    Registry.empty
    |> Registry.register (Wm.Layouts.Monocle.implementation "test-monocle")
    |> Registry.register (Wm.Layouts.MasterStack.implementation "test-master-stack")

let private monocleRef: LayoutRef = { Id = LayoutId "monocle"; ApiVersion = 1; Args = NoArgs }
let private masterStackRef (ratio: decimal) : LayoutRef = { Id = LayoutId "master-stack"; ApiVersion = 1; Args = MasterRatio ratio }

// `Monitor`はMonitorId 1L固定（v2.123、FrameInset系の既存テストが`m = MonitorId 1L`で`FrameInsets`を
// 種付けしている前提と揃える——別の値にすると、それらのテストの補正が新設のモニター一致チェックで
// 拒否されてしまう）。
let private ctxWith (tiled: WindowId list) : LayoutContext =
    { WorkArea = workArea; Dpi = 96; TiledWindows = tiled |> List.map (fun w -> { Id = w; MinWidthDip = None }); Focused = None; Monitor = MonitorId 1L }

let private aliveOf (ws: WindowId list) : Set<WindowId> = Set.ofList ws
let private managementOf (ws: WindowId list) : Map<WindowId, EffectiveManagement> = ws |> List.map (fun w -> w, EffTiled) |> Map.ofList

/// `onPendingDeadlineTimerFired`のDPI投影(v2.43)。MinWidth証拠を検査しないテストでは「このモニターの
/// DPIは分からない」を返す——MinWidth証拠が記録されないことを型で保証する(値を変えてしまう嘘のDPIを
/// 渡さない)。
let private noDpi (_: MonitorId) : int option = None

// ===== reconcileWorkspace / dispatchPlacements =====

[<Fact>]
let ``Monocleで1窓をReconcileすると、SetRectがWorkArea全体を指し、Pendingが1件作られる`` () =
    let w = WindowId 1L
    let ctx = ctxWith [ w ]
    let state', effects, timerCmds, _ = reconcileWorkspace 10L registry monocleRef ctx (managementOf [ w ]) (aliveOf [ w ]) initial
    Assert.Equal<Effect list>([ SetRect (RequestId 0L, w, workArea) ], effects)
    Assert.Equal(1, Map.count state'.Pending)
    Assert.NotEmpty(timerCmds)
    Assert.Equal(Some (Shown (Some workArea)), Map.tryFind w state'.Desired)

[<Fact>]
let ``allocateRequestIdは呼ぶたびに異なるRequestIdを返し、dispatchPlacementsの採番元と共有される(v2.27)`` () =
    // RequestIdはEffectCallReturnedで相関する唯一の鍵なので、採番元がPlacement内で2つに割れてはならない
    // （`NextRequestId`の型直前のコメント参照）——`allocateRequestId`（WmState.stepがFocus.SwitchSettleの
    // 再要求やMoveFocusedToWorkspaceのRequestForeground用に呼ぶ窓口）が、後続の`reconcileWorkspace`が
    // 発行するSetRectのRequestIdと衝突しないことを確認する。
    let rid1, stateAfterAlloc1 = allocateRequestId initial
    let rid2, stateAfterAlloc2 = allocateRequestId stateAfterAlloc1
    Assert.NotEqual(rid1, rid2)
    Assert.Equal(RequestId 0L, rid1)
    Assert.Equal(RequestId 1L, rid2)

    let w = WindowId 1L
    let ctx = ctxWith [ w ]
    let _, effects, _, _ = reconcileWorkspace 10L registry monocleRef ctx (managementOf [ w ]) (aliveOf [ w ]) stateAfterAlloc2
    Assert.Equal<Effect list>([ SetRect (RequestId 2L, w, workArea) ], effects)

[<Fact>]
let ``Desiredと同じ結果ならSetRectを再発行しない(§7.3重複要求の禁止)`` () =
    let w = WindowId 1L
    let ctx = ctxWith [ w ]
    let state1, _, _, _ = reconcileWorkspace 10L registry monocleRef ctx (managementOf [ w ]) (aliveOf [ w ]) initial
    let state2, effects2, _, _ = reconcileWorkspace 20L registry monocleRef ctx (managementOf [ w ]) (aliveOf [ w ]) state1
    Assert.Empty(effects2)
    Assert.Equal<Map<RequestId, PendingRequest>>(state1.Pending, state2.Pending)

[<Fact>]
let ``目標が変わったら新しいPendingを発行し、同じ窓宛ての古いPendingを失効させる(Superseded)`` () =
    let w1 = WindowId 1L
    let w2 = WindowId 2L
    let state1, effects1, _, _ = reconcileWorkspace 10L registry monocleRef (ctxWith [ w1 ]) (managementOf [ w1 ]) (aliveOf [ w1 ]) initial
    Assert.Equal<Effect list>([ SetRect (RequestId 0L, w1, workArea) ], effects1)
    // 2窓目が増えた→MasterStackに切り替えてw1の目標が変わる(全画面→左半分)。
    let state2, effects2, _, _ =
        reconcileWorkspace 20L registry (masterStackRef 0.5m) (ctxWith [ w1; w2 ]) (managementOf [ w1; w2 ]) (aliveOf [ w1; w2 ]) state1
    // w1の新しいPendingだけが残り(古いものは置き換わっている)、w2のPendingも追加される。
    Assert.Equal(2, Map.count state2.Pending)
    let w1Pending = state2.Pending |> Map.toSeq |> Seq.filter (fun (_, pr) -> pr.Window = w1) |> Seq.toList
    Assert.Equal(1, List.length w1Pending)
    Assert.NotEqual<Effect list>([], effects2)

[<Fact>]
let ``死亡/未管理の窓だけの違反はRelayoutWithoutで再計算され、結局何も発行しない(かつDecisionTraceも残さない)`` () =
    // TiledWindowsにある窓がAlive/Managementに無い(死亡/未管理)→UnmanagedOrDeadWindowはdecideのremovable
    // (RelayoutWithout)対象なので、その窓を除いて再計算される。残りが無ければ結局は何も発行しない。
    // v2.108：再計算が最終的に成功した場合は、1回目の拒否自体をノイズとしてDecisionTraceに残さない
    // （無害だった拒否まで記録すると、Reconcileするたびに毎回Traceが出るようになってしまう）。
    let w = WindowId 1L
    let ctx = ctxWith [ w ]
    let state', effects, _, traces = reconcileWorkspace 10L registry monocleRef ctx Map.empty Set.empty initial
    Assert.Empty(effects)
    Assert.Empty(state'.Pending)
    Assert.Empty(traces)

[<Fact>]
let ``MissingPlacement(構造的な違反)はRejectWholePlanとして何も発行せず、DecisionTraceにPlanRejectedを残す`` () =
    // w2はManagement/Aliveでは管理下のTiledだが、ctx.TiledWindowsに含まれない(WorkspacesとWindowsの
    // 不整合を模す)。MissingPlacementはdecideのremovable対象外なのでRejectWholePlanになる。
    // v2.108：この「既知のギャップ、Explain未実装」だった経路をDecisionTraceへ記録するようになった。
    let w1, w2 = WindowId 1L, WindowId 2L
    let ctx = ctxWith [ w1 ]
    let state', effects, _, traces = reconcileWorkspace 10L registry monocleRef ctx (managementOf [ w1; w2 ]) (aliveOf [ w1; w2 ]) initial
    Assert.Empty(effects)
    Assert.Empty(state'.Pending)
    match traces with
    | [ Wm.Core.DecisionTrace.PlanRejected (layoutId, errors) ] ->
        Assert.Equal(monocleRef.Id, layoutId)
        Assert.Contains(Wm.Layouts.PlanValidator.MissingPlacement w2, errors)
    | other -> failwithf "expected exactly one PlanRejected trace, got %A" other

[<Fact>]
let ``未登録のLayoutIdを参照するとDecisionTraceにLayoutNotRegisteredを残し、何も発行しない`` () =
    let w = WindowId 1L
    let ctx = ctxWith [ w ]
    let unregistered: LayoutRef = { Id = LayoutId "no-such-layout"; ApiVersion = 1; Args = NoArgs }
    let state', effects, _, traces = reconcileWorkspace 10L registry unregistered ctx (managementOf [ w ]) (aliveOf [ w ]) initial
    Assert.Empty(effects)
    Assert.Empty(state'.Pending)
    Assert.Equal<DecisionTrace list>([ Wm.Core.DecisionTrace.LayoutNotRegistered (unregistered.Id, unregistered.ApiVersion) ], traces)

[<Fact>]
let ``Layout関数自体が失敗(InvalidInput)するとDecisionTraceにLayoutInvocationFailedを残し、何も発行しない`` () =
    // MasterRatioは(0,1)の範囲外だとMasterStack.calculate自身がError(InvalidInput)を返す（既存の
    // ratioOfの契約、Layout関数の失敗であり、PlanValidatorの検証失敗とは別の経路）。
    let w = WindowId 1L
    let ctx = ctxWith [ w ]
    let invalidRatioRef = masterStackRef 1.5m
    let state', effects, _, traces = reconcileWorkspace 10L registry invalidRatioRef ctx (managementOf [ w ]) (aliveOf [ w ]) initial
    Assert.Empty(effects)
    Assert.Empty(state'.Pending)
    match traces with
    | [ Wm.Core.DecisionTrace.LayoutInvocationFailed (layoutId, InvalidInput _) ] -> Assert.Equal(invalidRatioRef.Id, layoutId)
    | other -> failwithf "expected exactly one LayoutInvocationFailed(InvalidInput) trace, got %A" other

// ===== Satisfied（§7.2） =====

let private emptyAppIdentity: AppIdentity = { Exe = None; HostExe = None; Aumid = None; CoreWindowExe = None }

let private observedSnapshot (w: WindowId) (rect: PixelRect) (sequence: int64) : WindowSnapshot =
    { Id = w
      Process = None
      AppIdentity = emptyAppIdentity
      Owner = None
      IsDialog = None
      NativeRect = None
      VisibleFrameRect = Some { Value = Ok rect; SampleSequence = sequence; MonotonicTime = sequence; Source = "test" }
      IsVisible = None
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

let private observedSnapshotWithMonitor (w: WindowId) (rect: PixelRect) (monitor: MonitorId) (sequence: int64) : WindowSnapshot =
    { observedSnapshot w rect sequence with
        Monitor = Some { Value = Ok monitor; SampleSequence = sequence; MonotonicTime = sequence; Source = "test" } }

/// `observedSnapshotWithMonitor`に、`IsMinimized = true`の観測も付け加えたもの。「最小化中」の除外
/// （v2.73、Opusレビューの条件5）をテストするのに使う。
let private observedSnapshotMinimized (w: WindowId) (rect: PixelRect) (monitor: MonitorId) (sequence: int64) : WindowSnapshot =
    { observedSnapshotWithMonitor w rect monitor sequence with
        IsMinimized = Some { Value = Ok true; SampleSequence = sequence; MonotonicTime = sequence; Source = "test" } }

/// `observedSnapshot`に、`IsMaximized = true`の観測を付け加えたもの。`RestoreBeforeMove`の発行
/// （v2.74、§6.2/§7.1）をテストするのに使う。
let private observedSnapshotMaximized (w: WindowId) (rect: PixelRect) (sequence: int64) : WindowSnapshot =
    { observedSnapshot w rect sequence with
        IsMaximized = Some { Value = Ok true; SampleSequence = sequence; MonotonicTime = sequence; Source = "test" } }

/// `observedSnapshotWithMonitor`に、`IsMaximized = true`の観測も付け加えたもの。「最大化」の除外
/// （v2.77）をテストするのに使う——`observedSnapshotMaximized`はMonitorを持たないため、
/// MinWidthのDIP正規化（Monitorの信念からdpiOfを引く）を検査する場面では使えない。
let private observedSnapshotMaximizedWithMonitor (w: WindowId) (rect: PixelRect) (monitor: MonitorId) (sequence: int64) : WindowSnapshot =
    { observedSnapshotWithMonitor w rect monitor sequence with
        IsMaximized = Some { Value = Ok true; SampleSequence = sequence; MonotonicTime = sequence; Source = "test" } }

// ===== RestoreBeforeMove（§6.2/§7.1、v2.74、CIスパイクS6bで実機確認） =====

[<Fact>]
let ``最大化中のTiled窓への新しいSetRectは、同じ直前にRestoreBeforeMoveを別RequestIdで発行する(v2.74)`` () =
    let w = WindowId 1L
    let afterDiscovered, _, _ = onWindowObserved 5L (observedSnapshotMaximized w workArea 1L) initial
    let state', effects, _, _ = reconcileWorkspace 10L registry monocleRef (ctxWith [ w ]) (managementOf [ w ]) (aliveOf [ w ]) afterDiscovered
    match effects with
    | [ RestoreBeforeMove (restoreRid, restoreWindow); SetRect (setRid, setWindow, rect) ] ->
        Assert.Equal(w, restoreWindow)
        Assert.Equal(w, setWindow)
        Assert.NotEqual(restoreRid, setRid)
        Assert.Equal(workArea, rect)
    | other -> Assert.True(false, sprintf "expected [RestoreBeforeMove; SetRect], got %A" other)
    // SetRectのRequestIdだけがPendingに乗る(RestoreBeforeMoveはfire-and-forgetで追跡しない)。
    Assert.Equal(1, Map.count state'.Pending)

[<Fact>]
let ``最大化していないTiled窓への新しいSetRectはRestoreBeforeMoveを伴わない(回帰確認、v2.74)`` () =
    let w = WindowId 1L
    let state', effects, _, _ = reconcileWorkspace 10L registry monocleRef (ctxWith [ w ]) (managementOf [ w ]) (aliveOf [ w ]) initial
    Assert.Equal<Effect list>([ SetRect (RequestId 0L, w, workArea) ], effects)
    Assert.Equal(1, Map.count state'.Pending)

[<Fact>]
let ``最大化状態が観測で解消された後の新しいSetRectはRestoreBeforeMoveを伴わない(v2.74)`` () =
    let w1, w2 = WindowId 1L, WindowId 2L
    let afterDiscovered, _, _ = onWindowObserved 5L (observedSnapshotMaximized w1 workArea 1L) initial
    // 最大化が解消されたという新しい観測(IsMaximized = false)が届く。
    let restoredSnapshot = { observedSnapshot w1 workArea 2L with IsMaximized = Some { Value = Ok false; SampleSequence = 2L; MonotonicTime = 6L; Source = "test" } }
    let afterRestored, _, _ = onWindowObserved 6L restoredSnapshot afterDiscovered
    // w2が増えてLayoutの目標が変わる(全画面→MasterStackの左半分)ので、w1への新しいSetRectが出る。
    let state', effects, _, _ =
        reconcileWorkspace 10L registry (masterStackRef 0.5m) (ctxWith [ w1; w2 ]) (managementOf [ w1; w2 ]) (aliveOf [ w1; w2 ]) afterRestored
    let w1Effects = effects |> List.filter (function RestoreBeforeMove (_, w) -> w = w1 | SetRect (_, w, _) -> w = w1 | _ -> false)
    match w1Effects with
    | [ SetRect _ ] -> ()
    | other -> Assert.True(false, sprintf "expected only a SetRect for w1 (no RestoreBeforeMove), got %A" other)
    Assert.Equal(2, Map.count state'.Pending)

[<Fact>]
let ``Desiredと同じ結果ならSetRectを再発行せず、最大化中でもRestoreBeforeMoveも出さない(§7.3重複要求の禁止、v2.74)`` () =
    let w = WindowId 1L
    let afterDiscovered, _, _ = onWindowObserved 5L (observedSnapshotMaximized w workArea 1L) initial
    let state1, _, _, _ = reconcileWorkspace 10L registry monocleRef (ctxWith [ w ]) (managementOf [ w ]) (aliveOf [ w ]) afterDiscovered
    let state2, effects2, _, _ = reconcileWorkspace 20L registry monocleRef (ctxWith [ w ]) (managementOf [ w ]) (aliveOf [ w ]) state1
    Assert.Empty(effects2)
    Assert.Equal<Map<RequestId, PendingRequest>>(state1.Pending, state2.Pending)

[<Fact>]
let ``観測された矩形がPendingのTargetRectと一致すればSatisfiedになりPendingが消える`` () =
    let w = WindowId 1L
    let afterReconcile, _, _, _ = reconcileWorkspace 10L registry monocleRef (ctxWith [ w ]) (managementOf [ w ]) (aliveOf [ w ]) initial
    Assert.Equal(1, Map.count afterReconcile.Pending)
    let afterObserved, _, timerCmds = onWindowObserved 20L (observedSnapshot w workArea 1L) afterReconcile
    Assert.Empty(afterObserved.Pending)
    Assert.NotEmpty(timerCmds) // CancelTimerが出る

[<Fact>]
let ``Satisfiedになった瞬間、Movabilityの確度はInferred[]からConfirmedへ進む`` () =
    let w = WindowId 1L
    // 発見（onWindowObserved）で楽観的既定値（Movable, Inferred []）が入る。
    let afterDiscovered, _, _ = onWindowObserved 5L (observedSnapshot w { X = 0; Y = 0; Width = 1; Height = 1 } 0L) initial
    match Map.tryFind w afterDiscovered.Movability with
    | Some { Value = Movable; Confidence = Inferred [] } -> ()
    | other -> Assert.True(false, sprintf "expected the discovery default, got %A" other)
    let afterReconcile, _, _, _ = reconcileWorkspace 10L registry monocleRef (ctxWith [ w ]) (managementOf [ w ]) (aliveOf [ w ]) afterDiscovered
    let afterObserved, _, _ = onWindowObserved 20L (observedSnapshot w workArea 1L) afterReconcile
    match Map.tryFind w afterObserved.Movability with
    | Some { Value = Movable; Confidence = Confirmed } -> ()
    | other -> Assert.True(false, sprintf "unexpected: %A" other)

[<Fact>]
let ``観測された矩形がTargetRectと一致しなければPendingは残る(Mismatchはこのラウンドでは静観)`` () =
    let w = WindowId 1L
    let afterReconcile, _, _, _ = reconcileWorkspace 10L registry monocleRef (ctxWith [ w ]) (managementOf [ w ]) (aliveOf [ w ]) initial
    let mismatchedRect = { workArea with Width = 100 }
    let afterObserved, _, _ = onWindowObserved 20L (observedSnapshot w mismatchedRect 1L) afterReconcile
    Assert.Equal(1, Map.count afterObserved.Pending)

// ===== Timeout（§7.2、v2.28でRetry実装） =====

[<Fact>]
let ``PendingDeadline満了は上限に達するまで同じTargetRectへSetRectを再発行し、Desiredは取り消さない`` () =
    let w = WindowId 1L
    let afterReconcile, _, _, _ = reconcileWorkspace 10L registry monocleRef (ctxWith [ w ]) (managementOf [ w ]) (aliveOf [ w ]) initial
    let rid0 = afterReconcile.Pending |> Map.toSeq |> Seq.head |> fst
    let afterRetry1, effects1, timerCmds1, _ = onPendingDeadlineTimerFired (10L + pendingDeadlineMs) rid0 noDpi afterReconcile
    Assert.Equal(1, Map.count afterRetry1.Pending)
    Assert.Equal(Some (Shown (Some workArea)), Map.tryFind w afterRetry1.Desired)
    Assert.Equal(Some 1, Map.tryFind w afterRetry1.RetryAttempts)
    let rid1 = afterRetry1.Pending |> Map.toSeq |> Seq.head |> fst
    Assert.NotEqual(rid0, rid1) // 新しいRequestIdで再発行する
    Assert.Equal<Effect list>([ SetRect (rid1, w, workArea) ], effects1)
    Assert.NotEmpty(timerCmds1) // 新しいPendingDeadlineを始動する

/// v2.119：`dispatchPlacements`と同じ不可視リサイズ枠補正（v2.118）が`retryOrGiveUp`（Timeoutでの
/// 再発行）にも効くことを確認する——Opus敵対的レビューの論点1-A「リトライ経路の漏れ」対策。
[<Fact>]
let ``PendingDeadline満了の再発行(retryOrGiveUp)もFrameInsetで補正し、FrameInsetAppliedを積む(v2.119)`` () =
    let w = WindowId 1L
    let afterReconcile, _, _, _ = reconcileWorkspace 10L registry monocleRef (ctxWith [ w ]) (managementOf [ w ]) (aliveOf [ w ]) initial
    let rid0 = afterReconcile.Pending |> Map.toSeq |> Seq.head |> fst
    // pad2py-pad-host実機（v2.112）で実測した不可視リサイズ枠：X+7/Width-14/Height-7/Y不変。
    // Monitorは`ctxWith`の発行先（MonitorId 1L）と一致させる（v2.123のモニター一致チェック対策）。
    let inset: FrameInset = { Left = 7; Top = 0; Right = 7; Bottom = 7; Monitor = MonitorId 1L }
    let seeded = { afterReconcile with FrameInsets = Map.add w inset afterReconcile.FrameInsets }
    let afterRetry1, effects1, _, traces1 = onPendingDeadlineTimerFired (10L + pendingDeadlineMs) rid0 noDpi seeded
    let rid1 = afterRetry1.Pending |> Map.toSeq |> Seq.head |> fst
    let expectedNativeTarget: PixelRect = { X = workArea.X - 7; Y = workArea.Y; Width = workArea.Width + 14; Height = workArea.Height + 7 }
    Assert.Equal<Effect list>([ SetRect (rid1, w, expectedNativeTarget) ], effects1)
    Assert.Equal<DecisionTrace list>([ FrameInsetApplied (rid1, workArea, expectedNativeTarget) ], traces1)

[<Fact>]
let ``PendingDeadline満了が上限回数続くと、最終的にPendingとDesiredの両方を取り消す(次のReconcileで最初から再試行させる)`` () =
    let w = WindowId 1L
    let afterReconcile, _, _, _ = reconcileWorkspace 10L registry monocleRef (ctxWith [ w ]) (managementOf [ w ]) (aliveOf [ w ]) initial
    let rid0 = afterReconcile.Pending |> Map.toSeq |> Seq.head |> fst
    // pendingRetryMaxAttempts回（既定3回）Timeoutを発火させ続けると、最後は単発の諦めと同じ終端になる。
    let rec driveToGiveUp (attemptsLeft: int) (now: int64) (rid: RequestId) (state: PlacementState) : PlacementState =
        let state', _, _, _ = onPendingDeadlineTimerFired now rid noDpi state
        if attemptsLeft <= 1 then
            state'
        else
            let nextRid = state'.Pending |> Map.toSeq |> Seq.head |> fst
            driveToGiveUp (attemptsLeft - 1) (now + pendingDeadlineMs) nextRid state'
    let final = driveToGiveUp pendingRetryMaxAttempts (10L + pendingDeadlineMs) rid0 afterReconcile
    Assert.Empty(final.Pending)
    Assert.Equal(None, Map.tryFind w final.Desired)
    Assert.Equal(None, Map.tryFind w final.RetryAttempts)

[<Fact>]
let ``新しい意図(Targetが変わったReconcile)はRetryの試行回数を引き継がない`` () =
    let w = WindowId 1L
    let afterReconcile, _, _, _ = reconcileWorkspace 10L registry monocleRef (ctxWith [ w ]) (managementOf [ w ]) (aliveOf [ w ]) initial
    let rid0 = afterReconcile.Pending |> Map.toSeq |> Seq.head |> fst
    let afterRetry, _, _, _ = onPendingDeadlineTimerFired (10L + pendingDeadlineMs) rid0 noDpi afterReconcile
    Assert.Equal(Some 1, Map.tryFind w afterRetry.RetryAttempts)

    // Reconcileのやり直し（例：別のモニターへ移った、Layoutが変わった）で新しいTargetが計算される——
    // これは古いTargetへの再試行ではなく新しい意図なので、試行回数は0へ戻るべき。
    let differentWorkArea: PixelRect = { X = 0; Y = 0; Width = 1280; Height = 720 }
    let differentCtx: LayoutContext = { ctxWith [ w ] with WorkArea = differentWorkArea }
    let afterNewIntent, effects, _, _ = reconcileWorkspace 20L registry monocleRef differentCtx (managementOf [ w ]) (aliveOf [ w ]) afterRetry
    Assert.Equal<Effect list>([ SetRect (RequestId afterRetry.NextRequestId, w, differentWorkArea) ], effects)
    Assert.Equal(None, Map.tryFind w afterNewIntent.RetryAttempts)

[<Fact>]
let ``古い発火(期限未到達)のPendingDeadlineは無視する`` () =
    let w = WindowId 1L
    let afterReconcile, _, _, _ = reconcileWorkspace 10L registry monocleRef (ctxWith [ w ]) (managementOf [ w ]) (aliveOf [ w ]) initial
    let rid = afterReconcile.Pending |> Map.toSeq |> Seq.head |> fst
    let stillPending, effects, timerCmds, _ = onPendingDeadlineTimerFired 10L rid noDpi afterReconcile // Now=IssuedAt、まだdeadline未到達
    Assert.Equal(1, Map.count stillPending.Pending)
    Assert.Empty(effects)
    Assert.Empty(timerCmds)

// ===== TopologySettled → Superseded（§7.2、v2.29） =====

[<Fact>]
let ``onTopologySettledは全PendingをCancelTimer付きで取り消し、RetryAttemptsも消す`` () =
    let w1, w2 = WindowId 1L, WindowId 2L
    let afterReconcile, _, _, _ = reconcileWorkspace 10L registry monocleRef (ctxWith [ w1 ]) (managementOf [ w1 ]) (aliveOf [ w1 ]) initial
    let rid1 = afterReconcile.Pending |> Map.toSeq |> Seq.head |> fst
    let afterRetry, _, _, _ = onPendingDeadlineTimerFired (10L + pendingDeadlineMs) rid1 noDpi afterReconcile
    Assert.Equal(Some 1, Map.tryFind w1 afterRetry.RetryAttempts)

    let afterSecondWindow, _, _, _ = reconcileWorkspace 20L registry (masterStackRef 0.5m) (ctxWith [ w1; w2 ]) (managementOf [ w1; w2 ]) (aliveOf [ w1; w2 ]) afterRetry
    Assert.Equal(2, Map.count afterSecondWindow.Pending)
    let expectedCancelCmds =
        afterSecondWindow.Pending |> Map.toSeq |> Seq.map (fun (rid, _) -> CancelTimer (TimerId.Placement (PendingDeadline rid))) |> List.ofSeq |> List.sort

    let afterSettled, cancelCmds = onTopologySettled afterSecondWindow
    Assert.Empty(afterSettled.Pending)
    Assert.Equal(None, Map.tryFind w1 afterSettled.RetryAttempts)
    Assert.Equal<TimerCommand<TimerId> list>(expectedCancelCmds, cancelCmds |> List.sort)

// ===== WorkspaceHidden/WorkspaceShown → HiddenByWm/Shown None（§4.4、v2.31） =====

[<Fact>]
let ``onWorkspaceHiddenは窓をHiddenByWmにしSetPresence(false)を発行する`` () =
    let w1, w2 = WindowId 1L, WindowId 2L
    let state', effects, _ = onWorkspaceHidden 1L [ w1; w2 ] initial
    Assert.Equal(Some HiddenByWm, Map.tryFind w1 state'.Desired)
    Assert.Equal(Some HiddenByWm, Map.tryFind w2 state'.Desired)
    let targets = effects |> List.choose (function SetPresence (_, w, shown) -> Some (w, shown) | _ -> None) |> List.sort
    Assert.Equal<(WindowId * bool) list>([ (w1, false); (w2, false) ], targets)
    // 2窓分、異なるRequestIdを採番している(§7.1「EffectCallReturnedのRequestIdで相関する」)。
    let rids = effects |> List.choose (function SetPresence (rid, _, _) -> Some rid | _ -> None)
    Assert.Equal(2, rids |> List.distinct |> List.length)

[<Fact>]
let ``onWorkspaceShownは窓をShown NoneにしSetPresence(true)を発行する`` () =
    let w = WindowId 1L
    let state', effects, _ = onWorkspaceShown 1L [ w ] initial
    Assert.Equal(Some (Shown None), Map.tryFind w state'.Desired)
    Assert.Equal<Effect list>([ SetPresence (RequestId 0L, w, true) ], effects)

[<Fact>]
let ``Workspaceが非表示→再表示になった後のReconcileは、Shown Noneを無条件に変化ありと見て新しいSetRectを発行する`` () =
    let w = WindowId 1L
    let afterReconcile, _, _, _ = reconcileWorkspace 10L registry monocleRef (ctxWith [ w ]) (managementOf [ w ]) (aliveOf [ w ]) initial
    Assert.Equal(Some (Shown (Some workArea)), Map.tryFind w afterReconcile.Desired)

    let afterHidden, _, _ = onWorkspaceHidden 20L [ w ] afterReconcile
    let afterShown, _, _ = onWorkspaceShown 25L [ w ] afterHidden
    Assert.Equal(Some (Shown None), Map.tryFind w afterShown.Desired)

    // 同じWorkArea/TiledWindowsでも、Shown Noneから再開すると必ず新しいSetRectが発行される
    // （隠されていた間に実際の位置が変わっていたかもしれないため、古い矩形を信用しない、§4.4）。
    let afterReReconcile, effects, _, _ = reconcileWorkspace 30L registry monocleRef (ctxWith [ w ]) (managementOf [ w ]) (aliveOf [ w ]) afterShown
    Assert.Equal<Effect list>([ SetRect (RequestId afterShown.NextRequestId, w, workArea) ], effects)
    Assert.Equal(Some (Shown (Some workArea)), Map.tryFind w afterReReconcile.Desired)

// ===== SetPresenceの観測による確認（checkPendingPresenceSatisfied、§7.2相当、v2.41） =====

let private presenceSnapshot (w: WindowId) (presence: ObservedPresence) (sequence: int64) : WindowSnapshot =
    { Id = w
      Process = None
      AppIdentity = emptyAppIdentity
      Owner = None
      IsDialog = None
      NativeRect = None
      VisibleFrameRect = None
      IsVisible = None
      IsMinimized = None
      IsMaximized = None
      Style = None
      Cloak = None
      Monitor = None
      MinWidthHint = None
      TargetIntegrity = None
      Responsiveness = None
      Presence = Some { Value = Ok presence; SampleSequence = sequence; MonotonicTime = sequence; Source = "test" }
      DpiAwareness = None
      TitleMatches = None }

[<Fact>]
let ``onWorkspaceHiddenはPendingPresenceを作りPresenceDeadlineタイマーを開始する`` () =
    let w = WindowId 1L
    let state', _, timerCmds = onWorkspaceHidden 10L [ w ] initial
    Assert.Equal(1, Map.count state'.PendingPresence)
    match state'.PendingPresence |> Map.toSeq |> Seq.map snd |> Seq.tryHead with
    | Some pp -> Assert.False(pp.DesiredVisible)
    | None -> Assert.True(false, "expected one PendingPresence entry")
    Assert.NotEmpty(timerCmds)

[<Fact>]
let ``HiddenByWmMethodの観測はonWorkspaceHidden後のPendingPresenceを消しCancelTimerを出す`` () =
    let w = WindowId 1L
    let afterHidden, _, _ = onWorkspaceHidden 10L [ w ] initial
    Assert.Equal(1, Map.count afterHidden.PendingPresence)
    let afterObserved, _, timerCmds = onWindowObserved 20L (presenceSnapshot w HiddenByWmMethod 1L) afterHidden
    Assert.Empty(afterObserved.PendingPresence)
    Assert.NotEmpty(timerCmds) // CancelTimerが出る

[<Fact>]
let ``VisibleToUserの観測はonWorkspaceHidden後のPendingPresenceを消さない(WMの意図が叶っていない)`` () =
    let w = WindowId 1L
    let afterHidden, _, _ = onWorkspaceHidden 10L [ w ] initial
    let afterObserved, _, timerCmds = onWindowObserved 20L (presenceSnapshot w VisibleToUser 1L) afterHidden
    Assert.Equal(1, Map.count afterObserved.PendingPresence)
    Assert.Empty(timerCmds)

[<Fact>]
let ``HiddenOtherの観測はonWorkspaceHidden後のPendingPresenceを消さない(ユーザー等の別経路をWM自身の非表示と誤認しない)`` () =
    let w = WindowId 1L
    let afterHidden, _, _ = onWorkspaceHidden 10L [ w ] initial
    let afterObserved, _, timerCmds = onWindowObserved 20L (presenceSnapshot w HiddenOther 1L) afterHidden
    Assert.Equal(1, Map.count afterObserved.PendingPresence)
    Assert.Empty(timerCmds)

[<Fact>]
let ``VisibleToUserの観測はonWorkspaceShown後のPendingPresenceを消す`` () =
    let w = WindowId 1L
    let afterShown, _, _ = onWorkspaceShown 10L [ w ] initial
    let afterObserved, _, timerCmds = onWindowObserved 20L (presenceSnapshot w VisibleToUser 1L) afterShown
    Assert.Empty(afterObserved.PendingPresence)
    Assert.NotEmpty(timerCmds)

[<Fact>]
let ``隠した直後に再び見せると、古いPendingPresenceは失効(Superseded)し新しい方だけが残る`` () =
    let w = WindowId 1L
    let afterHidden, _, _ = onWorkspaceHidden 10L [ w ] initial
    Assert.Equal(1, Map.count afterHidden.PendingPresence)
    let afterShown, _, cmds = onWorkspaceShown 11L [ w ] afterHidden
    // 1窓につき同時に1つまで（PendingPresenceの型直前のコメント参照）：古いエントリの取消し＋新しいエントリの開始。
    Assert.Equal(1, Map.count afterShown.PendingPresence)
    match afterShown.PendingPresence |> Map.toSeq |> Seq.map snd |> Seq.tryHead with
    | Some pp -> Assert.True(pp.DesiredVisible)
    | None -> Assert.True(false, "expected one PendingPresence entry")
    Assert.Contains(cmds, function CancelTimer (TimerId.Placement (PresenceDeadline _)) -> true | _ -> false)
    Assert.Contains(cmds, function StartTimer (TimerId.Placement (PresenceDeadline _), _) -> true | _ -> false)

[<Fact>]
let ``同じ窓を2回連続で隠しても、古いPendingPresenceは失効(Superseded)し新しい方だけが残る`` () =
    let w = WindowId 1L
    let afterHidden1, _, _ = onWorkspaceHidden 10L [ w ] initial
    let rid1 = afterHidden1.PendingPresence |> Map.toSeq |> Seq.map fst |> Seq.head
    let afterHidden2, _, cmds = onWorkspaceHidden 11L [ w ] afterHidden1
    Assert.Equal(1, Map.count afterHidden2.PendingPresence)
    Assert.DoesNotContain(rid1, afterHidden2.PendingPresence |> Map.toSeq |> Seq.map fst |> List.ofSeq)
    Assert.Contains(cmds, function CancelTimer (TimerId.Placement (PresenceDeadline r)) -> r = rid1 | _ -> false)

[<Fact>]
let ``PresenceDeadline満了は単発で諦め、PendingPresenceだけ消しDesiredは変えない`` () =
    let w = WindowId 1L
    let afterHidden, _, _ = onWorkspaceHidden 10L [ w ] initial
    let rid = afterHidden.PendingPresence |> Map.toSeq |> Seq.map fst |> Seq.head
    let afterTimeout, timerCmds = onPresenceDeadlineTimerFired (10L + pendingDeadlineMs) rid afterHidden
    Assert.Empty(afterTimeout.PendingPresence)
    Assert.Equal(Some HiddenByWm, Map.tryFind w afterTimeout.Desired) // Desiredは取り消さない(型直前のコメント参照)
    Assert.Empty(timerCmds) // 発火した本人のタイマーはもうCancelしない(他のonXxxTimerFiredと同じ)

[<Fact>]
let ``古い発火(期限未到達)のPresenceDeadlineは無視する`` () =
    let w = WindowId 1L
    let afterHidden, _, _ = onWorkspaceHidden 10L [ w ] initial
    let rid = afterHidden.PendingPresence |> Map.toSeq |> Seq.map fst |> Seq.head
    let stillPending, timerCmds = onPresenceDeadlineTimerFired 10L rid afterHidden // Now=IssuedAt、まだdeadline未到達
    Assert.Equal(1, Map.count stillPending.PendingPresence)
    Assert.Empty(timerCmds)

// ===== GateRejected（§7.2） =====

[<Fact>]
let ``onGateRejectedはそのRequestIdのPendingとDesiredを取り消し、CancelTimerを返す`` () =
    let w = WindowId 1L
    let afterReconcile, _, _, _ = reconcileWorkspace 10L registry monocleRef (ctxWith [ w ]) (managementOf [ w ]) (aliveOf [ w ]) initial
    let rid = afterReconcile.Pending |> Map.toSeq |> Seq.head |> fst
    let state', timerCmds = onGateRejected [ rid ] afterReconcile
    Assert.Empty(state'.Pending)
    Assert.Equal(None, Map.tryFind w state'.Desired)
    Assert.NotEmpty(timerCmds)

/// v2.86：`Desired`が`HiddenByWm`な窓宛ての`Pending`(`SetRect`)がゲートで拒否されても、拒否の原因である
/// `HiddenByWm`マーカー自体は取り消さない——`PendingPresence`拒否・`PresenceDeadline`満了（下記）と同じ
/// 「Desiredは取り消さない」規則（`onGateRejected`の型直前のコメント参照）。通常のTiled窓（`Shown _`
/// または`None`）は上記のテストが示すとおり従来どおり取り消す。
[<Fact>]
let ``onGateRejectedは、DesiredがHiddenByWmなら取り消さない(v2.86)`` () =
    let w = WindowId 1L
    let afterReconcile, _, _, _ = reconcileWorkspace 10L registry monocleRef (ctxWith [ w ]) (managementOf [ w ]) (aliveOf [ w ]) initial
    let rid = afterReconcile.Pending |> Map.toSeq |> Seq.head |> fst
    let hidden = { afterReconcile with Desired = Map.add w HiddenByWm afterReconcile.Desired }
    let state', timerCmds = onGateRejected [ rid ] hidden
    Assert.Empty(state'.Pending)
    Assert.Equal(Some HiddenByWm, Map.tryFind w state'.Desired)
    Assert.NotEmpty(timerCmds)

[<Fact>]
let ``onGateRejectedは対応するPendingが無いRequestIdを無害に無視する`` () =
    let state', timerCmds = onGateRejected [ RequestId 999L ] initial
    Assert.Equal(initial, state')
    Assert.Empty(timerCmds)

[<Fact>]
let ``onGateRejectedはPendingPresence宛てのRequestIdも取り消し、CancelTimerを返す(v2.41)`` () =
    let w = WindowId 1L
    let afterHidden, _, _ = onWorkspaceHidden 10L [ w ] initial
    let rid = afterHidden.PendingPresence |> Map.toSeq |> Seq.head |> fst
    let state', timerCmds = onGateRejected [ rid ] afterHidden
    Assert.Empty(state'.PendingPresence)
    Assert.Equal(Some HiddenByWm, Map.tryFind w state'.Desired) // Desiredは取り消さない(型直前のコメント参照)
    Assert.NotEmpty(timerCmds)

// ===== 消滅時クリーンアップ（Pending/Desired/Epochsを含む） =====

[<Fact>]
let ``onWindowVanishedはPending・Desired・Epochsも除き、PendingDeadlineのCancelTimerを出す`` () =
    let w = WindowId 1L
    let afterReconcile, _, _, _ = reconcileWorkspace 10L registry monocleRef (ctxWith [ w ]) (managementOf [ w ]) (aliveOf [ w ]) initial
    Assert.NotEmpty(afterReconcile.Pending)
    let state', timerCmds = onWindowVanished w afterReconcile
    Assert.Empty(state'.Pending)
    Assert.Equal(None, Map.tryFind w state'.Desired)
    Assert.Equal(None, Map.tryFind w state'.Epochs)
    Assert.NotEmpty(timerCmds)

[<Fact>]
let ``onWindowVanishedはPendingPresenceも除き、PresenceDeadlineのCancelTimerを出す(v2.41)`` () =
    let w = WindowId 1L
    let afterHidden, _, _ = onWorkspaceHidden 10L [ w ] initial
    Assert.NotEmpty(afterHidden.PendingPresence)
    let state', timerCmds = onWindowVanished w afterHidden
    Assert.Empty(state'.PendingPresence)
    Assert.Contains(timerCmds, function CancelTimer (TimerId.Placement (PresenceDeadline _)) -> true | _ -> false)

// ===== onEffectCallReturned（§7.2） =====

let private pendingRidFor (state: PlacementState) (w: WindowId) : RequestId =
    state.Pending |> Map.toSeq |> Seq.filter (fun (_, pr) -> pr.Window = w) |> Seq.map fst |> Seq.head

[<Fact>]
let ``Ok()はPendingをSatisfiedにしない(それは観測の一致だけ)が、DispatchSeqは記録する(v2.42)`` () =
    let w = WindowId 1L
    let afterReconcile, _, _, _ = reconcileWorkspace 10L registry monocleRef (ctxWith [ w ]) (managementOf [ w ]) (aliveOf [ w ]) initial
    let rid = pendingRidFor afterReconcile w
    let state', _, _, failureInfo, _ = onEffectCallReturned 20L 2L rid (Ok ()) (Some 42L) afterReconcile
    Assert.Equal(None, failureInfo)
    match Map.tryFind rid state'.Pending with
    | Some pr ->
        Assert.Equal(Some 42L, pr.DispatchSeq)
        // DispatchSeq以外は変わらない(Satisfiedにするのは観測の一致だけ、§7.2)。
        Assert.Equal({ (Map.find rid afterReconcile.Pending) with DispatchSeq = Some 42L }, pr)
    | None -> Assert.True(false, "expected the Pending to still exist")

[<Fact>]
let ``ERROR_INVALID_WINDOW_HANDLEはTargetGoneに分類され、PendingとDesiredを取り消す`` () =
    let w = WindowId 1L
    let afterReconcile, _, _, _ = reconcileWorkspace 10L registry monocleRef (ctxWith [ w ]) (managementOf [ w ]) (aliveOf [ w ]) initial
    let rid = pendingRidFor afterReconcile w
    let error: NativeCallRecord = { Api = "SetWindowPos"; Space = Win32LastError; Code = 1400; ElapsedUs = 0L }
    let state', _, cmds, failureInfo, _ = onEffectCallReturned 20L 2L rid (Error error) None afterReconcile
    // Retryしない分類でも、まだ生きているridのPendingDeadlineはCancelTimerする（v2.30で塞いだ既存の
    // 取りこぼし——以前はこの関数がTimerCommandを返せなかった）。
    Assert.Equal<TimerCommand<TimerId> list>([ CancelTimer (TimerId.Placement (PendingDeadline rid)) ], cmds)
    Assert.Empty(state'.Pending)
    Assert.Equal(None, Map.tryFind w state'.Desired)
    Assert.Equal(Some (w, TargetGone), failureInfo)

[<Fact>]
let ``ERROR_ACCESS_DENIEDはDenied OtherDenialに分類され、MovabilityをNotMovableへ修正する`` () =
    let w = WindowId 1L
    let afterReconcile, _, _, _ = reconcileWorkspace 10L registry monocleRef (ctxWith [ w ]) (managementOf [ w ]) (aliveOf [ w ]) initial
    let rid = pendingRidFor afterReconcile w
    let error: NativeCallRecord = { Api = "SetWindowPos"; Space = Win32LastError; Code = 5; ElapsedUs = 0L }
    let state', _, _, failureInfo, _ = onEffectCallReturned 20L 2L rid (Error error) None afterReconcile
    Assert.Empty(state'.Pending)
    Assert.Equal(Some (w, Denied OtherDenial), failureInfo)
    match Map.tryFind w state'.Movability with
    | Some { Value = NotMovable } -> ()
    | other -> Assert.True(false, sprintf "unexpected: %A" other)

[<Fact>]
let ``TargetHungと分類された場合、Movabilityは変えない(HealthのResponsiveness修正は呼び出し側の責務)`` () =
    let w = WindowId 1L
    let afterReconcile, _, _, _ = reconcileWorkspace 10L registry monocleRef (ctxWith [ w ]) (managementOf [ w ]) (aliveOf [ w ]) initial
    let rid = pendingRidFor afterReconcile w
    let error: NativeCallRecord = { Api = "SendMessageTimeout"; Space = Win32LastError; Code = 1460; ElapsedUs = 0L }
    let state', _, _, failureInfo, _ = onEffectCallReturned 20L 2L rid (Error error) None afterReconcile
    Assert.Empty(state'.Pending)
    Assert.Equal(Some (w, TargetHung), failureInfo)
    Assert.Equal(None, Map.tryFind w state'.Movability) // Movabilityはこの関数の対象外

[<Fact>]
let ``対応するPendingが無いRequestId(古い結果)は無害に無視する`` () =
    let error: NativeCallRecord = { Api = "SetWindowPos"; Space = Win32LastError; Code = 1400; ElapsedUs = 0L }
    let state', _, _, failureInfo, _ = onEffectCallReturned 20L 2L (RequestId 999L) (Error error) None initial
    Assert.Equal(initial, state')
    Assert.Equal(None, failureInfo)

[<Fact>]
let ``Transient(ハング判定前のERROR_TIMEOUT)は即座に再試行し、Desiredは取り消さない(v2.30)`` () =
    let w = WindowId 1L
    let afterReconcile, _, _, _ = reconcileWorkspace 10L registry monocleRef (ctxWith [ w ]) (managementOf [ w ]) (aliveOf [ w ]) initial
    let rid0 = pendingRidFor afterReconcile w
    let error: NativeCallRecord = { Api = "SendMessageTimeout"; Space = Win32LastError; Code = 1460; ElapsedUs = 500L } // >0なのでTransient
    let afterRetry, effects, cmds, failureInfo, _ = onEffectCallReturned 20L 2L rid0 (Error error) None afterReconcile
    Assert.Equal(Some (w, Transient), failureInfo)
    Assert.Equal(1, Map.count afterRetry.Pending)
    let rid1 = pendingRidFor afterRetry w
    Assert.NotEqual(rid0, rid1)
    Assert.Equal(Some (Shown (Some workArea)), Map.tryFind w afterRetry.Desired)
    Assert.Equal(Some 1, Map.tryFind w afterRetry.RetryAttempts)
    Assert.Equal<Effect list>([ SetRect (rid1, w, workArea) ], effects)
    // 古いridのPendingDeadlineを取り消し、新しいridのPendingDeadlineを始動する(どちらも必要)。
    Assert.Contains(CancelTimer (TimerId.Placement (PendingDeadline rid0)), cmds)
    Assert.Contains(StartTimer (TimerId.Placement (PendingDeadline rid1), pendingDeadlineMs), cmds)

[<Fact>]
let ``Transientが上限回数続くと、最終的にPendingとDesiredを取り消し古いPendingDeadlineもCancelTimerする(v2.30)`` () =
    let w = WindowId 1L
    let afterReconcile, _, _, _ = reconcileWorkspace 10L registry monocleRef (ctxWith [ w ]) (managementOf [ w ]) (aliveOf [ w ]) initial
    let error: NativeCallRecord = { Api = "SendMessageTimeout"; Space = Win32LastError; Code = 1460; ElapsedUs = 500L }
    let rec driveToGiveUp (attemptsLeft: int) (state: PlacementState) : PlacementState * TimerCommand<TimerId> list =
        let rid = pendingRidFor state w
        let state', _, cmds, _, _ = onEffectCallReturned 20L 2L rid (Error error) None state
        if attemptsLeft <= 1 then state', cmds else driveToGiveUp (attemptsLeft - 1) state'
    let final, lastCmds = driveToGiveUp pendingRetryMaxAttempts afterReconcile
    Assert.Empty(final.Pending)
    Assert.Equal(None, Map.tryFind w final.Desired)
    Assert.Equal(None, Map.tryFind w final.RetryAttempts)
    Assert.NotEmpty(lastCmds) // 最後の諦めでも、まだ生きている古いPendingDeadlineはCancelTimerする

// ===== Mismatch → Reevaluate（§7.2、v2.42。Opus敵対的レビューで設計、Reevaluate=「アプリと戦わない」
// はオーナー確認済み） =====

[<Fact>]
let ``本当に異なる矩形が発行後に観測されたら、PendingDeadline時にMismatchとして終端し、Retryせず、Desiredは残す(v2.42)`` () =
    let w = WindowId 1L
    let afterReconcile, _, _, _ = reconcileWorkspace 10L registry monocleRef (ctxWith [ w ]) (managementOf [ w ]) (aliveOf [ w ]) initial
    let rid = pendingRidFor afterReconcile w
    let afterDispatched, _, _, _, _ = onEffectCallReturned 11L 2L rid (Ok ()) (Some 5L) afterReconcile
    let mismatchedRect = { workArea with X = workArea.X + 50 }
    let afterObserved, _, _ = onWindowObserved 20L (observedSnapshot w mismatchedRect 10L) afterDispatched
    let afterDeadline, effects, _, _ = onPendingDeadlineTimerFired (10L + pendingDeadlineMs) rid noDpi afterObserved
    Assert.Empty(afterDeadline.Pending)
    Assert.Empty(effects) // Retryしない(同じTargetへSetRectを再発行しない)
    Assert.Equal(Some (Shown (Some workArea)), Map.tryFind w afterDeadline.Desired) // Desiredは目標のまま残す
    Assert.Equal(None, Map.tryFind w afterDeadline.RetryAttempts)

[<Fact>]
let ``DispatchSeqが未確認(EffectCallReturned Okがまだ届いていない)間は、異なる矩形を観測してもMismatchと判定せずTimeout/Retryのまま(v2.42)`` () =
    let w = WindowId 1L
    let afterReconcile, _, _, _ = reconcileWorkspace 10L registry monocleRef (ctxWith [ w ]) (managementOf [ w ]) (aliveOf [ w ]) initial
    let rid = pendingRidFor afterReconcile w
    let differentRect = { workArea with X = workArea.X + 50 }
    let afterObserved, _, _ = onWindowObserved 20L (observedSnapshot w differentRect 10L) afterReconcile
    let afterDeadline, effects, _, _ = onPendingDeadlineTimerFired (10L + pendingDeadlineMs) rid noDpi afterObserved
    Assert.Equal(1, Map.count afterDeadline.Pending)
    Assert.NotEmpty(effects)

[<Fact>]
let ``DispatchSeqより古い(発行前に取得された)観測はMismatch判定に使わない、Timeout/Retryのまま(v2.42)`` () =
    let w = WindowId 1L
    let afterReconcile, _, _, _ = reconcileWorkspace 10L registry monocleRef (ctxWith [ w ]) (managementOf [ w ]) (aliveOf [ w ]) initial
    let rid = pendingRidFor afterReconcile w
    let afterDispatched, _, _, _, _ = onEffectCallReturned 11L 2L rid (Ok ()) (Some 10L) afterReconcile
    let staleRect = { workArea with X = workArea.X + 999 }
    let afterObserved, _, _ = onWindowObserved 20L (observedSnapshot w staleRect 5L) afterDispatched // 5 < DispatchSeq(10)
    let afterDeadline, effects, _, _ = onPendingDeadlineTimerFired (10L + pendingDeadlineMs) rid noDpi afterObserved
    Assert.Equal(1, Map.count afterDeadline.Pending)
    Assert.NotEmpty(effects)

[<Fact>]
let ``発行後も観測がBaselineのまま変わらなければMismatchにしない(要求への無反応はTimeout/Retryのまま、オーナー確認済み)(v2.42)`` () =
    let w = WindowId 1L
    let baselineRect = { X = 10; Y = 10; Width = 300; Height = 200 }
    let afterDiscovered, _, _ = onWindowObserved 1L (observedSnapshot w baselineRect 0L) initial
    let afterReconcile, _, _, _ = reconcileWorkspace 10L registry monocleRef (ctxWith [ w ]) (managementOf [ w ]) (aliveOf [ w ]) afterDiscovered
    let rid = pendingRidFor afterReconcile w
    match Map.tryFind rid afterReconcile.Pending with
    | Some pr -> Assert.Equal(Some baselineRect, pr.Baseline)
    | None -> Assert.True(false, "expected a pending")
    let afterDispatched, _, _, _, _ = onEffectCallReturned 11L 2L rid (Ok ()) (Some 5L) afterReconcile
    let afterObserved, _, _ = onWindowObserved 20L (observedSnapshot w baselineRect 10L) afterDispatched // 発行後だが変化無し
    let afterDeadline, effects, _, _ = onPendingDeadlineTimerFired (10L + pendingDeadlineMs) rid noDpi afterObserved
    Assert.Equal(1, Map.count afterDeadline.Pending)
    Assert.NotEmpty(effects)

[<Fact>]
let ``supersedeされた旧Epochの目標が遅れて観測されても、新しいPendingのMismatchとして扱わない(v2.42)`` () =
    let w = WindowId 1L
    let afterReconcile1, _, _, _ = reconcileWorkspace 10L registry monocleRef (ctxWith [ w ]) (managementOf [ w ]) (aliveOf [ w ]) initial
    let rid0 = pendingRidFor afterReconcile1 w
    let oldTarget = workArea
    let afterDispatched0, _, _, _, _ = onEffectCallReturned 11L 2L rid0 (Ok ()) (Some 5L) afterReconcile1
    let narrowerCtx = { ctxWith [ w ] with WorkArea = { workArea with Width = workArea.Width - 100 } }
    let afterReconcile2, _, _, _ = reconcileWorkspace 15L registry monocleRef narrowerCtx (managementOf [ w ]) (aliveOf [ w ]) afterDispatched0
    let rid1 = pendingRidFor afterReconcile2 w
    Assert.NotEqual(rid0, rid1)
    match Map.tryFind w afterReconcile2.InFlightTargets with
    | Some targets -> Assert.Contains((IntentEpoch 0L, oldTarget), targets)
    | None -> Assert.True(false, "expected the superseded target to be tracked as in-flight")
    let afterDispatched1, _, _, _, _ = onEffectCallReturned 16L 2L rid1 (Ok ()) (Some 20L) afterReconcile2
    // 旧rid0のSetRectが遅れて適用され、旧Targetがそのまま観測される(新rid1のDispatchSeq(20)より新しい)。
    let afterObserved, _, _ = onWindowObserved 30L (observedSnapshot w oldTarget 25L) afterDispatched1
    let afterDeadline, effects, _, _ = onPendingDeadlineTimerFired (15L + pendingDeadlineMs) rid1 noDpi afterObserved
    Assert.Equal(1, Map.count afterDeadline.Pending) // Mismatchではない(旧Epochの目標として除外)、Timeout/Retryのまま
    Assert.NotEmpty(effects)

[<Fact>]
let ``onTopologySettledはInFlightTargetsも消す(v2.42)`` () =
    let w = WindowId 1L
    let afterReconcile1, _, _, _ = reconcileWorkspace 10L registry monocleRef (ctxWith [ w ]) (managementOf [ w ]) (aliveOf [ w ]) initial
    let narrowerCtx = { ctxWith [ w ] with WorkArea = { workArea with Width = workArea.Width - 100 } }
    let afterReconcile2, _, _, _ = reconcileWorkspace 15L registry monocleRef narrowerCtx (managementOf [ w ]) (aliveOf [ w ]) afterReconcile1
    Assert.NotEmpty(Map.tryFind w afterReconcile2.InFlightTargets |> Option.defaultValue [])
    let afterSettled, _ = onTopologySettled afterReconcile2
    Assert.Equal(None, Map.tryFind w afterSettled.InFlightTargets)

[<Fact>]
let ``onWindowVanishedはInFlightTargetsも消す(v2.42)`` () =
    let w = WindowId 1L
    let afterReconcile1, _, _, _ = reconcileWorkspace 10L registry monocleRef (ctxWith [ w ]) (managementOf [ w ]) (aliveOf [ w ]) initial
    let narrowerCtx = { ctxWith [ w ] with WorkArea = { workArea with Width = workArea.Width - 100 } }
    let afterReconcile2, _, _, _ = reconcileWorkspace 15L registry monocleRef narrowerCtx (managementOf [ w ]) (aliveOf [ w ]) afterReconcile1
    Assert.NotEmpty(Map.tryFind w afterReconcile2.InFlightTargets |> Option.defaultValue [])
    let afterVanished, _ = onWindowVanished w afterReconcile2
    Assert.Equal(None, Map.tryFind w afterVanished.InFlightTargets)

[<Fact>]
let ``Satisfied(checkPendingSatisfied)もInFlightTargetsを消す(v2.69、Opus敵対的レビューopus-review-unattributed-movecause.md §B-4)`` () =
    // 型直前のコメントは「該当の矩形が観測された時…消す」と書いていたが、実際に消していたのは
    // 消滅とTopologySettledだけで、Satisfiedでは一度も消していなかった既知のギャップ。消さないと、
    // Layoutが数種類の矩形を往復するだけの窓（MasterStackで窓数が変わるたびに同じ矩形を行き来する等）
    // では無限に積み重なり、それらの矩形が将来Mismatchの証拠として検出されなくなる。
    let w = WindowId 1L
    let afterReconcile1, _, _, _ = reconcileWorkspace 10L registry monocleRef (ctxWith [ w ]) (managementOf [ w ]) (aliveOf [ w ]) initial
    let narrowerCtx = { ctxWith [ w ] with WorkArea = { workArea with Width = workArea.Width - 100 } }
    let afterReconcile2, _, _, _ = reconcileWorkspace 15L registry monocleRef narrowerCtx (managementOf [ w ]) (aliveOf [ w ]) afterReconcile1
    Assert.NotEmpty(Map.tryFind w afterReconcile2.InFlightTargets |> Option.defaultValue [])
    // 新しい(2回目の)Targetと一致する観測が届き、Satisfiedになる。
    let rid2 = pendingRidFor afterReconcile2 w
    let newTarget = (Map.find rid2 afterReconcile2.Pending).TargetRect
    let afterObserved, _, _ = onWindowObserved 20L (observedSnapshot w newTarget 1L) afterReconcile2
    Assert.Empty(afterObserved.Pending)
    Assert.Equal(None, Map.tryFind w afterObserved.InFlightTargets)

// ===== MinWidth（Accumulating、§4.5、v2.43。Mismatchの証拠をDIP正規化して蓄積する） =====

[<Fact>]
let ``幅が目標より広い矩形が観測されると、DIP正規化してMinWidthの証拠にする(v2.43)`` () =
    let w = WindowId 1L
    let m = MonitorId 1L
    let afterReconcile, _, _, _ = reconcileWorkspace 10L registry monocleRef (ctxWith [ w ]) (managementOf [ w ]) (aliveOf [ w ]) initial
    let rid = pendingRidFor afterReconcile w
    let afterDispatched, _, _, _, _ = onEffectCallReturned 11L 2L rid (Ok ()) (Some 5L) afterReconcile
    let widerRect = { workArea with Width = 2000 } // 要求(workArea.Width=1920)より広い=アプリの最小幅制約
    let afterObserved, _, _ = onWindowObserved 20L (observedSnapshotWithMonitor w widerRect m 10L) afterDispatched
    let dpiOf (mid: MonitorId) = if mid = m then Some 192 else None // 200%
    let afterDeadline, _, _, _ = onPendingDeadlineTimerFired (10L + pendingDeadlineMs) rid dpiOf afterObserved
    match Map.tryFind w afterDeadline.MinWidth with
    | Some { Value = v; Confidence = Inferred _ } -> Assert.Equal(2000 * 96 / 192, v) // DIPへ正規化(200%なので半分)
    | other -> Assert.True(false, sprintf "expected an Inferred MinWidth belief, got %A" other)

[<Fact>]
let ``幅が目標より広くない観測はMismatchとして終端してもMinWidthの証拠にしない(v2.43)`` () =
    let w = WindowId 1L
    let m = MonitorId 1L
    let afterReconcile, _, _, _ = reconcileWorkspace 10L registry monocleRef (ctxWith [ w ]) (managementOf [ w ]) (aliveOf [ w ]) initial
    let rid = pendingRidFor afterReconcile w
    let afterDispatched, _, _, _, _ = onEffectCallReturned 11L 2L rid (Ok ()) (Some 5L) afterReconcile
    // 幅は要求と同じだがX位置が違う：Mismatchにはなるが、幅は広がっていないのでMinWidthの証拠ではない。
    let shiftedRect = { workArea with X = workArea.X + 50 }
    let afterObserved, _, _ = onWindowObserved 20L (observedSnapshotWithMonitor w shiftedRect m 10L) afterDispatched
    let dpiOf (mid: MonitorId) = if mid = m then Some 96 else None
    let afterDeadline, _, _, _ = onPendingDeadlineTimerFired (10L + pendingDeadlineMs) rid dpiOf afterObserved
    Assert.Empty(afterDeadline.Pending) // Mismatchとして終端はする
    Assert.Equal(None, Map.tryFind w afterDeadline.MinWidth) // だがMinWidthの証拠にはしない

[<Fact>]
let ``モニターのDPIが分からない場合は、幅が広い観測でもMinWidthの証拠にしない(v2.43)`` () =
    let w = WindowId 1L
    let m = MonitorId 1L
    let afterReconcile, _, _, _ = reconcileWorkspace 10L registry monocleRef (ctxWith [ w ]) (managementOf [ w ]) (aliveOf [ w ]) initial
    let rid = pendingRidFor afterReconcile w
    let afterDispatched, _, _, _, _ = onEffectCallReturned 11L 2L rid (Ok ()) (Some 5L) afterReconcile
    let widerRect = { workArea with Width = 2000 }
    let afterObserved, _, _ = onWindowObserved 20L (observedSnapshotWithMonitor w widerRect m 10L) afterDispatched
    let afterDeadline, _, _, _ = onPendingDeadlineTimerFired (10L + pendingDeadlineMs) rid noDpi afterObserved // dpiOfが常にNone
    Assert.Empty(afterDeadline.Pending)
    Assert.Equal(None, Map.tryFind w afterDeadline.MinWidth)

[<Fact>]
let ``同じ最小幅が繰り返し観測されると、値は変えず根拠(Inferred list)を追加する(v2.43)`` () =
    let w = WindowId 1L
    let m = MonitorId 1L
    let dpiOf (mid: MonitorId) = if mid = m then Some 96 else None
    let widerRect = { workArea with Width = 2000 } // 96dpiなのでDIP=物理px、常にこの幅を観測する(固定の最小幅)

    let afterReconcile1, _, _, _ = reconcileWorkspace 10L registry monocleRef (ctxWith [ w ]) (managementOf [ w ]) (aliveOf [ w ]) initial
    let rid1 = pendingRidFor afterReconcile1 w
    let afterDispatched1, _, _, _, _ = onEffectCallReturned 11L 2L rid1 (Ok ()) (Some 5L) afterReconcile1
    let afterObserved1, _, _ = onWindowObserved 20L (observedSnapshotWithMonitor w widerRect m 10L) afterDispatched1
    let afterDeadline1, _, _, _ = onPendingDeadlineTimerFired (10L + pendingDeadlineMs) rid1 dpiOf afterObserved1
    let refsAfterFirst =
        match Map.tryFind w afterDeadline1.MinWidth with
        | Some { Confidence = Inferred refs } -> refs
        | other ->
            Assert.True(false, sprintf "expected an Inferred MinWidth belief after the first evidence, got %A" other)
            []
    Assert.Equal(1, List.length refsAfterFirst)

    // 2回目のサイクルの前に、別の矩形が一度観測されたことにする(ユーザー操作等で実際に動いた、
    // という想定)——そうしないと2回目のBaselineが1回目のwiderRectのまま(前回のMismatch確定時点の
    // 最新観測)になり、2回目の「widerRectを再度観測」が「何も変わっていない」(Baseline一致)と
    // 判定されてMismatchにならない(これは`onPendingDeadlineTimerFired`型直前のコメント・条件3が
    // 意図的にそう作っているため正しい挙動——オーナー確認済みの「無反応はMismatchにしない」规则)。
    let intermediateRect = { workArea with Width = workArea.Width - 50 }
    let afterIntermediate, _, _ = onWindowObserved (10L + pendingDeadlineMs + 1L) (observedSnapshotWithMonitor w intermediateRect m 12L) afterDeadline1

    // 別のTargetへ切り替える(同じ窓を2回目のMismatchサイクルに乗せるため)が、観測される実際の幅は
    // 1回目と同じ(=アプリの最小幅が変わっていないことを模す)。
    let narrowerCtx = { ctxWith [ w ] with WorkArea = { workArea with Width = workArea.Width - 100 } }
    let afterReconcile2, _, _, _ = reconcileWorkspace 15L registry monocleRef narrowerCtx (managementOf [ w ]) (aliveOf [ w ]) afterIntermediate
    let rid2 = pendingRidFor afterReconcile2 w
    let afterDispatched2, _, _, _, _ = onEffectCallReturned 16L 2L rid2 (Ok ()) (Some 20L) afterReconcile2
    let afterObserved2, _, _ = onWindowObserved 30L (observedSnapshotWithMonitor w widerRect m 25L) afterDispatched2
    let afterDeadline2, _, _, _ = onPendingDeadlineTimerFired (15L + pendingDeadlineMs) rid2 dpiOf afterObserved2
    match Map.tryFind w afterDeadline2.MinWidth with
    | Some { Value = v; Confidence = Inferred refs } ->
        Assert.Equal(2000, v) // 値は変わらない
        Assert.Equal(2, List.length refs) // 根拠が追加された(1件→2件)
    | other -> Assert.True(false, sprintf "expected an accumulated Inferred MinWidth belief, got %A" other)

[<Fact>]
let ``onWindowVanishedはMinWidthも消す(v2.43)`` () =
    let w = WindowId 1L
    let m = MonitorId 1L
    let afterReconcile, _, _, _ = reconcileWorkspace 10L registry monocleRef (ctxWith [ w ]) (managementOf [ w ]) (aliveOf [ w ]) initial
    let rid = pendingRidFor afterReconcile w
    let afterDispatched, _, _, _, _ = onEffectCallReturned 11L 2L rid (Ok ()) (Some 5L) afterReconcile
    let widerRect = { workArea with Width = 2000 }
    let afterObserved, _, _ = onWindowObserved 20L (observedSnapshotWithMonitor w widerRect m 10L) afterDispatched
    let dpiOf (mid: MonitorId) = if mid = m then Some 96 else None
    let afterDeadline, _, _, _ = onPendingDeadlineTimerFired (10L + pendingDeadlineMs) rid dpiOf afterObserved
    Assert.NotEmpty(Map.tryFind w afterDeadline.MinWidth |> Option.toList)
    let afterVanished, _ = onWindowVanished w afterDeadline
    Assert.Equal(None, Map.tryFind w afterVanished.MinWidth)

// ===== MinWidthの過補正ガード（v2.120、Opusレビュー論点2-C対策） =====

[<Fact>]
let ``FrameInset補正済みの発行で、幅超過がinsetの左右合計以内ならMinWidthの証拠にしない(v2.120)`` () =
    let w = WindowId 1L
    let m = MonitorId 1L
    // pad2py-pad-host実機（v2.112）で実測した不可視リサイズ枠：X+7/Width-14/Height-7/Y不変。
    let inset: FrameInset = { Left = 7; Top = 0; Right = 7; Bottom = 7; Monitor = m }
    let seeded = { initial with FrameInsets = Map.ofList [ w, inset ] }
    let afterReconcile, _, _, _ = reconcileWorkspace 10L registry monocleRef (ctxWith [ w ]) (managementOf [ w ]) (aliveOf [ w ]) seeded
    let rid = pendingRidFor afterReconcile w
    Assert.Equal(Some inset, Map.tryFind rid afterReconcile.Pending |> Option.bind (fun pr -> pr.DispatchedInset))
    let afterDispatched, _, _, _, _ = onEffectCallReturned 11L 2L rid (Ok ()) (Some 5L) afterReconcile
    // 超過13px（左右合計14pxの枠の範囲内）——補正の誤差として説明できるので証拠にしない。
    let slightlyWiderRect = { workArea with Width = workArea.Width + 13 }
    let afterObserved, _, _ = onWindowObserved 20L (observedSnapshotWithMonitor w slightlyWiderRect m 10L) afterDispatched
    let dpiOf (mid: MonitorId) = if mid = m then Some 96 else None
    let afterDeadline, _, _, _ = onPendingDeadlineTimerFired (10L + pendingDeadlineMs) rid dpiOf afterObserved
    Assert.Equal(None, Map.tryFind w afterDeadline.MinWidth)

[<Fact>]
let ``FrameInset補正済みの発行でも、幅超過がinsetの左右合計を超えればMinWidthの証拠にする(v2.120)`` () =
    let w = WindowId 1L
    let m = MonitorId 1L
    let inset: FrameInset = { Left = 7; Top = 0; Right = 7; Bottom = 7; Monitor = m }
    let seeded = { initial with FrameInsets = Map.ofList [ w, inset ] }
    let afterReconcile, _, _, _ = reconcileWorkspace 10L registry monocleRef (ctxWith [ w ]) (managementOf [ w ]) (aliveOf [ w ]) seeded
    let rid = pendingRidFor afterReconcile w
    let afterDispatched, _, _, _, _ = onEffectCallReturned 11L 2L rid (Ok ()) (Some 5L) afterReconcile
    // 超過100px（左右合計14pxの枠を大きく超える）——補正誤差では説明できない、本物のMinWidth証拠。
    let muchWiderRect = { workArea with Width = workArea.Width + 100 }
    let afterObserved, _, _ = onWindowObserved 20L (observedSnapshotWithMonitor w muchWiderRect m 10L) afterDispatched
    let dpiOf (mid: MonitorId) = if mid = m then Some 96 else None
    let afterDeadline, _, _, _ = onPendingDeadlineTimerFired (10L + pendingDeadlineMs) rid dpiOf afterObserved
    match Map.tryFind w afterDeadline.MinWidth with
    | Some { Value = v; Confidence = Inferred _ } -> Assert.Equal(workArea.Width + 100, v)
    | other -> Assert.True(false, sprintf "expected an Inferred MinWidth belief, got %A" other)

// ===== FrameInset自己修復（v2.121、Opus敵対的レビューopus-review-frameinset-selfheal.md論点1-C対策） =====

/// `observedSnapshotWithMonitor`に、`NativeRect`も付け加えたもの。`onWindowObserved`の`FrameInsets`
/// 更新（`reviseFrameInset`、v2.117）はNativeRect/VisibleFrameRectが両方Okの観測だけを見るため、
/// 自己修復の契機（inset学習）を試験するにはこの対が要る。
let private observedPairSnapshotWithMonitor (w: WindowId) (native: PixelRect) (visible: PixelRect) (monitor: MonitorId) (sequence: int64) : WindowSnapshot =
    { observedSnapshotWithMonitor w visible monitor sequence with
        NativeRect = Some { Value = Ok native; SampleSequence = sequence; MonotonicTime = sequence; Source = "test" } }

[<Fact>]
let ``新規窓への無補正の最初の発行後にinsetが学習されると、PendingDeadline時に自己修復として今のinsetで再発行する(v2.121)`` () =
    let w = WindowId 1L
    let m = MonitorId 1L
    let inset: FrameInset = { Left = 7; Top = 0; Right = 7; Bottom = 7; Monitor = m }
    // FrameInsets信念がまだ無いので、最初の発行は無補正(DispatchedInset=None、発行先=TargetRectそのまま)。
    let afterReconcile, _, _, _ = reconcileWorkspace 10L registry monocleRef (ctxWith [ w ]) (managementOf [ w ]) (aliveOf [ w ]) initial
    let rid = pendingRidFor afterReconcile w
    Assert.Equal(None, Map.tryFind rid afterReconcile.Pending |> Option.bind (fun pr -> pr.DispatchedInset))
    let afterDispatched, _, _, _, _ = onEffectCallReturned 11L 2L rid (Ok ()) (Some 5L) afterReconcile
    // 実機(S26/S27)のとおりアプリは頼んだネイティブ矩形(=補正無しのworkArea)をそのまま受け入れたが、
    // 可視矩形はDWMの不可視リサイズ枠だけずれる——同じ観測がinsetそのものも学習させる。
    let nativeObserved = workArea
    let visibleObserved =
        { X = workArea.X + inset.Left
          Y = workArea.Y + inset.Top
          Width = workArea.Width - inset.Left - inset.Right
          Height = workArea.Height - inset.Top - inset.Bottom }
    let afterObserved, _, _ = onWindowObserved 20L (observedPairSnapshotWithMonitor w nativeObserved visibleObserved m 10L) afterDispatched
    Assert.Equal(Some inset, Map.tryFind w afterObserved.FrameInsets)
    Assert.Equal(1, Map.count afterObserved.Pending) // 可視矩形はTargetRectと一致しないのでまだPending
    let afterDeadline, effects, timerCmds, traces = onPendingDeadlineTimerFired (10L + pendingDeadlineMs) rid noDpi afterObserved
    // 通常のReevaluateはしない(Acceptedを書かない、MinWidthの証拠も採らない)。
    Assert.Equal(None, Map.tryFind w afterDeadline.Accepted)
    Assert.Equal(None, Map.tryFind w afterDeadline.MinWidth)
    Assert.Equal(1, Map.count afterDeadline.Pending) // 旧ridは消え、新しいridが1件
    let newRid = pendingRidFor afterDeadline w
    Assert.NotEqual(rid, newRid)
    match Map.tryFind newRid afterDeadline.Pending with
    | Some pr' ->
        Assert.Equal(workArea, pr'.TargetRect) // 望ましい可視矩形は変わらない
        Assert.Equal(Some inset, pr'.DispatchedInset)
        Assert.True(pr'.InsetHealed)
        Assert.Equal(None, pr'.Baseline) // Baselineは引き継ぐ(旧prのBaseline=None)、observedへ更新しない
    | None -> Assert.True(false, "expected a new Pending for the healed re-dispatch")
    let expectedDispatchTarget =
        { X = workArea.X - inset.Left
          Y = workArea.Y - inset.Top
          Width = workArea.Width + inset.Left + inset.Right
          Height = workArea.Height + inset.Top + inset.Bottom }
    Assert.Equal<Effect list>([ SetRect (newRid, w, expectedDispatchTarget) ], effects)
    Assert.NotEmpty(timerCmds) // 新ridのPendingDeadlineを再始動する
    match traces with
    | [ FrameInsetApplied (tracedRid, desired, dispatched) ] ->
        Assert.Equal(newRid, tracedRid)
        Assert.Equal(workArea, desired)
        Assert.Equal(expectedDispatchTarget, dispatched)
    | other -> Assert.True(false, sprintf "expected exactly one FrameInsetApplied trace, got %A" other)

[<Fact>]
let ``FrameInset自己修復は1つのEpochにつき1回まで、2回目はinsetがさらに変わっても通常のReevaluateに落ちる(v2.121)`` () =
    let w = WindowId 1L
    let m = MonitorId 1L
    let inset1: FrameInset = { Left = 7; Top = 0; Right = 7; Bottom = 7; Monitor = m }
    let afterReconcile, _, _, _ = reconcileWorkspace 10L registry monocleRef (ctxWith [ w ]) (managementOf [ w ]) (aliveOf [ w ]) initial
    let rid = pendingRidFor afterReconcile w
    let afterDispatched, _, _, _, _ = onEffectCallReturned 11L 2L rid (Ok ()) (Some 5L) afterReconcile
    let visibleObserved1 =
        { X = workArea.X + inset1.Left
          Y = workArea.Y + inset1.Top
          Width = workArea.Width - inset1.Left - inset1.Right
          Height = workArea.Height - inset1.Top - inset1.Bottom }
    let afterObserved, _, _ = onWindowObserved 20L (observedPairSnapshotWithMonitor w workArea visibleObserved1 m 10L) afterDispatched
    let afterHeal, healEffects, _, _ = onPendingDeadlineTimerFired (10L + pendingDeadlineMs) rid noDpi afterObserved
    Assert.NotEmpty(healEffects) // 1回目は自己修復として再発行する(前のテストで検査済み)
    let newRid = pendingRidFor afterHeal w
    match Map.tryFind newRid afterHeal.Pending with
    | Some pr' -> Assert.True(pr'.InsetHealed)
    | None -> Assert.True(false, "expected the healed Pending to remain")
    // insetがさらに変わったことにする(DPI丸めの揺れ等を想定)——InsetHealedが立っているので、
    // 等式が成立するとしても2回目は自己修復せず、通常のMismatch終端(Accepted更新)に落ちる。
    let inset2: FrameInset = { Left = 8; Top = 0; Right = 8; Bottom = 7; Monitor = m }
    let stateWithChangedInset = { afterHeal with FrameInsets = Map.ofList [ w, inset2 ] }
    // 1回目の自己修復の再発行は`now`=10L+pendingDeadlineMsの時点で行われた(IssuedAt/Deadlineもその
    // 基準)ので、2回目のPendingDeadline発火時刻もそこから数える。
    let healedIssuedAt = 10L + pendingDeadlineMs
    let afterDispatched2, _, _, _, _ = onEffectCallReturned (healedIssuedAt + 1L) 3L newRid (Ok ()) (Some (healedIssuedAt + 5L)) stateWithChangedInset
    let mismatchRect = { workArea with X = workArea.X + 50 } // inset差とは無関係な、通常のMismatch
    let afterObserved2, _, _ = onWindowObserved (healedIssuedAt + 10L) (observedSnapshotWithMonitor w mismatchRect m (healedIssuedAt + 10L)) afterDispatched2
    let afterDeadline2, effects2, _, _ = onPendingDeadlineTimerFired (healedIssuedAt + pendingDeadlineMs) newRid noDpi afterObserved2
    Assert.Empty(effects2) // 2回目は再発行しない(Opusレビュー論点2の打ち止め：1 Epochにつき1回)
    Assert.Empty(afterDeadline2.Pending)
    Assert.Equal(Some mismatchRect, Map.tryFind w afterDeadline2.Accepted) // 通常のReevaluateに落ちる

[<Fact>]
let ``inset差では説明できないMismatch(アプリ自身の制約等)は自己修復せず、通常のMinWidth経路に落ちる(v2.121)`` () =
    let w = WindowId 1L
    let m = MonitorId 1L
    let inset: FrameInset = { Left = 7; Top = 0; Right = 7; Bottom = 7; Monitor = m }
    let seeded = { initial with FrameInsets = Map.ofList [ w, inset ] } // 発行時点で既にinsetが分かっている
    let afterReconcile, _, _, _ = reconcileWorkspace 10L registry monocleRef (ctxWith [ w ]) (managementOf [ w ]) (aliveOf [ w ]) seeded
    let rid = pendingRidFor afterReconcile w
    Assert.Equal(Some inset, Map.tryFind rid afterReconcile.Pending |> Option.bind (fun pr -> pr.DispatchedInset))
    let afterDispatched, _, _, _, _ = onEffectCallReturned 11L 2L rid (Ok ()) (Some 5L) afterReconcile
    // insetは発行時のままだが、観測された幅がinsetの左右合計を大きく超えて広い
    // (v2.120の既存テストと同じ、アプリ自身の最小幅制約を想定)——inset差では説明できない。
    let muchWiderRect = { workArea with Width = workArea.Width + 100 }
    let afterObserved, _, _ = onWindowObserved 20L (observedSnapshotWithMonitor w muchWiderRect m 10L) afterDispatched
    let dpiOf (mid: MonitorId) = if mid = m then Some 96 else None
    let afterDeadline, effects, _, _ = onPendingDeadlineTimerFired (10L + pendingDeadlineMs) rid dpiOf afterObserved
    Assert.Empty(effects) // 自己修復は発火しない(insetは変わっていないので述語が最初から不成立)
    Assert.Empty(afterDeadline.Pending)
    Assert.Equal(Some muchWiderRect, Map.tryFind w afterDeadline.Accepted) // 通常のReevaluate
    match Map.tryFind w afterDeadline.MinWidth with
    | Some { Value = v; Confidence = Inferred _ } -> Assert.Equal(workArea.Width + 100, v) // MinWidthの証拠は採る
    | other -> Assert.True(false, sprintf "expected an Inferred MinWidth belief, got %A" other)

// ===== モニター不一致時の補正拒否（v2.123、Opus敵対的レビュー`opus-review-frame-inset.md`論点1-F対策） =====
//
// `FrameInset`信念の`Monitor`（観測時のモニター）と、このPendingの発行先モニター
// （`ctxWith`は`MonitorId 1L`固定）が一致しない場合、`correctedDispatchTarget`も自己修復の述語も
// その信念を「無い」ものとして扱う——別モニター（別のDPIかもしれない）で測ったinsetを確認せず
// 使い回さない。

[<Fact>]
let ``FrameInset信念のモニターと発行先モニターが異なる場合、dispatchPlacementsは補正を適用しない(v2.123)`` () =
    let w = WindowId 1L
    let insetWrongMonitor: FrameInset = { Left = 7; Top = 0; Right = 7; Bottom = 7; Monitor = MonitorId 99L }
    let seeded = { initial with FrameInsets = Map.ofList [ w, insetWrongMonitor ] }
    let afterReconcile, effects, _, traces = reconcileWorkspace 10L registry monocleRef (ctxWith [ w ]) (managementOf [ w ]) (aliveOf [ w ]) seeded
    Assert.Equal<Effect list>([ SetRect (RequestId 0L, w, workArea) ], effects) // 無補正(ネイティブ=望ましい可視矩形そのまま)
    Assert.Empty(traces) // FrameInsetAppliedも積まない(信念が無い場合と同じ扱い)
    let rid = pendingRidFor afterReconcile w
    Assert.Equal(None, Map.tryFind rid afterReconcile.Pending |> Option.bind (fun pr -> pr.DispatchedInset))

[<Fact>]
let ``FrameInset自己修復も、信念のモニターが発行先と異なる場合は発火せず通常のReevaluateに落ちる(v2.123)`` () =
    let w = WindowId 1L
    let wrongMonitor = MonitorId 99L
    let insetWrongMonitor: FrameInset = { Left = 7; Top = 0; Right = 7; Bottom = 7; Monitor = wrongMonitor }
    // 発行時点ではinset信念が無いので無補正で発行される(ctxWithの発行先はMonitorId 1L)。
    let afterReconcile, _, _, _ = reconcileWorkspace 10L registry monocleRef (ctxWith [ w ]) (managementOf [ w ]) (aliveOf [ w ]) initial
    let rid = pendingRidFor afterReconcile w
    let afterDispatched, _, _, _, _ = onEffectCallReturned 11L 2L rid (Ok ()) (Some 5L) afterReconcile
    // 観測は、モニター一致チェックが無ければ自己修復の等式を満たすように作る(前の`v2.121`テストと
    // 同じ算術)——ただし今回はこの観測自体が別モニター(wrongMonitor)からの対として学習される。
    let visibleObserved =
        { X = workArea.X + insetWrongMonitor.Left
          Y = workArea.Y + insetWrongMonitor.Top
          Width = workArea.Width - insetWrongMonitor.Left - insetWrongMonitor.Right
          Height = workArea.Height - insetWrongMonitor.Top - insetWrongMonitor.Bottom }
    let afterObserved, _, _ = onWindowObserved 20L (observedPairSnapshotWithMonitor w workArea visibleObserved wrongMonitor 10L) afterDispatched
    Assert.Equal(Some insetWrongMonitor, Map.tryFind w afterObserved.FrameInsets) // insetそのものは学習される
    let afterDeadline, effects, _, _ = onPendingDeadlineTimerFired (10L + pendingDeadlineMs) rid noDpi afterObserved
    Assert.Empty(effects) // モニター不一致のため自己修復は発火しない
    Assert.Empty(afterDeadline.Pending)
    Assert.Equal(Some visibleObserved, Map.tryFind w afterDeadline.Accepted) // 通常のReevaluateに落ちる

[<Fact>]
let ``DpiBehavior=RescalesItselfで2段目リサイズ待ち中(DpiCrossing未決着)のMismatchはMinWidthの証拠にしない(v2.45)`` () =
    let w = WindowId 1L
    let m = MonitorId 1L
    let narrowerCtx = { ctxWith [ w ] with WorkArea = { workArea with Width = workArea.Width - 100 } }
    let afterReconcile, _, _, _ = reconcileWorkspace 10L registry monocleRef narrowerCtx (managementOf [ w ]) (aliveOf [ w ]) initial
    let rid = pendingRidFor afterReconcile w
    let afterDispatched, _, _, _, _ = onEffectCallReturned 11L 2L rid (Ok ()) (Some 5L) afterReconcile
    // DPIクロッシング待ち中（checkDpiCrossingの記録を直接構成する——`PlacementState`は公開レコード）、
    // かつこの窓はRescalesItselfだと分かっていることにする。
    let afterMarked =
        { afterDispatched with
            DpiBehavior = Map.add w { Value = RescalesItself; Confidence = Inferred []; LastRevisedAt = 12L } afterDispatched.DpiBehavior
            DpiCrossing = Map.add w workArea afterDispatched.DpiCrossing }
    let widerRect = { workArea with Width = workArea.Width + 100 } // Targetより広い、かつDpiCrossingのbaseline(workArea)とも異なる
    let afterObserved, _, _ = onWindowObserved 20L (observedSnapshotWithMonitor w widerRect m 10L) afterMarked
    let dpiOf (mid: MonitorId) = if mid = m then Some 96 else None
    let afterDeadline, _, _, _ = onPendingDeadlineTimerFired (10L + pendingDeadlineMs) rid dpiOf afterObserved
    Assert.Empty(afterDeadline.Pending) // Mismatchとしての終端・Pending解消は変わらない
    Assert.Equal(None, Map.tryFind w afterDeadline.MinWidth) // だがMinWidthの証拠にはしない(2段目待ち中)

[<Fact>]
let ``DpiBehavior=OsBitmapScaledならDpiCrossing未決着中でもMismatchをMinWidthの証拠にする(v2.45)`` () =
    let w = WindowId 1L
    let m = MonitorId 1L
    let narrowerCtx = { ctxWith [ w ] with WorkArea = { workArea with Width = workArea.Width - 100 } }
    let afterReconcile, _, _, _ = reconcileWorkspace 10L registry monocleRef narrowerCtx (managementOf [ w ]) (aliveOf [ w ]) initial
    let rid = pendingRidFor afterReconcile w
    let afterDispatched, _, _, _, _ = onEffectCallReturned 11L 2L rid (Ok ()) (Some 5L) afterReconcile
    // 除外はRescalesItselfの窓だけが対象(§4.5)——OsBitmapScaledなら2段目自己リサイズという概念自体が
    // 無いので、DpiCrossingが(何らかの理由で)残っていても除外しない。
    let afterMarked =
        { afterDispatched with
            DpiBehavior = Map.add w { Value = OsBitmapScaled; Confidence = Inferred []; LastRevisedAt = 12L } afterDispatched.DpiBehavior
            DpiCrossing = Map.add w workArea afterDispatched.DpiCrossing }
    let widerRect = { workArea with Width = workArea.Width + 100 }
    let afterObserved, _, _ = onWindowObserved 20L (observedSnapshotWithMonitor w widerRect m 10L) afterMarked
    let dpiOf (mid: MonitorId) = if mid = m then Some 96 else None
    let afterDeadline, _, _, _ = onPendingDeadlineTimerFired (10L + pendingDeadlineMs) rid dpiOf afterObserved
    match Map.tryFind w afterDeadline.MinWidth with
    | Some { Confidence = Inferred _ } -> ()
    | other -> Assert.True(false, sprintf "expected an Inferred MinWidth belief, got %A" other)

// ===== DpiBehaviorの2段目自己リサイズ検出（checkDpiCrossing、v2.45） =====

[<Fact>]
let ``Coreが新しいSetRectを発行すると、未決着のDpiCrossingは帰属不能として消える(v2.45)`` () =
    let w = WindowId 1L
    let m0 = MonitorId 0L
    let m1 = MonitorId 1L
    let dpiOf (m: MonitorId) = if m = m0 then Some 96 elif m = m1 then Some 192 else None
    let afterFirst, _, _ = onWindowObserved 10L (observedSnapshotWithMonitor w workArea m0 1L) initial
    let oldMonitor = LatestWins.value (Map.find w afterFirst.Geometry).Monitor
    let afterSecond, _, _ = onWindowObserved 20L (observedSnapshotWithMonitor w workArea m1 2L) afterFirst
    let afterCrossCheck = checkDpiCrossing 20L dpiOf w oldMonitor afterSecond
    Assert.Equal(Some workArea, Map.tryFind w afterCrossCheck.DpiCrossing)

    // Coreが(別の理由で)この窓へ新しいSetRectを発行する——これより後の矩形変化はCore自身の要求で
    // 説明できてしまうので、まだ決着していないDpiCrossingの記録は帰属不能として諦める。
    let afterReconcile, _, _, _ = reconcileWorkspace 30L registry monocleRef (ctxWith [ w ]) (managementOf [ w ]) (aliveOf [ w ]) afterCrossCheck
    Assert.Equal(None, Map.tryFind w afterReconcile.DpiCrossing)

// ===== MoveSizeStarted/MoveSizeEnded（§4.4 Suspended、§7.2「ユーザーのドラッグ」除外、v2.65） =====

[<Fact>]
let ``onMoveSizeStartedはDraggingへ加え、onMoveSizeEndedは外す`` () =
    let w = WindowId 1L
    let afterStart = onMoveSizeStarted w initial
    Assert.True(Set.contains w afterStart.Dragging)
    let afterEnd = onMoveSizeEnded w afterStart
    Assert.False(Set.contains w afterEnd.Dragging)

[<Fact>]
let ``Dragging中の窓はPendingDeadline発火時にMismatch判定もRetryもせず、戦わずに終端する(v2.65、Opusレビューの条件6)`` () =
    let w = WindowId 1L
    let m = MonitorId 1L
    let afterReconcile, _, _, _ = reconcileWorkspace 10L registry monocleRef (ctxWith [ w ]) (managementOf [ w ]) (aliveOf [ w ]) initial
    let rid = pendingRidFor afterReconcile w
    let afterDispatched, _, _, _, _ = onEffectCallReturned 11L 2L rid (Ok ()) (Some 5L) afterReconcile
    // ユーザーがドラッグ中に、要求とは異なる矩形が観測される(ユーザー自身の操作の結果)。
    let draggedRect = { workArea with X = workArea.X + 300; Width = 2000 } // 幅も要求より広い(MinWidthの証拠候補)
    let afterObserved, _, _ = onWindowObserved 20L (observedSnapshotWithMonitor w draggedRect m 10L) afterDispatched
    let afterDragStart = onMoveSizeStarted w afterObserved
    let dpiOf (mid: MonitorId) = if mid = m then Some 96 else None
    let afterDeadline, effects, _, _ = onPendingDeadlineTimerFired (10L + pendingDeadlineMs) rid dpiOf afterDragStart
    Assert.Empty(afterDeadline.Pending) // 終端はする(Reevaluateと同じ、戦わない)
    Assert.Empty(effects) // Retryしない
    Assert.Equal(None, Map.tryFind w afterDeadline.RetryAttempts)
    // ドラッグ中の観測はMinWidthの証拠にもしない(幅が広くても、ユーザー操作の結果であって
    // アプリの最小幅制約の証拠ではない——条件6の除外そのもの)。
    Assert.Equal(None, Map.tryFind w afterDeadline.MinWidth)

[<Fact>]
let ``ドラッグ終了後は通常どおりMismatch判定に戻る(回帰確認、v2.65)`` () =
    let w = WindowId 1L
    let m = MonitorId 1L
    let afterReconcile, _, _, _ = reconcileWorkspace 10L registry monocleRef (ctxWith [ w ]) (managementOf [ w ]) (aliveOf [ w ]) initial
    let rid = pendingRidFor afterReconcile w
    let afterDispatched, _, _, _, _ = onEffectCallReturned 11L 2L rid (Ok ()) (Some 5L) afterReconcile
    let afterDragEnded = onMoveSizeEnded w afterDispatched // Draggingに入っていなくても安全にno-op
    let widerRect = { workArea with Width = 2000 }
    let afterObserved, _, _ = onWindowObserved 20L (observedSnapshotWithMonitor w widerRect m 10L) afterDragEnded
    let dpiOf (mid: MonitorId) = if mid = m then Some 96 else None
    let afterDeadline, _, _, _ = onPendingDeadlineTimerFired (10L + pendingDeadlineMs) rid dpiOf afterObserved
    match Map.tryFind w afterDeadline.MinWidth with
    | Some { Confidence = Inferred _ } -> ()
    | other -> Assert.True(false, sprintf "expected an Inferred MinWidth belief (normal Mismatch evidence capture), got %A" other)

// ===== 「最小化・非表示中」の除外（§7.2、Opusレビューの条件5、v2.73） =====

[<Fact>]
let ``最小化中の窓はPendingDeadline発火時にMismatch判定もRetryもせず、戦わずに終端する(v2.73、Opusレビューの条件5)`` () =
    let w = WindowId 1L
    let m = MonitorId 1L
    let afterReconcile, _, _, _ = reconcileWorkspace 10L registry monocleRef (ctxWith [ w ]) (managementOf [ w ]) (aliveOf [ w ]) initial
    let rid = pendingRidFor afterReconcile w
    let afterDispatched, _, _, _, _ = onEffectCallReturned 11L 2L rid (Ok ()) (Some 5L) afterReconcile
    // ユーザーが最小化した直後、`-32000`付近の座標が観測される(CIスパイクS25、幅も要求より広い
    // ——MinWidthの証拠候補)。
    let minimizedRect = { X = -32000; Y = -32000; Width = 2000; Height = 2000 }
    let afterObserved, _, _ = onWindowObserved 20L (observedSnapshotMinimized w minimizedRect m 10L) afterDispatched
    let dpiOf (mid: MonitorId) = if mid = m then Some 96 else None
    let afterDeadline, effects, _, _ = onPendingDeadlineTimerFired (10L + pendingDeadlineMs) rid dpiOf afterObserved
    Assert.Empty(afterDeadline.Pending) // 終端はする(Reevaluateと同じ、戦わない)
    Assert.Empty(effects) // Retryしない
    Assert.Equal(None, Map.tryFind w afterDeadline.RetryAttempts)
    // 最小化中の観測はMinWidthの証拠にもしない(`-32000`付近の座標・幅はアプリの最小幅制約の
    // 証拠ではない——条件5の除外そのもの)。
    Assert.Equal(None, Map.tryFind w afterDeadline.MinWidth)

[<Fact>]
let ``最大化中の窓はPendingDeadline発火時にMismatch判定もRetryもせず、戦わずに終端する(v2.77)`` () =
    let w = WindowId 1L
    let m = MonitorId 1L
    let afterReconcile, _, _, _ = reconcileWorkspace 10L registry monocleRef (ctxWith [ w ]) (managementOf [ w ]) (aliveOf [ w ]) initial
    let rid = pendingRidFor afterReconcile w
    let afterDispatched, _, _, _, _ = onEffectCallReturned 11L 2L rid (Ok ()) (Some 5L) afterReconcile
    // ユーザーが最大化すると、観測される矩形はモニターのWorkArea全体になる(TargetRectとは不一致、
    // 幅も要求より広い——MinWidthの証拠候補)。
    let maximizedRect = { workArea with Width = workArea.Width + 500 }
    let afterObserved, _, _ = onWindowObserved 20L (observedSnapshotMaximizedWithMonitor w maximizedRect m 10L) afterDispatched
    let dpiOf (mid: MonitorId) = if mid = m then Some 96 else None
    let afterDeadline, effects, _, _ = onPendingDeadlineTimerFired (10L + pendingDeadlineMs) rid dpiOf afterObserved
    Assert.Empty(afterDeadline.Pending) // 終端はする(Reevaluateと同じ、戦わない)
    Assert.Empty(effects) // Retryしない
    Assert.Equal(None, Map.tryFind w afterDeadline.RetryAttempts)
    // 最大化中の観測はMinWidthの証拠にもしない(モニターのWorkArea幅はアプリの最小幅制約の
    // 証拠ではない)。DpiOfが解決できる(=一般のMismatch経路なら証拠を記録できる)状態でもなお
    // 記録しないことを確かめる。
    Assert.Equal(None, Map.tryFind w afterDeadline.MinWidth)
    // 一般のMismatch経路（Reevaluate）とは違い、Acceptedも更新しない(除外そのもの、
    // 条件5・6と同じ終端)。
    Assert.Equal(None, Map.tryFind w afterDeadline.Accepted)

[<Fact>]
let ``最大化から復元した後は通常どおりMismatch判定に戻る(回帰確認、v2.77)`` () =
    let w = WindowId 1L
    let m = MonitorId 1L
    let afterReconcile, _, _, _ = reconcileWorkspace 10L registry monocleRef (ctxWith [ w ]) (managementOf [ w ]) (aliveOf [ w ]) initial
    let rid = pendingRidFor afterReconcile w
    let afterDispatched, _, _, _, _ = onEffectCallReturned 11L 2L rid (Ok ()) (Some 5L) afterReconcile
    let widerRect = { workArea with Width = 2000 }
    let afterObserved, _, _ = onWindowObserved 20L (observedSnapshotWithMonitor w widerRect m 10L) afterDispatched
    let dpiOf (mid: MonitorId) = if mid = m then Some 96 else None
    let afterDeadline, _, _, _ = onPendingDeadlineTimerFired (10L + pendingDeadlineMs) rid dpiOf afterObserved
    match Map.tryFind w afterDeadline.MinWidth with
    | Some { Confidence = Inferred _ } -> ()
    | other -> Assert.True(false, sprintf "expected an Inferred MinWidth belief (normal Mismatch evidence capture), got %A" other)

[<Fact>]
let ``最小化から復元した後は通常どおりMismatch判定に戻る(回帰確認、v2.73)`` () =
    let w = WindowId 1L
    let m = MonitorId 1L
    let afterReconcile, _, _, _ = reconcileWorkspace 10L registry monocleRef (ctxWith [ w ]) (managementOf [ w ]) (aliveOf [ w ]) initial
    let rid = pendingRidFor afterReconcile w
    let afterDispatched, _, _, _, _ = onEffectCallReturned 11L 2L rid (Ok ()) (Some 5L) afterReconcile
    let widerRect = { workArea with Width = 2000 }
    let afterObserved, _, _ = onWindowObserved 20L (observedSnapshotWithMonitor w widerRect m 10L) afterDispatched
    let dpiOf (mid: MonitorId) = if mid = m then Some 96 else None
    let afterDeadline, _, _, _ = onPendingDeadlineTimerFired (10L + pendingDeadlineMs) rid dpiOf afterObserved
    match Map.tryFind w afterDeadline.MinWidth with
    | Some { Confidence = Inferred _ } -> ()
    | other -> Assert.True(false, sprintf "expected an Inferred MinWidth belief (normal Mismatch evidence capture), got %A" other)

// ===== MoveCause.Unattributed：Accepted/ドリフト検知（§4.4 R3-4、v2.70、Opus敵対的レビュー
// opus-review-unattributed-movecause.md。ラウンド1はクロスモニターのずれだけを対象にする） =====

let private alwaysStable = true
let private isTiledTrue (_: WindowId) = true
let private isTiledFalse (_: WindowId) = false

[<Fact>]
let ``Satisfiedで観測された矩形がAcceptedに記録される(v2.70)`` () =
    let w = WindowId 1L
    let afterReconcile, _, _, _ = reconcileWorkspace 10L registry monocleRef (ctxWith [ w ]) (managementOf [ w ]) (aliveOf [ w ]) initial
    let afterObserved, _, _ = onWindowObserved 20L (observedSnapshot w workArea 1L) afterReconcile
    Assert.Empty(afterObserved.Pending)
    Assert.Equal(Some workArea, Map.tryFind w afterObserved.Accepted)

[<Fact>]
let ``Mismatch終端時は観測された矩形(Desiredではない)がAcceptedに記録される(v2.70、Opusレビュー§B-1)`` () =
    let w = WindowId 1L
    let afterReconcile, _, _, _ = reconcileWorkspace 10L registry monocleRef (ctxWith [ w ]) (managementOf [ w ]) (aliveOf [ w ]) initial
    let rid = pendingRidFor afterReconcile w
    let afterDispatched, _, _, _, _ = onEffectCallReturned 11L 2L rid (Ok ()) (Some 5L) afterReconcile
    let mismatchedRect = { workArea with X = workArea.X + 50 }
    let afterObserved, _, _ = onWindowObserved 20L (observedSnapshot w mismatchedRect 10L) afterDispatched
    let afterDeadline, _, _, _ = onPendingDeadlineTimerFired (10L + pendingDeadlineMs) rid noDpi afterObserved
    Assert.Equal(Some mismatchedRect, Map.tryFind w afterDeadline.Accepted) // Desired(workArea)ではない

[<Fact>]
let ``新しい意図をdispatchPlacementsが出すとAcceptedは消える(v2.70)`` () =
    let w = WindowId 1L
    let afterReconcile1, _, _, _ = reconcileWorkspace 10L registry monocleRef (ctxWith [ w ]) (managementOf [ w ]) (aliveOf [ w ]) initial
    let afterObserved, _, _ = onWindowObserved 20L (observedSnapshot w workArea 1L) afterReconcile1
    Assert.Equal(Some workArea, Map.tryFind w afterObserved.Accepted)
    let narrowerCtx = { ctxWith [ w ] with WorkArea = { workArea with Width = workArea.Width - 100 } }
    let afterReconcile2, _, _, _ = reconcileWorkspace 30L registry monocleRef narrowerCtx (managementOf [ w ]) (aliveOf [ w ]) afterObserved
    Assert.Equal(None, Map.tryFind w afterReconcile2.Accepted)

[<Fact>]
let ``checkUnattributedDriftは、Pendingが無くAcceptedが有りモニターが食い違えば、Desiredを消しDriftCorrectionsへ記録する(v2.70)`` () =
    let w = WindowId 1L
    let m0, m1 = MonitorId 0L, MonitorId 1L
    let afterReconcile, _, _, _ = reconcileWorkspace 10L registry monocleRef (ctxWith [ w ]) (managementOf [ w ]) (aliveOf [ w ]) initial
    let afterObserved, _, _ = onWindowObserved 20L (observedSnapshotWithMonitor w workArea m0 1L) afterReconcile
    Assert.Equal(Some workArea, Map.tryFind w afterObserved.Accepted)
    // OSが別モニターへ寄せた(MoveSizeイベントを伴わない)観測。
    let afterMoved, _, _ = onWindowObserved 30L (observedSnapshotWithMonitor w workArea m1 2L) afterObserved
    let afterDrift, corrected, _ = checkUnattributedDrift 30L isTiledTrue (fun _ -> Some m0) alwaysStable w afterMoved
    Assert.True(corrected)
    Assert.Equal(None, Map.tryFind w afterDrift.Desired)
    Assert.Equal<int64 list>([ 30L ], Map.tryFind w afterDrift.DriftCorrections |> Option.defaultValue [])

[<Fact>]
let ``checkUnattributedDriftは最小化中の窓には作用しない(v2.75)`` () =
    // CIスパイクS24/S25が確認した最小化時の挙動（矩形が-32000付近へ移る）は、v2.73で
    // onPendingDeadlineTimerFiredのMismatch判定を誤検知させることが分かっている。
    // checkUnattributedDriftは同じGeometry.Monitor信念を見るクロスモニター比較なので、
    // 最小化中にモニターの観測が食い違って見えても補正・カウントしてはならない。
    let w = WindowId 1L
    let m0, m1 = MonitorId 0L, MonitorId 1L
    let afterReconcile, _, _, _ = reconcileWorkspace 10L registry monocleRef (ctxWith [ w ]) (managementOf [ w ]) (aliveOf [ w ]) initial
    let afterObserved, _, _ = onWindowObserved 20L (observedSnapshotWithMonitor w workArea m0 1L) afterReconcile
    Assert.Equal(Some workArea, Map.tryFind w afterObserved.Accepted)
    // ユーザーが最小化し、観測されたモニターが(最小化中の報告の揺れ等で)食い違って見える。
    let afterMinimized, _, _ = onWindowObserved 30L (observedSnapshotMinimized w workArea m1 2L) afterObserved
    let afterDrift, corrected, shouldDegrade = checkUnattributedDrift 30L isTiledTrue (fun _ -> Some m0) alwaysStable w afterMinimized
    Assert.False(corrected)
    Assert.False(shouldDegrade)
    Assert.Equal(Some (Shown (Some workArea)), Map.tryFind w afterDrift.Desired)
    Assert.Empty(Map.tryFind w afterDrift.DriftCorrections |> Option.defaultValue [])

[<Fact>]
let ``checkUnattributedDriftは抑止区間中(ScreenPhase非Stable)は検知もカウントもしない(v2.70、Opusレビュー§Q4-1「必須」)`` () =
    let w = WindowId 1L
    let m0, m1 = MonitorId 0L, MonitorId 1L
    let afterReconcile, _, _, _ = reconcileWorkspace 10L registry monocleRef (ctxWith [ w ]) (managementOf [ w ]) (aliveOf [ w ]) initial
    let afterObserved, _, _ = onWindowObserved 20L (observedSnapshotWithMonitor w workArea m0 1L) afterReconcile
    let afterMoved, _, _ = onWindowObserved 30L (observedSnapshotWithMonitor w workArea m1 2L) afterObserved
    let afterDrift, corrected, _ = checkUnattributedDrift 30L isTiledTrue (fun _ -> Some m0) false w afterMoved
    Assert.False(corrected)
    Assert.Equal(Some (Shown (Some workArea)), Map.tryFind w afterDrift.Desired)
    Assert.Empty(Map.tryFind w afterDrift.DriftCorrections |> Option.defaultValue [])

[<Fact>]
let ``checkUnattributedDriftはTiledでない窓には作用しない(v2.70、Opusレビュー§B-3)`` () =
    let w = WindowId 1L
    let m0, m1 = MonitorId 0L, MonitorId 1L
    let afterReconcile, _, _, _ = reconcileWorkspace 10L registry monocleRef (ctxWith [ w ]) (managementOf [ w ]) (aliveOf [ w ]) initial
    let afterObserved, _, _ = onWindowObserved 20L (observedSnapshotWithMonitor w workArea m0 1L) afterReconcile
    let afterMoved, _, _ = onWindowObserved 30L (observedSnapshotWithMonitor w workArea m1 2L) afterObserved
    let afterDrift, corrected, _ = checkUnattributedDrift 30L isTiledFalse (fun _ -> Some m0) alwaysStable w afterMoved
    Assert.False(corrected)
    Assert.Equal(Some (Shown (Some workArea)), Map.tryFind w afterDrift.Desired)

[<Fact>]
let ``checkUnattributedDriftはPendingが残っている窓には作用しない(v2.70)`` () =
    // `dispatchPlacements`は新しい意図を出すたびに必ず`Accepted`を消す（型直前のコメント参照）ので、
    // 「Pendingがある∧Acceptedがある」という組合せは通常の状態遷移では実際には作られない——
    // Pending単体の除外条件を切り離して検査するため、ここでは意図的にその組合せを直接組み立てる
    // （v2.68のDecisionTraceテストで「到達しないはずの組合せ」を直接作ったのと同じ手法）。
    let w = WindowId 1L
    let m0, m1 = MonitorId 0L, MonitorId 1L
    let afterReconcile, _, _, _ = reconcileWorkspace 10L registry monocleRef (ctxWith [ w ]) (managementOf [ w ]) (aliveOf [ w ]) initial
    let afterObserved, _, _ = onWindowObserved 20L (observedSnapshotWithMonitor w workArea m0 1L) afterReconcile
    Assert.Equal(Some workArea, Map.tryFind w afterObserved.Accepted)
    // 先にGeometry.Monitorをm1へ観測させてから(この時点ではPendingが無いのでcheckPendingSatisfiedは
    // 無関係)、Acceptedを保ったまま人為的にPendingを追加する——これなら`onWindowObserved`の
    // Satisfied判定が割り込んでAccepted/Pendingを横取りしない。
    let afterMoved0, _, _ = onWindowObserved 30L (observedSnapshotWithMonitor w workArea m1 2L) afterObserved
    Assert.Equal(Some workArea, Map.tryFind w afterMoved0.Accepted) // Pendingが無いのでSatisfied判定は起きない
    let artificialPending: PendingRequest =
        { Window = w
          Epoch = IntentEpoch 0L
          TargetRect = { workArea with Width = workArea.Width - 1 }
          IssuedAt = 30L
          Deadline = 30L + pendingDeadlineMs
          DispatchSeq = None
          Baseline = None
          DispatchedInset = None
          InsetHealed = false
          TargetMonitor = m1 }
    let afterMoved = { afterMoved0 with Pending = Map.ofList [ RequestId 999L, artificialPending ] }
    let afterDrift, corrected, _ = checkUnattributedDrift 30L isTiledTrue (fun _ -> Some m0) alwaysStable w afterMoved
    Assert.False(corrected)

[<Fact>]
let ``checkUnattributedDriftはDragging中の窓には作用しない(v2.70)`` () =
    let w = WindowId 1L
    let m0, m1 = MonitorId 0L, MonitorId 1L
    let afterReconcile, _, _, _ = reconcileWorkspace 10L registry monocleRef (ctxWith [ w ]) (managementOf [ w ]) (aliveOf [ w ]) initial
    let afterObserved, _, _ = onWindowObserved 20L (observedSnapshotWithMonitor w workArea m0 1L) afterReconcile
    let afterMoved, _, _ = onWindowObserved 30L (observedSnapshotWithMonitor w workArea m1 2L) afterObserved
    let afterDragging = onMoveSizeStarted w afterMoved
    let afterDrift, corrected, _ = checkUnattributedDrift 30L isTiledTrue (fun _ -> Some m0) alwaysStable w afterDragging
    Assert.False(corrected)

[<Fact>]
let ``checkUnattributedDriftはAcceptedが無い(一度も受け入れられていない)窓には作用しない(v2.70)`` () =
    let w = WindowId 1L
    let m0, m1 = MonitorId 0L, MonitorId 1L
    let afterReconcile, _, _, _ = reconcileWorkspace 10L registry monocleRef (ctxWith [ w ]) (managementOf [ w ]) (aliveOf [ w ]) initial
    // まだ一度もSatisfiedになっていない(Acceptedが無い)。Pendingも消して「Pending無し」の条件だけを
    // 単独で満たす人為的な状態を作る——Acceptedが無ければ検知しないことだけを確認する。
    let rid = pendingRidFor afterReconcile w
    let afterCancelled = { afterReconcile with Pending = Map.remove rid afterReconcile.Pending }
    let afterMoved, _, _ = onWindowObserved 30L (observedSnapshotWithMonitor w workArea m1 2L) afterCancelled
    let afterDrift, corrected, _ = checkUnattributedDrift 30L isTiledTrue (fun _ -> Some m0) alwaysStable w afterMoved
    Assert.False(corrected)

[<Fact>]
let ``checkUnattributedDriftは同じモニターなら何もしない(v2.70)`` () =
    let w = WindowId 1L
    let m0 = MonitorId 0L
    let afterReconcile, _, _, _ = reconcileWorkspace 10L registry monocleRef (ctxWith [ w ]) (managementOf [ w ]) (aliveOf [ w ]) initial
    let afterObserved, _, _ = onWindowObserved 20L (observedSnapshotWithMonitor w workArea m0 1L) afterReconcile
    let afterDrift, corrected, _ = checkUnattributedDrift 30L isTiledTrue (fun _ -> Some m0) alwaysStable w afterObserved
    Assert.False(corrected)

[<Fact>]
let ``checkUnattributedDriftの補正は意図をまたいでもリセットされず、上限で打ち止めになる(v2.70、Opusレビュー§Q2)`` () =
    let w = WindowId 1L
    let m0, m1 = MonitorId 0L, MonitorId 1L
    let afterReconcile, _, _, _ = reconcileWorkspace 10L registry monocleRef (ctxWith [ w ]) (managementOf [ w ]) (aliveOf [ w ]) initial
    let afterObserved, _, _ = onWindowObserved 20L (observedSnapshotWithMonitor w workArea m0 1L) afterReconcile

    // 1〜3回目：補正が出る(unattributedCorrectionLimit=3)。`desiredMonitorOf`をm1(観測m0と食い違う)に
    // 固定し、毎回「新しい意図」を手で再現する必要は無い——Desiredが消えてもAccepted/Pendingはそのまま
    // なので、次の呼び出しも同じ条件で検知できる。
    let afterDrift1, c1, d1 = checkUnattributedDrift 1_000L isTiledTrue (fun _ -> Some m1) alwaysStable w afterObserved
    Assert.True(c1)
    Assert.False(d1) // まだ打ち止めではないので劣化も要求しない
    let afterDrift2, c2, d2 = checkUnattributedDrift 2_000L isTiledTrue (fun _ -> Some m1) alwaysStable w afterDrift1
    Assert.True(c2)
    Assert.False(d2)
    let afterDrift3, c3, d3 = checkUnattributedDrift 3_000L isTiledTrue (fun _ -> Some m1) alwaysStable w afterDrift2
    Assert.True(c3)
    Assert.False(d3)
    Assert.Equal(3, Map.tryFind w afterDrift3.DriftCorrections |> Option.defaultValue [] |> List.length)

    // 4回目(直近unattributedWindowMs=30秒以内)：打ち止め。補正しないが、Floatingへの劣化を要求する
    // (v2.71、Opusレビュー§Q3・§Q5「ラウンド2」)。
    let afterDrift4, c4, d4 = checkUnattributedDrift 4_000L isTiledTrue (fun _ -> Some m1) alwaysStable w afterDrift3
    Assert.False(c4)
    Assert.True(d4)
    Assert.Equal(3, Map.tryFind w afterDrift4.DriftCorrections |> Option.defaultValue [] |> List.length)

[<Fact>]
let ``checkUnattributedDriftの打ち止めは時間窓の外では再び補正できるようになる(v2.70)`` () =
    let w = WindowId 1L
    let m0, m1 = MonitorId 0L, MonitorId 1L
    let afterReconcile, _, _, _ = reconcileWorkspace 10L registry monocleRef (ctxWith [ w ]) (managementOf [ w ]) (aliveOf [ w ]) initial
    let afterObserved, _, _ = onWindowObserved 20L (observedSnapshotWithMonitor w workArea m0 1L) afterReconcile
    let afterDrift1, _, _ = checkUnattributedDrift 1_000L isTiledTrue (fun _ -> Some m1) alwaysStable w afterObserved
    let afterDrift2, _, _ = checkUnattributedDrift 2_000L isTiledTrue (fun _ -> Some m1) alwaysStable w afterDrift1
    let afterDrift3, _, _ = checkUnattributedDrift 3_000L isTiledTrue (fun _ -> Some m1) alwaysStable w afterDrift2
    // unattributedWindowMs(30秒)より後：直近の補正が時間窓から外れるので再び補正できる。
    let farFuture = 3_000L + unattributedWindowMs + 1L
    let afterDrift4, c4, _ = checkUnattributedDrift farFuture isTiledTrue (fun _ -> Some m1) alwaysStable w afterDrift3
    Assert.True(c4)

[<Fact>]
let ``onWindowVanishedはAccepted・DriftCorrectionsも消す(v2.70)`` () =
    let w = WindowId 1L
    let m0 = MonitorId 0L
    let afterReconcile, _, _, _ = reconcileWorkspace 10L registry monocleRef (ctxWith [ w ]) (managementOf [ w ]) (aliveOf [ w ]) initial
    let afterObserved, _, _ = onWindowObserved 20L (observedSnapshotWithMonitor w workArea m0 1L) afterReconcile
    let afterDrift, _, _ = checkUnattributedDrift 1_000L isTiledTrue (fun _ -> Some (MonitorId 1L)) alwaysStable w afterObserved
    Assert.NotEmpty(Map.tryFind w afterDrift.DriftCorrections |> Option.defaultValue [])
    let afterVanished, _ = onWindowVanished w afterDrift
    Assert.Equal(None, Map.tryFind w afterVanished.Accepted)
    Assert.Equal(None, Map.tryFind w afterVanished.DriftCorrections)

// ===== 3-Bの再発行ループ対策（v2.122、Opus敵対的レビュー`opus-review-frame-inset.md`論点3-B、
// v2.115でVirtualWorldに再現済み。実際のCore側対策はこのラウンドまで未着手だった） =====
//
// 実機（`Wm.Windows.RealObservation.observeWindow`）は`RawFirstSeen`の時点で常にNativeRect/
// VisibleFrameRectの対を問い合わせるため、WM再起動時に既にタイル済みだった窓の発見イベントは
// 本来矩形を伴う（v2.115の`ConstrainedWindowScenario.fs`の`visibleWindowObserved`ヘルパーは
// 無関係な別の既知の不具合——SampleSequence衝突——を避けるため、意図的に発見イベントから矩形を
// 省いた簡略化モデルであり、実機の挙動を表していない）。このラウンドでは、発見時点で既に
// `Geometry.VisibleFrameRect`信念がTargetと一致している（＝実機の発見イベントが矩形を運んだ）
// 場合に対策する——`dispatchPlacements`がこの一致を発行前に検出して`SetRect`自体を出さず、
// 直接`checkPendingSatisfied`と同じ終端を行う。

[<Fact>]
let ``発見時に既にVisibleFrameRectがTargetと一致している窓へのReconcileは、SetRectを発行せず直接Satisfied相当にする(v2.122)`` () =
    let w = WindowId 1L
    // WM再起動時に既にタイル済みだった窓を模す——実機の発見イベント(RawFirstSeen)は常にNativeRect/
    // VisibleFrameRectの対を運ぶため(RealObservation.observeWindow)、Reconcile前に既に信念がある。
    let afterDiscovered, _, _ = onWindowObserved 1L (observedSnapshot w workArea 0L) initial
    let afterReconcile, effects, _, _ = reconcileWorkspace 10L registry monocleRef (ctxWith [ w ]) (managementOf [ w ]) (aliveOf [ w ]) afterDiscovered
    Assert.Empty(effects) // SetRectを一切発行しない(=この窓への無反応no-opが起きない)
    Assert.Empty(afterReconcile.Pending)
    Assert.Equal(Some (Shown (Some workArea)), Map.tryFind w afterReconcile.Desired)
    Assert.Equal(Some workArea, Map.tryFind w afterReconcile.Accepted)
    match Map.tryFind w afterReconcile.Movability with
    | Some { Value = Movable; Confidence = Confirmed } -> ()
    | other -> Assert.True(false, sprintf "expected Movable/Confirmed, got %A" other)

[<Fact>]
let ``発見時のVisibleFrameRectがTargetと異なる窓へのReconcileは、通常どおりSetRectを発行する(v2.122の回帰確認)`` () =
    let w = WindowId 1L
    let offRect = { workArea with X = workArea.X + 50 }
    let afterDiscovered, _, _ = onWindowObserved 1L (observedSnapshot w offRect 0L) initial
    let afterReconcile, effects, _, _ = reconcileWorkspace 10L registry monocleRef (ctxWith [ w ]) (managementOf [ w ]) (aliveOf [ w ]) afterDiscovered
    Assert.Equal<Effect list>([ SetRect (RequestId 0L, w, workArea) ], effects)
    Assert.Equal(1, Map.count afterReconcile.Pending)

[<Fact>]
let ``ドラッグ中の窓は、観測がTargetと偶然一致していてもSetRectスキップをせず通常どおり発行する(v2.122)`` () =
    let w = WindowId 1L
    let afterDiscovered, _, _ = onWindowObserved 1L (observedSnapshot w workArea 0L) initial
    let afterDragging = onMoveSizeStarted w afterDiscovered
    let afterReconcile, effects, _, _ = reconcileWorkspace 10L registry monocleRef (ctxWith [ w ]) (managementOf [ w ]) (aliveOf [ w ]) afterDragging
    Assert.Equal<Effect list>([ SetRect (RequestId 0L, w, workArea) ], effects)
    Assert.Equal(1, Map.count afterReconcile.Pending)

[<Fact>]
let ``最大化中の窓は、観測がTargetと偶然一致していてもSetRectスキップをせず通常どおりRestoreBeforeMove+SetRectを発行する(v2.122)`` () =
    let w = WindowId 1L
    let afterDiscovered, _, _ = onWindowObserved 1L (observedSnapshotMaximized w workArea 0L) initial
    let afterReconcile, effects, _, _ = reconcileWorkspace 10L registry monocleRef (ctxWith [ w ]) (managementOf [ w ]) (aliveOf [ w ]) afterDiscovered
    match effects with
    | [ RestoreBeforeMove (_, restoreWindow); SetRect (_, setWindow, rect) ] ->
        Assert.Equal(w, restoreWindow)
        Assert.Equal(w, setWindow)
        Assert.Equal(workArea, rect)
    | other -> Assert.True(false, sprintf "expected [RestoreBeforeMove; SetRect], got %A" other)
    Assert.Equal(1, Map.count afterReconcile.Pending)
