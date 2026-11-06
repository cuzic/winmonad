/// architecture.md §10.1/§10.3：Named Pipe経由でWm.Hostに接続するREPLクライアント。v2.144で、
/// `Wm.Host/Program.fs`のRound 17〜19に埋め込まれていたテスト専用のクライアントコード（生の
/// `NamedPipeClientStream`＋手動フレーミング呼出）から、再利用可能な独立プロジェクトへ切り出した。
/// §10.1「REPL式はクライアント側で実行される」の原則どおり、`Wm.Core`/`Wm.Host`（net8.0-windows
/// 専用）には一切依存しない（`Wm.Contracts`/`Wm.DebugProtocol`のみ、plain net8.0）。
///
/// §10.3の概念コード（`let! wm = WmClient.connectLocalAsync ()`、`let! snapshot = wm.SnapshotAsync()`）
/// のうち、今回実装するのは接続＋`SnapshotAsync`/`PolicyAsync`/`JournalAsync`/`ExplainAsync`
/// （v2.145/v2.147）——Inspectの残り（Analyzer、§14のPinnedPolicy/DeployablePolicyが前提）・
/// Fork/Probe/Intervene/Deployは後続のスライス（memory `project_named_pipe_ipc_task_breakdown.md`
/// 参照）。
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
