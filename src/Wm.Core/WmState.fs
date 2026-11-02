/// ADR-019実装順15。`WmState`型 → `RuntimePolicy`型 → `StepResult`型 → `step`関数（architecture.md §4.4/§7.1）。
/// 「最初はTopologyの関わる段だけ配線する」：WmState型は最初はTopologyのフィールドだけを持ち、
/// 他のドメインを実装するたびにそのドメインのフィールドをWmStateへ追加していく（ADR-019決定8）。
module Wm.Core.WmState

open Wm.Contracts.Identifiers
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

/// `step`が各段へ配線する環境（状態ではない。§3.3「RuntimePolicyは環境なので決定性ゲートの対象外」）。
/// Policy評価器・Layout RegistryはWm.Policy/Wm.Layoutsが実装された時点でここへ段⑤・⑦用のフィールドを追加する。
type RuntimePolicy = { Topology: TopologyConfig }

module RuntimePolicy =
    let defaults = { Topology = TopologyConfig.defaults }

/// 親の状態。最初はTopologyのフィールドだけを持ち、ドメインを実装するたびにフィールドを追加する
/// （ADR-019決定8。6フィールドを骨格だけ一括で用意する代替案は退けた）。Workspaces・Health・Focusを追加
/// （決定2：6ドメインに強制順序は無い）。
type WmState = { Topology: TopologyState; Workspaces: WorkspaceSet; Health: HealthState; Focus: FocusState }

let initial (runtimePolicy: RuntimePolicy) : WmState =
    { Topology = Topology.initial Map.empty
      Workspaces = WorkspaceSet.empty
      Health = Health.initial
      Focus = Focus.initial }

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

