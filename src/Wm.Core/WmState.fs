/// ADR-019実装順15。`WmState`型 → `RuntimePolicy`型 → `StepResult`型 → `step`関数（architecture.md §4.4/§7.1）。
/// 「最初はTopologyの関わる段だけ配線する」：WmState型は最初はTopologyのフィールドだけを持ち、
/// 他のドメインを実装するたびにそのドメインのフィールドをWmStateへ追加していく（ADR-019決定8）。
module Wm.Core.WmState

open Wm.Contracts.Identifiers
open Wm.Contracts.ErrorTypes
open Wm.Contracts.Observation
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
/// 両者は常に対になって更新される。PolicyActivated実配線（vNext、architecture.md §14/§21）：
/// `InputEvent.PolicyActivated`自体は依然`step`で一切パターンマッチされない（下記の未配線のまま、
/// 使われない値として残す）——実際のライブ反映は`step`の外側、`Wm.Runtime.Reducer.ActivatePolicy`が
/// この`RuntimePolicy`環境そのものを丸ごと入れ替える経路で行う（`step`の純粋な署名——環境を引数で
/// 受け取るだけで返り値に含まない——を変えないための設計判断、`Reducer.fs`の`ActivatePolicy`型直前
/// コメント参照）。
type RuntimePolicy =
    { Topology: TopologyConfig
      Policy: PolicyIR
      PatternSet: TitlePatternSet
      /// §5.4「Unknownには期限を設ける」。実測値未確定のため暫定値（TopologyConfigのSettleMs/GraceMsと同様）。
      ClassificationTimeoutMs: int64
      /// `Focus.SwitchSettle`（§4.4/R4-5、Workspace切替直後の揺れの吸収）の待ち時間。CI実機スパイク
      /// S20/S20b（docs/spikes.md、自プロセス窓間の`SetWinEventHook(EVENT_SYSTEM_FOREGROUND)`配送、
      /// 単純なフォーカス交換・実際のhide→show→RequestForeground順序の両方）では一貫して2〜4msで
      /// 届き揺れは一度も観測されなかったが、S4が実デスクトップ再検証（S4r）で初めて限界が判明した
      /// 前例があるため、CIの数値そのままではなく十分な安全マージンを取った暫定値とする。
      /// 実デスクトップでの再検証（S20r）が持ち越しの課題。
      SwitchSettleMs: int64
      /// §7.1段⑦「Reconcile」が`PolicyIR`の`LayoutRef`からF#関数を解決するためのRegistry（§5.6）。
      /// ADR-019実装順14、PlacementのPending/Desiredラウンドで追加した。
      LayoutRegistry: Registry }

module RuntimePolicy =
    let defaults =
        { Topology = TopologyConfig.defaults
          Policy = PolicyIR.defaults
          PatternSet = buildTitlePatternSet PolicyIR.defaults
          ClassificationTimeoutMs = 3_000L
          SwitchSettleMs = 150L
          LayoutRegistry =
            Registry.empty
            |> Registry.register (Wm.Layouts.MasterStack.implementation "builtin-master-stack-v1")
            |> Registry.register (Wm.Layouts.Monocle.implementation "builtin-monocle-v1")
            |> Registry.register (Wm.Layouts.Columns.implementation "builtin-columns-v1") }

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

/// `runtimePolicy.Policy.WorkspaceAssignments`（§5.2「設定上のモニター割当」、R3-1）を初期状態の
/// `Topology.Assignments`へ写す（v2.80、既知のギャップ修正：`initial`が`runtimePolicy`引数を
/// 受け取っているにもかかわらず、長らく`Map.empty`を決め打ちしており、Policyで設定した割当が
/// 新しいモニターの出現時に一度も効かなかった——`MonitorLifecycle.reconcile`の`byAssignment`
/// フォールバック、`Topology.fs`型直前のコメント参照）。同じ`WorkspaceId`が複数回現れる場合は
/// `Map.ofList`の「後が勝つ」規則に委ねる——`PolicyAnalyzer.checkDuplicateWorkspaceAssignments`
/// （同ラウンドで追加）がこの状況自体を`Error`として検出するので、ここでは暗黙の規則をこれ以上
/// 明文化しない。Policy再適用（`PolicyActivated`）時の再読込は依然未配線のまま——vNextの
/// `Reducer.ActivatePolicy`（PolicyActivated実配線）は`RuntimePolicy.Policy`/`PatternSet`だけを
/// 入れ替え、ここ（`WmState.Topology.Assignments`、`initial`が一度だけ呼ばれた時点の値）は
/// 意図的に対象外のまま——`op=deploy`でWorkspaceAssignmentsを変えても、既存のTopology割当へは
/// Host再起動まで反映されない既知のギャップとして21章に残す。
let initial (runtimePolicy: RuntimePolicy) : WmState =
    { Topology = Topology.initial (runtimePolicy.Policy.WorkspaceAssignments |> Map.ofList)
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
/// Opusレビュー4-2：これは「消滅そのもの」（`WindowRemoved`、Sticky規則のProcess不一致によるHWND
/// 再利用検知も同じ経路を使う——`WindowVanishedByReuse`、v2.47で配線済み。Windows.fsのファイル冒頭
/// コメント参照）専用であり、「管理外化」
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
      Movability = state.Placement.Movability
      MinWidth = state.Placement.MinWidth }

/// `op=trypolicy`（architecture.md §10.2/§10.3、v2.152）の計算結果。Opus敵対的相談
/// `opus-review-fork-design.md`の結論：本格的なFork/Counterfactual SimulationはADR-006でPhase D
/// まで意図的に先送りされたまま残し、代わりに§10.3が実際に必要とする唯一の具体例——候補Policyの下で
/// 各窓がどう分類されるかの差分——だけを実装する（「Fork」ではなく「Policy Diff」と呼ぶ、ユーザー
/// 決定2026-11-06）。`WouldClassifyAs`/`WouldAwaitEvidence`は`PolicyEvaluator.ClassificationResult`の
/// `Resolved`/`AwaitingEvidence`をそのまま写す。`Undeterminable`は下記`remapTitleMatches`参照。
type ClassifyOutcome =
    | WouldClassifyAs of Management * matchedRule: PolicyRuleId option
    | WouldAwaitEvidence of blockedBy: PolicyRuleId
    /// タイトル証拠の付け替えができなかった（Opusレビュー F-a）：`ClassInputBeliefs.TitleMatches`は
    /// 採用中の`TitlePatternSet`のハッシュに紐付いた`TitleMatchVector`しか持たず、生タイトル文字列
    /// 自体はどこにも保存されない（E-6）。候補のPolicyが参照する`TitleContains`パターンのうち、
    /// 現在のPatternSetで一度も測定されていないものがあれば、安全に付け替えられず「分からない」と
    /// しか言えない——`missingTitlePatterns`がその一覧。
    | Undeterminable of missingTitlePatterns: string list

