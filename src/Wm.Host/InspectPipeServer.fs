/// architecture.md §10.1 Named Pipeトランスポート。S31〜S36（CIスパイク、v2.135、
/// failure-catalog.md F38〜F43）で実機検証済みの設計をそのまま実装する：
/// - パイプ作成は生`CreateNamedPipe`のP/Invoke経由（.NET標準の`NamedPipeServerStreamAcl.Create`は
///   SACL付きで`A required privilege is not held by the client`により確実に失敗する、S31/F38）。
/// - DACL＋整合性ラベルは、固定値ではなくHostプロセス自身の整合性レベルへ動的に設定する
///   （opus-review-named-pipe-transport.md 5-A「昇格Hostによる権限昇格経路」対応：固定Mediumだと
///   Hostが昇格して動いている場合に非昇格プロセスが接続できてしまう）。
/// - `FILE_FLAG_FIRST_PIPE_INSTANCE`を付け、`PIPE_REJECT_REMOTE_CLIENTS`も常に設定する。
/// - サーバーはパイプインスタンスを一切`Dispose`せず、`Disconnect`→同じインスタンスで
///   `WaitForConnectionAsync`を繰り返す（F40：全インスタンスを閉じた隙に名前を乗っ取られる
///   という実機確認済みの穴を作らない）。
/// - クライアントは`TokenImpersonationLevel.Impersonation`で接続する（F42／ユーザー決定
///   2026-11-05）。サーバーは`RunAsClient`＋`OpenThreadToken`で接続済みクライアントの実トークン
///   （整合性レベル）を検査する。
///
/// ワイヤプロトコルのフレーミング（長さプレフィックス＋JSON、サイズ上限・タイムアウト、
/// opus-review 1-3/1-4/5-E）とパイプ名の発見用ファイルの読み取り側の形式（5-C）は、
/// `Wm.ReplClient`（v2.144で新設、`Wm.Host`——net8.0-windows専用——へ依存できない）とも共有する
/// 必要があるため`Wm.DebugProtocol.WireFraming`へ移した（§17の依存方向：`Wm.DebugProtocol`は
/// Contractsのみに依存）。ここに残るのは接続・認可・パイプ作成・発見用ファイルの書き込み/削除
/// （いずれもHostのライフサイクルに属す、サーバー側専用の操作）だけ。
module Wm.Host.InspectPipeServer

open System
open System.Diagnostics
open System.IO
open System.IO.Pipes
open System.Runtime.InteropServices
open System.Security.Principal
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open Microsoft.Win32.SafeHandles
open Wm.DebugProtocol.Snapshot
open Wm.DebugProtocol.SnapshotJson
open Wm.DebugProtocol.Policy
open Wm.DebugProtocol.PolicyJson
open Wm.DebugProtocol.WireFraming

module private Native =
    [<StructLayout(LayoutKind.Sequential)>]
    type SECURITY_ATTRIBUTES =
        struct
            val mutable nLength: int
            val mutable lpSecurityDescriptor: IntPtr
            val mutable bInheritHandle: bool
        end

    [<DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)>]
    extern IntPtr CreateNamedPipe(string lpName, uint32 dwOpenMode, uint32 dwPipeMode, uint32 nMaxInstances, uint32 nOutBufferSize, uint32 nInBufferSize, uint32 nDefaultTimeOut, SECURITY_ATTRIBUTES& lpSecurityAttributes)

    [<DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)>]
    extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string StringSecurityDescriptor, uint32 StringSDRevision, IntPtr& SecurityDescriptor, uint32& SecurityDescriptorSize)

    [<DllImport("kernel32.dll")>]
    extern IntPtr LocalFree(IntPtr hMem)

    [<DllImport("advapi32.dll", SetLastError = true)>]
    extern bool OpenProcessToken(IntPtr ProcessHandle, uint32 DesiredAccess, IntPtr& TokenHandle)

    [<DllImport("advapi32.dll", SetLastError = true)>]
    extern bool GetTokenInformation(IntPtr TokenHandle, uint32 TokenInformationClass, byte[] TokenInformation, uint32 TokenInformationLength, uint32& ReturnLength)

    [<DllImport("kernel32.dll")>]
    extern IntPtr GetCurrentProcess()

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern bool CloseHandle(IntPtr hObject)

    [<DllImport("advapi32.dll", SetLastError = true)>]
    extern bool OpenThreadToken(IntPtr ThreadHandle, uint32 DesiredAccess, bool OpenAsSelf, IntPtr& TokenHandle)

    [<DllImport("kernel32.dll")>]
    extern IntPtr GetCurrentThread()

