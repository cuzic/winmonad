/// ADR-019実装順15。`WmState`型 → `RuntimePolicy`型 → `StepResult`型 → `step`関数（architecture.md §4.4/§7.1）。
/// 「最初はTopologyの関わる段だけ配線する」：WmState型は最初はTopologyのフィールドだけを持ち、
/// 他のドメインを実装するたびにそのドメインのフィールドをWmStateへ追加していく（ADR-019決定8）。
module Wm.Core.WmState

open Wm.Contracts.Identifiers
open Wm.Contracts.ErrorTypes
open Wm.Contracts.CoreCommand
open Wm.Contracts.Timers
open Wm.Contracts.GatedEffect
open Wm.Contracts.Beliefs
open Wm.Contracts.SharedVocabulary
open Wm.Core.DomainEvents
open Wm.Core.DecisionTrace
open Wm.Core.Topology
open Wm.Core.Workspaces
open Wm.Core.Health
open Wm.Core.Focus
open Wm.Core.Windows
open Wm.Core.Placement
open Wm.Policy.PolicyTypes
open Wm.Policy.PolicyEvaluator
open Wm.Layouts.LayoutRegistry

/// `step`が各段へ配線する環境（状態ではない。§3.3「RuntimePolicyは環境なので決定性ゲートの対象外」）。
/// Windowsドメインの実装に伴い、段⑤が読む採用中Policy用のフィールドを追加する（ADR-019実装順14）。
/// `PatternSet`は`Policy`から一度だけ計算し（SHA256のパターンハッシュをstepごとに再計算しないため）、
/// 両者は常に対になって更新される（`PolicyActivated`の配線は未実装、21章の未決事項のまま）。
type RuntimePolicy =
    { Topology: TopologyConfig
      Policy: PolicyIR
      PatternSet: TitlePatternSet
      /// §5.4「Unknownには期限を設ける」。実測値未確定のため暫定値（TopologyConfigのSettleMs/GraceMsと同様）。
      ClassificationTimeoutMs: int64
      /// §7.1段⑦「Reconcile」が`PolicyIR`の`LayoutRef`からF#関数を解決するためのRegistry（§5.6）。
      /// ADR-019実装順14、PlacementのPending/Desiredラウンドで追加した。
      LayoutRegistry: Registry }

module RuntimePolicy =
    let defaults =
        { Topology = TopologyConfig.defaults
          Policy = PolicyIR.defaults
          PatternSet = buildTitlePatternSet PolicyIR.defaults
          ClassificationTimeoutMs = 3_000L
          LayoutRegistry =
            Registry.empty
            |> Registry.register (Wm.Layouts.MasterStack.implementation "builtin-master-stack-v1")
            |> Registry.register (Wm.Layouts.Monocle.implementation "builtin-monocle-v1") }

/// 親の状態。最初はTopologyのフィールドだけを持ち、ドメインを実装するたびにフィールドを追加する
/// （ADR-019決定8。6フィールドを骨格だけ一括で用意する代替案は退けた）。Workspaces・Health・Focus・
/// Windows・Placementを追加（決定2：6ドメインに強制順序は無い）。Placementは今はGeometryの信念だけを
/// 持つ（Placement.fsのファイル冒頭コメント参照。Desired/Pending等は専用のラウンドで追加する）。
type WmState =
    { Topology: TopologyState
      Workspaces: WorkspaceSet
      Health: HealthState
      Focus: FocusState
      Windows: WindowRegistry
      Placement: PlacementState }

let initial (runtimePolicy: RuntimePolicy) : WmState =
    { Topology = Topology.initial Map.empty
      Workspaces = WorkspaceSet.empty
      Health = Health.initial
      Focus = Focus.initial
      Windows = Windows.initial
      Placement = Placement.initial }

type StepResult = {
    Next: WmState
    Effects: GatedEffect list
    Timers: TimerCommand<TimerId> list
    Trace: DecisionTrace list
}