/// 候補の`TitlePatternSet`が元のものと異なる場合、観測済みの`TitleMatchVector`（元のPatternSetの
/// 添字空間）を候補の添字空間へパターン文字列で付け替える。候補が参照する全パターンが元のPatternSet
/// にも存在すれば安全に付け替えられる（`Believed`のまま）。1つでも無ければ、どのルールが本当に
/// ブロックされるかを厳密には判定できないため、安全側に倒してスロット全体を`Unformed`にし
/// （`selectManagement`は該当ルールで`AwaitingEvidence`に落ちる）、呼び出し側が`Undeterminable`として
/// 報告する。元のPatternSet自体が現在のPolicy（`livePatternSet`）と食い違う場合（直近のPolicy
/// live swap後、まだ再観測されていない窓）も同様に付け替えを諦める——既知の限界（architecture.md
/// §21）。
let private remapTitleMatches
    (livePatternSet: TitlePatternSet)
    (candidatePatternSet: TitlePatternSet)
    (slot: BeliefSlot<TitleMatchVector>)
    : BeliefSlot<TitleMatchVector> * string list = // 2番目はmissingPatterns（候補が参照するが元のPatternSetに無いパターン文字列）
    match slot with
    | Unformed gaps -> Unformed gaps, []
    | Believed b when b.Value.PatternSetHash = candidatePatternSet.PatternSetHash -> Believed b, []
    | Believed b when b.Value.PatternSetHash <> livePatternSet.PatternSetHash -> Unformed [], candidatePatternSet.Patterns
    | Believed b ->
        let missing = candidatePatternSet.Patterns |> List.filter (fun p -> not (Map.containsKey p livePatternSet.IndexOf))
        if not (List.isEmpty missing) then
            Unformed [], missing
        else
            let remapped =
                candidatePatternSet.IndexOf
                |> Map.toList
                |> List.choose (fun (pattern, candidateIdx) ->
                    let liveIdx = Map.find pattern livePatternSet.IndexOf
                    if Set.contains liveIdx b.Value.Matches then Some candidateIdx else None)
                |> Set.ofList
            Believed { b with Value = { PatternSetHash = candidatePatternSet.PatternSetHash; Matches = remapped } }, []

/// `op=trypolicy`本体。`state`の各窓を、候補の`RuntimePolicy`（`Policy`/`PatternSet`を候補に
/// 差し替えた環境、`Movability`/`MinWidth`/`ActiveWorkspace`/`ForegroundWindow`は現在の実際の値の
/// まま）の下で再評価する——§14/Analyzerと同じ「クライアントの自己申告を信用しない、Hostが自分の
/// 現在の状態に対して実際に評価し直す」原則。`livePatternSet`は現在実際に採用中の`RuntimePolicy`の
/// `PatternSet`（呼び出し側が`buildTitlePatternSet`で算出、`Policy`から一意に決まるので別に保持する
/// 必要は無い）——`remapTitleMatches`が観測済みタイトル証拠をどの添字空間から読むかを知るために使う。
let classifyAllUnder (livePatternSet: TitlePatternSet) (candidate: RuntimePolicy) (now: int64) (state: WmState) : (WindowId * ClassifyOutcome) list =
    let candidateCtx = classificationCtx candidate now state
    state.Windows.Records
    |> Map.toList
    |> List.map (fun (w, _) ->
        let identity =
            Map.tryFind w state.Windows.Identity
            |> Option.defaultValue { Exe = Unformed []; HostExe = Unformed []; Aumid = Unformed []; CoreWindowExe = Unformed []; Process = Unformed [] }
        let inputs =
            Map.tryFind w state.Windows.ClassInputs
            |> Option.defaultValue
                { IsDialog = LatestWins.unformed
                  Owner = LatestWins.unformed
                  IsVisible = LatestWins.unformed
                  Cloak = LatestWins.unformed
                  TitleMatches = LatestWins.unformed }
        let baseView = Windows.classificationView candidateCtx w identity inputs
        let remappedTitleMatches, missingPatterns = remapTitleMatches livePatternSet candidate.PatternSet inputs.TitleMatches.Slot
        let view = { baseView with TitleMatches = remappedTitleMatches }
        let outcome =
            if not (List.isEmpty missingPatterns) then
                Undeterminable missingPatterns
            else
                match selectManagement candidateCtx.PatternSet view candidateCtx.Policy with
                | Resolved(m, ruleId) -> WouldClassifyAs(m, ruleId)
                | ClassificationResult.AwaitingEvidence ruleId -> WouldAwaitEvidence ruleId
        w, outcome)

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