/// 窓の消滅（vanished）時のクリーンアップ（§4.4「Windows.Recordsと各ドメインの信念は窓が消滅した時点でのみ
/// 除去する」）。Workspaces・Focus.History・Health（Responsiveness/Reprobes）・SwitchSettleが保持する
/// focusTargetから、同じstepの中で原子的に除去する。
///
/// Opusレビュー4-2：これは「消滅そのもの」（`WindowRemoved`、将来はSticky規則のProcess不一致によるHWND
/// 再利用検知も含む）専用であり、「管理外化」（Excluded化・Unplaced化。消滅はしていないが
/// Workspaces/Historyからだけ外れる）とは意図的に分けている。後者はWindows実装時に`WindowUnmanaged`
/// （DomainEvent）を契機にし、Health等の信念は消さない（Excludedの窓も信念を持ち続け再分類に備える、§4.4）。
/// この2つを同じ関数に融合すると、Windows実装者が自然に「WindowUnmanagedでこの関数を呼ぶ」と書き換えて
/// Excluded窓の信念まで消してしまう、あるいは逆にProcess不一致時にHealthの信念を消し忘れる、という
/// 取りこぼしが起きる。
let private vanishWindow (w: WindowId) (state: WmState) : WmState =
    let wsOfRemoved = WorkspaceSet.workspaceOf w state.Workspaces
    let workspaces' = WorkspaceSet.removeWindow w state.Workspaces
    let history' = Focus.removeFromHistory w state.Focus.History

    // SwitchSettleが消滅した窓を指したまま残さない（Opusレビュー4-1）。除去後の同じWorkspaceのzipperの
    // Focus（Stackが空ならNone）に置き換える——そうしないと、期限到達時にPlacement結線後は
    // 消滅した窓へのRequestForegroundがゲートのTargetNotAliveで弾かれ、本来再要求すべき隣の窓へ
    // フォーカスが渡らない。
    let switchSettle' =
        match state.Focus.SwitchSettle with
        | Some (Some target, deadline) when target = w ->
            let newTarget =
                wsOfRemoved
                |> Option.bind (fun ws -> (WorkspaceSet.stateOf ws workspaces').Stack)
                |> Option.map (fun z -> z.Focus)
            Some (newTarget, deadline)
        | other -> other

    let health' =
        { state.Health with
            Responsiveness = Map.remove w state.Health.Responsiveness
            Reprobes = Map.remove w state.Health.Reprobes }

    { state with
        Workspaces = workspaces'
        Focus = { state.Focus with History = history'; SwitchSettle = switchSettle' }
        Health = health' }

/// Topology段（§7.1の段②）、Focusの観測反映（段①相当）、Health/FocusのTimerElapsedディスパッチ、
/// 窓の消滅クリーンアップ、focusedの変化を見たHistory/zipperのFocus更新（段⑥相当）を配線する。
/// 他ドメインが実装されるたびに該当する段を追加する（ADR-018：各ドメインは自分の状態だけを返し、
/// 他ドメインの状態を書かない）。
///
/// Opusレビュー5-3：§7.1の段の順そのままではなく、今実装済みのドメインだけを素直に並べている
/// （Windows/Placementが無い間はWindowsの分類やPlacementのReconcileが存在しないため、段③〜⑤⑦は
/// 配線先が無い）。Windows着手時には、ここを「段ごとの関数＋DomainEventの累積」という形へ
/// 組み直すことを前提に、各段の入出力をDomainEventの受け渡しとして明示しておく。
let step (runtimePolicy: RuntimePolicy) (state: WmState) (envelope: EventEnvelope<InputEvent>) : StepResult =
    let now = envelope.MonotonicTime
    let focusedBefore = focused state

    // 窓の消滅（vanished）のクリーンアップ。今はWindowRemovedだけが引き金になる。
    let state =
        match envelope.Input with
        | WindowRemoved w -> vanishWindow w state
        | _ -> state

    // 段①相当：観測の信念反映（全入力について無条件に最初に行う、§7.1）。今はFocusのForegroundだけを
    // 配線する（Windows/Placement/Healthの観測反映はそれぞれのドメインが実装されるまで未配線）。
    // 信念の値が変わったらForegroundChanged（§7.1表の段①が出すDomainEvent）を出す。
    let focusAfterBelief, stage1Events =
        match envelope.Input with
        | ForegroundObserved obs ->
            let revised = Focus.reviseForeground now state.Focus.Foreground obs
            let changed = LatestWins.value revised <> LatestWins.value state.Focus.Foreground
            { state.Focus with Foreground = revised },
            (if changed then [ ForegroundChanged (LatestWins.value revised |> Option.flatten) ] else [])
        | _ -> state.Focus, []

    // 段②：Topologyの原因・コマンドの受付。
    let topologyState', topologyResponse, topologyEvents, traces =
        Topology.update runtimePolicy.Topology now envelope.Input state.Topology

    // Health：ResponsivenessReprobeタイマーの発火だけを配線する（証拠の供給元はWindows/Placementが無いため未配線）。
    let healthState', healthTimerCmds =
        match envelope.Input with
        | TimerElapsed (TimerId.Health (ResponsivenessReprobe w)) -> Health.onReprobeTimerFired now w state.Health
        | _ -> state.Health, []

    // Focus：SwitchSettleタイマーの発火。「再要求が必要な窓」はPlacement（RequestIdの採番元）が
    // 無いためEffectへ変換できず、既知のギャップとして捨てる（TimerCommand（CancelTimer）だけは反映する）。
    let focusAfterTimer, _switchSettleReRequest, switchSettleTimerCmds =
        match envelope.Input with
        | TimerElapsed (TimerId.Focus WorkspaceSwitchSettle) -> Focus.onSwitchSettleTimerFired now focusAfterBelief
        | _ -> focusAfterBelief, None, []

    let stateAfterBeliefs =
        { state with
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

    // 段⑧ゲート（§6.2/§7.1）：他ドメインが未実装の間はAlive/Management等が全て空集合になる
    // （GateInputはこの時点で「全ての窓は生存していない」という保守的な既定値になるが、
    // Topology段はSetRect等の窓単位Effectを出さない（ObserveAllのみ）ため、今の範囲では実害が無い）。
    let gateInput =
        { Alive = Set.empty
          Management = Map.empty
          Monitors =
            match Topology.displays next.Topology with
            | NoMonitors -> []
            | Displays (current, visible) -> (current :: visible) |> List.map (fun sc -> sc.Monitor)
          NotMovable = Set.empty
          Hung = Set.empty
          HiddenByWm = Set.empty
          NeedsRestore = Set.empty }

    let gated, _rejected = GatedEffect.gate gateInput topologyResponse.Actions

    // 段①・段②のDomainEventは、消費する後続の段（Topologyの`current`追従、Placementの配置判断等）が
    // まだ無いため捨てる（Opusレビュー5-3。Windows/Placement実装時にここへ配線を追加する）。
    ignore stage1Events
    ignore topologyEvents

    { Next = next
      Effects = gated
      Timers = topologyResponse.TimerCommands @ healthTimerCmds @ switchSettleTimerCmds
      Trace = traces }
