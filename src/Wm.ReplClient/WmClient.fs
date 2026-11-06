/// architecture.md §10.1/§10.3：Named Pipe経由でWm.Hostに接続するREPLクライアント。v2.144で、
/// `Wm.Host/Program.fs`のRound 17〜19に埋め込まれていたテスト専用のクライアントコード（生の
/// `NamedPipeClientStream`＋手動フレーミング呼出）から、再利用可能な独立プロジェクトへ切り出した。
/// §10.1「REPL式はクライアント側で実行される」の原則どおり、`Wm.Core`/`Wm.Host`（net8.0-windows
/// 専用）には一切依存しない（`Wm.Contracts`/`Wm.DebugProtocol`のみ、plain net8.0）。
///
/// §10.3の概念コード（`let! wm = WmClient.connectLocalAsync ()`、`let! snapshot = wm.SnapshotAsync()`、
/// `let! diagnostics = wm.AnalyzeAsync(rules)`）のうち、Inspectの5操作（Snapshot/Policy/Journal/
/// Explain/Analyzer、v2.145/v2.147/v2.149）とDeploy（v2.150、§14のPolicyArtifact検証＋保存＋
/// Journal記録に加え、vNextでReducerの実行中Policyの実際のswapまで、PolicyActivated実配線）、
/// Intervene（v2.151、`SetManagement`/`ClearUserOverride`限定の最初のスライス）を実装済み——
/// Fork/Probeは後続のスライス（memory `project_named_pipe_ipc_task_breakdown.md`参照）。
module Wm.ReplClient.WmClient

open System
open System.IO
open System.IO.Pipes
open System.Security.Principal
open System.Text
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open Wm.Contracts.Identifiers
open Wm.DebugProtocol.Snapshot
open Wm.DebugProtocol.SnapshotJson
open Wm.DebugProtocol.Policy
open Wm.DebugProtocol.PolicyJson
open Wm.DebugProtocol.Journal
open Wm.DebugProtocol.JournalJson
open Wm.DebugProtocol.Explain
open Wm.DebugProtocol.ExplainJson
open Wm.DebugProtocol.Analyzer
open Wm.DebugProtocol.AnalyzerJson
open Wm.DebugProtocol.Deploy
open Wm.DebugProtocol.DeployJson
open Wm.DebugProtocol.Intervene
open Wm.DebugProtocol.InterveneJson
open Wm.DebugProtocol.WireFraming