/// v2.89（コード品質の簡素化ラウンド、survey-next-goal-29の指摘）：`Placement.allocateRequestId`で
/// 採番し、更新後の`Placement`を`WmState`へ差し戻し、`mkEffect`が組み立てた1件のEffectを返す——
/// `step`内の`MoveFocusedToWorkspace`/`SwitchWorkspace`（既にvisible）/`SwitchSettle`（開始時の
/// 再フォアグラウンド）/`FocusNext`・`FocusPrevious`/`CloseFocused`/`SwitchSettle`タイマー発火時の
/// 再要求、計6箇所で機械的に重複していた「採番→Placementを差し戻す→1件のEffectを返す」という
/// 同一の配線を1つに集約する。挙動は変えない（各呼び出し元は今までと全く同じ値を計算していた）ので、
/// 既存のテスト（6箇所全てを個別にカバーしている）がそのまま回帰を検出する。
let private allocateAndEmit (mkEffect: RequestId -> Effect) (state: WmState) : WmState * Effect list =
    let rid, placementSt' = Placement.allocateRequestId state.Placement
    { state with Placement = placementSt' }, [ mkEffect rid ]

/// Topology段（§7.1の段②）、Focusの観測反映（段①相当）、Windows/Placementの信念反映・分類（段①⑤相当）、
/// Health/FocusのTimerElapsedディスパッチ、窓の消滅クリーンアップ、focusedの変化を見たHistory/zipperの
/// Focus更新（段⑥相当）を配線する。他ドメインが実装されるたびに該当する段を追加する
/// （ADR-018：各ドメインは自分の状態だけを返し、他ドメインの状態を書かない）。
///
/// Opusレビュー5-3：§7.1の段の順そのままではなく、今実装済みのドメインが処理できる入力の分だけ
/// 素直に並べている。段⑦（Reconcile）は現在表示中の全Workspace（アクティブなものに限らない）を対象に
/// 配線している（v2.26で拡張済み。このコメントが長らくv2.11時点の「アクティブな1つだけ」のまま
/// 更新されていなかった誤りをv2.47で訂正した。`StyleChanged`は依然未配線、Windows.fs/Placement.fsの
/// ファイル冒頭コメント参照）。表どおりの8段パイプラインへの完全な組み直しは今後のラウンドへ持ち越す。
///
/// Intervene実配線（architecture.md §10.2/§10.3、vNext）：v2.151で`private`化し、本体はそのまま
/// 一切変更していない（この関数の中身は以前の`step`そのもので、以後`InterveneRequested`の前提検査
/// だけを担う新しい薄いラッパーが外側の`step`という名前を引き継ぐ、下記参照）。
let private stepCore (runtimePolicy: RuntimePolicy) (state: WmState) (envelope: EventEnvelope<InputEvent>) : StepResult =
    let now = envelope.MonotonicTime
    let focusedBefore = focused state

    // 窓の消滅（vanished）のクリーンアップ。`WindowRemoved`はここで即座に引き金になる。HWND再利用
    // （`WindowVanishedByReuse`、v2.47）はWindowsが`WindowObserved`を処理した後にしか分からないので、
    // 同じ`vanishWindow`を下（`stateAfterWindows`の直後）でも別途呼ぶ。
    let state, vanishTimerCmds =
        match envelope.Input with
        | WindowRemoved w -> vanishWindow w state
        | _ -> state, []

    // 段①⑤相当（Windows）：WindowObserved/VisibilityChanged/CloakChangedからの信念反映と、
    // ClassificationDeadlineタイマー・ユーザーコマンド（ToggleFloating/SetManagement/ClearUserOverride）
    // による分類の確定を同じ入力について続けて行い、出たDomainEventをその場でWorkspaces/Focusへ反映する。
    let windowsCtx = classificationCtx runtimePolicy now state
    let windowsRegistry', windowsEvents, windowsTimerCmds, windowsTraces =
        match envelope.Input with
        | WindowObserved snapshot -> Windows.onWindowObserved windowsCtx snapshot state.Windows
        | VisibilityChanged (w, obs) -> Windows.onVisibilityChanged windowsCtx w obs state.Windows
        | CloakChanged (w, obs) -> Windows.onCloakChanged windowsCtx w obs state.Windows
        | TimerElapsed (TimerId.Windows (ClassificationDeadline w)) -> Windows.onClassificationDeadline windowsCtx w state.Windows
        | UserCommand cmd -> Windows.onUserCommand windowsCtx cmd state.Windows
        | MembershipHintsLoaded hints -> Windows.onMembershipHintsLoaded hints state.Windows, [], [], []
        | _ -> state.Windows, [], [], []

    let stateAfterWindows =
        { state with Windows = windowsRegistry' }
        |> fun s -> windowsEvents |> List.fold applyWindowsEvent s

    // HWND再利用の検知（§4.5 Sticky規則のProcess不一致、v2.47）：`WindowVanishedByReuse`は
    // `applyWindowsEvent`が処理しない特別なケースで、`WindowRemoved`と同じ`vanishWindow`（全ドメインの
    // 信念・Workspaces/Focus/SwitchSettle・待ちタイマーの除去）を適用する。`windowsRegistry'`はこの時点
    // でまだ旧Processの値のまま（`Windows.onWindowObserved`が検知時は信念を書き換えずに返す、Windows.fs
    // 参照）なので、`vanishWindow`内の`Windows.onWindowVanished`が初めてRecords/Identity/ClassInputsを
    // 除去する。`ForgetWindow`EffectはShellの対応表から外させるためのもの（7.1）。
    let stateAfterWindows, reuseVanishTimerCmds =
        windowsEvents
        |> List.fold
            (fun (s, cmds) ev ->
                match ev with
                | WindowVanishedByReuse w ->
                    let s', c = vanishWindow w s
                    s', cmds @ c
                | _ -> s, cmds)
            (stateAfterWindows, [])

    let reuseForgetEffects = windowsEvents |> List.choose (function WindowVanishedByReuse w -> Some (ForgetWindow w) | _ -> None)

    // 段①相当（Placement）：WindowObservedはWindowsと同じ入力から自分の信念（Geometry/Movability/
    // TargetIntegrity）だけを修正する（§7.1「Windows→Placement→Health→Focus」の順）。TargetIntegrityの
    // ProbeOnceタイマー（IntegrityReprobe）の発火、`EffectCallReturned`（Pendingの終端遷移の一部）もここで
    // 処理する。MinimizedStateChangedもここで配線する（Placement.fsのファイル冒頭コメント参照）。
    // `effectCallFailure`は`EffectCallReturned`が`TargetHung`と判定した場合に、Health側の
    // Responsiveness修正（Placementの管轄外）へ渡すための情報（下記）。
    // `dpiOf`（v2.43）：窓のMonitor信念からDPIを引く読み取り専用の投影。`Placement.onPendingDeadlineTimerFired`
    // がMinWidthの証拠をDIPへ正規化するために使う（Placementは他ドメインの状態を直接読まない、ADR-018。
    // `reconcileOneScreen`が`LayoutContext.Dpi`を`screen.Monitor.Dpi`から組み立てるのと同じ考え方）。
    // この入力（`TimerElapsed (Placement (PendingDeadline _))`）はTopologyを更新しないので、まだこの
    // stepのTopology更新（下記、段②）を経ていない`state.Topology`（関数引数そのもの）を読んでよい。
    let dpiOf (m: MonitorId) : int option =
        match Topology.displays state.Topology with
        | NoMonitors -> None
        | Displays (current, visible) -> (current :: visible) |> List.tryFind (fun sc -> sc.Monitor.Id = m) |> Option.map (fun sc -> sc.Monitor.Dpi)

    let placementState', placementEffects, placementTimerCmds, effectCallFailure, placementTraces =
        match envelope.Input with
        | WindowObserved snapshot when windowsEvents |> List.exists (function WindowVanishedByReuse w -> w = snapshot.Id | _ -> false) ->
            // HWND再利用の検知時（v2.47/v2.48で発見）：Windowsが同じ観測をこのWindowIdの信念として
            // 採用しなかった（`onWindowObserved`参照）のに、Placementが同じ`WindowObserved`を素直に
            // 処理すると`Geometry`等をこの`WindowId`の下で作り直してしまい、`vanishWindow`が消したはずの
            // 信念が同じstep内で復活する（性質テスト#5で発見した実際のバグ）。Windows→Placementの処理
            // 順序（§7.1段①）は、Windowsが「この観測は別の実体のものだ」と判断した結果を後段が無視
            // できることを前提にしているので、ここで素通りさせない。
            stateAfterWindows.Placement, [], [], None, []
        | WindowObserved snapshot ->
            // `checkDpiCrossing`（v2.45）：`onWindowObserved`が今回の観測でMonitor信念を上書きする前の
            // 値を覚えておき、上書き後の新しい値と比較して「異なるDPIのモニターへ移った」かを判定する
            // （Placement.fsの型直前のコメント参照）。
            let oldMonitor = Map.tryFind snapshot.Id stateAfterWindows.Placement.Geometry |> Option.bind (fun g -> LatestWins.value g.Monitor)
            let s, e, t = Placement.onWindowObserved now snapshot stateAfterWindows.Placement
            // `checkFullscreen`（E-4、v2.128）：`checkDpiCrossing`と同じ理由で`dpiOf`の隣に置く
            // 読み取り専用投影（`state.Topology`はこの入力ではまだ更新されていない、`dpiOf`の型直前の
            // コメントと同じ正当化）。`checkDpiCrossing`より前に置く——後で`checkDpiCrossing`側が
            // `Fullscreen`を読むようになったとき、この観測で更新済みの値を読めるようにするため
            // （opus-review-fullscreen-detection-round2.md §1-1）。
            let monitorBounds =
                match Topology.displays state.Topology with
                | NoMonitors -> []
                | Displays (current, visible) -> (current :: visible) |> List.map (fun sc -> sc.Monitor.Bounds)
            let screenPhaseIsStable = match Topology.phase state.Topology with Stable -> true | _ -> false
            let s = Placement.checkFullscreen monitorBounds screenPhaseIsStable snapshot s
            let s = Placement.checkDpiCrossing now dpiOf snapshot.Id oldMonitor s
            s, e, t, None, []
        | MinimizedStateChanged (w, obs) -> Placement.onMinimizedStateChanged now w obs stateAfterWindows.Placement, [], [], None, []
        | MoveSizeStarted w -> Placement.onMoveSizeStarted w stateAfterWindows.Placement, [], [], None, []
        | MoveSizeEnded (w, _, _) -> Placement.onMoveSizeEnded w stateAfterWindows.Placement, [], [], None, []
        | UserCommand (SetManagement (w, _)) | UserCommand (ToggleFloating w) ->
            // v2.83：`Movability`が`NotMovable`な窓へのユーザーの明示操作による1回だけの試し直し
            // （Placement.onUserCommandの型直前のコメント参照）。Windowsドメイン（段①、上記）が
            // 同じ入力で分類を直すのとは別に、Placementは自分の信念だけを直す（ADR-018）。
            Placement.onUserCommand now w stateAfterWindows.Placement, [], [], None, []
        | TimerElapsed (TimerId.Placement (IntegrityReprobe w)) ->
            let s, e, t = Placement.onIntegrityReprobeTimerFired now w stateAfterWindows.Placement
            s, e, t, None, []
        | TimerElapsed (TimerId.Placement (PendingDeadline rid)) ->
            let s, e, t, traces = Placement.onPendingDeadlineTimerFired now rid dpiOf stateAfterWindows.Placement
            s, e, t, None, traces
        | TimerElapsed (TimerId.Placement (PresenceDeadline rid)) ->
            let s, t = Placement.onPresenceDeadlineTimerFired now rid stateAfterWindows.Placement
            s, [], t, None, []
        | EffectCallReturned (rid, result, dispatchSeq) ->
            let s, e, t, failureInfo, traces = Placement.onEffectCallReturned now envelope.Sequence rid result dispatchSeq stateAfterWindows.Placement
            s, e, t, failureInfo, traces
        | _ -> stateAfterWindows.Placement, [], [], None, []

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
                    allocateAndEmit (fun rid -> RequestForeground (rid, neighborZipper.Focus)) { stateAfterMovability with Workspaces = workspaces' }
                | None -> { stateAfterMovability with Workspaces = workspaces' }, []
            | _ -> stateAfterMovability, []
        | _ -> stateAfterMovability, []

    // 段①相当：観測の信念反映（全入力について無条件に最初に行う、§7.1）。FocusのForegroundを配線する。
    // 信念の値が変わったらForegroundChanged（§7.1表の段①が出すDomainEvent）を出す。v2.40で
    // `stateAfterMovability.Focus`へ即座に書き戻すようにした（以前は`focusAfterBelief`という別の
    // 値のまま持ち越し、段⑤のSwitchSettleタイマー処理まで`stateAfterMovability.Focus`に反映しなかった
    // ——その間に`SwitchWorkspace`の`switchSettleStartCmds`配線が同じ`stateAfterMovability.Focus`へ
    // `SwitchSettle`を書いても、段⑤の消費が古い`focusAfterBelief`を使っていたせいで消されてしまう
    // バグを実際にテストで踏んだ）。
    let stateAfterMovability, stage1Events =
        match envelope.Input with
        | ForegroundObserved obs ->
            let revised = Focus.reviseForeground now stateAfterMovability.Focus.Foreground obs
            let changed = LatestWins.value revised <> LatestWins.value stateAfterMovability.Focus.Foreground
            { stateAfterMovability with Focus = { stateAfterMovability.Focus with Foreground = revised } },
            (if changed then [ ForegroundChanged (LatestWins.value revised |> Option.flatten) ] else [])
        | _ -> stateAfterMovability, []

    // 段②：Topologyの原因・コマンドの受付。
    let topologyState', topologyResponse, topologyEvents, topologyTraces =
        Topology.update runtimePolicy.Topology now envelope.Input stateAfterMovability.Topology
    // `DecisionTrace`（§7.1「起こり得ない組合せに出会ったら、例外を投げずに入力を捨て、DecisionTraceに
    // 記録する」、v2.68）：段②のTopologyが出す分（今のところ常に`[]`、Topology.updateのどの分岐も
    // 疑わしい組合せに出会っていない）と、段①⑤のWindows（`windowsTraces`、上記）が出す分を合わせて
    // 1つのstepのTraceとする。
    let traces = topologyTraces @ windowsTraces

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
                    let state', effects = allocateAndEmit (fun rid -> RequestForeground (rid, w)) stateAfterMovability
                    topologyState', topologyEvents, state', effects
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
    // `SwitchSettle`が待ち中（v2.40）は、hiddenなWorkspaceの`ForegroundFollow`分岐（Workspaceを
    // 切り替える方）だけを止める（§4.4/R4-5「この待ちの間は、hiddenなWorkspaceの窓のフォアグラウンド
    // 化でWorkspaceを切り替えない」）。`Foreground`の信念自体は上の段①で既に更新済みなので影響は無い。
    // 同じcurrentのモニター追従（`followForegroundToVisibleMonitor`）はWorkspaceを切り替えないので対象外。
    let isSwitchSettling = stateAfterMovability.Focus.SwitchSettle |> Option.isSome
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
                | Displays _ when isSwitchSettling -> topologyState', topologyEvents, stateAfterMovability
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

    // `MoveAttributed (w, UserDrag)`（§4.4「観測されたモニターと意図されたモニターが食い違うとき」の
    // UserDrag分岐、R3-4、v2.66）：`Topology.update`が`MoveSizeEnded`を段②で無条件に`UserDrag`と
    // ラベルする（R5-9）。ここでは`MoveSizeEnded`自身が運ぶドロップ時点のモニター観測（段①では
    // 使わない、Placementの`Geometry.Monitor`信念とは別経路）を読み、そのモニターに現在表示中の
    // Workspaceへ窓を移す（architecture.md「窓が別のモニターmへ落とされたら、mに表示中のWorkspaceへ
    // その窓を移す（i3/komorebiと同じ）」）。同じモニター内のドラッグ（WorkspaceId不変）・観測失敗
    // （`Error`）・落とされたモニターが現在表示されていない場合は何もしない。
    let stateAfterMovability, crossMonitorDropMoved =
        match topologyEvents |> List.exists (function MoveAttributed (_, UserDrag) -> true | _ -> false), envelope.Input with
        | true, MoveSizeEnded (w, _, monitorObs) ->
            match monitorObs.Value with
            | Error _ -> stateAfterMovability, false
            | Ok droppedMonitor ->
                match Topology.displays topologyState' with
                | NoMonitors -> stateAfterMovability, false
                | Displays (current, visible) ->
                    match (current :: visible) |> List.tryFind (fun sc -> sc.Monitor.Id = droppedMonitor) with
                    | None -> stateAfterMovability, false // 落とされたモニターは現在表示されていない
                    | Some targetScreen ->
                        match WorkspaceSet.workspaceOf w stateAfterMovability.Workspaces with
                        | Some srcWs when srcWs <> targetScreen.Workspace ->
                            { stateAfterMovability with Workspaces = WorkspaceSet.moveToWorkspace w targetScreen.Workspace stateAfterMovability.Workspaces }, true
                        | _ -> stateAfterMovability, false // 同じWorkspace内のドラッグ、または管理下に無い窓
        | _ -> stateAfterMovability, false

    // `WorkspaceHidden`/`WorkspaceShown`（v2.31で消費を開始。モニター接続・切断経由の`MonitorLifecycle.
    // reconcile`に加え、v2.32で`SwitchWorkspace`も同じ受け口を共有する）：該当Workspaceに属する窓を
    // `Placement.onWorkspaceHidden`/`onWorkspaceShown`へ渡し、`Desired`/`SetPresence`を更新する
    // （Placement.fsのコメント参照）。
    let windowsOfWorkspace (ws: WorkspaceId) : WindowId list =
        (WorkspaceSet.stateOf ws stateAfterMovability.Workspaces).Stack |> Option.map Zipper.toList |> Option.defaultValue []
    let hiddenWindows = topologyEvents |> List.choose (function WorkspaceHidden ws -> Some ws | _ -> None) |> List.collect windowsOfWorkspace
    let shownWindows = topologyEvents |> List.choose (function WorkspaceShown (ws, _) -> Some ws | _ -> None) |> List.collect windowsOfWorkspace
    let stateAfterMovability, presenceEffects, presenceTimerCmds =
        let placementSt1, e1, t1 = Placement.onWorkspaceHidden now hiddenWindows stateAfterMovability.Placement
        let placementSt2, e2, t2 = Placement.onWorkspaceShown now shownWindows placementSt1
        { stateAfterMovability with Placement = placementSt2 }, e1 @ e2, t1 @ t2

    // `CurrentChanged`/`WorkspaceShown (_, UserView)`（v2.32で消費を開始）：ユーザーが`SwitchWorkspace`で
    // 明示的に切り替えた先のWorkspaceへ、そのzipperのFocus（Stack=NoneならNone、RequestForegroundは
    // 発行しない）へ一度だけRequestForegroundする（§4.4「ユーザーの操作でWorkspaceが表示されたとき…
    // フォーカスを渡す窓はそのWorkspaceのzipperのFocus」）。アプリの自己アクティブ化
    // （`ForegroundFollow`）やモニター再接続（`Reconcile`）による表示ではフォーカスを要求しない
    // （§4.4、同じ理由はPlacement.fsのコメント参照）。切替直後の揺れを吸収する`SwitchSettle`
    // （R4-5、v2.40で配線。CI実機スパイクS20/S20bで同一プロセス内の揺れ無しを確認済みだが、S4が
    // S4rで実デスクトップ再検証を要した前例に鑑み安全マージンを大きく取った暫定値、
    // `RuntimePolicy.SwitchSettleMs`参照）を同じタイミングで開始する——フォーカスを渡す窓が無い
    // （`Stack = None`）場合もFocus.startSwitchSettleへ`None`を渡して待ちだけ開始する（R5-1の
    // `ForegroundFollow`分岐を一時的に止めるのが目的で、再要求するものが無くても待ち自体は要る）。
    let userSwitchedWorkspaces =
        topologyEvents
        |> List.choose (function
            | CurrentChanged ws -> Some ws
            | WorkspaceShown (ws, UserView) -> Some ws
            | _ -> None)
    let stateAfterMovability, switchForegroundEffects, switchSettleStartCmds =
        userSwitchedWorkspaces
        |> List.fold
            (fun (s: WmState, effects, cmds) ws ->
                let focusTarget = (WorkspaceSet.stateOf ws s.Workspaces).Stack |> Option.map (fun z -> z.Focus)
                let settleValue, settleCmds = Focus.startSwitchSettle now runtimePolicy.SwitchSettleMs focusTarget
                let s = { s with Focus = { s.Focus with SwitchSettle = Some settleValue } }
                match focusTarget with
                | None -> s, effects, cmds @ settleCmds
                | Some w ->
                    let s', newEffects = allocateAndEmit (fun rid -> RequestForeground (rid, w)) s
                    s', effects @ newEffects, cmds @ settleCmds)
            (stateAfterMovability, [], [])

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
                allocateAndEmit (fun rid -> RequestForeground (rid, z.Focus)) { stateAfterMovability with Workspaces = workspaces' }
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
            allocateAndEmit (fun rid -> RequestClose (rid, w)) stateAfterMovability
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
    // ここで行う、ADR-018）を配線する。**v2.84で`WindowObserved`も配線**：`snapshot.Responsiveness`
    // （`PumpResponded`/`PumpTimedOut`/`IsHungAppWindowTrue`、§4.5）をHealthへ渡す経路がそれまで
    // 一度も無く、`Hung`から`Responding`へ回復する唯一の経路（本文「`Reprobe`で再確認を要求する」）が
    // 0%実装だった既知のギャップ（`applyEvidence`/`onReprobeTimerFired`のFromObservation分岐は
    // 個別にテスト済みだったが、WmStateから呼ばれたことが一度も無かった）。HWND再利用検知時
    // （`stateAfterWindows`と同じ`windowsEvents`ガード）はPlacementと同じ理由で素通りさせる——
    // 消滅が決まった旧WindowIdの下にHealthの信念を新しく作らない。
    let healthState', healthEffects, healthTimerCmds =
        match envelope.Input with
        | TimerElapsed (TimerId.Health (ResponsivenessReprobe w)) -> Health.onReprobeTimerFired now w stateAfterMovability.Health
        | WindowObserved snapshot when windowsEvents |> List.exists (function WindowVanishedByReuse w -> w = snapshot.Id | _ -> false) ->
            stateAfterMovability.Health, [], []
        | WindowObserved snapshot ->
            match snapshot.Responsiveness with
            | Some { Value = Ok sample } -> Health.applyEvidence now snapshot.Id (FromObservation sample) stateAfterMovability.Health
            | _ -> stateAfterMovability.Health, [], []
        | EffectCallReturned _ ->
            match effectCallFailure with
            | Some (w, TargetHung) -> Health.applyEvidence now w (FromEffectFailure TargetHung) stateAfterMovability.Health
            | _ -> stateAfterMovability.Health, [], []
        | _ -> stateAfterMovability.Health, [], []

    // Focus：SwitchSettleタイマーの発火（R4-5、v2.40で再要求を配線。以前は採番元が無いとして捨てていたが、
    // 他のUserCommand同様`stateAfterMovability.Placement`から採番できる）。期限到達時点でまだ`focused`が
    // 渡すべき窓`w`になっていなければ（hiddenなWorkspaceの窓に揺れたままか、あるいはまだElsewhereなら）
    // `RequestForeground`を1回だけ再発行する。`focused`は`Foreground`信念とWorkspaces/Topologyだけで
    // 決まり`SwitchSettle`自体は読まないので、タイマー処理前の`stateAfterMovability`で評価してよい。
    let stateAfterMovability, focusAfterTimer, switchSettleTimerCmds, switchSettleReRequestEffects =
        match envelope.Input with
        | TimerElapsed (TimerId.Focus WorkspaceSwitchSettle) ->
            let focusAfterTimer, reRequestTarget, cmds = Focus.onSwitchSettleTimerFired now stateAfterMovability.Focus
            match reRequestTarget with
            | Some w when focused stateAfterMovability <> ManagedWindow w ->
                let state', effects = allocateAndEmit (fun rid -> RequestForeground (rid, w)) stateAfterMovability
                state', focusAfterTimer, cmds, effects
            | _ -> stateAfterMovability, focusAfterTimer, cmds, []
        | _ -> stateAfterMovability, stateAfterMovability.Focus, [], []

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

    // `MoveCause.Unattributed`のドリフト検知（v2.70、§4.4 R3-4、`Placement.checkUnattributedDrift`の
    // 型直前のコメント参照）：`WindowObserved`のたびに、観測されたモニターとそのWorkspaceを表示中の
    // モニターが食い違っていないかを確かめる。`isTiled`/`desiredMonitorOf`/`screenPhaseIsStable`は
    // Placementが他ドメインの型を直接知らないための読み取り専用の投影（`dpiOf`と同じ考え方、ADR-018）。
    let isTiled (w: WindowId) : bool = Map.tryFind w managementByWindow = Some EffTiled
    let desiredMonitorOf (w: WindowId) : MonitorId option =
        match WorkspaceSet.workspaceOf w next.Workspaces with
        | None -> None
        | Some ws ->
            match Topology.displays next.Topology with
            | NoMonitors -> None
            | Displays (current, visible) -> (current :: visible) |> List.tryFind (fun sc -> sc.Workspace = ws) |> Option.map (fun sc -> sc.Monitor.Id)
    let screenPhaseIsStable = match Topology.phase next.Topology with Stable -> true | _ -> false
    // 1つの窓へ`checkUnattributedDrift`を適用し、打ち止めが実際にFloating劣化を要求した場合（v2.71、
    // Opusレビュー§Q3・§Q5「ラウンド2」）は`Windows.onOscillationDegraded`へ中継する（`onMovabilityBecameNotMovable`
    // と同じ「Placementの信号→WmStateが中継→Windowsの専用関数」経路、ADR-018）。`WindowObserved`（この窓自身の
    // 観測、下記）と`ScreenGrace`満了時の全窓スキャン（v2.72、下記）の両方から呼ぶ共通部分。
    let applyDriftCheck (w: WindowId) (next: WmState) : WmState * bool * bool =
        let placement', corrected, shouldDegrade = Placement.checkUnattributedDrift now isTiled desiredMonitorOf screenPhaseIsStable w next.Placement
        let next = { next with Placement = placement' }
        let next =
            if shouldDegrade then
                let windowsAfterOscillation, oscillationEvents = Windows.onOscillationDegraded w next.Windows
                { next with Windows = windowsAfterOscillation } |> fun s -> oscillationEvents |> List.fold applyWindowsEvent s
            else
                next
        next, corrected, shouldDegrade
    // `MoveCause.OsInduced`（v2.72、§4.4 R3-4、Opusレビュー§A・§Q5「ラウンド3」）：ラウンド1/2の検知は
    // `WindowObserved`（この窓自身の新しい観測）に反応する形なので、OSがモニター構成を一時的に乱して
    // 元の構成へ戻した場合（v2.69の赤いテストが固定した既知のギャップ）は、動かされた窓についてその後
    // 新しい`WindowObserved`が届かない限り永遠に検知されない——grace window満了（`ScreenGrace`→Stable）
    // 自体は`Topology.update`が何のDomainEventも出さない（Topology.fs:398-402）。このギャップを閉じるため、
    // grace満了のこのstep（`ScreenPhase`が実際に`Grace`から`Stable`へ遷移した、その一致するstepに限る——
    // 古い発火の空振りで二重に適用しないため）に限り、Placementが`Accepted`を持つ（＝一度は要求が
    // 受け入れられた）全ての窓へ同じ検知を直接適用する——新しい観測を待たない（レビュー推奨どおり
    // ラウンド1の部品をそのまま使う。新しいカウンタ・新しい`ClassifiedBy`は作らない）。
    let screenGraceJustFired =
        match envelope.Input, Topology.phase state.Topology, Topology.phase next.Topology with
        | TimerElapsed (TimerId.Topology ScreenGrace), Grace _, Stable -> true
        | _ -> false
    let next, driftCorrected, driftDegraded =
        match envelope.Input with
        | WindowObserved snapshot -> applyDriftCheck snapshot.Id next
        | _ when screenGraceJustFired ->
            next.Placement.Accepted
            |> Map.toList
            |> List.map fst
            |> List.fold
                (fun (next, anyCorrected, anyDegraded) w ->
                    let next, corrected, shouldDegrade = applyDriftCheck w next
                    next, anyCorrected || corrected, anyDegraded || shouldDegrade)
                (next, false, false)
        | _ -> next, false, false

    // Floating劣化（v2.71、上記`driftDegraded`）はWindows.Recordsの分類を変えるので、`reconcileOneScreen`
    // が読む`managementByWindow`/`aliveWindows`（劣化前に組み立てた投影）をここで最新の状態へ作り直す
    // ——作り直さないと、Layoutは（Workspace.Floatingが先に更新されているので）劣化後の窓を正しく
    // 除外して計算するのに、`PlanValidator.checkNoMissingPlacement`は古い`managementByWindow`の
    // `EffTiled`のまま見てしまい、「計画にあるべき窓が無い」という誤検知で`RejectWholePlan`になる
    // （mutation testingで実際に踏んだ食い違い）。
    let aliveWindows, managementByWindow =
        if driftDegraded then
            let aliveWindows' = next.Windows.Records |> Map.toSeq |> Seq.map fst |> Set.ofSeq
            let managementByWindow' =
                next.Windows.Records
                |> Map.toSeq
                |> Seq.choose (fun (w, r) -> match r.Classification with Classified (eff, _) -> Some (w, eff) | _ -> None)
                |> Map.ofSeq
            aliveWindows', managementByWindow'
        else
            aliveWindows, managementByWindow

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
        // `MoveAttributed (_, UserDrag)`経由で窓のWorkspace所属が実際に変わった場合（v2.66、上記
        // `crossMonitorDropMoved`）：`windowsEvents`（分類の変化）を伴わない所属変更なので、
        // `PromoteToMaster`/`ChangeLayout`と同じ理由で明示的にここで引き金にする——元・先の両方の
        // Workspaceが対象になる（`reconcileOneScreen`は現在表示中の全Workspaceを毎回走査するため、
        // どちらのWorkspaceを個別に指定する必要も無い）。
        || crossMonitorDropMoved
        // `MoveCause.Unattributed`のドリフト補正（v2.70、上記`driftCorrected`）：`checkUnattributedDrift`が
        // `Desired`を消しただけなので、ここで明示的に引き金にしないとReconcile自体が走らず、消した
        // `Desired`の代わりが発行されない。
        || driftCorrected
        // Floating劣化（v2.71、上記`driftDegraded`）：`WindowClassified`は`windowsEvents`（段①⑤の
        // Windows分、このstepのPlacement段よりも前に確定済み）には含まれないので、明示的にここで
        // 引き金にする——劣化した窓を外した残りのTiled構成でLayoutを計算し直す必要がある。
        || driftDegraded

    let reconcileOneScreen (placementSt: PlacementState, effects, cmds, reconcileTr) (screen: Screen) =
        let wsState = WorkspaceSet.stateOf screen.Workspace next.Workspaces
        let stackList = wsState.Stack |> Option.map Zipper.toList |> Option.defaultValue []
        let tiledIds = stackList |> List.filter (fun w -> not (Set.contains w wsState.Floating))
        let focusedTiled =
            wsState.Stack
            |> Option.bind (fun z -> if Set.contains z.Focus wsState.Floating then None else Some z.Focus)
        let ctx: LayoutContext =
            { WorkArea = screen.Monitor.WorkArea
              Dpi = screen.Monitor.Dpi
              Monitor = screen.Monitor.Id
              // v2.48：Placement.MinWidthの実際の信念を渡すようになった（それまでは決め打ちの`None`で、
              // LayoutFunction実装（MasterStack/Monocle）がそもそも読んでいないので観測可能な挙動は
              // 変わらない——§6.2が述べる「推定最小サイズをソフト制約として扱い、満たせない場合は
              // 調整・Floating・Suspended等へ劣化させる」という実際の強制ロジックは、具体的な劣化方法が
              // 本書に数値・手順として無いため依然未実装のまま、専用のラウンドへ持ち越す）。
              TiledWindows = tiledIds |> List.map (fun w -> { Id = w; MinWidthDip = Map.tryFind w placementSt.MinWidth })
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
        // `Dragging`中の窓（§4.4 Suspended、v2.65）も、生死とは別の理由でこのstepのLayout対象から外す
        // ——新しいSetRectを発行しない（CoreCommand.fsの`MoveSizeStarted`型直前のコメント「自動整列を
        // 抑止する」）。`ctx.TiledWindows`自体はそのまま渡してよい——除いた窓を`Alive`から外せば、
        // `PlanValidator.checkManagedAndAlive`が`UnmanagedOrDeadWindow`として検出し、
        // `decide`の`RelayoutWithout`（既存の「検証時点で死亡/Excluded化していた窓を除いて1回だけ
        // 再計算する」経路）がこの窓を除いて自動的に計算し直す——生死以外の理由で除外する窓も、
        // 既存の「このstepでは対象から外す」機構に相乗りできる（新しい特別扱いの分岐は不要）。
        let aliveForScreen = Set.intersect aliveWindows windowsInThisWorkspace |> fun s -> Set.difference s placementSt.Dragging
        let st', e', t', tr' = Placement.reconcileWorkspace now runtimePolicy.LayoutRegistry layoutRef ctx managementForScreen aliveForScreen placementSt
        st', effects @ e', cmds @ t', reconcileTr @ tr'

    let placementAfterReconcile, reconcileEffects, reconcileTimerCmds, reconcileTraces =
        if not shouldReconcile then
            next.Placement, [], [], []
        else
            match Topology.displays next.Topology with
            | Displays (current, visible) -> (current :: visible) |> List.fold reconcileOneScreen (next.Placement, [], [], [])
            | NoMonitors -> next.Placement, [], [], [] // モニター0台。Layout計算もEffect発行も行わない（§4.4）

    let next = { next with Placement = placementAfterReconcile }

    // 段⑧ゲート（§6.2/§7.1）：Windows/Placementの実装に伴い、Alive/Management/NotMovable/Hungを
    // 実際の状態から組み立てる。NeedsRestore（v2.74）はPlacementの`Geometry.IsMaximized`信念から
    // 直接組み立てる——`dispatchPlacements`（Placement.fs）がこの信念を見て`SetRect`の直前に
    // 自分で`RestoreBeforeMove`を発行するので、ここでの集合は「ゲートが見る構造的な前提」の役割
    // （同じ窓へ他の経路から直接`SetRect`/`RestoreBeforeMove`を発行する将来の変更に対する防御）。
    // **v2.86でHiddenByWmも実際の状態から組み立てた**：`GatedEffect.appliesTable`のR4-6
    // （`TargetHiddenByWm`、SetRect/RestoreBeforeMove/RequestForeground/RequestCloseに適用）は
    // 既に正しく実装済みだったが、それを駆動する`GateInput.HiddenByWm`自体がv2.31以来ずっと
    // `Set.empty`の決め打ちで、`Placement.PlacementState.Desired`（`onWorkspaceHidden`/
    // `onWorkspaceShown`が書く`HiddenByWm`/`Shown _`）を一度も反映していなかった——
    // `NotMovable`/`Hung`と全く同じ形で組み立てられるのに素通しのままだった既知のギャップ。
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
          HiddenByWm =
            next.Placement.Desired
            |> Map.toSeq
            |> Seq.filter (fun (_, dp) -> dp = Wm.Core.Placement.HiddenByWm)
            |> Seq.map fst
            |> Set.ofSeq
          NeedsRestore =
            next.Placement.Geometry
            |> Map.toSeq
            |> Seq.filter (fun (_, g) -> LatestWins.value g.IsMaximized = Some true)
            |> Seq.map fst
            |> Set.ofSeq
          Fullscreen = next.Placement.Fullscreen }

    let gated, rejected = GatedEffect.gate gateInput (reuseForgetEffects @ topologyResponse.Actions @ placementEffects @ healthEffects @ reconcileEffects @ moveFocusedEffects @ presenceEffects @ switchForegroundEffects @ focusRotateEffects @ closeFocusedEffects @ focusMostRecentEffects @ switchSettleReRequestEffects)

    // §7.2「GateRejected → Cancelled（Deadlineまで待たず同じstep内で即時に遷移する）」。
    let rejectedRequestIds = rejected |> List.choose fst
    let placementAfterGateRejection, gateRejectionTimerCmds = Placement.onGateRejected rejectedRequestIds next.Placement
    let finalNext = { next with Placement = placementAfterGateRejection }

    // 段①のDomainEventは、消費する後続の段（Topologyの`current`追従等）がまだ無いため捨てる
    // （Opusレビュー5-3）。段②のDomainEventのうち`TopologySettled`（v2.29）/`WorkspaceShown`/
    // `WorkspaceHidden`/`CurrentChanged`（v2.31・v2.32）/`MoveAttributed (_, UserDrag)`（v2.66）は
    // 全て上で消費するようになった。`MoveAttributed (_, OsInduced | Unattributed)`は、段②のTopologyが
    // まだ`MoveSizeEnded`以外の「移動の観測」を一切受け取らないため（WindowObservedは段①⑤でWindows/
    // Placementが処理し、Topologyには流れない）、Topology.update自体がこの2ケースを一度も生成しない
    // ——`MoveCause`型そのものは3ケース全て存在するが、生成されるのはv2.66時点では`UserDrag`のみ
    // （Topology.updateの`MoveSizeEnded`分岐の型直前のコメント参照）。
    ignore stage1Events
    ignore topologyEvents

    { Next = finalNext
      Effects = gated
      Timers =
        topologyResponse.TimerCommands
        @ healthTimerCmds
        @ switchSettleTimerCmds
        @ switchSettleStartCmds
        @ windowsTimerCmds
        @ vanishTimerCmds
        @ reuseVanishTimerCmds
        @ placementTimerCmds
        @ presenceTimerCmds
        @ topologySettledTimerCmds
        @ reconcileTimerCmds
        @ gateRejectionTimerCmds
      Trace = traces @ placementTraces @ reconcileTraces }

/// Intervene（architecture.md §10.2/§10.3、v2.151）：ユーザー決定2026-11-06（Opus敵対的相談
/// `opus-review-intervene-trust-boundary.md`を踏まえ、architecture.md §10.2/F-1のGate条件を改定して
/// 今進める、という決定）。`InterveneRequested`だけをここで特別扱いし、他の入力は従来どおり
/// `stepCore`へそのまま渡す——`stepCore`の中身（旧`step`そのもの）には一切触れない設計。
///
/// 前提検査（F-4/R2-25）は「対象WindowIdが依然`Windows.Records`に存在するか」だけ（Opusレビューの
/// 推奨する最小スライス：`SetManagement(Tiled/Floating)`/`ClearUserOverride`限定なので、HWND再利用
/// までを区別する実体識別子はまだ無い——既知のギャップとして残す、architecture.md §21参照）。
/// 不成立なら`InterveneRejected`を記録するだけで状態は一切変えず、`stepCore`へは委譲しない
/// （§7.1「前提検査は決定的でJournalに残るstepの内側で評価する」の要求どおり、TOCTOUを避ける）。
/// 成立したら、等価な`UserCommand`へ書き換えて`stepCore`自身に委譲する——Opusレビューの推奨
/// 「既存のSetManagement/ClearUserOverrideの経路をそのまま再利用する」ための設計で、Windows.fsの
/// `onUserCommand`に一切新しいコードを書かずに済む。`stepCore`が返した`StepResult`に
/// `InterveneAccepted`（誰が送ったかの帰属）を足して返す——ただし恒久的な監査記録はこの揮発性の
/// `DecisionTrace`（Explain経由のみ可視）ではなく、`Reducer.Submit`が無条件に書くJournalの
/// `InputReceived`/`InterveneRequested`エントリ自身（`ClientPid`/`ClientImage`/`CorrelationId`を
/// 含む、`req`そのもの）が担う——`EventEnvelope.Source`はJournalに永続化されないことがOpusレビューで
/// 判明したため、この帰属は`Source`ではなく`InterveneRequested`のPayload自体に乗せている。
let step (runtimePolicy: RuntimePolicy) (state: WmState) (envelope: EventEnvelope<InputEvent>) : StepResult =
    match envelope.Input with
    | InterveneRequested req ->
        let w = targetWindowOf req.Command
        if not (Map.containsKey w state.Windows.Records) then
            { Next = state
              Effects = []
              Timers = []
              Trace = [ InterveneRejected(w, "window is not known/managed") ] }
        else
            let cmd = coreCommandOf req.Command
            let result = stepCore runtimePolicy state { envelope with Input = UserCommand cmd }
            { result with Trace = InterveneAccepted(req.CorrelationId, req.ClientPid, req.ClientImage) :: result.Trace }
    | _ -> stepCore runtimePolicy state envelope
