/// architecture.md §17：Phase C Named Pipeトランスポートの前段スライス
/// （opus-review-named-pipe-transport.md 1-2「Host側の『状態の所有者』が未定」、v2.135参照）。
///
/// Program.fsのRound 1〜14はいずれも「固定の実時間だけApplication.DoEvents()で回して結果を見て
/// 終わる」デモであり、Reducerを所有し続ける常駐ループが存在しない。ResidentHostはその骨格そのもの
/// ——将来Named Pipeサーバーがこのホストへ繋いでSnapshotを問い合わせる経路の土台になる。
///
/// 設計方式は、opus-review-named-pipe-transport.md Q1-2が挙げた2案のうち(A)を採用した：
/// 外部スレッドからの問い合わせは`ConcurrentQueue`へ積むだけにし、Reducer状態（読み取りも含む）へ
/// 触れるのはこのホストの`Run`を呼んでいるスレッド（＝EffectHarnessへSubmitする既存の不変条件と
/// 同じメインスレッド、ADR-012）だけに限定する。(A)を選んだ理由は、Intervene（§10.2）が将来使う
/// 「キュー投入→Queued of Sequence、結果はJournal購読で確認」という経路と形を揃えられるため
/// （(B)のVolatile.Write公開方式はReducerを止めない利点はあるが、Intervene側の設計とは別の形になる）。
///
/// このスライスで実装するのはSnapshotクエリの往復だけ（Inspectの最小部分、Wm.DebugProtocol.Snapshot
/// 参照）。実際のNamed Pipeサーバー・権限検査・Intervene経路は後続のスライス。
module Wm.Host.ResidentHost

open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open System.Windows.Forms
open Wm.Contracts.CoreCommand
open Wm.Core.Windows
open Wm.Runtime.Reducer
open Wm.Host.EffectHarness
open Wm.DebugProtocol.Snapshot
open Wm.DebugProtocol.Policy
open Wm.DebugProtocol.Journal
open Wm.DebugProtocol.Explain
open Wm.DebugProtocol.Analyzer
open Wm.Policy.PolicyTypes
open Wm.Policy.MonitorSelector
open Wm.Policy.PolicyAnalyzer
open Wm.Core.DecisionTrace
open Wm.Layouts.LayoutRegistry
open Wm.Layouts.PlanValidator

/// 1件のSnapshot問い合わせ。`TaskCompletionSource`自体はスレッドセーフに`SetResult`できるため、
/// 問い合わせ元スレッドはただ`Task`を待てばよい。
type SnapshotQuery = TaskCompletionSource<SnapshotDto>

/// 1件のExplain問い合わせ（v2.147）。`reducer.State`は可変の状態なので、`Policy`/`RecentJournal`と
/// 違い——ADR-012の単一スレッドアクセス不変条件を厳密に守るため——Snapshotと同じキュー方式にする
/// （対象のWindowIdを添えるだけの違い）。
type ExplainQuery = Wm.Contracts.Identifiers.WindowId * TaskCompletionSource<ExplainDto option>

/// 1件のPolicy活性化要求（PolicyActivated実配線、vNext、architecture.md §14/§21）。
/// `Reducer.ActivatePolicy`は書込みなのでADR-012の単一スレッドアクセス不変条件の対象——Snapshot/Explain
/// と同じ「他スレッドはキューへ積むだけ、実際の適用はReducer所有スレッドが次の`Run`ループで行う」方式にする。
type ActivatePolicyRequest = PolicyIR * TaskCompletionSource<unit>

/// `WmState`＋リビジョン番号から`SnapshotDto`を組み立てる純粋関数（FCIS、ADR-002）。分類の判断
/// そのものは`Wm.Core.Windows.registryManagementSnapshot`（Functional Core）に閉じており、ここで
/// やっているのはその結果を`SnapshotDto`/`WindowSnapshotDto`（Wm.DebugProtocol、§17の依存方向で
/// Coreはこちらへ依存できない）へfield-for-fieldで詰め替えるだけ。生きている`Reducer`を
/// 一切要求しないため、`WmState`の値だけでユニットテストできる。
let snapshotOf (state: Wm.Core.WmState.WmState) (revision: int64) : SnapshotDto =
    { Revision = revision
      Windows =
        registryManagementSnapshot state.Windows
        |> List.map (fun (w, management, matchedRule) ->
            { Window = w; Management = management; MatchedRule = matchedRule }) }