open Native

/// S7/S34スパイクと同じ手法：`TOKEN_MANDATORY_LABEL`のSID末尾RIDを整合性レベル名へ変換する。
let private integrityLabelOf (rid: int) =
    match rid with
    | 0x0000 -> "Untrusted"
    | 0x1000 -> "Low"
    | 0x2000 -> "Medium"
    | 0x2100 -> "Medium Plus"
    | 0x3000 -> "High"
    | 0x4000 -> "System"
    | 0x5000 -> "Protected Process"
    | v -> sprintf "Unknown(0x%04x)" v

let private getIntegrityRid (hToken: IntPtr) : Result<int, string> =
    let TokenIntegrityLevel = 25u
    let mutable len = 0u
    GetTokenInformation(hToken, TokenIntegrityLevel, null, 0u, &len) |> ignore
    let buf = Array.zeroCreate<byte> (int len)
    if not (GetTokenInformation(hToken, TokenIntegrityLevel, buf, len, &len)) then
        Error(sprintf "GetTokenInformation(TokenIntegrityLevel) failed, GetLastError=%d" (Marshal.GetLastWin32Error()))
    else
        let pSid = IntPtr(BitConverter.ToInt64(buf, 0))
        let subAuthorityCount = int (Marshal.ReadByte(pSid, 1))
        Ok(Marshal.ReadInt32(pSid, 8 + 4 * (subAuthorityCount - 1)))

/// 現在プロセス（＝Wm.Host自身）の整合性レベルRIDを読む。パイプの整合性ラベルをこの値へ動的に
/// 設定するために使う（opus-review 5-A対応：固定Mediumだと昇格Hostで保護にならない）。
let ownIntegrityRid () : int option =
    let TOKEN_QUERY = 0x0008u
    let mutable hToken = IntPtr.Zero
    if not (OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &hToken)) then
        None
    else
        try
            match getIntegrityRid hToken with
            | Ok rid -> Some rid
            | Error _ -> None
        finally
            CloseHandle(hToken) |> ignore

type ClientPrincipal = { IntegrityLevel: string; IntegrityRid: int }

/// 接続済みクライアントの実トークンを`RunAsClient`（＝`ImpersonateNamedPipeClient`）経由で検査する。
/// F42（S35で実機確認済み）：クライアントは`TokenImpersonationLevel.Impersonation`で接続している
/// 前提——Identification/Anonymousでは`RunAsClient`自体が例外で失敗する。
let inspectConnectedClient (pipe: NamedPipeServerStream) : Result<ClientPrincipal, string> =
    let mutable result = Error "RunAsClient did not invoke the callback"
    try
        pipe.RunAsClient(fun () ->
            let TOKEN_QUERY = 0x0008u
            let mutable hThreadToken = IntPtr.Zero
            if not (OpenThreadToken(GetCurrentThread(), TOKEN_QUERY, true, &hThreadToken)) then
                result <- Error(sprintf "OpenThreadToken failed, GetLastError=%d" (Marshal.GetLastWin32Error()))
            else
                try
                    match getIntegrityRid hThreadToken with
                    | Ok rid -> result <- Ok { IntegrityLevel = integrityLabelOf rid; IntegrityRid = rid }
                    | Error e -> result <- Error e
                finally
                    CloseHandle(hThreadToken) |> ignore)
        result
    with ex ->
        Error(sprintf "RunAsClient failed: %s" ex.Message)

