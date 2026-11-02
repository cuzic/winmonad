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
open Wm.Core.Windows
open Wm.Policy.PolicyTypes
open Wm.Policy.PolicyEvaluator

/// `step`が各段へ配線する環境（状態ではない。§3.3「RuntimePolicyは環境なので決定性ゲートの対象外」）。
/// Windowsドメインの実装に伴い、段⑤が読む採用中Policy用のフィールドを追加する（ADR-019実装順14）。
/// `PatternSet`は`Policy`から一度だけ計算し（SHA256のパターンハッシュをstepごとに再計算しないため）、
/// 両者は常に対になって更新される（`PolicyActivated`の配線は未実装、21章の未決事項のまま）。
type RuntimePolicy =
    { Topology: TopologyConfig
      Policy: PolicyIR
      PatternSet: TitlePatternSet
      /// §5.4「Unknownには期限を設ける」。実測値未確定のため暫定値（TopologyConfigのSettleMs/GraceMsと同様）。
      ClassificationTimeoutMs: int64 }

module RuntimePolicy =
    let defaults =
        { Topology = TopologyConfig.defaults
          Policy = PolicyIR.defaults
          PatternSet = buildTitlePatternSet PolicyIR.defaults
          ClassificationTimeoutMs = 3_000L }

/// 親の状態。最初はTopologyのフィールドだけを持ち、ドメインを実装するたびにフィールドを追加する
/// （ADR-019決定8。6フィールドを骨格だけ一括で用意する代替案は退けた）。Workspaces・Health・Focus・
/// Windowsを追加（決定2：6ドメインに強制順序は無い）。
type WmState =
    { Topology: TopologyState
      Workspaces: WorkspaceSet
      Health: HealthState
      Focus: FocusState
      Windows: WindowRegistry }

