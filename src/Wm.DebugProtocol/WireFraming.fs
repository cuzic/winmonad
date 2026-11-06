/// architecture.md §10.1 Named Pipeワイヤプロトコル。長さプレフィックス（4バイトLE UInt32）＋
/// UTF8本体のフレーミングと、パイプ名の発見用ファイル（opus-review-named-pipe-transport.md 5-C）の
/// 読み取り側——サーバー（`Wm.Host.InspectPipeServer`）とクライアント（`Wm.ReplClient`、v2.144で
/// 新設）の両方が同じ形式に同意する必要がある部分だけをここに置く。接続・認可・パイプ作成・
/// 発見用ファイルの書き込み/削除（Hostのライフサイクルに属す操作）はサーバー側専用として
/// `Wm.Host.InspectPipeServer`に残る。元は`Wm.Host.InspectPipeServer`（v2.138/v2.142/v2.143）に
/// あったが、`Wm.ReplClient`を別プロジェクトへ切り出す際に、`Wm.Host`（net8.0-windows、Windows専用）
/// へ依存できないクライアント側のために共有場所へ移した——§17の依存方向どおり、
/// `Wm.DebugProtocol`はContractsのみに依存する。
module Wm.DebugProtocol.WireFraming

open System
open System.IO
open System.IO.Pipes
open System.Text.Json
open System.Threading
open System.Threading.Tasks

/// ワイヤプロトコルのバージョン（opus-review 1-3対応）。
let protocolVersion = 1

/// 1メッセージの最大バイト数（opus-review 5-E対応）。
let maxMessageBytes = 1 <<< 20 // 1 MiB

let private readExact (stream: PipeStream) (buf: byte[]) =
    let mutable offset = 0
    while offset < buf.Length do
        let n = stream.Read(buf, offset, buf.Length - offset)
        if n = 0 then failwith "pipe closed before message complete"
        offset <- offset + n

/// タイムアウト付き読み取りが使う（`ct`の期限切れで保留中の`ReadAsync`が`OperationCanceledException`
/// で中断される——パイプが`isAsync=true`で作成されていることが前提）。
let private readExactAsync (stream: PipeStream) (buf: byte[]) (ct: CancellationToken) : Task =
    task {
        let mutable offset = 0
        while offset < buf.Length do
            let! n = stream.ReadAsync(buf, offset, buf.Length - offset, ct)
            if n = 0 then failwith "pipe closed before message complete"
            offset <- offset + n
    }

/// 長さプレフィックス（4バイトリトルエンディアンUInt32）＋UTF8本体。長さを読んだ時点で上限を
/// 超えていればバッファ確保前に例外を投げる（opus-review 1-5/5-E対応）。
let readFramedMessage (stream: PipeStream) : byte[] =
    let lenBuf = Array.zeroCreate<byte> 4
    readExact stream lenBuf
    let len = int (BitConverter.ToUInt32(lenBuf, 0))
    if len > maxMessageBytes then
        failwithf "incoming message (%d bytes) exceeds maxMessageBytes (%d)" len maxMessageBytes
    let buf = Array.zeroCreate<byte> len
    readExact stream buf
    buf

/// `ct`の期限切れ（handshake/読取途中タイムアウト、5-E）で中断できる版。
let readFramedMessageAsync (stream: PipeStream) (ct: CancellationToken) : Task<byte[]> =
    task {
        let lenBuf = Array.zeroCreate<byte> 4
        do! readExactAsync stream lenBuf ct
        let len = int (BitConverter.ToUInt32(lenBuf, 0))
        if len > maxMessageBytes then
            failwithf "incoming message (%d bytes) exceeds maxMessageBytes (%d)" len maxMessageBytes
        let buf = Array.zeroCreate<byte> len
        do! readExactAsync stream buf ct
        return buf
    }

let writeFramedMessage (stream: PipeStream) (payload: byte[]) =
    if payload.Length > maxMessageBytes then
        failwithf "outgoing message (%d bytes) exceeds maxMessageBytes (%d)" payload.Length maxMessageBytes
    let lenBuf = BitConverter.GetBytes(uint32 payload.Length)
    stream.Write(lenBuf, 0, 4)
    stream.Write(payload, 0, payload.Length)
    stream.Flush()

/// 応答をクライアントが読み切らず詰まるケースも同じ`ct`で中断できるようにする（読み取りだけでなく
/// 送信側も塞がれ得る、同じ5-Eの精神を書き込みにも及ぼす）。
let writeFramedMessageAsync (stream: PipeStream) (payload: byte[]) (ct: CancellationToken) : Task =
    task {
        if payload.Length > maxMessageBytes then
            failwithf "outgoing message (%d bytes) exceeds maxMessageBytes (%d)" payload.Length maxMessageBytes
        let lenBuf = BitConverter.GetBytes(uint32 payload.Length)
        do! stream.WriteAsync(lenBuf, 0, 4, ct)
        do! stream.WriteAsync(payload, 0, payload.Length, ct)
        do! stream.FlushAsync(ct)
    }

/// パイプ名の発見用ファイル（opus-review 5-C対応）。Hostは固定の既知の場所
/// （`%TEMP%\winmonad-repl-discovery.json`——実運用では常駐Hostは1つだけという前提）に
/// `{pipeName, pid, createdAtUtc}`を書く。書き込み・削除はHost側（`Wm.Host.InspectPipeServer`）の
/// ライフサイクルに属すのでそちらに残るが、場所・形式はクライアントも合意する必要があるので
/// ここに置く。**このファイルの内容自体の真正性は検証しない**：なりすまされても実害は無い——偽の
/// 内容を信じて繋いだクライアントは、繋いだ先のパイプ自身のDACL/整合性ラベル＋
/// `FILE_FLAG_FIRST_PIPE_INSTANCE`(F38-F40)・`RunAsClient`によるトークン検査(F41/F42)に阻まれる
/// だけで、Hostへの偽装はこのファイルの内容だけでは成立しない。
let discoveryFilePath () : string =
    Path.Combine(Path.GetTempPath(), "winmonad-repl-discovery.json")

type DiscoveryInfo = { PipeName: string; Pid: int; CreatedAtUtc: DateTime }

/// クライアントがパイプ名を知るために読む。
let tryReadDiscoveryFile () : Result<DiscoveryInfo, string> =
    try
        let path = discoveryFilePath ()
        if not (File.Exists path) then
            Error "discovery file not found"
        else
            use doc = JsonDocument.Parse(File.ReadAllText path)
            let root = doc.RootElement
            Ok
                { PipeName = root.GetProperty("pipeName").GetString()
                  Pid = root.GetProperty("pid").GetInt32()
                  CreatedAtUtc = root.GetProperty("createdAtUtc").GetDateTime() }
    with ex ->
        Error ex.Message
