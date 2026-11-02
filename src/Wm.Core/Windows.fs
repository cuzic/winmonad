/// Core.Windows（ADR-019実装順14、architecture.md §4.4/§4.5/§5.4/§7.1段①⑤）。
/// 窓ごとの分類（Unknownの待ちを含む）、ユーザー指定、識別に関する信念（AppIdentity、Process、
/// 分類述語が読む属性）。
///
/// このラウンドで意図的に対象外にしたこと（いずれも既知のギャップとして明示する）：
/// - **HWND再利用の検知**（§4.5「Processが異なる値で観測されたら…」）：`reviseIdentity`はSticky規則の
///   「値は変えず確度を下げる」だけを行い、`Process`の不一致を`WindowUnmanaged`＋`ForgetWindow`へ
///   変換する分岐は実装しない。これは「消滅と同じに扱う」特殊な経路であり、DomainEventの意味論を
///   どう割り振るか（既存の`WindowUnmanaged`を再利用するか、新しいケースを追加するか）が非自明なため、
///   独立したラウンドで扱う。
/// - **`StyleChanged`/`MinimizedStateChanged`の配線**：前者は信念を運ばない通知だけのイベントであり、
///   どの段でどう使うか（`ObserveWindow`を発行するか）が7.1の表に明記されていない。後者はPlacement
///   ドメイン（`GeometryBeliefs.IsMinimized`、§4.5）の所轄であり、Placement着手まで配線先が無い。
/// - **`ByPolicy`で確定済みの窓の増分再分類**：§5.4「分類確定後に再分類（タイトル変化等）で結果が変わる
///   場合は、7.3の振動防止の対象に含める」は一般の再分類を想定しているが、具体的な振動の閾値
///   （何回・どの時間窓で「振動」とみなすか）は本書に数値が無い。`ByTimeout`からの一度だけの
///   再分類（§5.4で具体的に規定されている分）だけを実装し、それ以外の増分再分類と、
///   `ManagementChangedAt`を実際に読んで劣化させるロジックは次のラウンドへ持ち越す
///   （このラウンドでは記録だけ行う）。
/// - **`Unplaced`への読み替え**（§4.4「Movability = NotMovableならUnplacedに読み替える」）：
///   Movabilityの信念はPlacementドメインが持つため、Placement着手まで`EffUnplaced`は到達しない
///   （`classificationView`は4.5の初期既定値と同じ`Movable`を常に返す——Placementが実装されれば
///   実際の信念に置き換わるだけで、`WindowPredicate.MovableIs`の評価自体は変わらない）。
module Wm.Core.Windows

open Wm.Contracts.Identifiers
open Wm.Contracts.Observation
open Wm.Contracts.Beliefs
open Wm.Contracts.SharedVocabulary
open Wm.Contracts.Timers
open Wm.Contracts.CoreCommand
open Wm.Core.DomainEvents
open Wm.Policy.PolicyTypes
open Wm.Policy.PolicyEvaluator

/// §4.4。`ByPolicy`の`PolicyRuleId option`は`selectManagement`の`matchedRule`をそのまま運ぶ
/// （`None` = どのルールにも一致せず`DefaultManagement`で確定）。
type ClassifiedBy =
    | ByPolicy of PolicyRuleId option
    | ByTimeout
    | ByUserOverride

type ClassificationState =
    | NotCandidate
    | AwaitingEvidence of deadline: int64 * homeWorkspace: WorkspaceId option
    | Classified of EffectiveManagement * ClassifiedBy

type WindowRecord = {
    Classification: ClassificationState
    UserOverride: Management option
    LateReclassifyUsed: bool
    /// 7.3の振動検知用（直近8件の時刻）。このラウンドでは記録のみ行い、読んで劣化させる判定は
    /// 実装しない（上記ファイル冒頭コメント参照）。
    ManagementChangedAt: int64 list
}

module WindowRecord =
    let initial: WindowRecord =
        { Classification = NotCandidate
          UserOverride = None
          LateReclassifyUsed = false
          ManagementChangedAt = [] }

type WindowRegistry = {
    Records: Map<WindowId, WindowRecord>
    Identity: Map<WindowId, IdentityBeliefs>
    ClassInputs: Map<WindowId, ClassInputBeliefs>
}

let initial: WindowRegistry = { Records = Map.empty; Identity = Map.empty; ClassInputs = Map.empty }

let private identityInitial: IdentityBeliefs =
    { Exe = Unformed []; HostExe = Unformed []; Aumid = Unformed []; CoreWindowExe = Unformed []; Process = Unformed [] }

