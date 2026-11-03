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
open Wm.Core.Placement

let private workArea: PixelRect = { X = 0; Y = 0; Width = 1920; Height = 1080 }

let private registry: Registry =
    Registry.empty
    |> Registry.register (Wm.Layouts.Monocle.implementation "test-monocle")
    |> Registry.register (Wm.Layouts.MasterStack.implementation "test-master-stack")

let private monocleRef: LayoutRef = { Id = LayoutId "monocle"; ApiVersion = 1; Args = NoArgs }
let private masterStackRef (ratio: decimal) : LayoutRef = { Id = LayoutId "master-stack"; ApiVersion = 1; Args = MasterRatio ratio }

let private ctxWith (tiled: WindowId list) : LayoutContext =
    { WorkArea = workArea; Dpi = 96; TiledWindows = tiled |> List.map (fun w -> { Id = w; MinWidthDip = None }); Focused = None }

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
    let state', effects, timerCmds = reconcileWorkspace 10L registry monocleRef ctx (managementOf [ w ]) (aliveOf [ w ]) initial
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
    let _, effects, _ = reconcileWorkspace 10L registry monocleRef ctx (managementOf [ w ]) (aliveOf [ w ]) stateAfterAlloc2
    Assert.Equal<Effect list>([ SetRect (RequestId 2L, w, workArea) ], effects)

[<Fact>]
let ``Desiredと同じ結果ならSetRectを再発行しない(§7.3重複要求の禁止)`` () =
    let w = WindowId 1L
    let ctx = ctxWith [ w ]
    let state1, _, _ = reconcileWorkspace 10L registry monocleRef ctx (managementOf [ w ]) (aliveOf [ w ]) initial
    let state2, effects2, _ = reconcileWorkspace 20L registry monocleRef ctx (managementOf [ w ]) (aliveOf [ w ]) state1
    Assert.Empty(effects2)
    Assert.Equal<Map<RequestId, PendingRequest>>(state1.Pending, state2.Pending)

[<Fact>]
let ``目標が変わったら新しいPendingを発行し、同じ窓宛ての古いPendingを失効させる(Superseded)`` () =
    let w1 = WindowId 1L
    let w2 = WindowId 2L
    let state1, effects1, _ = reconcileWorkspace 10L registry monocleRef (ctxWith [ w1 ]) (managementOf [ w1 ]) (aliveOf [ w1 ]) initial
    Assert.Equal<Effect list>([ SetRect (RequestId 0L, w1, workArea) ], effects1)
    // 2窓目が増えた→MasterStackに切り替えてw1の目標が変わる(全画面→左半分)。
    let state2, effects2, _ =
        reconcileWorkspace 20L registry (masterStackRef 0.5m) (ctxWith [ w1; w2 ]) (managementOf [ w1; w2 ]) (aliveOf [ w1; w2 ]) state1
    // w1の新しいPendingだけが残り(古いものは置き換わっている)、w2のPendingも追加される。
    Assert.Equal(2, Map.count state2.Pending)
    let w1Pending = state2.Pending |> Map.toSeq |> Seq.filter (fun (_, pr) -> pr.Window = w1) |> Seq.toList
    Assert.Equal(1, List.length w1Pending)
    Assert.NotEqual<Effect list>([], effects2)