/// S32で実機確認済みの経路でパイプを1本作る：生`CreateNamedPipe`＋SDDL（現在ユーザー＋Host自身の
/// 整合性レベル以上のみ許可、NETWORKは明示Deny）。`FILE_FLAG_FIRST_PIPE_INSTANCE`＋
/// `PIPE_REJECT_REMOTE_CLIENTS`を常に付ける。
let createRestrictedPipe (pipeName: string) : Result<NamedPipeServerStream, string> =
    match ownIntegrityRid () with
    | None -> Error "could not determine own integrity level"
    | Some rid ->
        let userSid = WindowsIdentity.GetCurrent().User.Value
        let sddl = sprintf "D:P(A;;GA;;;%s)(D;;GA;;;NU)S:(ML;;NWNR;;;S-1-16-%d)" userSid rid
        let mutable sa = SECURITY_ATTRIBUTES()
        sa.nLength <- Marshal.SizeOf<SECURITY_ATTRIBUTES>()
        sa.lpSecurityDescriptor <- IntPtr.Zero
        sa.bInheritHandle <- false
        let mutable pSd = IntPtr.Zero
        let mutable sdSize = 0u
        if not (ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, 1u, &pSd, &sdSize)) then
            Error(sprintf "ConvertStringSecurityDescriptorToSecurityDescriptorW failed, GetLastError=%d" (Marshal.GetLastWin32Error()))
        else
            sa.lpSecurityDescriptor <- pSd
            try
                let PIPE_ACCESS_DUPLEX = 0x00000003u
                let FILE_FLAG_OVERLAPPED = 0x40000000u
                let FILE_FLAG_FIRST_PIPE_INSTANCE = 0x00080000u
                let PIPE_TYPE_BYTE = 0x00000000u
                let PIPE_REJECT_REMOTE_CLIENTS = 0x00000008u
                let h =
                    CreateNamedPipe(
                        sprintf "\\\\.\\pipe\\%s" pipeName,
                        PIPE_ACCESS_DUPLEX ||| FILE_FLAG_OVERLAPPED ||| FILE_FLAG_FIRST_PIPE_INSTANCE,
                        PIPE_TYPE_BYTE ||| PIPE_REJECT_REMOTE_CLIENTS,
                        1u, 4096u, 4096u, 0u, &sa)
                if h = IntPtr(-1) then
                    Error(sprintf "CreateNamedPipe failed, GetLastError=%d" (Marshal.GetLastWin32Error()))
                else
                    let safeHandle = new SafePipeHandle(h, true)
                    Ok(new NamedPipeServerStream(PipeDirection.InOut, true, false, safeHandle))
            finally
                if pSd <> IntPtr.Zero then LocalFree(pSd) |> ignore

/// パイプ名にセッションID＋ランダム値を含める（名前乗っ取り対策の一部、§10.1）。
let generatePipeName () : string =
    let sessionId =
        try string (Process.GetCurrentProcess().SessionId)
        with _ -> "unknown-session"
    sprintf "winmonad-repl-%s-%s" sessionId (Guid.NewGuid().ToString("N").Substring(0, 12))

/// パイプ名の発見用ファイル（opus-review-named-pipe-transport.md 5-C対応、場所・形式・読み取り側は
/// `Wm.DebugProtocol.WireFraming.discoveryFilePath`/`tryReadDiscoveryFile`参照）。Hostはパイプ作成
/// 直後にここで書く——ユーザーの実運用では常駐Hostは1つだけなので、固定名で「現在稼働中のHost」を
/// 指す1枚のファイルという設計にできる。書き込み失敗（権限等）はHost起動そのものを失敗させるほどの
/// 重大性ではないので`Result`で呼び出し側へ返すだけにする。
let writeDiscoveryFile (pipeName: string) : Result<unit, string> =
    try
        let o = JsonObject()
        o.["pipeName"] <- JsonValue.Create(pipeName)
        o.["pid"] <- JsonValue.Create(Process.GetCurrentProcess().Id)
        o.["createdAtUtc"] <- JsonValue.Create(DateTime.UtcNow.ToString("o"))
        File.WriteAllText(discoveryFilePath (), o.ToJsonString())
        Ok()
    with ex ->
        Error ex.Message