/// `PolicyIR`から`PolicyDto`を組み立てる純粋関数（v2.145、Phase C Inspect「Policyを見る」、§10.2）。
/// `snapshotOf`と同じ配置の理由：DTO自体（`Wm.DebugProtocol.Policy`）は`Wm.Policy`の内部型
/// （`WindowPredicate`/`LayoutPredicate`/`MonitorSelector`）をそのまま晒さない方針（§17の依存方向、
/// `Snapshot.fs`冒頭コメント参照）なので、両方へ依存できる`Wm.Host`側でこの射影を行う。
let rec private windowPredicateDtoOf (p: WindowPredicate) : WindowPredicateDto =
    match p with
    | AnyWindow -> AnyWindowDto
    | AppIs (src, v) -> AppIsDto (src, v)
    | TitleContains v -> TitleContainsDto v
    | DialogIs v -> DialogIsDto v
    | MinWidthAtLeast v -> MinWidthAtLeastDto v
    | MovableIs v -> MovableIsDto v
    | All children -> AllDto (children |> List.map windowPredicateDtoOf)
    | AnyOf children -> AnyOfDto (children |> List.map windowPredicateDtoOf)
    | Not child -> NotDto (windowPredicateDtoOf child)

let rec private layoutPredicateDtoOf (p: LayoutPredicate) : LayoutPredicateDto =
    match p with
    | Always -> AlwaysDto
    | TiledCountAtLeast v -> TiledCountAtLeastDto v
    | MonitorWidthAtLeast v -> MonitorWidthAtLeastDto v
    | Both children -> BothDto (children |> List.map layoutPredicateDtoOf)

let private monitorSelectorDtoOf (s: MonitorSelector) : MonitorSelectorDto =
    match s with
    | PrimaryMonitor -> PrimaryMonitorDto
    | MonitorKey k -> MonitorKeyDto k

/// `WindowRule`/`WindowRuleDto`、`LayoutRule`/`LayoutRuleDto`は`Id`/`Priority`/`When`/`Apply`(/`Use`)
/// というフィールド名が完全に一致する（元の型をそのまま写したため）。両方の型が同時に`open`されて
/// いる（`Wm.Policy.PolicyTypes`/`Wm.DebugProtocol.Policy`）ので、レコード構築時にF#が正しい側
/// （Dto側）を選べるよう、ラムダの戻り値型を明示する——型推論の伝播に頼って黒魔術的に正しく解決
/// されることを期待しない。
let policyDtoOf (policy: PolicyIR) : PolicyDto =
    { SchemaVersion = policy.SchemaVersion
      WindowRules =
        policy.WindowRules
        |> List.map (fun (r: WindowRule) ->
            ({ Id = r.Id; Priority = r.Priority; When = windowPredicateDtoOf r.When; Apply = r.Apply }: WindowRuleDto))
      DefaultManagement = policy.DefaultManagement
      LayoutRules =
        policy.LayoutRules
        |> List.map (fun (r: LayoutRule) ->
            ({ Id = r.Id; Priority = r.Priority; When = layoutPredicateDtoOf r.When; Use = r.Use }: LayoutRuleDto))
      DefaultLayout = policy.DefaultLayout
      WorkspaceAssignments =
        policy.WorkspaceAssignments
        |> List.map (fun (wid, sel) -> wid, monitorSelectorDtoOf sel) }

/// `policyDtoOf`の逆方向（v2.148、§14「`PinnedPolicy.ofStored : PolicyDto -> ArtifactIdentity ->
/// Result<PinnedPolicy, IntegrityError>`」が実際にcanonical hashを再計算するには、保存されていた
/// `PolicyDto`から実際の`PolicyIR`を復元する必要がある）。`policyDtoOf`と対になる関数として
/// 同じファイルに置く。
let rec private windowPredicateOf (p: WindowPredicateDto) : WindowPredicate =
    match p with
    | AnyWindowDto -> AnyWindow
    | AppIsDto (src, v) -> AppIs (src, v)
    | TitleContainsDto v -> TitleContains v
    | DialogIsDto v -> DialogIs v
    | MinWidthAtLeastDto v -> MinWidthAtLeast v
    | MovableIsDto v -> MovableIs v
    | AllDto children -> All (children |> List.map windowPredicateOf)
    | AnyOfDto children -> AnyOf (children |> List.map windowPredicateOf)
    | NotDto child -> Not (windowPredicateOf child)