let private classInputsInitial: ClassInputBeliefs =
    { IsDialog = LatestWins.unformed
      Owner = LatestWins.unformed
      IsVisible = LatestWins.unformed
      Cloak = LatestWins.unformed
      TitleMatches = LatestWins.unformed }

/// 段⑤・Policy評価・タイマー・ユーザー指定の解決が読む環境（§3.3「RuntimePolicyは環境」、
/// §7.1「(a)この段より前に処理されたドメインの今回の状態、(b)任意のドメインのstep開始時点の状態」）。
/// `WmState.step`がstepごとにその場で組み立てて渡す（Topology/Focusの実際の信念レコードを先取りしない
/// 最小限の投影——`ClassificationView`/`LayoutContext`/`ValidationInput`と同じ位置づけ）。
type ClassificationCtx = {
    Now: int64
    PatternSet: TitlePatternSet
    Policy: PolicyIR
    ActiveWorkspace: WorkspaceId option
    ForegroundWindow: WindowId option
    ClassificationTimeoutMs: int64
    /// Placement `Movability`の読み取り専用の写し（§4.4「Movability = NotMovableならTiled/Floatingを
    /// Unplacedに読み替える」）。段①⑤でWindowsがPlacementより先に処理されるため（§7.1）、
    /// `WmState.step`がstep開始時点のPlacement状態からその場で渡す（v2.23追加、Core.Windowsの
    /// 次ラウンドでMovabilityを実際に読むまでは常に空だった既知のギャップを塞ぐ）。
    Movability: Map<WindowId, Belief<Movability>>
}

// ===== 信念の修正（§4.5） =====