let initial (runtimePolicy: RuntimePolicy) : WmState =
    { Topology = Topology.initial Map.empty
      Workspaces = WorkspaceSet.empty
      Health = Health.initial
      Focus = Focus.initial
      Windows = Windows.initial }

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
/// 対応する待ちのタイマーが動いていれば`CancelTimer`を返す。Reprobes（Health）とAwaitingEvidenceの
/// ClassificationDeadline（Windows）の両方が対象——`Map.remove`だけではタイマー実行器側のエントリが
/// 残り、消滅した窓へ向けて後から（二度と意味を持たない）発火が起こり得る。
let private vanishWindow (w: WindowId) (state: WmState) : WmState * TimerCommand<TimerId> list =
    let state' = removeFromWorkspacesAndFocus w state
    let reprobeCancel =
        if Map.containsKey w state'.Health.Reprobes then [ CancelTimer (TimerId.Health (ResponsivenessReprobe w)) ] else []
    let classificationCancel =
        match Map.tryFind w state'.Windows.Records with
        | Some { Classification = ClassificationState.AwaitingEvidence _ } -> [ CancelTimer (TimerId.Windows (ClassificationDeadline w)) ]
        | _ -> []
    let next =
        { state' with
            Health =
                { state'.Health with
                    Responsiveness = Map.remove w state'.Health.Responsiveness
                    Reprobes = Map.remove w state'.Health.Reprobes }
            Windows = Windows.onWindowVanished w state'.Windows }
    next, reprobeCancel @ classificationCancel

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

/// Topology段（§7.1の段②）、Focusの観測反映（段①相当）、Windowsの信念反映・分類（段①⑤相当）、
/// Health/FocusのTimerElapsedディスパッチ、窓の消滅クリーンアップ、focusedの変化を見たHistory/zipperの
/// Focus更新（段⑥相当）を配線する。他ドメインが実装されるたびに該当する段を追加する
/// （ADR-018：各ドメインは自分の状態だけを返し、他ドメインの状態を書かない）。
///
/// Opusレビュー5-3：§7.1の段の順そのままではなく、今実装済みのドメインが処理できる入力の分だけ
/// 素直に並べている（Placementが無い間は段③④⑦の配線先が無い。`MinimizedStateChanged`/`StyleChanged`も
/// 同様に未配線、Windows.fsのファイル冒頭コメント参照）。表どおりの8段パイプラインへの完全な組み直しは
/// Placement着手時まで持ち越す。
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

    // 段①相当：観測の信念反映（全入力について無条件に最初に行う、§7.1）。FocusのForegroundを配線する
    // （Placementの観測反映はPlacementが実装されるまで未配線）。信念の値が変わったらForegroundChanged
    // （§7.1表の段①が出すDomainEvent）を出す。
    let focusAfterBelief, stage1Events =
        match envelope.Input with
        | ForegroundObserved obs ->
            let revised = Focus.reviseForeground now stateAfterWindows.Focus.Foreground obs
            let changed = LatestWins.value revised <> LatestWins.value stateAfterWindows.Focus.Foreground
            { stateAfterWindows.Focus with Foreground = revised },
            (if changed then [ ForegroundChanged (LatestWins.value revised |> Option.flatten) ] else [])
        | _ -> stateAfterWindows.Focus, []

    // 段②：Topologyの原因・コマンドの受付。
    let topologyState', topologyResponse, topologyEvents, traces =
        Topology.update runtimePolicy.Topology now envelope.Input stateAfterWindows.Topology

    // Health：ResponsivenessReprobeタイマーの発火だけを配線する（証拠の供給元はPlacementが無いため未配線）。
    let healthState', healthTimerCmds =
        match envelope.Input with
        | TimerElapsed (TimerId.Health (ResponsivenessReprobe w)) -> Health.onReprobeTimerFired now w stateAfterWindows.Health
        | _ -> stateAfterWindows.Health, []

    // Focus：SwitchSettleタイマーの発火。「再要求が必要な窓」はPlacement（RequestIdの採番元）が
    // 無いためEffectへ変換できず、既知のギャップとして捨てる（TimerCommand（CancelTimer）だけは反映する）。
    let focusAfterTimer, _switchSettleReRequest, switchSettleTimerCmds =
        match envelope.Input with
        | TimerElapsed (TimerId.Focus WorkspaceSwitchSettle) -> Focus.onSwitchSettleTimerFired now focusAfterBelief
        | _ -> focusAfterBelief, None, []

    let stateAfterBeliefs =
        { stateAfterWindows with
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

    // 段⑧ゲート（§6.2/§7.1）：Windowsの実装に伴い、Alive/Management/Hungを実際の状態から組み立てる
    // （以前はPlacementが無く窓単位Effectも発行していないため全て空集合にしていた。NotMovable/HiddenByWm/
    // NeedsRestoreはPlacementの信念が必要なため、引き続き空集合のまま、既知のギャップとして残す）。
    let gateInput =
        { Alive = next.Windows.Records |> Map.toSeq |> Seq.map fst |> Set.ofSeq
          Management =
            next.Windows.Records
            |> Map.toSeq
            |> Seq.choose (fun (w, r) -> match r.Classification with Classified (eff, _) -> Some (w, eff) | _ -> None)
            |> Map.ofSeq
          Monitors =
            match Topology.displays next.Topology with
            | NoMonitors -> []
            | Displays (current, visible) -> (current :: visible) |> List.map (fun sc -> sc.Monitor)
          NotMovable = Set.empty
          Hung =
            next.Health.Responsiveness
            |> Map.toSeq
            |> Seq.filter (fun (_, b) -> b.Value = Hung)
            |> Seq.map fst
            |> Set.ofSeq
          HiddenByWm = Set.empty
          NeedsRestore = Set.empty }

    let gated, _rejected = GatedEffect.gate gateInput topologyResponse.Actions

    // 段①・段②のDomainEventは、消費する後続の段（Topologyの`current`追従、Placementの配置判断等）が
    // まだ無いため捨てる（Opusレビュー5-3。Placement実装時にここへ配線を追加する）。
    ignore stage1Events
    ignore topologyEvents

    { Next = next
      Effects = gated
      Timers = topologyResponse.TimerCommands @ healthTimerCmds @ switchSettleTimerCmds @ windowsTimerCmds @ vanishTimerCmds
      Trace = traces }