let rec private layoutPredicateOf (p: LayoutPredicateDto) : LayoutPredicate =
    match p with
    | AlwaysDto -> Always
    | TiledCountAtLeastDto v -> TiledCountAtLeast v
    | MonitorWidthAtLeastDto v -> MonitorWidthAtLeast v
    | BothDto children -> Both (children |> List.map layoutPredicateOf)

let private monitorSelectorOf (s: MonitorSelectorDto) : MonitorSelector =
    match s with
    | PrimaryMonitorDto -> PrimaryMonitor
    | MonitorKeyDto k -> MonitorKey k

let policyIrOf (dto: PolicyDto) : PolicyIR =
    { SchemaVersion = dto.SchemaVersion
      WindowRules =
        dto.WindowRules
        |> List.map (fun (r: WindowRuleDto) ->
            ({ Id = r.Id; Priority = r.Priority; When = windowPredicateOf r.When; Apply = r.Apply }: WindowRule))
      DefaultManagement = dto.DefaultManagement
      LayoutRules =
        dto.LayoutRules
        |> List.map (fun (r: LayoutRuleDto) ->
            ({ Id = r.Id; Priority = r.Priority; When = layoutPredicateOf r.When; Use = r.Use }: LayoutRule))
      DefaultLayout = dto.DefaultLayout
      WorkspaceAssignments =
        dto.WorkspaceAssignments
        |> List.map (fun (wid, sel) -> wid, monitorSelectorOf sel) }

/// `Wm.Layouts.LayoutRegistry.LayoutError`から`LayoutErrorDto`への射影（v2.147、Explain）。
let private layoutErrorDtoOf (e: LayoutError) : LayoutErrorDto =
    match e with
    | InvalidInput v -> InvalidInputDto v
    | Unsatisfiable v -> UnsatisfiableDto v
    | LayoutCrashed exnType -> LayoutCrashedDto exnType

/// `Wm.Layouts.PlanValidator.ValidationError`から`ValidationErrorDto`への射影。
let private validationErrorDtoOf (e: ValidationError) : ValidationErrorDto =
    match e with
    | UnmanagedOrDeadWindow w -> UnmanagedOrDeadWindowDto w
    | DuplicateInPlan w -> DuplicateInPlanDto w
    | NonPositiveSize (w, width, height) -> NonPositiveSizeDto (w, width, height)
    | ArithmeticOverflow w -> ArithmeticOverflowDto w
    | OutsideWorkArea (w, rect) -> OutsideWorkAreaDto (w, rect)
    | ExcludedTarget w -> ExcludedTargetDto w
    | MissingPlacement w -> MissingPlacementDto w

/// `Wm.Core.DecisionTrace.DecisionTrace`から`DecisionTraceDto`への射影。
let private decisionTraceDtoOf (t: DecisionTrace) : DecisionTraceDto =
    match t with
    | InconsistencySuspected (domain, detail) -> InconsistencySuspectedDto (domain, detail)
    | LayoutNotRegistered (layoutId, apiVersion) -> LayoutNotRegisteredDto (layoutId, apiVersion)
    | LayoutInvocationFailed (layoutId, error) -> LayoutInvocationFailedDto (layoutId, layoutErrorDtoOf error)
    | PlanRejected (layoutId, errors) -> PlanRejectedDto (layoutId, errors |> List.map validationErrorDtoOf)
    | FrameInsetApplied (requestId, desiredVisible, dispatchedNative) -> FrameInsetAppliedDto (requestId, desiredVisible, dispatchedNative)

/// `Wm.Policy.PolicyAnalyzer.AnalyzerIssue`から`AnalyzerIssueDto`への射影（v2.149、Analyzer Inspect）。
let private analyzerIssueDtoOf (issue: AnalyzerIssue) : AnalyzerIssueDto =
    match issue with
    | DuplicateWindowRuleId rid -> DuplicateWindowRuleIdDto rid
    | DuplicateLayoutRuleId rid -> DuplicateLayoutRuleIdDto rid
    | AnalyzerIssue.UnregisteredLayout(layoutId, apiVersion, ruleId) -> UnregisteredLayoutDto(layoutId, apiVersion, ruleId)
    | InvalidMasterRatio(ratio, ruleId) -> InvalidMasterRatioDto(ratio, ruleId)
    | InvalidColumnCount(count, ruleId) -> InvalidColumnCountDto(count, ruleId)
    | DuplicateWorkspaceAssignment wsid -> DuplicateWorkspaceAssignmentDto wsid

