/// Wm.DebugProtocol.WireFraming：Named Pipeの長さプレフィックス＋UTF8フレーミングと、パイプ名の
/// 発見用ファイルの読み取り側（v2.144、`Wm.ReplClient`切り出しに伴い`Wm.Host.InspectPipeServer`から
/// 移設）。実際のWindows Named PipeはLinux CIで使えないが、`AnonymousPipeServerStream`/
/// `AnonymousPipeClientStream`（どちらも`PipeStream`の実サブクラス、クロスプラットフォーム）を使って
/// フレーミング自体の往復をLinux上で本物の`PipeStream`越しに検査できる——モックではない。
module Wm.DebugProtocol.Tests.WireFramingTests

open System
open System.IO.Pipes
open System.Text
open Xunit
open Wm.DebugProtocol.WireFraming

[<Fact>]
let ``writeFramedMessage/readFramedMessageは実際のPipeStream越しに往復する`` () =
    use server = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.None)
    use client = new AnonymousPipeClientStream(PipeDirection.In, server.ClientSafePipeHandle)
    let payload = Encoding.UTF8.GetBytes("""{"v":1,"op":"snapshot"}""")
    writeFramedMessage server payload
    let received = readFramedMessage client
    Assert.Equal<byte[]>(payload, received)

[<Fact>]
let ``readFramedMessageAsyncも同じ往復をasyncで行える`` () =
    use server = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.None)
    use client = new AnonymousPipeClientStream(PipeDirection.In, server.ClientSafePipeHandle)
    let payload = Encoding.UTF8.GetBytes("hello async framing")
    writeFramedMessage server payload
    let received = (readFramedMessageAsync client System.Threading.CancellationToken.None).Result
    Assert.Equal<byte[]>(payload, received)

[<Fact>]
let ``readFramedMessageは上限を超える長さプレフィックスを見た時点で例外を投げ、本体バッファは確保しない`` () =
    use server = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.None)
    use client = new AnonymousPipeClientStream(PipeDirection.In, server.ClientSafePipeHandle)
    // writeFramedMessageはpayload自身の上限チェックを通るので、ここでは生の長さプレフィックスだけを
    // 直接書き、実際に`maxMessageBytes`バイトの本体を送らずに例外経路だけを検査する。
    let oversizedLenBuf = BitConverter.GetBytes(uint32 (maxMessageBytes + 1))
    server.Write(oversizedLenBuf, 0, 4)
    server.Flush()
    Assert.Throws<Exception>(fun () -> readFramedMessage client |> ignore) |> ignore

[<Fact>]
let ``tryReadDiscoveryFileはファイルが無ければErrorを返す`` () =
    try
        System.IO.File.Delete(discoveryFilePath ())
    with _ -> ()
    match tryReadDiscoveryFile () with
    | Error _ -> ()
    | Ok info -> Assert.True(false, sprintf "expected Error, got Ok %A" info)

[<Fact>]
let ``tryReadDiscoveryFileはHostが書く形式(pipeName/pid/createdAtUtc)を正しく読み戻す`` () =
    let path = discoveryFilePath ()
    let createdAt = DateTime.UtcNow
    let json = sprintf """{"pipeName":"winmonad-repl-test-abc","pid":4242,"createdAtUtc":"%s"}""" (createdAt.ToString("o"))
    System.IO.File.WriteAllText(path, json)
    try
        match tryReadDiscoveryFile () with
        | Error e -> Assert.True(false, sprintf "expected Ok, got Error %s" e)
        | Ok info ->
            Assert.Equal("winmonad-repl-test-abc", info.PipeName)
            Assert.Equal(4242, info.Pid)
            Assert.Equal(createdAt, info.CreatedAtUtc, TimeSpan.FromSeconds(1.0))
    finally
        try System.IO.File.Delete(path) with _ -> ()