[<Fact>]
let ``死亡/未管理の窓だけの違反はRelayoutWithoutで再計算され、結局何も発行しない`` () =
    // TiledWindowsにある窓がAlive/Managementに無い(死亡/未管理)→UnmanagedOrDeadWindowはdecideのremovable
    // (RelayoutWithout)対象なので、その窓を除いて再計算される。残りが無ければ結局は何も発行しない。
    let w = WindowId 1L
    let ctx = ctxWith [ w ]
    let state', effects, _ = reconcileWorkspace 10L registry monocleRef ctx Map.empty Set.empty initial
    Assert.Empty(effects)
    Assert.Empty(state'.Pending)

[<Fact>]
let ``MissingPlacement(構造的な違反)はRejectWholePlanとして何も発行しない(再計算しても直らない)`` () =
    // w2はManagement/Aliveでは管理下のTiledだが、ctx.TiledWindowsに含まれない(WorkspacesとWindowsの
    // 不整合を模す)。MissingPlacementはdecideのremovable対象外なのでRejectWholePlanになる。
    let w1, w2 = WindowId 1L, WindowId 2L
    let ctx = ctxWith [ w1 ]
    let state', effects, _ = reconcileWorkspace 10L registry monocleRef ctx (managementOf [ w1; w2 ]) (aliveOf [ w1; w2 ]) initial
    Assert.Empty(effects)
    Assert.Empty(state'.Pending)

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

[<Fact>]
let ``観測された矩形がPendingのTargetRectと一致すればSatisfiedになりPendingが消える`` () =
    let w = WindowId 1L
    let afterReconcile, _, _ = reconcileWorkspace 10L registry monocleRef (ctxWith [ w ]) (managementOf [ w ]) (aliveOf [ w ]) initial
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
    let afterReconcile, _, _ = reconcileWorkspace 10L registry monocleRef (ctxWith [ w ]) (managementOf [ w ]) (aliveOf [ w ]) afterDiscovered
    let afterObserved, _, _ = onWindowObserved 20L (observedSnapshot w workArea 1L) afterReconcile
    match Map.tryFind w afterObserved.Movability with
    | Some { Value = Movable; Confidence = Confirmed } -> ()
    | other -> Assert.True(false, sprintf "unexpected: %A" other)

[<Fact>]
let ``観測された矩形がTargetRectと一致しなければPendingは残る(Mismatchはこのラウンドでは静観)`` () =
    let w = WindowId 1L
    let afterReconcile, _, _ = reconcileWorkspace 10L registry monocleRef (ctxWith [ w ]) (managementOf [ w ]) (aliveOf [ w ]) initial
    let mismatchedRect = { workArea with Width = 100 }
    let afterObserved, _, _ = onWindowObserved 20L (observedSnapshot w mismatchedRect 1L) afterReconcile
    Assert.Equal(1, Map.count afterObserved.Pending)

// ===== Timeout（§7.2、v2.28でRetry実装） =====

[<Fact>]
let ``PendingDeadline満了は上限に達するまで同じTargetRectへSetRectを再発行し、Desiredは取り消さない`` () =
    let w = WindowId 1L
    let afterReconcile, _, _ = reconcileWorkspace 10L registry monocleRef (ctxWith [ w ]) (managementOf [ w ]) (aliveOf [ w ]) initial
    let rid0 = afterReconcile.Pending |> Map.toSeq |> Seq.head |> fst
    let afterRetry1, effects1, timerCmds1 = onPendingDeadlineTimerFired (10L + pendingDeadlineMs) rid0 noDpi afterReconcile
    Assert.Equal(1, Map.count afterRetry1.Pending)
    Assert.Equal(Some (Shown (Some workArea)), Map.tryFind w afterRetry1.Desired)
    Assert.Equal(Some 1, Map.tryFind w afterRetry1.RetryAttempts)
    let rid1 = afterRetry1.Pending |> Map.toSeq |> Seq.head |> fst
    Assert.NotEqual(rid0, rid1) // 新しいRequestIdで再発行する
    Assert.Equal<Effect list>([ SetRect (rid1, w, workArea) ], effects1)
    Assert.NotEmpty(timerCmds1) // 新しいPendingDeadlineを始動する

[<Fact>]
let ``PendingDeadline満了が上限回数続くと、最終的にPendingとDesiredの両方を取り消す(次のReconcileで最初から再試行させる)`` () =
    let w = WindowId 1L
    let afterReconcile, _, _ = reconcileWorkspace 10L registry monocleRef (ctxWith [ w ]) (managementOf [ w ]) (aliveOf [ w ]) initial
    let rid0 = afterReconcile.Pending |> Map.toSeq |> Seq.head |> fst
    // pendingRetryMaxAttempts回（既定3回）Timeoutを発火させ続けると、最後は単発の諦めと同じ終端になる。
    let rec driveToGiveUp (attemptsLeft: int) (now: int64) (rid: RequestId) (state: PlacementState) : PlacementState =
        let state', _, _ = onPendingDeadlineTimerFired now rid noDpi state
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
    let afterReconcile, _, _ = reconcileWorkspace 10L registry monocleRef (ctxWith [ w ]) (managementOf [ w ]) (aliveOf [ w ]) initial
    let rid0 = afterReconcile.Pending |> Map.toSeq |> Seq.head |> fst
    let afterRetry, _, _ = onPendingDeadlineTimerFired (10L + pendingDeadlineMs) rid0 noDpi afterReconcile
    Assert.Equal(Some 1, Map.tryFind w afterRetry.RetryAttempts)

    // Reconcileのやり直し（例：別のモニターへ移った、Layoutが変わった）で新しいTargetが計算される——
    // これは古いTargetへの再試行ではなく新しい意図なので、試行回数は0へ戻るべき。
    let differentWorkArea: PixelRect = { X = 0; Y = 0; Width = 1280; Height = 720 }
    let differentCtx: LayoutContext = { ctxWith [ w ] with WorkArea = differentWorkArea }
    let afterNewIntent, effects, _ = reconcileWorkspace 20L registry monocleRef differentCtx (managementOf [ w ]) (aliveOf [ w ]) afterRetry
    Assert.Equal<Effect list>([ SetRect (RequestId afterRetry.NextRequestId, w, differentWorkArea) ], effects)
    Assert.Equal(None, Map.tryFind w afterNewIntent.RetryAttempts)

[<Fact>]
let ``古い発火(期限未到達)のPendingDeadlineは無視する`` () =
    let w = WindowId 1L
    let afterReconcile, _, _ = reconcileWorkspace 10L registry monocleRef (ctxWith [ w ]) (managementOf [ w ]) (aliveOf [ w ]) initial
    let rid = afterReconcile.Pending |> Map.toSeq |> Seq.head |> fst
    let stillPending, effects, timerCmds = onPendingDeadlineTimerFired 10L rid noDpi afterReconcile // Now=IssuedAt、まだdeadline未到達
    Assert.Equal(1, Map.count stillPending.Pending)
    Assert.Empty(effects)
    Assert.Empty(timerCmds)

// ===== TopologySettled → Superseded（§7.2、v2.29） =====

[<Fact>]
let ``onTopologySettledは全PendingをCancelTimer付きで取り消し、RetryAttemptsも消す`` () =
    let w1, w2 = WindowId 1L, WindowId 2L
    let afterReconcile, _, _ = reconcileWorkspace 10L registry monocleRef (ctxWith [ w1 ]) (managementOf [ w1 ]) (aliveOf [ w1 ]) initial
    let rid1 = afterReconcile.Pending |> Map.toSeq |> Seq.head |> fst
    let afterRetry, _, _ = onPendingDeadlineTimerFired (10L + pendingDeadlineMs) rid1 noDpi afterReconcile
    Assert.Equal(Some 1, Map.tryFind w1 afterRetry.RetryAttempts)

    let afterSecondWindow, _, _ = reconcileWorkspace 20L registry (masterStackRef 0.5m) (ctxWith [ w1; w2 ]) (managementOf [ w1; w2 ]) (aliveOf [ w1; w2 ]) afterRetry
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
    let afterReconcile, _, _ = reconcileWorkspace 10L registry monocleRef (ctxWith [ w ]) (managementOf [ w ]) (aliveOf [ w ]) initial
    Assert.Equal(Some (Shown (Some workArea)), Map.tryFind w afterReconcile.Desired)

    let afterHidden, _, _ = onWorkspaceHidden 20L [ w ] afterReconcile
    let afterShown, _, _ = onWorkspaceShown 25L [ w ] afterHidden
    Assert.Equal(Some (Shown None), Map.tryFind w afterShown.Desired)

    // 同じWorkArea/TiledWindowsでも、Shown Noneから再開すると必ず新しいSetRectが発行される
    // （隠されていた間に実際の位置が変わっていたかもしれないため、古い矩形を信用しない、§4.4）。
    let afterReReconcile, effects, _ = reconcileWorkspace 30L registry monocleRef (ctxWith [ w ]) (managementOf [ w ]) (aliveOf [ w ]) afterShown
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
    let afterReconcile, _, _ = reconcileWorkspace 10L registry monocleRef (ctxWith [ w ]) (managementOf [ w ]) (aliveOf [ w ]) initial
    let rid = afterReconcile.Pending |> Map.toSeq |> Seq.head |> fst
    let state', timerCmds = onGateRejected [ rid ] afterReconcile
    Assert.Empty(state'.Pending)
    Assert.Equal(None, Map.tryFind w state'.Desired)
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
    let afterReconcile, _, _ = reconcileWorkspace 10L registry monocleRef (ctxWith [ w ]) (managementOf [ w ]) (aliveOf [ w ]) initial
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
    let afterReconcile, _, _ = reconcileWorkspace 10L registry monocleRef (ctxWith [ w ]) (managementOf [ w ]) (aliveOf [ w ]) initial
    let rid = pendingRidFor afterReconcile w
    let state', _, _, failureInfo = onEffectCallReturned 20L 2L rid (Ok ()) (Some 42L) afterReconcile
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
    let afterReconcile, _, _ = reconcileWorkspace 10L registry monocleRef (ctxWith [ w ]) (managementOf [ w ]) (aliveOf [ w ]) initial
    let rid = pendingRidFor afterReconcile w
    let error: NativeCallRecord = { Api = "SetWindowPos"; Space = Win32LastError; Code = 1400; ElapsedUs = 0L }
    let state', _, cmds, failureInfo = onEffectCallReturned 20L 2L rid (Error error) None afterReconcile
    // Retryしない分類でも、まだ生きているridのPendingDeadlineはCancelTimerする（v2.30で塞いだ既存の
    // 取りこぼし——以前はこの関数がTimerCommandを返せなかった）。
    Assert.Equal<TimerCommand<TimerId> list>([ CancelTimer (TimerId.Placement (PendingDeadline rid)) ], cmds)
    Assert.Empty(state'.Pending)
    Assert.Equal(None, Map.tryFind w state'.Desired)
    Assert.Equal(Some (w, TargetGone), failureInfo)

[<Fact>]
let ``ERROR_ACCESS_DENIEDはDenied OtherDenialに分類され、MovabilityをNotMovableへ修正する`` () =
    let w = WindowId 1L
    let afterReconcile, _, _ = reconcileWorkspace 10L registry monocleRef (ctxWith [ w ]) (managementOf [ w ]) (aliveOf [ w ]) initial
    let rid = pendingRidFor afterReconcile w
    let error: NativeCallRecord = { Api = "SetWindowPos"; Space = Win32LastError; Code = 5; ElapsedUs = 0L }
    let state', _, _, failureInfo = onEffectCallReturned 20L 2L rid (Error error) None afterReconcile
    Assert.Empty(state'.Pending)
    Assert.Equal(Some (w, Denied OtherDenial), failureInfo)
    match Map.tryFind w state'.Movability with
    | Some { Value = NotMovable } -> ()
    | other -> Assert.True(false, sprintf "unexpected: %A" other)

[<Fact>]
let ``TargetHungと分類された場合、Movabilityは変えない(HealthのResponsiveness修正は呼び出し側の責務)`` () =
    let w = WindowId 1L
    let afterReconcile, _, _ = reconcileWorkspace 10L registry monocleRef (ctxWith [ w ]) (managementOf [ w ]) (aliveOf [ w ]) initial
    let rid = pendingRidFor afterReconcile w
    let error: NativeCallRecord = { Api = "SendMessageTimeout"; Space = Win32LastError; Code = 1460; ElapsedUs = 0L }
    let state', _, _, failureInfo = onEffectCallReturned 20L 2L rid (Error error) None afterReconcile
    Assert.Empty(state'.Pending)
    Assert.Equal(Some (w, TargetHung), failureInfo)
    Assert.Equal(None, Map.tryFind w state'.Movability) // Movabilityはこの関数の対象外

[<Fact>]
let ``対応するPendingが無いRequestId(古い結果)は無害に無視する`` () =
    let error: NativeCallRecord = { Api = "SetWindowPos"; Space = Win32LastError; Code = 1400; ElapsedUs = 0L }
    let state', _, _, failureInfo = onEffectCallReturned 20L 2L (RequestId 999L) (Error error) None initial
    Assert.Equal(initial, state')
    Assert.Equal(None, failureInfo)

[<Fact>]
let ``Transient(ハング判定前のERROR_TIMEOUT)は即座に再試行し、Desiredは取り消さない(v2.30)`` () =
    let w = WindowId 1L
    let afterReconcile, _, _ = reconcileWorkspace 10L registry monocleRef (ctxWith [ w ]) (managementOf [ w ]) (aliveOf [ w ]) initial
    let rid0 = pendingRidFor afterReconcile w
    let error: NativeCallRecord = { Api = "SendMessageTimeout"; Space = Win32LastError; Code = 1460; ElapsedUs = 500L } // >0なのでTransient
    let afterRetry, effects, cmds, failureInfo = onEffectCallReturned 20L 2L rid0 (Error error) None afterReconcile
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
    let afterReconcile, _, _ = reconcileWorkspace 10L registry monocleRef (ctxWith [ w ]) (managementOf [ w ]) (aliveOf [ w ]) initial
    let error: NativeCallRecord = { Api = "SendMessageTimeout"; Space = Win32LastError; Code = 1460; ElapsedUs = 500L }
    let rec driveToGiveUp (attemptsLeft: int) (state: PlacementState) : PlacementState * TimerCommand<TimerId> list =
        let rid = pendingRidFor state w
        let state', _, cmds, _ = onEffectCallReturned 20L 2L rid (Error error) None state
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
    let afterReconcile, _, _ = reconcileWorkspace 10L registry monocleRef (ctxWith [ w ]) (managementOf [ w ]) (aliveOf [ w ]) initial
    let rid = pendingRidFor afterReconcile w
    let afterDispatched, _, _, _ = onEffectCallReturned 11L 2L rid (Ok ()) (Some 5L) afterReconcile
    let mismatchedRect = { workArea with X = workArea.X + 50 }
    let afterObserved, _, _ = onWindowObserved 20L (observedSnapshot w mismatchedRect 10L) afterDispatched
    let afterDeadline, effects, _ = onPendingDeadlineTimerFired (10L + pendingDeadlineMs) rid noDpi afterObserved
    Assert.Empty(afterDeadline.Pending)
    Assert.Empty(effects) // Retryしない(同じTargetへSetRectを再発行しない)
    Assert.Equal(Some (Shown (Some workArea)), Map.tryFind w afterDeadline.Desired) // Desiredは目標のまま残す
    Assert.Equal(None, Map.tryFind w afterDeadline.RetryAttempts)

[<Fact>]
let ``DispatchSeqが未確認(EffectCallReturned Okがまだ届いていない)間は、異なる矩形を観測してもMismatchと判定せずTimeout/Retryのまま(v2.42)`` () =
    let w = WindowId 1L
    let afterReconcile, _, _ = reconcileWorkspace 10L registry monocleRef (ctxWith [ w ]) (managementOf [ w ]) (aliveOf [ w ]) initial
    let rid = pendingRidFor afterReconcile w
    let differentRect = { workArea with X = workArea.X + 50 }
    let afterObserved, _, _ = onWindowObserved 20L (observedSnapshot w differentRect 10L) afterReconcile
    let afterDeadline, effects, _ = onPendingDeadlineTimerFired (10L + pendingDeadlineMs) rid noDpi afterObserved
    Assert.Equal(1, Map.count afterDeadline.Pending)
    Assert.NotEmpty(effects)

[<Fact>]
let ``DispatchSeqより古い(発行前に取得された)観測はMismatch判定に使わない、Timeout/Retryのまま(v2.42)`` () =
    let w = WindowId 1L
    let afterReconcile, _, _ = reconcileWorkspace 10L registry monocleRef (ctxWith [ w ]) (managementOf [ w ]) (aliveOf [ w ]) initial
    let rid = pendingRidFor afterReconcile w
    let afterDispatched, _, _, _ = onEffectCallReturned 11L 2L rid (Ok ()) (Some 10L) afterReconcile
    let staleRect = { workArea with X = workArea.X + 999 }
    let afterObserved, _, _ = onWindowObserved 20L (observedSnapshot w staleRect 5L) afterDispatched // 5 < DispatchSeq(10)
    let afterDeadline, effects, _ = onPendingDeadlineTimerFired (10L + pendingDeadlineMs) rid noDpi afterObserved
    Assert.Equal(1, Map.count afterDeadline.Pending)
    Assert.NotEmpty(effects)

[<Fact>]
let ``発行後も観測がBaselineのまま変わらなければMismatchにしない(要求への無反応はTimeout/Retryのまま、オーナー確認済み)(v2.42)`` () =
    let w = WindowId 1L
    let baselineRect = { X = 10; Y = 10; Width = 300; Height = 200 }
    let afterDiscovered, _, _ = onWindowObserved 1L (observedSnapshot w baselineRect 0L) initial
    let afterReconcile, _, _ = reconcileWorkspace 10L registry monocleRef (ctxWith [ w ]) (managementOf [ w ]) (aliveOf [ w ]) afterDiscovered
    let rid = pendingRidFor afterReconcile w
    match Map.tryFind rid afterReconcile.Pending with
    | Some pr -> Assert.Equal(Some baselineRect, pr.Baseline)
    | None -> Assert.True(false, "expected a pending")
    let afterDispatched, _, _, _ = onEffectCallReturned 11L 2L rid (Ok ()) (Some 5L) afterReconcile
    let afterObserved, _, _ = onWindowObserved 20L (observedSnapshot w baselineRect 10L) afterDispatched // 発行後だが変化無し
    let afterDeadline, effects, _ = onPendingDeadlineTimerFired (10L + pendingDeadlineMs) rid noDpi afterObserved
    Assert.Equal(1, Map.count afterDeadline.Pending)
    Assert.NotEmpty(effects)

[<Fact>]
let ``supersedeされた旧Epochの目標が遅れて観測されても、新しいPendingのMismatchとして扱わない(v2.42)`` () =
    let w = WindowId 1L
    let afterReconcile1, _, _ = reconcileWorkspace 10L registry monocleRef (ctxWith [ w ]) (managementOf [ w ]) (aliveOf [ w ]) initial
    let rid0 = pendingRidFor afterReconcile1 w
    let oldTarget = workArea
    let afterDispatched0, _, _, _ = onEffectCallReturned 11L 2L rid0 (Ok ()) (Some 5L) afterReconcile1
    let narrowerCtx = { ctxWith [ w ] with WorkArea = { workArea with Width = workArea.Width - 100 } }
    let afterReconcile2, _, _ = reconcileWorkspace 15L registry monocleRef narrowerCtx (managementOf [ w ]) (aliveOf [ w ]) afterDispatched0
    let rid1 = pendingRidFor afterReconcile2 w
    Assert.NotEqual(rid0, rid1)
    match Map.tryFind w afterReconcile2.InFlightTargets with
    | Some targets -> Assert.Contains((IntentEpoch 0L, oldTarget), targets)
    | None -> Assert.True(false, "expected the superseded target to be tracked as in-flight")
    let afterDispatched1, _, _, _ = onEffectCallReturned 16L 2L rid1 (Ok ()) (Some 20L) afterReconcile2
    // 旧rid0のSetRectが遅れて適用され、旧Targetがそのまま観測される(新rid1のDispatchSeq(20)より新しい)。
    let afterObserved, _, _ = onWindowObserved 30L (observedSnapshot w oldTarget 25L) afterDispatched1
    let afterDeadline, effects, _ = onPendingDeadlineTimerFired (15L + pendingDeadlineMs) rid1 noDpi afterObserved
    Assert.Equal(1, Map.count afterDeadline.Pending) // Mismatchではない(旧Epochの目標として除外)、Timeout/Retryのまま
    Assert.NotEmpty(effects)

[<Fact>]
let ``onTopologySettledはInFlightTargetsも消す(v2.42)`` () =
    let w = WindowId 1L
    let afterReconcile1, _, _ = reconcileWorkspace 10L registry monocleRef (ctxWith [ w ]) (managementOf [ w ]) (aliveOf [ w ]) initial
    let narrowerCtx = { ctxWith [ w ] with WorkArea = { workArea with Width = workArea.Width - 100 } }
    let afterReconcile2, _, _ = reconcileWorkspace 15L registry monocleRef narrowerCtx (managementOf [ w ]) (aliveOf [ w ]) afterReconcile1
    Assert.NotEmpty(Map.tryFind w afterReconcile2.InFlightTargets |> Option.defaultValue [])
    let afterSettled, _ = onTopologySettled afterReconcile2
    Assert.Equal(None, Map.tryFind w afterSettled.InFlightTargets)

[<Fact>]
let ``onWindowVanishedはInFlightTargetsも消す(v2.42)`` () =
    let w = WindowId 1L
    let afterReconcile1, _, _ = reconcileWorkspace 10L registry monocleRef (ctxWith [ w ]) (managementOf [ w ]) (aliveOf [ w ]) initial
    let narrowerCtx = { ctxWith [ w ] with WorkArea = { workArea with Width = workArea.Width - 100 } }
    let afterReconcile2, _, _ = reconcileWorkspace 15L registry monocleRef narrowerCtx (managementOf [ w ]) (aliveOf [ w ]) afterReconcile1
    Assert.NotEmpty(Map.tryFind w afterReconcile2.InFlightTargets |> Option.defaultValue [])
    let afterVanished, _ = onWindowVanished w afterReconcile2
    Assert.Equal(None, Map.tryFind w afterVanished.InFlightTargets)

// ===== MinWidth（Accumulating、§4.5、v2.43。Mismatchの証拠をDIP正規化して蓄積する） =====

[<Fact>]
let ``幅が目標より広い矩形が観測されると、DIP正規化してMinWidthの証拠にする(v2.43)`` () =
    let w = WindowId 1L
    let m = MonitorId 1L
    let afterReconcile, _, _ = reconcileWorkspace 10L registry monocleRef (ctxWith [ w ]) (managementOf [ w ]) (aliveOf [ w ]) initial
    let rid = pendingRidFor afterReconcile w
    let afterDispatched, _, _, _ = onEffectCallReturned 11L 2L rid (Ok ()) (Some 5L) afterReconcile
    let widerRect = { workArea with Width = 2000 } // 要求(workArea.Width=1920)より広い=アプリの最小幅制約
    let afterObserved, _, _ = onWindowObserved 20L (observedSnapshotWithMonitor w widerRect m 10L) afterDispatched
    let dpiOf (mid: MonitorId) = if mid = m then Some 192 else None // 200%
    let afterDeadline, _, _ = onPendingDeadlineTimerFired (10L + pendingDeadlineMs) rid dpiOf afterObserved
    match Map.tryFind w afterDeadline.MinWidth with
    | Some { Value = v; Confidence = Inferred _ } -> Assert.Equal(2000 * 96 / 192, v) // DIPへ正規化(200%なので半分)
    | other -> Assert.True(false, sprintf "expected an Inferred MinWidth belief, got %A" other)

[<Fact>]
let ``幅が目標より広くない観測はMismatchとして終端してもMinWidthの証拠にしない(v2.43)`` () =
    let w = WindowId 1L
    let m = MonitorId 1L
    let afterReconcile, _, _ = reconcileWorkspace 10L registry monocleRef (ctxWith [ w ]) (managementOf [ w ]) (aliveOf [ w ]) initial
    let rid = pendingRidFor afterReconcile w
    let afterDispatched, _, _, _ = onEffectCallReturned 11L 2L rid (Ok ()) (Some 5L) afterReconcile
    // 幅は要求と同じだがX位置が違う：Mismatchにはなるが、幅は広がっていないのでMinWidthの証拠ではない。
    let shiftedRect = { workArea with X = workArea.X + 50 }
    let afterObserved, _, _ = onWindowObserved 20L (observedSnapshotWithMonitor w shiftedRect m 10L) afterDispatched
    let dpiOf (mid: MonitorId) = if mid = m then Some 96 else None
    let afterDeadline, _, _ = onPendingDeadlineTimerFired (10L + pendingDeadlineMs) rid dpiOf afterObserved
    Assert.Empty(afterDeadline.Pending) // Mismatchとして終端はする
    Assert.Equal(None, Map.tryFind w afterDeadline.MinWidth) // だがMinWidthの証拠にはしない

[<Fact>]
let ``モニターのDPIが分からない場合は、幅が広い観測でもMinWidthの証拠にしない(v2.43)`` () =
    let w = WindowId 1L
    let m = MonitorId 1L
    let afterReconcile, _, _ = reconcileWorkspace 10L registry monocleRef (ctxWith [ w ]) (managementOf [ w ]) (aliveOf [ w ]) initial
    let rid = pendingRidFor afterReconcile w
    let afterDispatched, _, _, _ = onEffectCallReturned 11L 2L rid (Ok ()) (Some 5L) afterReconcile
    let widerRect = { workArea with Width = 2000 }
    let afterObserved, _, _ = onWindowObserved 20L (observedSnapshotWithMonitor w widerRect m 10L) afterDispatched
    let afterDeadline, _, _ = onPendingDeadlineTimerFired (10L + pendingDeadlineMs) rid noDpi afterObserved // dpiOfが常にNone
    Assert.Empty(afterDeadline.Pending)
    Assert.Equal(None, Map.tryFind w afterDeadline.MinWidth)

[<Fact>]
let ``同じ最小幅が繰り返し観測されると、値は変えず根拠(Inferred list)を追加する(v2.43)`` () =
    let w = WindowId 1L
    let m = MonitorId 1L
    let dpiOf (mid: MonitorId) = if mid = m then Some 96 else None
    let widerRect = { workArea with Width = 2000 } // 96dpiなのでDIP=物理px、常にこの幅を観測する(固定の最小幅)

    let afterReconcile1, _, _ = reconcileWorkspace 10L registry monocleRef (ctxWith [ w ]) (managementOf [ w ]) (aliveOf [ w ]) initial
    let rid1 = pendingRidFor afterReconcile1 w
    let afterDispatched1, _, _, _ = onEffectCallReturned 11L 2L rid1 (Ok ()) (Some 5L) afterReconcile1
    let afterObserved1, _, _ = onWindowObserved 20L (observedSnapshotWithMonitor w widerRect m 10L) afterDispatched1
    let afterDeadline1, _, _ = onPendingDeadlineTimerFired (10L + pendingDeadlineMs) rid1 dpiOf afterObserved1
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
    let afterReconcile2, _, _ = reconcileWorkspace 15L registry monocleRef narrowerCtx (managementOf [ w ]) (aliveOf [ w ]) afterIntermediate
    let rid2 = pendingRidFor afterReconcile2 w
    let afterDispatched2, _, _, _ = onEffectCallReturned 16L 2L rid2 (Ok ()) (Some 20L) afterReconcile2
    let afterObserved2, _, _ = onWindowObserved 30L (observedSnapshotWithMonitor w widerRect m 25L) afterDispatched2
    let afterDeadline2, _, _ = onPendingDeadlineTimerFired (15L + pendingDeadlineMs) rid2 dpiOf afterObserved2
    match Map.tryFind w afterDeadline2.MinWidth with
    | Some { Value = v; Confidence = Inferred refs } ->
        Assert.Equal(2000, v) // 値は変わらない
        Assert.Equal(2, List.length refs) // 根拠が追加された(1件→2件)
    | other -> Assert.True(false, sprintf "expected an accumulated Inferred MinWidth belief, got %A" other)

[<Fact>]
let ``onWindowVanishedはMinWidthも消す(v2.43)`` () =
    let w = WindowId 1L
    let m = MonitorId 1L
    let afterReconcile, _, _ = reconcileWorkspace 10L registry monocleRef (ctxWith [ w ]) (managementOf [ w ]) (aliveOf [ w ]) initial
    let rid = pendingRidFor afterReconcile w
    let afterDispatched, _, _, _ = onEffectCallReturned 11L 2L rid (Ok ()) (Some 5L) afterReconcile
    let widerRect = { workArea with Width = 2000 }
    let afterObserved, _, _ = onWindowObserved 20L (observedSnapshotWithMonitor w widerRect m 10L) afterDispatched
    let dpiOf (mid: MonitorId) = if mid = m then Some 96 else None
    let afterDeadline, _, _ = onPendingDeadlineTimerFired (10L + pendingDeadlineMs) rid dpiOf afterObserved
    Assert.NotEmpty(Map.tryFind w afterDeadline.MinWidth |> Option.toList)
    let afterVanished, _ = onWindowVanished w afterDeadline
    Assert.Equal(None, Map.tryFind w afterVanished.MinWidth)

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
    let afterReconcile, _, _ = reconcileWorkspace 30L registry monocleRef (ctxWith [ w ]) (managementOf [ w ]) (aliveOf [ w ]) afterCrossCheck
    Assert.Equal(None, Map.tryFind w afterReconcile.DpiCrossing)