/// `Wm.Host.PolicyArtifact`（v2.150、Deploy Inspect）も再利用するため公開（`SnapshotJson.fs`の
/// `policyRuleIdOptionToJson`等と同じ「後続ファイルでの再利用のために`private`を外す」規約）。
let analyzerReportDtoOf (report: AnalyzerReport) : AnalyzerReportDto =
    { Errors = report.Errors |> List.map analyzerIssueDtoOf }

/// Analyzer Inspect（architecture.md §10.2、v2.149）：クライアントが送ってきた候補`PolicyDto`を、
/// Hostが自分の採用中のLayout Registryに対して現行Analyzerで実際に分析する——§14「クライアントの
/// 自己申告を信用しない」と同じ原則で、Analyzer自身の実行はこのスライスでも常にHost側で行う。
let analyzeCandidateOf (registry: Registry) (candidate: PolicyDto) : AnalyzerReportDto =
    let manifest = Registry.toManifest registry
    analyzerReportDtoOf (Wm.Policy.PolicyAnalyzer.analyze manifest (policyIrOf candidate))

/// Journal Inspect（architecture.md §10.2、v2.147）：直近`n`件を新しい順で返す。ユーザー決定
/// 2026-11-05：カーソル型APIやセッション全体ではなく、シンプルに直近N件だけ。ファイルの読み取りと
/// 同時刻の書き込み（`Reducer.Submit`が別スレッドから`appendEntry`する可能性）が競合する稀なケースは
/// 例外を飲んで空扱いにする——Inspectは最善努力の読み取りであり、強い一貫性を保証しない
/// （grounded discipline：ここで排他制御を持ち込む必要性はまだ裏付けられていない）。
let recentJournalOf (journalPath: string option) (n: int) : JournalDto =
    match journalPath with
    | None -> { Entries = [] }
    | Some path ->
        try
            let all = Wm.Journal.JournalWriter.readAll path |> Seq.toArray
            let start = max 0 (all.Length - n)
            { Entries =
                all.[start ..]
                |> Array.rev
                |> Array.map Wm.Journal.JournalWriter.entryToJson
                |> Array.toList }
        with _ ->
            { Entries = [] }

/// Explain（architecture.md §10.2、v2.147）：対象窓の現在の分類＋直近の`DecisionTrace`全件
/// （`Explain.fs`冒頭コメントの既知の限界どおり、窓単位には絞らない）。対象窓がまだ知られていなければ
/// `None`。
let explainOf (registry: Wm.Core.Windows.WindowRegistry) (recentTrace: DecisionTrace list) (w: Wm.Contracts.Identifiers.WindowId) : ExplainDto option =
    registryManagementSnapshot registry
    |> List.tryFind (fun (wid, _, _) -> wid = w)
    |> Option.map (fun (_, management, matchedRule) ->
        { Window = w
          Management = management
          MatchedRule = matchedRule
          RecentTrace = recentTrace |> List.map decisionTraceDtoOf })

