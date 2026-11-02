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

// ===== Timeout（§7.2） =====

[<Fact>]
let ``PendingDeadline満了はPendingとDesiredの両方を取り消す(次のReconcileで最初から再試行させる)`` () =
    let w = WindowId 1L
    let afterReconcile, _, _ = reconcileWorkspace 10L registry monocleRef (ctxWith [ w ]) (managementOf [ w ]) (aliveOf [ w ]) initial
    let rid = afterReconcile.Pending |> Map.toSeq |> Seq.head |> fst
    let afterTimeout = onPendingDeadlineTimerFired (10L + pendingDeadlineMs) rid afterReconcile
    Assert.Empty(afterTimeout.Pending)
    Assert.Equal(None, Map.tryFind w afterTimeout.Desired)

[<Fact>]
let ``古い発火(期限未到達)のPendingDeadlineは無視する`` () =
    let w = WindowId 1L
    let afterReconcile, _, _ = reconcileWorkspace 10L registry monocleRef (ctxWith [ w ]) (managementOf [ w ]) (aliveOf [ w ]) initial
    let rid = afterReconcile.Pending |> Map.toSeq |> Seq.head |> fst
    let stillPending = onPendingDeadlineTimerFired 10L rid afterReconcile // Now=IssuedAt、まだdeadline未到達
    Assert.Equal(1, Map.count stillPending.Pending)

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

// ===== onEffectCallReturned（§7.2） =====

let private pendingRidFor (state: PlacementState) (w: WindowId) : RequestId =
    state.Pending |> Map.toSeq |> Seq.filter (fun (_, pr) -> pr.Window = w) |> Seq.map fst |> Seq.head

[<Fact>]
let ``Ok()はPendingを変えない(Satisfiedにするのは観測の一致だけ)`` () =
    let w = WindowId 1L
    let afterReconcile, _, _ = reconcileWorkspace 10L registry monocleRef (ctxWith [ w ]) (managementOf [ w ]) (aliveOf [ w ]) initial
    let rid = pendingRidFor afterReconcile w
    let state', failureInfo = onEffectCallReturned 20L 2L rid (Ok ()) afterReconcile
    Assert.Equal<Map<RequestId, PendingRequest>>(afterReconcile.Pending, state'.Pending)
    Assert.Equal(None, failureInfo)

[<Fact>]
let ``ERROR_INVALID_WINDOW_HANDLEはTargetGoneに分類され、PendingとDesiredを取り消す`` () =
    let w = WindowId 1L
    let afterReconcile, _, _ = reconcileWorkspace 10L registry monocleRef (ctxWith [ w ]) (managementOf [ w ]) (aliveOf [ w ]) initial
    let rid = pendingRidFor afterReconcile w
    let error: NativeCallRecord = { Api = "SetWindowPos"; Space = Win32LastError; Code = 1400; ElapsedUs = 0L }
    let state', failureInfo = onEffectCallReturned 20L 2L rid (Error error) afterReconcile
    Assert.Empty(state'.Pending)
    Assert.Equal(None, Map.tryFind w state'.Desired)
    Assert.Equal(Some (w, TargetGone), failureInfo)

[<Fact>]
let ``ERROR_ACCESS_DENIEDはDenied OtherDenialに分類され、MovabilityをNotMovableへ修正する`` () =
    let w = WindowId 1L
    let afterReconcile, _, _ = reconcileWorkspace 10L registry monocleRef (ctxWith [ w ]) (managementOf [ w ]) (aliveOf [ w ]) initial
    let rid = pendingRidFor afterReconcile w
    let error: NativeCallRecord = { Api = "SetWindowPos"; Space = Win32LastError; Code = 5; ElapsedUs = 0L }
    let state', failureInfo = onEffectCallReturned 20L 2L rid (Error error) afterReconcile
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
    let state', failureInfo = onEffectCallReturned 20L 2L rid (Error error) afterReconcile
    Assert.Empty(state'.Pending)
    Assert.Equal(Some (w, TargetHung), failureInfo)
    Assert.Equal(None, Map.tryFind w state'.Movability) // Movabilityはこの関数の対象外

[<Fact>]
let ``対応するPendingが無いRequestId(古い結果)は無害に無視する`` () =
    let error: NativeCallRecord = { Api = "SetWindowPos"; Space = Win32LastError; Code = 1400; ElapsedUs = 0L }
    let state', failureInfo = onEffectCallReturned 20L 2L (RequestId 999L) (Error error) initial
    Assert.Equal(initial, state')
    Assert.Equal(None, failureInfo)
