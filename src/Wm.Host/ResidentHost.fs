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
open Wm.Policy.PolicyTypes
open Wm.Policy.MonitorSelector

/// 1件のSnapshot問い合わせ。`TaskCompletionSource`自体はスレッドセーフに`SetResult`できるため、
/// 問い合わせ元スレッドはただ`Task`を待てばよい。
type SnapshotQuery = TaskCompletionSource<SnapshotDto>

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

/// Reducerを1つ所有し、常駐ループとして回り続ける骨格。`Run()`を呼んだスレッドだけがReducer状態へ
/// 触れる（ADR-012の単一スレッドアクセス不変条件をSnapshotクエリにも適用する）。
type ResidentHost(reducer: Reducer, liveNow: unit -> int64) =
    let harness = EffectHarness(reducer, liveNow)
    let queries = ConcurrentQueue<SnapshotQuery>()
    let cts = new CancellationTokenSource()

    let projectSnapshot () : SnapshotDto = snapshotOf reducer.State reducer.Sequence

    /// `reducer.Policy`は不変の環境（`Reducer.Policy`の型直前のコメント参照）なので、Snapshotと違い
    /// キュー経由にせず、呼び出したスレッドから直接読んでよい。
    member _.Policy : PolicyDto = policyDtoOf reducer.Policy

    /// 他スレッド（将来のNamed Pipe接続スレッド）から呼ぶ。即座には解決しない——次の`Run`ループの
    /// イテレーションで、Reducer所有スレッドがキューを汲んで`SetResult`する。
    member _.EnqueueSnapshotQuery() : Task<SnapshotDto> =
        let tcs = SnapshotQuery(TaskCreationOptions.RunContinuationsAsynchronously)
        queries.Enqueue tcs
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
            Thread.Sleep(tickMs)
        harness.Stop()