/// Hostが正常終了時に呼ぶ（best-effort、無ければ何もしない）。
let removeDiscoveryFile () : unit =
    try
        File.Delete(discoveryFilePath ())
    with _ ->
        ()

/// 1接続あたりの壁時計デッドライン(ms)。handshake(接続後最初の受信)・読取途中(送信の停滞)の
/// どちらも、現行プロトコル(1接続=1リクエスト、`handleOneRequest`が1件処理して戻ったら
/// `runAcceptLoop`が`Disconnect`する)では「このリクエストを読み切り応答を送り終えるまで」という
/// 1本のデッドラインで等しく塞げる——送受信とも`PipeStream.ReadAsync`/`WriteAsync`＋
/// `CancellationToken`を使う（`createRestrictedPipe`が`isAsync=true`でパイプを作成済みなので、
/// キャンセルは実際に保留中のI/Oを中断できる）。opus-review-named-pipe-transport.md 5-E対応。
/// 「アイドル」(1接続のまま複数リクエストを送れる持続的接続)は現行プロトコルにまだ存在しない概念
/// なので今は作らない——無い機能を守るタイムアウトは書かない(grounded discipline)。
let requestTimeoutMs = 5000

/// サーバーは一切パイプインスタンスを`Dispose`しない——`Disconnect`→同じインスタンスで
/// `WaitForConnectionAsync`を繰り返す（F40：全インスタンスを閉じた隙に別プロセスが同名＋
/// `FIRST_PIPE_INSTANCE`で先取りできる、という実機確認済みの穴を作らない）。`ct`がキャンセルされる
/// まで戻らない。`onClient`は接続のたびに呼ばれ、接続ごとの(`ct`にリンクした)タイムアウト付き
/// トークンを受け取る——接続だけして何も送らない・送信を途中で止めるクライアントがいても、
/// `perConnectionTimeoutMs`後にこのトークンがキャンセルされ、保留中の`ReadAsync`/`WriteAsync`が
/// 例外で中断される（呼び出し側の既存の`try ... with _ -> ()`がこれを飲み込み、次の接続へ進む）。
/// シングルスレッドのこのループが1クライアントに無期限に塞がれる心配はもう無い。タイムアウト値を
/// 外から指定できるようにしたのは、CIで本番の`requestTimeoutMs`(5秒)の実演を毎回待たされないよう、
/// Program.fsの実演Roundから短い値を渡せるようにするため——本番経路は次の`runAcceptLoop`
/// （既定`requestTimeoutMs`）を使う。
let runAcceptLoopWithTimeout (pipe: NamedPipeServerStream) (ct: CancellationToken) (perConnectionTimeoutMs: int) (onClient: NamedPipeServerStream -> Result<ClientPrincipal, string> -> CancellationToken -> unit) =
    while not ct.IsCancellationRequested do
        try
            pipe.WaitForConnectionAsync(ct).GetAwaiter().GetResult()
            use requestCts = CancellationTokenSource.CreateLinkedTokenSource(ct)
            requestCts.CancelAfter(perConnectionTimeoutMs)
            onClient pipe (inspectConnectedClient pipe) requestCts.Token
        with
        | :? OperationCanceledException -> ()
        | _ -> ()

        if pipe.IsConnected then
            try
                pipe.Disconnect()
            with _ ->
                ()

let runAcceptLoop (pipe: NamedPipeServerStream) (ct: CancellationToken) (onClient: NamedPipeServerStream -> Result<ClientPrincipal, string> -> CancellationToken -> unit) =
    runAcceptLoopWithTimeout pipe ct requestTimeoutMs onClient

