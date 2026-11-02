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
      ClassificationTimeoutMs = runtimePolicy.ClassificationTimeoutMs
      Movability = state.Placement.Movability }

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
            let s, e, t = Placement.onPendingDeadlineTimerFired now rid stateAfterWindows.Placement
            s, e, t, None
        | EffectCallReturned (rid, result) ->
            let s, e, t, failureInfo = Placement.onEffectCallReturned now envelope.Sequence rid result stateAfterWindows.Placement
            s, e, t, failureInfo
        | _ -> stateAfterWindows.Placement, [], [], None

    let stateAfterPlacement = { stateAfterWindows with Placement = placementState' }

    // §4.4「Movability = NotMovableならTiled/Floatingを Unplacedに読み替える」。Placementの
    // EffectCallReturned処理が`Denied OtherDenial`と判定した場合だけ、Windowsにその窓の分類を
    // Unplacedへ降格させる（Movability自体の修正はPlacement.fsが行い、再分類の反映はここで
    // WmState.stepが仲介する——TargetHungのHealth修正と同じ理由、ADR-018）。
    let windowsAfterMovability, movabilityEvents =
        match effectCallFailure with
        | Some (w, Denied OtherDenial) -> Windows.onMovabilityBecameNotMovable w stateAfterPlacement.Windows
        | _ -> stateAfterPlacement.Windows, []

    let stateAfterMovability =
        { stateAfterPlacement with Windows = windowsAfterMovability }
        |> fun s -> movabilityEvents |> List.fold applyWindowsEvent s

    // `MoveFocusedToWorkspace`（§7.4「xmonadのdeleteと同じ」、v2.27）：フォーカス中の窓を別のWorkspaceへ
    // 送る。`focused`がManagedWindow wを返す時点で、wのWorkspaceは必ずTopology.isShownなので
    // （`focused`の定義参照）、元のWorkspaceのzipperが窓を除いた後に残す隣の窓へRequestForegroundする
    // ことは常に意味を持つ。`WorkspaceSet.moveToWorkspace`が内部で呼ぶ`Zipper.remove`が「取り除いた窓が
    // Focusだった場合は隣へ移る」（xmonadのdelete）を既に実装しているので、ここでは移動と
    // RequestForegroundの発行だけを行う。`dest = srcWs`（同じWorkspaceへの移動）は並び順を乱すだけの
    // 無意味な操作なので何もしない。
    let stateAfterMovability, moveFocusedEffects =
        match envelope.Input, focusedBefore with
        | UserCommand (MoveFocusedToWorkspace dest), ManagedWindow w ->
            match WorkspaceSet.workspaceOf w stateAfterMovability.Workspaces with
            | Some srcWs when srcWs <> dest ->
                let workspaces' = WorkspaceSet.moveToWorkspace w dest stateAfterMovability.Workspaces
                match (WorkspaceSet.stateOf srcWs workspaces').Stack with
                | Some neighborZipper ->
                    let rid, placement' = Placement.allocateRequestId stateAfterMovability.Placement
                    { stateAfterMovability with Workspaces = workspaces'; Placement = placement' },
                    [ RequestForeground (rid, neighborZipper.Focus) ]
                | None -> { stateAfterMovability with Workspaces = workspaces' }, []
            | _ -> stateAfterMovability, []
        | _ -> stateAfterMovability, []

    // 段①相当：観測の信念反映（全入力について無条件に最初に行う、§7.1）。FocusのForegroundを配線する。
    // 信念の値が変わったらForegroundChanged（§7.1表の段①が出すDomainEvent）を出す。
    let focusAfterBelief, stage1Events =
        match envelope.Input with
        | ForegroundObserved obs ->
            let revised = Focus.reviseForeground now stateAfterMovability.Focus.Foreground obs
            let changed = LatestWins.value revised <> LatestWins.value stateAfterMovability.Focus.Foreground
            { stateAfterMovability.Focus with Foreground = revised },
            (if changed then [ ForegroundChanged (LatestWins.value revised |> Option.flatten) ] else [])
        | _ -> stateAfterMovability.Focus, []

    // 段②：Topologyの原因・コマンドの受付。
    let topologyState', topologyResponse, topologyEvents, traces =
        Topology.update runtimePolicy.Topology now envelope.Input stateAfterMovability.Topology

    // `UserCommand FocusMostRecent`（§4.4「focused = ManagedWindow _ならHistoryの2番目、Elsewhereなら
    // 先頭へ移る」、v2.38）：architecture.mdの表では行き先解決は段②のFocus、view遷移は段⑤のTopologyだが、
    // Placement着手前の現状の`step`は表どおりの8段パイプラインへの組み直しをまだ行っていない（ファイル
    // 冒頭コメント参照）。v2.27以降の他のUserCommand同様、ここで直接解決・反映する簡略形を採る。
    // `stateAfterMovability.Focus.History`はこの時点でまだ今回のstepによる更新を受けていない
    // （Historyの更新は段⑥相当、下記）ので、`focusedBefore`と対にして読める。行き先の窓wのWorkspaceが
    // hiddenなら`Topology.showWorkspace`（`SwitchWorkspace`と共有、ShownBy=UserView）で表示する——これにより
    // 下のWorkspaceHidden/Shown・CurrentChanged/WorkspaceShown(UserView)の既存の受け口
    // （Presence反映・RequestForeground）をそのまま再利用できる。zipperのFocusを先にwへ動かすので、
    // 下のswitchForegroundEffectsが自然にwへRequestForegroundする（Elsewhereだった場合も含め、常に
    // 「渡すべき窓」はwそのもの、架空のzipper位置ではない）。
    let topologyState', topologyEvents, stateAfterMovability, focusMostRecentEffects =
        match envelope.Input with
        | UserCommand FocusMostRecent ->
            let history = stateAfterMovability.Focus.History
            let target =
                match focusedBefore with
                | ManagedWindow _ -> history |> List.tryItem 1
                | Elsewhere -> history |> List.tryItem 0
            match target |> Option.bind (fun w -> WorkspaceSet.workspaceOf w stateAfterMovability.Workspaces |> Option.map (fun ws -> w, ws)) with
            | None -> topologyState', topologyEvents, stateAfterMovability, []
            | Some (w, ws) ->
                let workspaces' =
                    WorkspaceSet.setFocus ws w stateAfterMovability.Workspaces
                    |> Option.defaultValue stateAfterMovability.Workspaces
                let stateAfterMovability = { stateAfterMovability with Workspaces = workspaces' }
                let alreadyCurrent =
                    match Topology.displays topologyState' with
                    | Displays (current, _) -> current.Workspace = ws
                    | NoMonitors -> false
                if alreadyCurrent then
                    // 既にcurrentなWorkspace内の窓へ戻るだけ。`showWorkspace`はこの場合[]を返す
                    // （Topologyのview自体が変わらない）ので、下のCurrentChanged/WorkspaceShown(UserView)
                    // 経由のswitchForegroundEffectsは発生しない——ここで直接RequestForegroundする。
                    let rid, placementSt' = Placement.allocateRequestId stateAfterMovability.Placement
                    topologyState', topologyEvents, { stateAfterMovability with Placement = placementSt' }, [ RequestForeground (rid, w) ]
                else
                    let topologyState'', viewEvents = Topology.showWorkspace ws topologyState'
                    topologyState'', topologyEvents @ viewEvents, stateAfterMovability, []
        | _ -> topologyState', topologyEvents, stateAfterMovability, []

    // R5-1「アクティブなモニターの追従」とhiddenなWorkspaceの窓への`ForegroundFollow`（§4.4「更新規則」、
    // v2.39）：`stage1Events`の`ForegroundChanged`（信念の値が変わったstepに限るedge-triggered。state
    // から毎stepに適用するlevel-triggeredだと、`FocusMonitor`直後・`ForegroundObserved`が届く前の
    // 同じstepでcurrentが元へ戻ってしまう、R5-1の警告——ただし`ForegroundChanged`は`ForegroundObserved`
    // からしか出ず`UserCommand`とは同じstepで両立しないため、実際にはこの競合は構造的に起きない）を
    // 引き金にする。wが（step開始時点で）管理下の窓かどうかは`state.Workspaces`（今回のstepの変更を
    // 一切受けていない、関数の引数そのもの）で判定する——そのstepで初めて分類された窓はstep開始時には
    // 所属を持たないので、その窓へのフォアグラウンドの変化では追従しない（新しい窓は常にアクティブな
    // WSに入るため追従の必要が無い、architecture.md §7.1補足）。`ForegroundChanged`は`ForegroundObserved`
    // 入力からしか出ず、`windowsEvents`（分類）を生む入力（`WindowObserved`等）とは同じstepで両立しない
    // ため、現状の実装では`state.Workspaces`と`stateAfterMovability.Workspaces`は`ForegroundObserved`の
    // stepでは常に一致する——今はこの区別を実際には踏めないが、表どおりの8段パイプラインへの組み直し
    // （ファイル冒頭コメント）後に複数の原因が同じstepへ合流する余地に備えて、より仕様に忠実な
    // `state.Workspaces`を選んでおく。`Reconfiguring`中はDisplaysが未確定なので何もしない。
    let topologyState', topologyEvents, stateAfterMovability =
        match stage1Events |> List.tryPick (function ForegroundChanged wOpt -> Some wOpt | _ -> None) with
        | Some (Some w) when (match Topology.phase topologyState' with Reconfiguring _ -> false | _ -> true) ->
            match WorkspaceSet.workspaceOf w state.Workspaces with
            | None -> topologyState', topologyEvents, stateAfterMovability
            | Some ws ->
                match Topology.displays topologyState' with
                | NoMonitors -> topologyState', topologyEvents, stateAfterMovability
                | Displays (current, _) when current.Workspace = ws -> topologyState', topologyEvents, stateAfterMovability
                | Displays (_, visible) when visible |> List.exists (fun sc -> sc.Workspace = ws) ->
                    Topology.followForegroundToVisibleMonitor ws topologyState', topologyEvents, stateAfterMovability
                | Displays _ ->
                    let topologyState'', viewEvents = Topology.showWorkspaceAsForegroundFollow ws topologyState'
                    topologyState'', topologyEvents @ viewEvents, stateAfterMovability
        | _ -> topologyState', topologyEvents, stateAfterMovability

    // `TopologySettled`（Opusレビュー5-3、v2.29で配線）：モニター構成が確定したので、古い配置に基づく
    // 全Pendingを`Superseded`として一括で取り消す（`Placement.onTopologySettled`のコメント参照）。
    // 取り消した代わりを同じstepで必ず発行させるため、`shouldReconcile`（下記）もこのイベントで
    // 真にする——そうしないと取り消しただけで何も再配置されない空白が生まれる。
    let topologySettled = topologyEvents |> List.exists (function TopologySettled _ -> true | _ -> false)
    let stateAfterMovability, topologySettledTimerCmds =
        if topologySettled then
            let placementSt', cmds = Placement.onTopologySettled stateAfterMovability.Placement
            { stateAfterMovability with Placement = placementSt' }, cmds
        else
            stateAfterMovability, []

    // `WorkspaceHidden`/`WorkspaceShown`（v2.31で消費を開始。モニター接続・切断経由の`MonitorLifecycle.
    // reconcile`に加え、v2.32で`SwitchWorkspace`も同じ受け口を共有する）：該当Workspaceに属する窓を
    // `Placement.onWorkspaceHidden`/`onWorkspaceShown`へ渡し、`Desired`/`SetPresence`を更新する
    // （Placement.fsのコメント参照）。
    let windowsOfWorkspace (ws: WorkspaceId) : WindowId list =
        (WorkspaceSet.stateOf ws stateAfterMovability.Workspaces).Stack |> Option.map Zipper.toList |> Option.defaultValue []
    let hiddenWindows = topologyEvents |> List.choose (function WorkspaceHidden ws -> Some ws | _ -> None) |> List.collect windowsOfWorkspace
    let shownWindows = topologyEvents |> List.choose (function WorkspaceShown (ws, _) -> Some ws | _ -> None) |> List.collect windowsOfWorkspace
    let stateAfterMovability, presenceEffects =
        let placementSt1, e1 = Placement.onWorkspaceHidden hiddenWindows stateAfterMovability.Placement
        let placementSt2, e2 = Placement.onWorkspaceShown shownWindows placementSt1
        { stateAfterMovability with Placement = placementSt2 }, e1 @ e2

    // `CurrentChanged`/`WorkspaceShown (_, UserView)`（v2.32で消費を開始）：ユーザーが`SwitchWorkspace`で
    // 明示的に切り替えた先のWorkspaceへ、そのzipperのFocus（Stack=NoneならNone、RequestForegroundは
    // 発行しない）へ一度だけRequestForegroundする（§4.4「ユーザーの操作でWorkspaceが表示されたとき…
    // フォーカスを渡す窓はそのWorkspaceのzipperのFocus」）。アプリの自己アクティブ化
    // （`ForegroundFollow`）やモニター再接続（`Reconcile`）による表示ではフォーカスを要求しない
    // （§4.4、同じ理由はPlacement.fsのコメント参照）。切替直後の揺れを吸収する`SwitchSettle`は
    // このラウンドの対象外（Topology.fsのコメント参照）——ここでは一度だけ要求する。
    let userSwitchedWorkspaces =
        topologyEvents
        |> List.choose (function
            | CurrentChanged ws -> Some ws
            | WorkspaceShown (ws, UserView) -> Some ws
            | _ -> None)
    let stateAfterMovability, switchForegroundEffects =
        userSwitchedWorkspaces
        |> List.fold
            (fun (s: WmState, effects) ws ->
                match (WorkspaceSet.stateOf ws s.Workspaces).Stack with
                | None -> s, effects
                | Some z ->
                    let rid, placementSt' = Placement.allocateRequestId s.Placement
                    { s with Placement = placementSt' }, effects @ [ RequestForeground (rid, z.Focus) ])
            (stateAfterMovability, [])

    // `UserCommand FocusNext`/`FocusPrevious`（§4.4「アクティブなWorkspaceのzipperを回す」、v2.33）：
    // `WorkspaceSet.rotateNext`/`rotatePrevious`は既にTiled/Floating両方を巡回する意味で実装済み
    // （Floatingだけを外すフィルタはしない）。`focused`が`Elsewhere`でも、このzipper自身の`Focus`を
    // 基準に次/前へ移る（`focused`の値を経由しない——zipperが唯一の基準）。ユーザーが最小化した窓を
    // 巡回から外す規則（R7-5、`ObservedPresence`の信念が必要）はまだ対象外：FocusドメインはPlacementの
    // 信念を読む経路を持っていないため、専用のラウンドへ切り出す。回転後の新しいFocusへ一度だけ
    // RequestForegroundする。
    let stateAfterMovability, focusRotateEffects =
        match envelope.Input, activeWorkspace stateAfterMovability with
        | UserCommand FocusNext, Some ws | UserCommand FocusPrevious, Some ws ->
            let rotate = match envelope.Input with UserCommand FocusNext -> WorkspaceSet.rotateNext | _ -> WorkspaceSet.rotatePrevious
            let oldFocus = (WorkspaceSet.stateOf ws stateAfterMovability.Workspaces).Stack |> Option.map (fun z -> z.Focus)
            let workspaces' = rotate ws stateAfterMovability.Workspaces
            match (WorkspaceSet.stateOf ws workspaces').Stack with
            | Some z when Some z.Focus <> oldFocus ->
                // 窓が1つしかない等で回転してもFocusが変わらない場合は、無意味なRequestForegroundを
                // 発行しない（既にフォアグラウンドのはずの窓へ再要求しても害は無いはずだが、
                // 変化が無い入力には何もしないのがこのstepの他の反応と一貫する）。
                let rid, placementSt' = Placement.allocateRequestId stateAfterMovability.Placement
                { stateAfterMovability with Workspaces = workspaces'; Placement = placementSt' }, [ RequestForeground (rid, z.Focus) ]
            | _ -> { stateAfterMovability with Workspaces = workspaces' }, []
        | _ -> stateAfterMovability, []

    // `UserCommand CloseFocused`（§4.4「focused = Elsewhereなら何もしない」、v2.34）：`RequestClose`
    // （`WM_CLOSE`相当の丁寧な要求、アプリが拒否できる——8.4「窓を破壊・終了するEffectを提供しない」に
    // 反しない）を発行する。architecture.mdの§4.4本文は、より層を分けた経路（Workspacesが
    // `CloseRequested w`を出し、Placementが`RequestClose`へ変換する、§7.1段⑤/⑦の表参照）を描くが、
    // この経路は複数の入口から来る`FocusTargetRequested`/`RequestClose`候補を1つに調停するための
    // もの（R6-2「RequestForegroundは全経路のFocusTargetRequestedのうち最後の1件だけから作る」）。
    // 現在のstepは1入力につき1つのUserCommandしか処理しない（競合する候補が同じstep内で複数生じる
    // 経路が無い）ため、v2.27のMoveFocusedToWorkspace以降の3ラウンドと同じく、調停が要らない
    // この段階ではWmState.stepから直接Effectを発行する簡略形を採る——複数の経路が同じstepで競合する
    // 入口（`TopologySettled`のHeldFocusTarget再発行等、R7-3）を実装する時点で、この簡略化は見直しが
    // 必要になる。
    let stateAfterMovability, closeFocusedEffects =
        match envelope.Input, focused stateAfterMovability with
        | UserCommand CloseFocused, ManagedWindow w ->
            let rid, placementSt' = Placement.allocateRequestId stateAfterMovability.Placement
            { stateAfterMovability with Placement = placementSt' }, [ RequestClose (rid, w) ]
        | _ -> stateAfterMovability, []

    // `UserCommand PromoteToMaster`/`SwapWithMaster`（§4.4「コマンドの意味」、v2.35）：`focused`が
    // `ManagedWindow w`のときだけ、wが属するWorkspaceのzipperを並べ替える（xmonadの`shiftMaster`/
    // `swapMaster`、`Zipper.promoteToMaster`/`swapWithMaster`参照）。フォーカス中の窓自体は変わらない
    // （`z.Focus`は同じ値のまま並びの位置だけが変わる）のでRequestForegroundは発行しない——MasterStack
    // Layoutでの見た目の並びが変わるだけなので、代わりに`shouldReconcile`（下記）を拡張してこの
    // stepのReconcileが新しい並びでLayoutを計算し直すようにする。
    let stateAfterMovability =
        match envelope.Input, focused stateAfterMovability with
        | UserCommand PromoteToMaster, ManagedWindow w ->
            match WorkspaceSet.workspaceOf w stateAfterMovability.Workspaces with
            | Some ws -> { stateAfterMovability with Workspaces = WorkspaceSet.promoteToMaster ws stateAfterMovability.Workspaces }
            | None -> stateAfterMovability
        | UserCommand SwapWithMaster, ManagedWindow w ->
            match WorkspaceSet.workspaceOf w stateAfterMovability.Workspaces with
            | Some ws -> { stateAfterMovability with Workspaces = WorkspaceSet.swapWithMaster ws stateAfterMovability.Workspaces }
            | None -> stateAfterMovability
        | _ -> stateAfterMovability

    // `UserCommand ChangeLayout`（§4.4「コマンドの意味」、v2.37）：アクティブなWorkspaceの
    // `LayoutOverride`へ記録する（`WorkspaceState.LayoutOverride`の型直前のコメント参照：`None`なら
    // Policyの`LayoutRules`で選ぶ、`Some`なら明示的にそれを使う——`reconcileOneScreen`が読む）。
    // `PromoteToMaster`/`SwapWithMaster`とは違い、`focused`を基準にしない——Layoutの切替はWorkspace
    // 全体の設定であり、特定の窓がフォーカスされているかとは無関係（`focused = Elsewhere`でも働く）。
    let stateAfterMovability =
        match envelope.Input, activeWorkspace stateAfterMovability with
        | UserCommand (ChangeLayout layoutRef), Some ws ->
            { stateAfterMovability with Workspaces = WorkspaceSet.setLayoutOverride ws layoutRef stateAfterMovability.Workspaces }
        | _ -> stateAfterMovability

    // Health：ResponsivenessReprobeタイマーの発火と、PlacementのEffectCallReturned処理が`TargetHung`と
    // 判定した場合のResponsiveness修正（§7.2「EffectCallReturned(Error) → classify → TargetHung →
    // Cancelled ＋ Responsiveness:=Hung」。この信念修正自体はHealthの管轄なのでPlacement.fsではなく
    // ここで行う、ADR-018）を配線する。
    let healthState', healthTimerCmds =
        match envelope.Input with
        | TimerElapsed (TimerId.Health (ResponsivenessReprobe w)) -> Health.onReprobeTimerFired now w stateAfterMovability.Health
        | EffectCallReturned _ ->
            match effectCallFailure with
            | Some (w, TargetHung) -> Health.applyEvidence now w (FromEffectFailure TargetHung) stateAfterMovability.Health
            | _ -> stateAfterMovability.Health, []
        | _ -> stateAfterMovability.Health, []

    // Focus：SwitchSettleタイマーの発火。「再要求が必要な窓」はPlacement（RequestIdの採番元）が
    // 無いためEffectへ変換できず、既知のギャップとして捨てる（TimerCommand（CancelTimer）だけは反映する）。
    let focusAfterTimer, _switchSettleReRequest, switchSettleTimerCmds =
        match envelope.Input with
        | TimerElapsed (TimerId.Focus WorkspaceSwitchSettle) -> Focus.onSwitchSettleTimerFired now focusAfterBelief
        | _ -> focusAfterBelief, None, []

    let stateAfterBeliefs =
        { stateAfterMovability with
            Topology = topologyState'
            Health = healthState'
            Focus = focusAfterTimer }

    // 段⑥相当：focusedの導出値がstepの前後で変わったときだけHistory/zipperのFocusを更新する（R6-3）。
    let focusedAfter = focused stateAfterBeliefs

    let focusFinal =
        { stateAfterBeliefs.Focus with
            History = Focus.updateHistory focusedBefore focusedAfter stateAfterBeliefs.Focus.History }

    // v2.38で`focusedBefore <> focusedAfter`のガードを追加（バグ修正）：architecture.mdの表の出す
    // DomainEventは「Focus：FocusObserved w」で、Focusがそれを出す条件自体が「focusedの値がstep開始時から
    // 変わりManagedWindow wになったら」（すぐ上の`Focus.updateHistory`と同じ条件）。ガード無しだと、
    // 今回のstepでFocusedの信念（OSからの実際のForegroundObserved）が変わっていない間は毎stepこの資格無き
    // 再同期が走り、FocusNext/FocusPrevious等がこの直前のコードで動かしたzipperのFocusを、まだ古い
    // ままのFocus.Foreground信念の値へ毎回書き戻してしまう（RequestForegroundのEffect自体は資格通りの
    // 新しいFocusを使って計算済みなので実害は無いが、`next.Workspaces`のzipperのFocusとReconcileの
    // `ctx.Focused`が古い窓のままになる——OS側の実際のForegroundObserved確認が届く次のstepまで、
    // 内部状態が実際のコマンドの結果と食い違う）。
    let workspacesFinal =
        match focusedAfter with
        | ManagedWindow w when focusedBefore <> focusedAfter ->
            match WorkspaceSet.workspaceOf w stateAfterBeliefs.Workspaces with
            | Some ws -> WorkspaceSet.setFocus ws w stateAfterBeliefs.Workspaces |> Option.defaultValue stateAfterBeliefs.Workspaces
            | None -> stateAfterBeliefs.Workspaces
        | _ -> stateAfterBeliefs.Workspaces

    let next = { stateAfterBeliefs with Focus = focusFinal; Workspaces = workspacesFinal }

    // Windows/Healthの実際の状態から組み立てる（gateInputとReconcileの両方が使う共通の投影）。
    let aliveWindows = next.Windows.Records |> Map.toSeq |> Seq.map fst |> Set.ofSeq
    let managementByWindow =
        next.Windows.Records
        |> Map.toSeq
        |> Seq.choose (fun (w, r) -> match r.Classification with Classified (eff, _) -> Some (w, eff) | _ -> None)
        |> Map.ofSeq

    // 段⑦相当（Placement、Reconcile）：WindowClassified/WindowUnmanaged（windowsEvents）・WindowRemoved・
    // TopologySettled（v2.29。上で一括取り消したPendingの代わりを同じstepで発行する必要があるため）の
    // いずれかでWorkspaceの構成が変わった可能性がある場合に限り、現在表示されている全てのWorkspace
    // （アクティブなものだけでなく、他モニターに表示中の可視Workspaceも含む）を順にReconcileする
    // （v2.26。Placement.fsのファイル冒頭「対象外」が挙げていた2項目のうち「複数の可視Workspaceへの
    // 拡張」を閉じた）。
    let shouldReconcile =
        not (List.isEmpty windowsEvents)
        || (match envelope.Input with WindowRemoved _ -> true | _ -> false)
        || topologySettled
        // PromoteToMaster/SwapWithMaster（v2.35）：Workspaceの所属は変えないが、並び順が変わるので
        // MasterStack LayoutのTiledWindows順も変わる。windowsEvents（分類の変化）は出ないため、
        // 明示的にここで引き金にする。
        || (match envelope.Input with UserCommand PromoteToMaster | UserCommand SwapWithMaster -> true | _ -> false)
        // ChangeLayout（v2.37）：LayoutOverrideが変わるだけで窓の所属は変わらないため、同じ理由で
        // 明示的にここで引き金にする。
        || (match envelope.Input with UserCommand (ChangeLayout _) -> true | _ -> false)
        // `WorkspaceShown`経由のReconcileトリガー（v2.38で追加。既存の`SwitchWorkspace`（v2.32）の
        // バグ修正でもある）：`onWorkspaceShown`（上のpresenceEffects）は`Desired`を`Shown None`に
        // するだけで、実際のレイアウト矩形は計算しない。新しくcurrentへ入ってきたWorkspaceの窓は、
        // 直前にReconcileされてから分類・並び順が変わっていてもその間Reconcileされていない
        // （hiddenな間はこのstepの対象外、下のmatch）ため、ここで引き金にしないと`Desired`が
        // 矩形無しのまま残り`SetRect`が発行されない。`WorkspaceHidden`は自分自身を引き金にする必要は
        // 無い（hiddenになった側はもうreconcileOneScreenの対象外になるので、トリガーにしても何も
        // 起こらない）が、同じstepに両方出ることが多いため区別せず検査する。
        || (topologyEvents |> List.exists (function WorkspaceShown _ | WorkspaceHidden _ -> true | _ -> false))

    let reconcileOneScreen (placementSt: PlacementState, effects, cmds) (screen: Screen) =
        let wsState = WorkspaceSet.stateOf screen.Workspace next.Workspaces
        let stackList = wsState.Stack |> Option.map Zipper.toList |> Option.defaultValue []
        let tiledIds = stackList |> List.filter (fun w -> not (Set.contains w wsState.Floating))
        let focusedTiled =
            wsState.Stack
            |> Option.bind (fun z -> if Set.contains z.Focus wsState.Floating then None else Some z.Focus)
        let ctx: LayoutContext =
            { WorkArea = screen.Monitor.WorkArea
              Dpi = screen.Monitor.Dpi
              TiledWindows = tiledIds |> List.map (fun w -> { Id = w; MinWidthDip = None })
              Focused = focusedTiled }
        let monitorWidthDip = screen.Monitor.WorkArea.Width * 96 / screen.Monitor.Dpi
        // §4.4「ChangeLayoutの記録」（v2.37）：`LayoutOverride`が明示的に設定されていればそれを使い、
        // `None`のときだけPolicyの`LayoutRules`（`selectLayout`）で選ぶ（`WorkspaceState.LayoutOverride`
        // の型直前のコメント参照）。
        let layoutRef =
            wsState.LayoutOverride
            |> Option.defaultValue (selectLayout (List.length tiledIds) monitorWidthDip runtimePolicy.Policy)
        // PlanValidator.checkNoMissingPlacement（§6.2「TiledWindows ⊆ Alive ∩ Managed(Tiled)」、
        // 実際にはこの呼び出しでは等号が要求される）は、渡された`Management`/`Alive`に含まれる
        // 全てのTiled×Aliveな窓がこの`placements`に現れることを要求する。複数のWorkspaceを順に
        // Reconcileする今、グローバルな`managementByWindow`/`aliveWindows`をそのまま渡すと、
        // 他のWorkspaceに属する窓が「このWorkspaceの計画に無い」という誤検知でRejectWholePlanになる
        // ——このWorkspaceのStack（Tiled∪Floating、WindowClassifiedの反映先と一致）に属する窓だけへ
        // 絞り込む（単一のアクティブWorkspaceしか無かった前ラウンドまでは、たまたま全窓が同じ
        // Workspaceに属していたため表面化していなかった誤り）。
        let windowsInThisWorkspace = Set.ofList stackList
        let managementForScreen = managementByWindow |> Map.filter (fun w _ -> Set.contains w windowsInThisWorkspace)
        let aliveForScreen = Set.intersect aliveWindows windowsInThisWorkspace
        let st', e', t' = Placement.reconcileWorkspace now runtimePolicy.LayoutRegistry layoutRef ctx managementForScreen aliveForScreen placementSt
        st', effects @ e', cmds @ t'

    let placementAfterReconcile, reconcileEffects, reconcileTimerCmds =
        if not shouldReconcile then
            next.Placement, [], []
        else
            match Topology.displays next.Topology with
            | Displays (current, visible) -> (current :: visible) |> List.fold reconcileOneScreen (next.Placement, [], [])
            | NoMonitors -> next.Placement, [], [] // モニター0台。Layout計算もEffect発行も行わない（§4.4）

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

    let gated, rejected = GatedEffect.gate gateInput (topologyResponse.Actions @ placementEffects @ reconcileEffects @ moveFocusedEffects @ presenceEffects @ switchForegroundEffects @ focusRotateEffects @ closeFocusedEffects @ focusMostRecentEffects)

    // §7.2「GateRejected → Cancelled（Deadlineまで待たず同じstep内で即時に遷移する）」。
    let rejectedRequestIds = rejected |> List.choose fst
    let placementAfterGateRejection, gateRejectionTimerCmds = Placement.onGateRejected rejectedRequestIds next.Placement
    let finalNext = { next with Placement = placementAfterGateRejection }

    // 段①のDomainEventは、消費する後続の段（Topologyの`current`追従等）がまだ無いため捨てる
    // （Opusレビュー5-3）。段②のDomainEventのうち`TopologySettled`（v2.29）/`WorkspaceShown`/
    // `WorkspaceHidden`/`CurrentChanged`（v2.31・v2.32）は全て上で消費するようになった。
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
        @ topologySettledTimerCmds
        @ reconcileTimerCmds
        @ gateRejectionTimerCmds
      Trace = traces }