/// 接続済みのクライアント。コンストラクタは`internal`——`connectAsync`/`connectLocalAsync`
/// （接続に成功した場合のみ`Ok`を返す）だけがインスタンスを作れる（`ResidentHost`/`GatedEffect`と
/// 同じ、未接続の値を誤って持たせない設計）。`Dispose`すると下層のパイプも閉じる。
type WmClient internal (pipe: NamedPipeClientStream) =
    interface IDisposable with
        member _.Dispose() = pipe.Dispose()

    /// 1件のリクエスト(`op`、`buildRequest`で追加フィールドを足せる)を送り、応答本体を`parse`で読む——
    /// `SnapshotAsync`/`PolicyAsync`/`JournalAsync`/`ExplainAsync`で共通の往復部分。例外は全て
    /// `ConnectionLost`へ畳む（§10.3のエラー三層分離のうち、現時点でクライアント側が区別できるのは
    /// 「IPCが切れた」だけで、サーバー側拒否の理由はまだ運ばれてこない——必要になったら
    /// `AuthorizationError`等へ分ける）。
    member private _.RequestAsync (op: string) (buildRequest: JsonObject -> unit) (parse: JsonNode -> 'a) : Task<Result<'a, TransportError>> =
        task {
            try
                let request = JsonObject()
                request.["v"] <- JsonValue.Create(protocolVersion)
                request.["op"] <- JsonValue.Create(op)
                buildRequest request
                do! writeFramedMessageAsync pipe (Encoding.UTF8.GetBytes(request.ToJsonString())) CancellationToken.None
                let! responseBytes = readFramedMessageAsync pipe CancellationToken.None
                let json = JsonNode.Parse(Encoding.UTF8.GetString responseBytes)
                return Ok(parse json)
            with
            | :? IOException -> return Error ConnectionLost
            | :? ObjectDisposedException -> return Error ConnectionLost
            | _ -> return Error ConnectionLost
        }

    /// §10.2「Inspect：Snapshot、Policy、Journal、Explain、Analyzerを見る」のうちSnapshot。
    member this.SnapshotAsync() : Task<Result<SnapshotDto, TransportError>> =
        this.RequestAsync "snapshot" (fun _ -> ()) snapshotDtoOfJson

    /// §10.2「Inspect」のうちPolicy（v2.145で追加）。
    member this.PolicyAsync() : Task<Result<PolicyDto, TransportError>> =
        this.RequestAsync "policy" (fun _ -> ()) policyDtoOfJson

    /// §10.2「Inspect」のうちJournal（v2.147で追加、ユーザー決定2026-11-06：直近50〜100件だけ）。
    member this.JournalAsync() : Task<Result<JournalDto, TransportError>> =
        this.RequestAsync "journal" (fun _ -> ()) journalDtoOfJson

    /// §10.2「Inspect」のうちExplain（v2.147で追加、ユーザー決定2026-11-06：DecisionTraceも含める。
    /// `Explain.fs`冒頭コメントの既知の限界どおり、`RecentTrace`は窓単位には絞られていない）。
    member this.ExplainAsync(w: WindowId) : Task<Result<ExplainDto option, TransportError>> =
        this.RequestAsync
            "explain"
            (fun req ->
                let (WindowId wid) = w
                req.["window"] <- JsonValue.Create(wid))
            explainOptionOfJson

    /// §10.2「Inspect」のうちAnalyzer（v2.149で追加、§14完成により前提解消）。§10.3
    /// 「`wm.AnalyzeAsync(rules)`」——候補`PolicyDto`を送り、Hostが自分の採用中のLayout Registryに
    /// 対して現行Analyzerで実際に分析した結果を受け取る。クライアントの自己申告（候補policyが
    /// すでに分析済みだという主張）は無く、常にHost側で再実行する（§14と同じ原則）。
    member this.AnalyzeAsync(candidate: PolicyDto) : Task<Result<AnalyzerReportDto, TransportError>> =
        this.RequestAsync "analyze" (fun req -> req.["policy"] <- policyDtoToJson candidate) analyzerReportDtoOfJson

    /// §10.2「Inspect」のうちDeploy（v2.150で追加、§14完成により前提解消）。候補`PolicyDto`を送る
    /// だけで、`ArtifactIdentity`はクライアントからは渡さない——Hostが自分自身のRegistry/CoreBuildId
    /// から組み立てる（§14と同じ「クライアントの自己申告を信用しない」原則）。検証＋
    /// `artifacts/policy/`への保存＋Journal記録に加え、vNext（PolicyActivated実配線、
    /// ユーザー決定2026-11-06）でReducerの実行中Policyの実際のswapまで行うようになった——
    /// この呼び出しが`Ok(DeploySucceededDto _)`を返した時点で、ライブ反映は既に完了している
    /// （Host側が`activatePolicy`の完了を待ってから応答する、`PolicyArtifact.deployCandidateOf`
    /// 参照）。既に`ByPolicy`で確定済みの窓は遡って再評価されない（同日のユーザー決定「再評価
    /// しない」）——新Policyは以後に観測される窓の初回分類からだけ効く。
    member this.DeployAsync(candidate: PolicyDto) : Task<Result<DeployResultDto, TransportError>> =
        this.RequestAsync "deploy" (fun req -> req.["policy"] <- policyDtoToJson candidate) deployResultDtoOfJson

    /// Intervene（architecture.md §10.2/§10.3、v2.151）。ユーザー決定2026-11-06（Opus敵対的相談
    /// `opus-review-intervene-trust-boundary.md`を踏まえ、architecture.md §10.2/F-1のGate条件を
    /// 改定して進める決定）。送るのは`CorrelationId`（任意）と`InterveneCommandDto`（`SetManagementTo`/
    /// `ClearOverride`の2ケースだけに限定された閉じた許可リスト）だけ——`CloseFocused`等の危険な
    /// `CoreCommand`はこの型自体に存在しないため送れない。`ClientPid`/`ClientImage`はここでは送らない
    /// ——Host自身が接続済みパイプから取得する（§14と同じ「クライアントの自己申告を信用しない」原則）。
    /// 応答は同期（Opusレビュー2-B）：`InterveneAcceptedDto`が返った時点で前提検査(F-4)を通過し
    /// `step`が実際に適用済み。`InterveneRejectedDto`はレート制限、または前提検査不成立（対象窓が
    /// 既に不明/未管理）のいずれか。
    member this.InterveneAsync(correlationId: string option, command: InterveneCommandDto) : Task<Result<InterveneResultDto, TransportError>> =
        this.RequestAsync
            "intervene"
            (fun req ->
                req.["correlationId"] <- (correlationId |> Option.map JsonValue.Create<string> |> Option.defaultValue null)
                req.["command"] <- interveneCommandDtoToJson command)
            interveneResultDtoOfJson

/// パイプ名を直接知っている場合に使う経路。§10.1「クライアントは`TokenImpersonationLevel.Impersonation`
/// で接続する」（F42、ユーザー決定2026-11-05——サーバーが`RunAsClient`で実トークンを読むにはこれが
/// 必須）。
let connectAsync (pipeName: string) (timeoutMs: int) : Task<Result<WmClient, TransportError>> =
    task {
        let pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.None, TokenImpersonationLevel.Impersonation)
        try
            do! pipe.ConnectAsync(timeoutMs)
            return Ok(new WmClient(pipe))
        with
        | :? TimeoutException ->
            pipe.Dispose()
            return Error(Timeout timeoutMs)
        | _ ->
            pipe.Dispose()
            return Error ConnectionLost
    }

/// 実際のクライアントプロセスが使う、パイプ名を直接知らない経路：Hostが書いた発見用ファイル
/// （opus-review-named-pipe-transport.md 5-C、`Wm.DebugProtocol.WireFraming.tryReadDiscoveryFile`）
/// からパイプ名を知る。
let connectLocalAsync (timeoutMs: int) : Task<Result<WmClient, TransportError>> =
    task {
        match tryReadDiscoveryFile () with
        | Error _ -> return Error ConnectionLost
        | Ok info -> return! connectAsync info.PipeName timeoutMs
    }