/// Reducerを1つ所有し、常駐ループとして回り続ける骨格。`Run()`を呼んだスレッドだけがReducer状態へ
/// 触れる（ADR-012の単一スレッドアクセス不変条件をSnapshotクエリにも適用する）。
type ResidentHost(reducer: Reducer, liveNow: unit -> int64) =
    let harness = EffectHarness(reducer, liveNow)
    let queries = ConcurrentQueue<SnapshotQuery>()
    let explainQueries = ConcurrentQueue<ExplainQuery>()
    let activateRequests = ConcurrentQueue<ActivatePolicyRequest>()
    let cts = new CancellationTokenSource()

    let projectSnapshot () : SnapshotDto = snapshotOf reducer.State reducer.Sequence

    /// `reducer.Policy`は不変の環境（`Reducer.Policy`の型直前のコメント参照）なので、Snapshotと違い
    /// キュー経由にせず、呼び出したスレッドから直接読んでよい。
    member _.Policy : PolicyDto = policyDtoOf reducer.Policy

    /// Journal Inspect（v2.147）。`reducer.JournalPath`は不変の環境値（`Policy`と同じ理由）だが、
    /// ファイル自体への読み取りはReducer所有スレッドが書き込み中のものと競合しうる——
    /// `recentJournalOf`が例外を飲んで最善努力にしているので、キュー経由にせず直接読んでよい。
    member _.RecentJournal(n: int) : JournalDto = recentJournalOf reducer.JournalPath n

    /// Analyzer Inspect（v2.149）。`reducer.LayoutRegistry`は`Policy`と同じ理由で不変の環境値なので
    /// 直接読んでよい。
    member _.AnalyzeCandidate(candidate: PolicyDto) : AnalyzerReportDto = analyzeCandidateOf reducer.LayoutRegistry candidate

    /// 他スレッド（将来のNamed Pipe接続スレッド）から呼ぶ。即座には解決しない——次の`Run`ループの
    /// イテレーションで、Reducer所有スレッドがキューを汲んで`SetResult`する。
    member _.EnqueueSnapshotQuery() : Task<SnapshotDto> =
        let tcs = SnapshotQuery(TaskCreationOptions.RunContinuationsAsynchronously)
        queries.Enqueue tcs
        tcs.Task

    /// Explain（v2.147）。`EnqueueSnapshotQuery`と同じキュー方式——`State`は可変なので、ADR-012の
    /// 単一スレッドアクセス不変条件を厳密に守る。
    member _.EnqueueExplainQuery(w: Wm.Contracts.Identifiers.WindowId) : Task<ExplainDto option> =
        let tcs = TaskCompletionSource<ExplainDto option>(TaskCreationOptions.RunContinuationsAsynchronously)
        explainQueries.Enqueue(w, tcs)
        tcs.Task

    /// PolicyActivated実配線（vNext）：`op=deploy`が検証・保存・Journal記録に成功した新Policyを、
    /// 実際にReducerへ反映させるための書込み要求口。`EnqueueSnapshotQuery`/`EnqueueExplainQuery`と
    /// 同じキュー方式——返した`Task`は、次の`Run`ループでReducer所有スレッドが実際に
    /// `Reducer.ActivatePolicy`を呼び終えるまで完了しない（§14「クライアントの自己申告を信用しない」
    /// と同じ精神で、`op=deploy`の応答自体も「本当にswapが終わった後」まで待ってから返せるようにする）。
    member _.EnqueueActivatePolicy(policy: PolicyIR) : Task<unit> =
        let tcs = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        activateRequests.Enqueue(policy, tcs)
        tcs.Task

    /// 他スレッドから呼んでよい、スレッドセーフな停止要求。
    member _.RequestStop() = cts.Cancel()

    member _.IsStopRequested = cts.IsCancellationRequested

    /// `EffectHarness.Submit`と同様、Reducerへの投入は`Run()`を回しているスレッドから直接呼ぶ想定
    /// （WinEventコールバック等、既存Round群と同じ使い方）。
    member _.Submit (now: int64) (source: string) (input: InputEvent) = harness.Submit now source input

    /// 呼び出したスレッドをブロックして常駐ループを回す。`RequestStop()`が呼ばれるまで戻らない
    /// （`Application.DoEvents()`でメッセージポンプする既存の確立済み方式——EffectHarness.Pumpと同じ
    /// 土台を、固定時間ではなく`RequestStop`まで無期限に続ける点だけが違う）。
    member _.Run(?tickMs: int) =
        let tickMs = defaultArg tickMs 1
        while not cts.IsCancellationRequested do
            Application.DoEvents()
            harness.DrainResults(liveNow ())
            // EffectHarness.Pumpと同じ規律：実時間でまだ来ていないタイマーは発火させない
            // （`Advance`自体は仮想時計を問答無用で次の期限まで進めてしまうため、ここで
            // 呼出側がゲートする必要がある。ラウンド8の実機ログ調査で一度破られていた既知の地雷）。
            while reducer.Timers.NextDeadline |> Option.exists (fun d -> d <= liveNow ()) do
                harness.Advance() |> ignore
            let mutable q = Unchecked.defaultof<SnapshotQuery>
            while queries.TryDequeue(&q) do
                q.SetResult(projectSnapshot ())
            let mutable eq = Unchecked.defaultof<ExplainQuery>
            while explainQueries.TryDequeue(&eq) do
                let w, tcs = eq
                tcs.SetResult(explainOf reducer.State.Windows reducer.RecentTrace w)
            let mutable aq = Unchecked.defaultof<ActivatePolicyRequest>
            while activateRequests.TryDequeue(&aq) do
                let policy, tcs = aq
                reducer.ActivatePolicy policy
                tcs.SetResult(())
            Thread.Sleep(tickMs)
        harness.Stop()