/// §4.4「導出値。どのドメインの状態にもコピーを持たない」。
let activeWorkspace (s: WmState) =
    match Topology.displays s.Topology with
    | NoMonitors -> None
    | Displays (current, _) -> Some current.Workspace

/// 「今フォーカスされている窓」の唯一の定義（§4.4、R3-3/R4-4/R5-4）。状態として持たず、毎回導出する。
let focused (s: WmState) : FocusTarget =
    match LatestWins.value s.Focus.Foreground with
    | Some (Some w) ->
        match WorkspaceSet.workspaceOf w s.Workspaces with
        | Some ws when Topology.isShown ws s.Topology -> ManagedWindow w
        | _ -> Elsewhere
    | _ -> Elsewhere

/// Workspaces・Focus.History・SwitchSettleから窓を除く（「消滅」「管理外化」共通の部分、§4.4）。
/// SwitchSettleが除いた窓を指したまま残さない（Opusレビュー4-1）。除去後の同じWorkspaceのzipperの
/// Focus（Stackが空ならNone）に置き換える——そうしないと、期限到達時にPlacement結線後は除いた窓への
/// RequestForegroundがゲートのTargetNotAliveで弾かれ、本来再要求すべき隣の窓へフォーカスが渡らない。
let private removeFromWorkspacesAndFocus (w: WindowId) (state: WmState) : WmState =
    let wsOfRemoved = WorkspaceSet.workspaceOf w state.Workspaces
    let workspaces' = WorkspaceSet.removeWindow w state.Workspaces
    let history' = Focus.removeFromHistory w state.Focus.History
    let switchSettle' =
        match state.Focus.SwitchSettle with
        | Some (Some target, deadline) when target = w ->
            let newTarget =
                wsOfRemoved
                |> Option.bind (fun ws -> (WorkspaceSet.stateOf ws workspaces').Stack)
                |> Option.map (fun z -> z.Focus)
            Some (newTarget, deadline)
        | other -> other
    { state with
        Workspaces = workspaces'
        Focus = { state.Focus with History = history'; SwitchSettle = switchSettle' } }

/// 窓の消滅（vanished）時のクリーンアップ（§4.4「Windows.Recordsと各ドメインの信念は窓が消滅した時点でのみ
/// 除去する」）。Workspaces・Focus.History・SwitchSettleに加え、Health（Responsiveness/Reprobes）と
/// Windows（Records/Identity/ClassInputs）の信念も、同じstepの中で原子的に除去する。
///
/// Opusレビュー4-2：これは「消滅そのもの」（`WindowRemoved`、将来はSticky規則のProcess不一致によるHWND
/// 再利用検知も含む——後者はまだ未配線、Windows.fsのファイル冒頭コメント参照）専用であり、「管理外化」
/// （Excluded化・Unplaced化。消滅はしていないがWorkspaces/Focus.History/SwitchSettleからだけ外れる）とは
/// 意図的に分けている（`unmanageWindow`、下記）。後者はHealth等の信念を消さない（Excludedの窓も信念を
/// 持ち続け再分類に備える、§4.4）。この2つを同じ関数に融合すると、片方を書き換えたときにもう片方が壊れる
/// 取りこぼしが起きやすい。
///
/// Opusレビュー4-3（docs/review-phase-a-round1.md、v2.15でCore.Windows着手時に反映）：除去するMapに
/// 対応する待ちのタイマーが動いていれば`CancelTimer`を返す。Reprobes（Health）、AwaitingEvidenceの
/// ClassificationDeadline（Windows）、IntegrityReprobes（Placement、v2.19で追加）が対象——
/// `Map.remove`だけではタイマー実行器側のエントリが残り、消滅した窓へ向けて後から
/// （二度と意味を持たない）発火が起こり得る。
let private vanishWindow (w: WindowId) (state: WmState) : WmState * TimerCommand<TimerId> list =
    let state' = removeFromWorkspacesAndFocus w state
    let reprobeCancel =
        if Map.containsKey w state'.Health.Reprobes then [ CancelTimer (TimerId.Health (ResponsivenessReprobe w)) ] else []
    let classificationCancel =
        match Map.tryFind w state'.Windows.Records with
        | Some { Classification = ClassificationState.AwaitingEvidence _ } -> [ CancelTimer (TimerId.Windows (ClassificationDeadline w)) ]
        | _ -> []
    let placement', integrityReprobeCancel = Placement.onWindowVanished w state'.Placement
    let next =
        { state' with
            Health =
                { state'.Health with
                    Responsiveness = Map.remove w state'.Health.Responsiveness
                    Reprobes = Map.remove w state'.Health.Reprobes }
            Windows = Windows.onWindowVanished w state'.Windows
            Placement = placement' }
    next, reprobeCancel @ classificationCancel @ integrityReprobeCancel


/// 窓の「管理外化」（Excluded化。消滅はしていない）。Workspaces/Focus.History/SwitchSettleからだけ除き、
/// 各ドメインの信念（Health/Windows）は保持する（§4.4、`WindowUnmanaged`DomainEventの契機）。
let private unmanageWindow (w: WindowId) (state: WmState) : WmState = removeFromWorkspacesAndFocus w state

/// Windowsドメインが段①⑤で読む環境（§3.3、Windows.fsの`ClassificationCtx`参照）を、このstep開始時点の
/// 状態からその場で組み立てる。`ActiveWorkspace`/`ForegroundWindow`はstep開始時点の値を使う
/// （§7.1「(b)任意のドメインのstep開始時点の状態」。Windowsが処理する入力はForegroundObserved自体では
/// ないので、同じstep内でFocus.Foregroundが変わることはない）。
let private classificationCtx (runtimePolicy: RuntimePolicy) (now: int64) (state: WmState) : Windows.ClassificationCtx =
    { Now = now
      PatternSet = runtimePolicy.PatternSet
      Policy = runtimePolicy.Policy
      ActiveWorkspace = activeWorkspace state
      ForegroundWindow = LatestWins.value state.Focus.Foreground |> Option.flatten
      ClassificationTimeoutMs = runtimePolicy.ClassificationTimeoutMs }

/// Windowsが出した`WindowClassified`/`WindowUnmanaged`をWorkspaces/Focusへ反映する（§7.1段⑤→Workspaces）。
/// `WindowClassified`の`home`が`None`（「新しい好みは無い」、Windows.fsの`resolveHome`参照）なら、
/// 窓の現在のWorkspace所属、無ければ最小のWorkspaceId（R6-11）へフォールバックする。
let private applyWindowsEvent (state: WmState) (ev: DomainEvent) : WmState =
    match ev with
    | WindowClassified (w, eff, homeOpt) ->
        let home = homeOpt |> Option.orElse (WorkspaceSet.workspaceOf w state.Workspaces) |> Option.defaultValue (WorkspaceId 0)
        { state with Workspaces = WorkspaceSet.addWindow home w (eff = EffFloating) state.Workspaces }
    | WindowUnmanaged w -> unmanageWindow w state
    | _ -> state

/// Topology段（§7.1の段②）、Focusの観測反映（段①相当）、Windows/Placementの信念反映・分類（段①⑤相当）、
/// Health/FocusのTimerElapsedディスパッチ、窓の消滅クリーンアップ、focusedの変化を見たHistory/zipperの
/// Focus更新（段⑥相当）を配線する。他ドメインが実装されるたびに該当する段を追加する
/// （ADR-018：各ドメインは自分の状態だけを返し、他ドメインの状態を書かない）。
///
/// Opusレビュー5-3：§7.1の段の順そのままではなく、今実装済みのドメインが処理できる入力の分だけ
/// 素直に並べている。段⑦（Reconcile）はアクティブなWorkspace1つだけを対象に配線した（`StyleChanged`は
/// 依然未配線、Windows.fs/Placement.fsのファイル冒頭コメント参照）。表どおりの8段パイプラインへの
/// 完全な組み直し（複数の可視Workspace、`WorkspaceShown`/`Hidden`経由のReconcileトリガー等）は
/// 今後のラウンドへ持ち越す。
let step (runtimePolicy: RuntimePolicy) (state: WmState) (envelope: EventEnvelope<InputEvent>) : StepResult =
    let now = envelope.MonotonicTime
    let focusedBefore = focused state

    // 窓の消滅（vanished）のクリーンアップ。今はWindowRemovedだけが引き金になる。
    let state, vanishTimerCmds =
        match envelope.Input with
        | WindowRemoved w -> vanishWindow w state
        | _ -> state, []

    // 段①⑤相当（Windows）：WindowObserved/VisibilityChanged/CloakChangedからの信念反映と、
    // ClassificationDeadlineタイマー・ユーザーコマンド（ToggleFloating/SetManagement/ClearUserOverride）
    // による分類の確定を同じ入力について続けて行い、出たDomainEventをその場でWorkspaces/Focusへ反映する。
    let windowsCtx = classificationCtx runtimePolicy now state
    let windowsRegistry', windowsEvents, windowsTimerCmds =
        match envelope.Input with
        | WindowObserved snapshot -> Windows.onWindowObserved windowsCtx snapshot state.Windows
        | VisibilityChanged (w, obs) -> Windows.onVisibilityChanged windowsCtx w obs state.Windows
        | CloakChanged (w, obs) -> Windows.onCloakChanged windowsCtx w obs state.Windows
        | TimerElapsed (TimerId.Windows (ClassificationDeadline w)) -> Windows.onClassificationDeadline windowsCtx w state.Windows
        | UserCommand cmd -> Windows.onUserCommand windowsCtx cmd state.Windows
        | _ -> state.Windows, [], []

    let stateAfterWindows =
        { state with Windows = windowsRegistry' }
        |> fun s -> windowsEvents |> List.fold applyWindowsEvent s

    // 段①相当（Placement）：WindowObservedはWindowsと同じ入力から自分の信念（Geometry/Movability/
    // TargetIntegrity）だけを修正する（§7.1「Windows→Placement→Health→Focus」の順）。TargetIntegrityの
    // ProbeOnceタイマー（IntegrityReprobe）の発火、`EffectCallReturned`（Pendingの終端遷移の一部）もここで
    // 処理する。MinimizedStateChangedもここで配線する（Placement.fsのファイル冒頭コメント参照）。
    // `effectCallFailure`は`EffectCallReturned`が`TargetHung`と判定した場合に、Health側の
    // Responsiveness修正（Placementの管轄外）へ渡すための情報（下記）。
    let placementState', placementEffects, placementTimerCmds, effectCallFailure =
        match envelope.Input with
        | WindowObserved snapshot ->
            let s, e, t = Placement.onWindowObserved now snapshot stateAfterWindows.Placement
            s, e, t, None
        | MinimizedStateChanged (w, obs) -> Placement.onMinimizedStateChanged now w obs stateAfterWindows.Placement, [], [], None
        | TimerElapsed (TimerId.Placement (IntegrityReprobe w)) ->
            let s, e, t = Placement.onIntegrityReprobeTimerFired now w stateAfterWindows.Placement
            s, e, t, None
        | TimerElapsed (TimerId.Placement (PendingDeadline rid)) ->
            Placement.onPendingDeadlineTimerFired now rid stateAfterWindows.Placement, [], [], None
        | EffectCallReturned (rid, result) ->
            let s, failureInfo = Placement.onEffectCallReturned now envelope.Sequence rid result stateAfterWindows.Placement
            s, [], [], failureInfo
        | _ -> stateAfterWindows.Placement, [], [], None

    let stateAfterPlacement = { stateAfterWindows with Placement = placementState' }

    // 段①相当：観測の信念反映（全入力について無条件に最初に行う、§7.1）。FocusのForegroundを配線する。
    // 信念の値が変わったらForegroundChanged（§7.1表の段①が出すDomainEvent）を出す。
    let focusAfterBelief, stage1Events =
        match envelope.Input with
        | ForegroundObserved obs ->
            let revised = Focus.reviseForeground now stateAfterPlacement.Focus.Foreground obs
            let changed = LatestWins.value revised <> LatestWins.value stateAfterPlacement.Focus.Foreground
            { stateAfterPlacement.Focus with Foreground = revised },
            (if changed then [ ForegroundChanged (LatestWins.value revised |> Option.flatten) ] else [])
        | _ -> stateAfterPlacement.Focus, []

    // 段②：Topologyの原因・コマンドの受付。
    let topologyState', topologyResponse, topologyEvents, traces =
        Topology.update runtimePolicy.Topology now envelope.Input stateAfterPlacement.Topology

    // Health：ResponsivenessReprobeタイマーの発火と、PlacementのEffectCallReturned処理が`TargetHung`と
    // 判定した場合のResponsiveness修正（§7.2「EffectCallReturned(Error) → classify → TargetHung →
    // Cancelled ＋ Responsiveness:=Hung」。この信念修正自体はHealthの管轄なのでPlacement.fsではなく
    // ここで行う、ADR-018）を配線する。
    let healthState', healthTimerCmds =
        match envelope.Input with
        | TimerElapsed (TimerId.Health (ResponsivenessReprobe w)) -> Health.onReprobeTimerFired now w stateAfterPlacement.Health
        | EffectCallReturned _ ->
            match effectCallFailure with
            | Some (w, TargetHung) -> Health.applyEvidence now w (FromEffectFailure TargetHung) stateAfterPlacement.Health
            | _ -> stateAfterPlacement.Health, []
        | _ -> stateAfterPlacement.Health, []

    // Focus：SwitchSettleタイマーの発火。「再要求が必要な窓」はPlacement（RequestIdの採番元）が
    // 無いためEffectへ変換できず、既知のギャップとして捨てる（TimerCommand（CancelTimer）だけは反映する）。
    let focusAfterTimer, _switchSettleReRequest, switchSettleTimerCmds =
        match envelope.Input with
        | TimerElapsed (TimerId.Focus WorkspaceSwitchSettle) -> Focus.onSwitchSettleTimerFired now focusAfterBelief
        | _ -> focusAfterBelief, None, []

    let stateAfterBeliefs =
        { stateAfterPlacement with
            Topology = topologyState'
            Health = healthState'
            Focus = focusAfterTimer }

    // 段⑥相当：focusedの導出値がstepの前後で変わったときだけHistory/zipperのFocusを更新する（R6-3）。
    let focusedAfter = focused stateAfterBeliefs

    let focusFinal =
        { stateAfterBeliefs.Focus with
            History = Focus.updateHistory focusedBefore focusedAfter stateAfterBeliefs.Focus.History }

    let workspacesFinal =
        match focusedAfter with
        | ManagedWindow w ->
            match WorkspaceSet.workspaceOf w stateAfterBeliefs.Workspaces with
            | Some ws -> WorkspaceSet.setFocus ws w stateAfterBeliefs.Workspaces |> Option.defaultValue stateAfterBeliefs.Workspaces
            | None -> stateAfterBeliefs.Workspaces
        | Elsewhere -> stateAfterBeliefs.Workspaces

    let next = { stateAfterBeliefs with Focus = focusFinal; Workspaces = workspacesFinal }

    // Windows/Healthの実際の状態から組み立てる（gateInputとReconcileの両方が使う共通の投影）。
    let aliveWindows = next.Windows.Records |> Map.toSeq |> Seq.map fst |> Set.ofSeq
    let managementByWindow =
        next.Windows.Records
        |> Map.toSeq
        |> Seq.choose (fun (w, r) -> match r.Classification with Classified (eff, _) -> Some (w, eff) | _ -> None)
        |> Map.ofSeq

    // 段⑦相当（Placement、Reconcile）：WindowClassified/WindowUnmanaged（windowsEvents）かWindowRemovedで
    // アクティブなWorkspaceの構成が変わった可能性がある場合に限り、そのWorkspaceだけをReconcileする
    // （Placement.fsのファイル冒頭「対象外」：複数の可視Workspaceへの拡張、WorkspaceShown/Hidden経由の
    // トリガーはまだ無い）。
    let shouldReconcile =
        not (List.isEmpty windowsEvents)
        || (match envelope.Input with WindowRemoved _ -> true | _ -> false)

    let placementAfterReconcile, reconcileEffects, reconcileTimerCmds =
        if not shouldReconcile then
            next.Placement, [], []
        else
            match activeWorkspace next, Topology.displays next.Topology with
            | Some ws, Displays (current, _) ->
                let wsState = WorkspaceSet.stateOf ws next.Workspaces
                let stackList = wsState.Stack |> Option.map Zipper.toList |> Option.defaultValue []
                let tiledIds = stackList |> List.filter (fun w -> not (Set.contains w wsState.Floating))
                let focusedTiled =
                    wsState.Stack
                    |> Option.bind (fun z -> if Set.contains z.Focus wsState.Floating then None else Some z.Focus)
                let ctx: LayoutContext =
                    { WorkArea = current.Monitor.WorkArea
                      Dpi = current.Monitor.Dpi
                      TiledWindows = tiledIds |> List.map (fun w -> { Id = w; MinWidthDip = None })
                      Focused = focusedTiled }
                let monitorWidthDip = current.Monitor.WorkArea.Width * 96 / current.Monitor.Dpi
                let layoutRef = selectLayout (List.length tiledIds) monitorWidthDip runtimePolicy.Policy
                Placement.reconcileWorkspace now runtimePolicy.LayoutRegistry layoutRef ctx managementByWindow aliveWindows next.Placement
            | _ -> next.Placement, [], [] // モニター0台。Layout計算もEffect発行も行わない（§4.4）

    let next = { next with Placement = placementAfterReconcile }

    // 段⑧ゲート（§6.2/§7.1）：Windows/Placementの実装に伴い、Alive/Management/NotMovable/Hungを
    // 実際の状態から組み立てる（HiddenByWm/NeedsRestoreはDesired全体の反映が無いため、引き続き空集合の
    // まま、既知のギャップとして残す）。
    let gateInput =
        { Alive = aliveWindows
          Management = managementByWindow
          Monitors =
            match Topology.displays next.Topology with
            | NoMonitors -> []
            | Displays (current, visible) -> (current :: visible) |> List.map (fun sc -> sc.Monitor)
          NotMovable =
            next.Placement.Movability
            |> Map.toSeq
            |> Seq.filter (fun (_, b) -> b.Value = NotMovable)
            |> Seq.map fst
            |> Set.ofSeq
          Hung =
            next.Health.Responsiveness
            |> Map.toSeq
            |> Seq.filter (fun (_, b) -> b.Value = Hung)
            |> Seq.map fst
            |> Set.ofSeq
          HiddenByWm = Set.empty
          NeedsRestore = Set.empty }

    let gated, rejected = GatedEffect.gate gateInput (topologyResponse.Actions @ placementEffects @ reconcileEffects)

    // §7.2「GateRejected → Cancelled（Deadlineまで待たず同じstep内で即時に遷移する）」。
    let rejectedRequestIds = rejected |> List.choose fst
    let placementAfterGateRejection, gateRejectionTimerCmds = Placement.onGateRejected rejectedRequestIds next.Placement
    let finalNext = { next with Placement = placementAfterGateRejection }

    // 段①・段②のDomainEventは、消費する後続の段（Topologyの`current`追従等）がまだ無いため捨てる
    // （Opusレビュー5-3。段⑦自体はPlacementのReconcileとして配線済みになった）。
    ignore stage1Events
    ignore topologyEvents

    { Next = finalNext
      Effects = gated
      Timers =
        topologyResponse.TimerCommands
        @ healthTimerCmds
        @ switchSettleTimerCmds
        @ windowsTimerCmds
        @ vanishTimerCmds
        @ placementTimerCmds
        @ reconcileTimerCmds
        @ gateRejectionTimerCmds
      Trace = traces }
