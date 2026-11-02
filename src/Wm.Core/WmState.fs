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
/// （ADR-019決定8。6フィールドを骨格だけ一括で用意する代替案は退けた）。Workspaces・Healthを追加（決定2：
/// 6ドメインに強制順序は無い）。`step`はまだWorkspaces/Health向けの段を配線していないため、`{ state with ... }`で
/// 素通りする（いずれも不変なレコードなので、触れないフィールドは常にそのまま保たれる）。
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
    match s.Focus.Foreground with
    | Believed { Value = Some w } ->
        match WorkspaceSet.workspaceOf w s.Workspaces with
        | Some ws when Topology.isShown ws s.Topology -> ManagedWindow w
        | _ -> Elsewhere
    | _ -> Elsewhere

/// Topology段（§7.1の段②）に加え、ForegroundObservedに対するFocus/Workspaces段⑥相当
/// （Foreground信念の修正→focusedの変化を見てHistory/zipperのFocusを更新）と、
/// Health/FocusのTimerElapsedディスパッチを配線する。他ドメインが実装されるたびに該当する段を追加する
/// （ADR-018：各ドメインは自分の状態だけを返し、他ドメインの状態を書かない）。
let step (runtimePolicy: RuntimePolicy) (state: WmState) (envelope: EventEnvelope<InputEvent>) : StepResult =
    let now = envelope.MonotonicTime
    let focusedBefore = focused state

    let topologyState', topologyResponse, domainEvents, traces =
        Topology.update runtimePolicy.Topology now envelope.Input state.Topology

    // 窓の消滅時のクリーンアップ（§4.4「Windows.Recordsと各ドメインの信念は窓が消滅した時点でのみ除去する」。
    // Workspacesと`Focus.History`からは同じstepの中で原子的に除去する）。Windowsドメイン自体はまだ無いので
    // `Records`側の除去はWindows実装時に追加する。これが無いと、Workspacesから窓が消えても
    // `Focus.History`に残り続け、§15.1 #17(X3前半)が破れる経路が残る。
    let workspacesAfterRemoval, healthAfterRemoval, historyAfterRemoval =
        match envelope.Input with
        | WindowRemoved w ->
            WorkspaceSet.removeWindow w state.Workspaces,
            { state.Health with
                Responsiveness = Map.remove w state.Health.Responsiveness
                Reprobes = Map.remove w state.Health.Reprobes },
            Focus.removeFromHistory w state.Focus.History
        | _ -> state.Workspaces, state.Health, state.Focus.History

    // Health：ResponsivenessReprobeタイマーの発火だけを配線する（証拠の供給元はWindows/Placementが無いため未配線）。
    let healthState', healthTimerCmds =
        match envelope.Input with
        | TimerElapsed (Health (ResponsivenessReprobe w)) -> Health.onReprobeTimerFired now w healthAfterRemoval
        | _ -> healthAfterRemoval, []

    // Focus 段①相当：ForegroundObservedでForeground信念を修正する。
    let focusAfterBelief =
        match envelope.Input with
        | ForegroundObserved obs ->
            { state.Focus with
                Foreground = Focus.reviseForeground (Some state.Focus.Foreground) obs
                History = historyAfterRemoval }
        | _ -> { state.Focus with History = historyAfterRemoval }

    // Focus 段②相当：SwitchSettleタイマーの発火。「再要求が必要な窓」はPlacement（RequestIdの採番元）が
    // 無いためEffectへ変換できず、既知のギャップとして捨てる（TimerCommand（CancelTimer）だけは反映する）。
    let focusAfterTimer, _switchSettleReRequest, switchSettleTimerCmds =
        match envelope.Input with
        | TimerElapsed (Focus WorkspaceSwitchSettle) -> Focus.onSwitchSettleTimerFired now focusAfterBelief
        | _ -> focusAfterBelief, None, []

    let stateAfterBeliefs =
        { state with
            Topology = topologyState'
            Health = healthState'
            Focus = focusAfterTimer
            Workspaces = workspacesAfterRemoval }

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
          RestoreIssuedInBatch = Set.empty
          NotMovable = Set.empty
          Hung = Set.empty
          HiddenByWm = Set.empty }

    let gated, _rejected = GatedEffect.gate gateInput topologyResponse.Actions

    ignore domainEvents // 他ドメインが実装されるまで消費先が無い（WindowClassified等）

    { Next = next
      Effects = gated
      Timers = topologyResponse.TimerCommands @ healthTimerCmds @ switchSettleTimerCmds
      Trace = traces }