type InspectRequest =
    | SnapshotRequest
    | PolicyRequest

/// リクエストのJSONパースは標準の`System.Text.Json`を使う（信頼境界の内側に入ってくる入力を
/// 手書きパーサで読まない、opus-review-named-pipe-transport.md 1-5対応——`Wm.Journal`の手書き
/// `JsonNode`規約は「書き出し」専用であり、こちらは受信＝逆方向）。
let private parseRequest (bytes: byte[]) : Result<InspectRequest, string> =
    try
        use doc = JsonDocument.Parse(bytes, JsonDocumentOptions(MaxDepth = 8))
        let root = doc.RootElement
        let v = root.GetProperty("v").GetInt32()
        if v <> protocolVersion then
            Error(sprintf "unsupported protocol version %d (expected %d)" v protocolVersion)
        else
            match root.GetProperty("op").GetString() with
            | "snapshot" -> Ok SnapshotRequest
            | "policy" -> Ok PolicyRequest
            | other -> Error(sprintf "unknown op '%s'" other)
    with ex ->
        Error(sprintf "malformed request: %s" ex.Message)

/// 1接続ぶんのInspectハンドラ本体。`principal`/`ownRid`による多層防御（カーネルのDACL/ラベルを
/// 通過したはずの接続だが、ここでも「同一ユーザー・Host自身の整合性レベル以上」を再確認する）→
/// 1件のリクエストを読む→`getSnapshot`/`getPolicy`を呼んで応答する、という最小の往復を実装する。
/// Inspect（§10.2「Snapshot、Policy、Journal、Explain、Analyzerを見る」）のうちSnapshot/Policyの
/// 2つがこのスライスまでの対象——Journal/Explain/Analyzerは後続。`ct`は`runAcceptLoop`が接続ごとに
/// `requestTimeoutMs`で組み立てたデッドライン（5-E対応）：読取・応答送信どちらが詰まっても
/// このトークンの期限切れで中断される。
let handleOneRequestAsync
    (pipe: NamedPipeServerStream)
    (principal: Result<ClientPrincipal, string>)
    (ownRid: int option)
    (getSnapshot: unit -> SnapshotDto)
    (getPolicy: unit -> PolicyDto)
    (ct: CancellationToken)
    : Task =
    task {
        let authorized =
            match principal, ownRid with
            | Ok p, Some own -> p.IntegrityRid >= own
            | _ -> false
        // TODO(診断用、v2.145のRound 20回帰調査): 原因が分かったら削除する。
        printfn "handleOneRequestAsync: principal=%A ownRid=%A authorized=%b" principal ownRid authorized
        if authorized then
            let! requestBytes = readFramedMessageAsync pipe ct
            printfn "handleOneRequestAsync: requestBytes.Length=%d" requestBytes.Length
            match parseRequest requestBytes with
            | Error e ->
                printfn "handleOneRequestAsync: parseRequest Error %s" e
            | Ok SnapshotRequest ->
                printfn "handleOneRequestAsync: parsed SnapshotRequest, writing response"
                let json = snapshotDtoToJson (getSnapshot ())
                try
                    do! writeFramedMessageAsync pipe (Encoding.UTF8.GetBytes(json.ToJsonString())) ct
                    printfn "handleOneRequestAsync: wrote SnapshotRequest response successfully"
                with ex ->
                    printfn "handleOneRequestAsync: writeFramedMessageAsync(Snapshot) exception: %s" (ex.ToString())
            | Ok PolicyRequest ->
                printfn "handleOneRequestAsync: parsed PolicyRequest, writing response"
                let json = policyDtoToJson (getPolicy ())
                try
                    do! writeFramedMessageAsync pipe (Encoding.UTF8.GetBytes(json.ToJsonString())) ct
                    printfn "handleOneRequestAsync: wrote PolicyRequest response successfully"
                with ex ->
                    printfn "handleOneRequestAsync: writeFramedMessageAsync(Policy) exception: %s" (ex.ToString())
    }