/// Sticky規則：最初の妥当なOk観測で固定する（Confirmed）。既知の不定値はOkとして扱わない
/// （`isValid`、R4-13：`CoreWindowExe`=`ApplicationFrameHost.exe`の例外）。固定後に異なる妥当な値が
/// 観測されたら、値は変えず確度を`Inferred`へ下げる（HWND再利用の検知はこの関数の対象外、ファイル冒頭参照）。
let private reviseSticky (now: int64) (isValid: 'T -> bool) (current: BeliefSlot<'T>) (observation: Observation<'T>) : BeliefSlot<'T> =
    match observation.Value with
    | Error _ -> current
    | Ok v when not (isValid v) -> current
    | Ok v ->
        match current with
        | Unformed _ -> Believed { Value = v; Confidence = Confirmed; LastRevisedAt = now }
        | Believed b when b.Value = v -> current
        | Believed b -> Believed { b with Confidence = Inferred [ Sample observation.SampleSequence ] }

let private alwaysValid (_: 'T) = true
let private notApplicationFrameHost (v: string) = v <> "ApplicationFrameHost.exe"

/// `WindowSnapshot`から`IdentityBeliefs`をSticky規則で修正する（§4.5）。
let reviseIdentity (now: int64) (current: IdentityBeliefs) (snapshot: WindowSnapshot) : IdentityBeliefs =
    { Exe = snapshot.AppIdentity.Exe |> Option.map (reviseSticky now alwaysValid current.Exe) |> Option.defaultValue current.Exe
      HostExe = snapshot.AppIdentity.HostExe |> Option.map (reviseSticky now alwaysValid current.HostExe) |> Option.defaultValue current.HostExe
      Aumid = snapshot.AppIdentity.Aumid |> Option.map (reviseSticky now alwaysValid current.Aumid) |> Option.defaultValue current.Aumid
      CoreWindowExe =
        snapshot.AppIdentity.CoreWindowExe
        |> Option.map (reviseSticky now notApplicationFrameHost current.CoreWindowExe)
        |> Option.defaultValue current.CoreWindowExe
      Process = snapshot.Process |> Option.map (reviseSticky now alwaysValid current.Process) |> Option.defaultValue current.Process }

/// `WindowSnapshot`から`ClassInputBeliefs`をLatestWins規則で修正する（§4.5。`Beliefs.LatestWins.revise`を
/// 直接使う。R4-9/Opusレビュー1-1：ドメインごとに再実装しない）。
let reviseClassInputsFromSnapshot (now: int64) (current: ClassInputBeliefs) (snapshot: WindowSnapshot) : ClassInputBeliefs =
    { IsDialog = snapshot.IsDialog |> Option.map (LatestWins.revise now current.IsDialog) |> Option.defaultValue current.IsDialog
      Owner = snapshot.Owner |> Option.map (LatestWins.revise now current.Owner) |> Option.defaultValue current.Owner
      IsVisible = snapshot.IsVisible |> Option.map (LatestWins.revise now current.IsVisible) |> Option.defaultValue current.IsVisible
      Cloak = snapshot.Cloak |> Option.map (LatestWins.revise now current.Cloak) |> Option.defaultValue current.Cloak
      TitleMatches = snapshot.TitleMatches |> Option.map (LatestWins.revise now current.TitleMatches) |> Option.defaultValue current.TitleMatches }

let reviseVisibility (now: int64) (current: ClassInputBeliefs) (obs: Observation<bool>) : ClassInputBeliefs =
    { current with IsVisible = LatestWins.revise now current.IsVisible obs }

let reviseCloak (now: int64) (current: ClassInputBeliefs) (obs: Observation<CloakState>) : ClassInputBeliefs =
    { current with Cloak = LatestWins.revise now current.Cloak obs }

/// 候補条件（§4.4「WM自身の非表示と候補条件」、§5.4「Normalizerの候補条件（可視、非ツール窓、非Cloak等…）」）。
/// `ClassInputBeliefs`からCoreが判定できるのは可視性とCloak状態の2つだけである：「非ツール窓」
/// （`WS_EX_TOOLWINDOW`相当）を判定する属性は`WindowSnapshot`が運ばないため、Shellの列挙時点で
/// 既に除外されている前提とする——この設計判断は21章に記録する（ADR-019実装順14、Core.Windows着手時）。
/// Cloak未観測は「まだCloakされていないとみなす」楽観的既定（4.5の他の信念の既定値と同じ考え方）。
let isCandidate (inputs: ClassInputBeliefs) : bool =
    match LatestWins.value inputs.IsVisible with
    | Some true ->
        match LatestWins.value inputs.Cloak with
        | Some cloak -> not (cloak.App || cloak.Shell || cloak.Inherited)
        | None -> true
    | _ -> false

/// §4.4「Movability = NotMovableならTiled/Floatingを Unplacedに読み替える」。`Excluded`は対象外
/// （すでにどのWorkspaceにも属さない。Unplacedとは意味が別、§4.4）。
let private toEffective (movability: Movability) (m: Management) : EffectiveManagement =
    match m with
    | Excluded -> EffExcluded
    | Tiled when movability = NotMovable -> EffUnplaced
    | Floating when movability = NotMovable -> EffUnplaced
    | Tiled -> EffTiled
    | Floating -> EffFloating

/// `ctx.Movability`（Placementの信念の写し）に窓のエントリが無ければ、4.5の発見時の楽観的既定値
/// （`Movable`、`Inferred []`）を返す（Placementがまだこの窓を見ていない、または消滅後の古い参照）。
/// エントリがあれば、その確度・根拠（`Confidence`/`LastRevisedAt`）をそのまま伝える。
let private movabilityBeliefOf (ctx: ClassificationCtx) (w: WindowId) : Belief<Movability> =
    Map.tryFind w ctx.Movability
    |> Option.defaultValue { Value = Movable; Confidence = Inferred []; LastRevisedAt = ctx.Now }

let private movabilityOf (ctx: ClassificationCtx) (w: WindowId) : Movability = (movabilityBeliefOf ctx w).Value

/// Policyの述語が読む信念だけを集めた最小限の投影（§4.5「Policyの評価とLayoutが読むビュー」、
/// PolicyEvaluator.fsのドキュメンテーションコメント参照）。`MinWidthDip`はPlacementの信念だが、
/// 収集はできてもまだ蓄積する修正関数が無いため、既定どおりUnknownを返す（Placement.fsのファイル
/// 冒頭コメント参照）。`Movability`はv2.23で`ctx.Movability`から実際の値を読むようになった。
let classificationView (ctx: ClassificationCtx) (w: WindowId) (identity: IdentityBeliefs) (inputs: ClassInputBeliefs) : ClassificationView =
    { Identity = identity
      IsDialog = inputs.IsDialog.Slot
      TitleMatches = inputs.TitleMatches.Slot
      MinWidthDip = Unformed [ UnknownMinSize w ]
      Movability = Believed (movabilityBeliefOf ctx w) }

let private tryClassify (ctx: ClassificationCtx) (w: WindowId) (identity: IdentityBeliefs) (inputs: ClassInputBeliefs) : ClassificationResult =
    classificationView ctx w identity inputs |> fun view -> selectManagement ctx.PatternSet view ctx.Policy

/// 窓の所属先（§5.4「期限超過で解決した窓の所属先は…最初にUnknownと評価された時点のアクティブな
/// Workspaceとする。ただし分類が確定する時点でその窓がフォアグラウンドなら、所属はその時点の
/// アクティブなWorkspaceとする」、R5-4）。`recordedHome`が`None`のときは「新しい好みは無い」ことを
/// 意味し、呼び出し側（`WmState.step`）が窓の現在のWorkspace所属、無ければ最小のWorkspaceId
/// （R6-11）へフォールバックする。
let private resolveHome (ctx: ClassificationCtx) (w: WindowId) (recordedHome: WorkspaceId option) : WorkspaceId option =
    if ctx.ForegroundWindow = Some w then ctx.ActiveWorkspace else recordedHome

/// `EffExcluded`/`EffUnplaced`なら`WindowUnmanaged`（Workspaces/Focus.History/SwitchSettleからだけ除く、
/// §4.4「動かせない窓（Unplaced）はどのWorkspaceにも属さない」）、それ以外なら`WindowClassified`
/// （WorkspacesへStack末尾挿入、§4.4）を返す。
let private classificationEvent (w: WindowId) (eff: EffectiveManagement) (home: WorkspaceId option) : DomainEvent =
    match eff with
    | EffExcluded | EffUnplaced -> WindowUnmanaged w
    | _ -> WindowClassified (w, eff, home)

/// 現在の信念から、1つの窓の分類を再評価する（§5.4/§7.1段⑤）。`WindowObserved`等で信念が
/// 更新された直後、またはタイマー・ユーザーコマンドの処理の一部として呼ぶ。
let private reconsider
    (ctx: ClassificationCtx)
    (w: WindowId)
    (identity: IdentityBeliefs)
    (inputs: ClassInputBeliefs)
    (record: WindowRecord)
    : WindowRecord * DomainEvent list * TimerCommand<TimerId> list =
    match record.UserOverride with
    | Some _ ->
        // ユーザー指定はPolicyより優先し、信念の変化を理由には上書きしない（§4.4 R4-20）。
        // `UserOverride = Some _`は常に`Classification = Classified (_, ByUserOverride)`と対になっている
        // （この対応はapplyUserOverride/ClearUserOverrideだけが変更する）。
        record, [], []
    | None ->
        if not (isCandidate inputs) then
            // 一度Classifiedになった窓は、候補条件を外れたことではNotCandidateへ戻さない（§4.4 R5-2）。
            record, [], []
        else
            match record.Classification with
            | Classified (_, ByUserOverride) ->
                // 到達しないはずの組合せ（`UserOverride = None`なら`ByUserOverride`は無い、上記の対応）だが、
                // 全域関数として安全側に倒す（§7.1「起こり得ない組合せに出会ったら例外を投げずに入力を捨てる」）。
                record, [], []
            | Classified (_, ByPolicy _) ->
                // ByPolicyで確定済みの窓の増分再分類はこのラウンドでは対象外（ファイル冒頭コメント参照）。
                record, [], []
            | Classified (eff, ByTimeout) when not record.LateReclassifyUsed ->
                match tryClassify ctx w identity inputs with
                | Resolved (m', ruleId) ->
                    let eff' = toEffective (movabilityOf ctx w) m'
                    if eff' = eff then
                        record, [], []
                    else
                        let home = resolveHome ctx w None
                        { record with
                            Classification = Classified (eff', ByPolicy ruleId)
                            LateReclassifyUsed = true
                            ManagementChangedAt = List.truncate 8 (ctx.Now :: record.ManagementChangedAt) },
                        [ classificationEvent w eff' home ], []
                | ClassificationResult.AwaitingEvidence _ -> record, [], [] // まだUnknown。既定分類(Floating)のまま待つ
            | Classified (_, ByTimeout) ->
                record, [], [] // 再分類は既に1回使用済み（§5.4）
            | NotCandidate ->
                match tryClassify ctx w identity inputs with
                | Resolved (m', ruleId) ->
                    let eff = toEffective (movabilityOf ctx w) m'
                    let home = resolveHome ctx w ctx.ActiveWorkspace
                    { record with Classification = Classified (eff, ByPolicy ruleId) },
                    [ classificationEvent w eff home ], []
                | ClassificationResult.AwaitingEvidence _ ->
                    let deadline = ctx.Now + ctx.ClassificationTimeoutMs
                    { record with Classification = AwaitingEvidence (deadline, ctx.ActiveWorkspace) },
                    [], [ StartTimer (TimerId.Windows (ClassificationDeadline w), ctx.ClassificationTimeoutMs) ]
            | AwaitingEvidence (deadline, home) ->
                match tryClassify ctx w identity inputs with
                | Resolved (m', ruleId) ->
                    let eff = toEffective (movabilityOf ctx w) m'
                    let home' = resolveHome ctx w home
                    { record with Classification = Classified (eff, ByPolicy ruleId) },
                    [ classificationEvent w eff home' ], [ CancelTimer (TimerId.Windows (ClassificationDeadline w)) ]
                | ClassificationResult.AwaitingEvidence _ -> record, [], [] // まだUnknown、期限はそのまま

let private withRecord
    (w: WindowId)
    (registry: WindowRegistry)
    (f: WindowRecord -> WindowRecord * DomainEvent list * TimerCommand<TimerId> list)
    : WindowRegistry * DomainEvent list * TimerCommand<TimerId> list =
    let record0 = Map.tryFind w registry.Records |> Option.defaultValue WindowRecord.initial
    let record', events, cmds = f record0
    { registry with Records = Map.add w record' registry.Records }, events, cmds

// ===== 公開ハンドラ（§7.1段①⑤、WmState.stepから呼ぶ） =====

/// `WindowObserved`：Identity/ClassInputsを修正し、同じstepで分類を再評価する。
let onWindowObserved (ctx: ClassificationCtx) (snapshot: WindowSnapshot) (registry: WindowRegistry) : WindowRegistry * DomainEvent list * TimerCommand<TimerId> list =
    let w = snapshot.Id
    let identity0 = Map.tryFind w registry.Identity |> Option.defaultValue identityInitial
    let inputs0 = Map.tryFind w registry.ClassInputs |> Option.defaultValue classInputsInitial
    let identity' = reviseIdentity ctx.Now identity0 snapshot
    let inputs' = reviseClassInputsFromSnapshot ctx.Now inputs0 snapshot
    let registry' = { registry with Identity = Map.add w identity' registry.Identity; ClassInputs = Map.add w inputs' registry.ClassInputs }
    withRecord w registry' (fun record -> reconsider ctx w identity' inputs' record)

let private withBeliefUpdate
    (ctx: ClassificationCtx)
    (w: WindowId)
    (reviseInputs: ClassInputBeliefs -> ClassInputBeliefs)
    (registry: WindowRegistry)
    : WindowRegistry * DomainEvent list * TimerCommand<TimerId> list =
    let identity = Map.tryFind w registry.Identity |> Option.defaultValue identityInitial
    let inputs0 = Map.tryFind w registry.ClassInputs |> Option.defaultValue classInputsInitial
    let inputs' = reviseInputs inputs0
    let registry' = { registry with ClassInputs = Map.add w inputs' registry.ClassInputs }
    withRecord w registry' (fun record -> reconsider ctx w identity inputs' record)

let onVisibilityChanged (ctx: ClassificationCtx) (w: WindowId) (obs: Observation<bool>) (registry: WindowRegistry) =
    withBeliefUpdate ctx w (fun inputs -> reviseVisibility ctx.Now inputs obs) registry

let onCloakChanged (ctx: ClassificationCtx) (w: WindowId) (obs: Observation<CloakState>) (registry: WindowRegistry) =
    withBeliefUpdate ctx w (fun inputs -> reviseCloak ctx.Now inputs obs) registry

/// `TimerElapsed (Windows (ClassificationDeadline w))`：期限超過時、安全な既定分類（Floating）へ解決する
/// （§5.4「無期限に待たない」）。`awaitDeadline`で古い発火を無視する（Topology/Health/Focusと同じパターン）。
let onClassificationDeadline (ctx: ClassificationCtx) (w: WindowId) (registry: WindowRegistry) : WindowRegistry * DomainEvent list * TimerCommand<TimerId> list =
    match Map.tryFind w registry.Records with
    | None -> registry, [], [] // 既に消滅した窓（古い発火）
    | Some record ->
        match record.Classification with
        | AwaitingEvidence (deadline, home) when awaitDeadline (Some deadline) ctx.Now ->
            let home' = resolveHome ctx w home
            let record' = { record with Classification = Classified (EffFloating, ByTimeout) }
            { registry with Records = Map.add w record' registry.Records }, [ classificationEvent w EffFloating home' ], []
        | _ -> registry, [], [] // 古い発火、またはユーザー操作等で既に確定済み

let private applyUserOverride (ctx: ClassificationCtx) (w: WindowId) (m: Management) (registry: WindowRegistry) : WindowRegistry * DomainEvent list * TimerCommand<TimerId> list =
    withRecord w registry (fun record ->
        let eff = toEffective (movabilityOf ctx w) m
        let home = resolveHome ctx w None
        let wasAwaiting = match record.Classification with AwaitingEvidence _ -> true | _ -> false
        let changed = match record.Classification with Classified (prevEff, _) -> prevEff <> eff | _ -> true
        let record' =
            { record with
                UserOverride = Some m
                Classification = Classified (eff, ByUserOverride)
                ManagementChangedAt = if changed then List.truncate 8 (ctx.Now :: record.ManagementChangedAt) else record.ManagementChangedAt }
        let cmds = if wasAwaiting then [ CancelTimer (TimerId.Windows (ClassificationDeadline w)) ] else []
        record', [ classificationEvent w eff home ], cmds)

/// `ToggleFloating`/`SetManagement`/`ClearUserOverride`（§4.4のCoreCommand）。管理下に無い窓
/// （`Records`に無いWindowId）への指定は何もしない。
let onUserCommand (ctx: ClassificationCtx) (cmd: CoreCommand) (registry: WindowRegistry) : WindowRegistry * DomainEvent list * TimerCommand<TimerId> list =
    match cmd with
    | SetManagement (w, m) when Map.containsKey w registry.Records -> applyUserOverride ctx w m registry
    | ToggleFloating w when Map.containsKey w registry.Records ->
        let record = Map.find w registry.Records
        let current =
            match record.UserOverride with
            | Some m -> m
            | None ->
                match record.Classification with
                | Classified (EffFloating, _) -> Floating
                | Classified (EffExcluded, _) -> Excluded
                | _ -> Tiled
        applyUserOverride ctx w (if current = Floating then Tiled else Floating) registry
    | ClearUserOverride w when Map.containsKey w registry.Records ->
        let record = Map.find w registry.Records
        let identity = Map.tryFind w registry.Identity |> Option.defaultValue identityInitial
        let inputs = Map.tryFind w registry.ClassInputs |> Option.defaultValue classInputsInitial
        // UserOverrideを外し、候補条件とPolicyの評価から改めて分類をやり直す（§4.4「Policyの分類に戻す」）。
        let baseRecord = { record with UserOverride = None; Classification = NotCandidate }
        let record', events, cmds = reconsider ctx w identity inputs baseRecord
        { registry with Records = Map.add w record' registry.Records }, events, cmds
    | _ -> registry, [], []

/// `Movability`の信念が`NotMovable`へ修正された直後（`Denied OtherDenial`の帰結、§4.5）に呼ぶ。
/// すでに`Tiled`/`Floating`として確定している窓だけを`Unplaced`へ読み替える（§4.4）。`Excluded`/
/// `Unplaced`は対象外（前者はそもそも対象外、後者は既にUnplaced）。ユーザー指定（`ByUserOverride`）
/// も対象外（§4.4のUnplaced読み替えはPolicyの分類結果に対する規則であり、R4-20「ユーザー指定は
/// Policyより優先する」と同じ理由でユーザーの明示的な指定を上書きしない——v2.23で実装時に判断）。
/// `Movable`への回復時の逆方向はこのラウンドでは対象外（`Satisfied`→`Movable`遷移自体がまだ無い、
/// Placement.fsのファイル冒頭コメント参照）。
let onMovabilityBecameNotMovable (w: WindowId) (registry: WindowRegistry) : WindowRegistry * DomainEvent list =
    match Map.tryFind w registry.Records with
    | None -> registry, []
    | Some record ->
        match record.Classification with
        | Classified ((EffTiled | EffFloating), by) when by <> ByUserOverride ->
            let record' = { record with Classification = Classified (EffUnplaced, by) }
            { registry with Records = Map.add w record' registry.Records }, [ WindowUnmanaged w ]
        | _ -> registry, []

/// 窓の消滅時、`Records`/`Identity`/`ClassInputs`を除く（§4.4「Windows.Recordsと各ドメインの信念は
/// 窓が消滅した時点でのみ除去する」）。`WmState.vanishWindow`から呼ぶ。
let onWindowVanished (w: WindowId) (registry: WindowRegistry) : WindowRegistry =
    { registry with
        Records = Map.remove w registry.Records
        Identity = Map.remove w registry.Identity
        ClassInputs = Map.remove w registry.ClassInputs }
