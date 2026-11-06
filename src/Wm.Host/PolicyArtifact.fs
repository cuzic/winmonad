/// architecture.md §14「成果物識別と再現可能性」（v2.148）。PolicyArtifactの二分割
/// （`PinnedPolicy`/`DeployablePolicy`）・canonical hash・`artifacts/policy/`への最小限のファイル
/// I/O・「以前に承認済み」の信頼アンカー（Journalの`PolicyActivatedKind`、`JournalEntry`自身の
/// `PolicyArtifactHash`フィールドに実際のhashを載せる）・Rollback・起動時の「報告のみモード」を
/// まとめて実装する（ユーザー決定2026-11-06：§14をほぼ全部、一気に）。
///
/// `op=deploy`のNamed Pipe配線は`deployCandidateOf`（v2.150）としてこのファイルに実装済み——ユーザー
/// 決定2026-11-06：このスライスは「検証＋保存＋Journal記録」までで、Reducerの実行中Policyは
/// swapしない。`InputEvent.PolicyActivated`は`WmState.step`で一切パターンマッチされておらず
/// （正規の`| _ ->`に落ちる）、ライブ切替自体は§21の未決事項のまま——新Policyを実際に有効化するには
/// 現時点ではHost再起動が必要（§21にその旨を明記する）。`ArtifactIdentity`はクライアントから受け取らず
/// Host自身が`buildArtifactIdentity`で組み立てる（§14「クライアントの自己申告を信用しない」の実演、
/// v2.149のAnalyzerと同じ原則）。architecture.md §14自身が言う「トレイ通知」は依然対象外——`Wm.Host`
/// にはトレイUIが無いという
/// 既知の欠落（Probe用UIと同じ、Phase Eへ）で、通知の「配信先」が無いため。代わりに
/// `PolicyStaleAnalysisKind`としてJournalへ記録することまでは行う——Inspectの`Journal`操作
/// （v2.147）で見られる、という形に留める。
///
/// §14自身が明記する「analyzerVersion」/「normalizerSchemaVersion」の実際の値を決める既存の
/// バージョニング規約はまだコードに無い（ビルド番号・gitコミットハッシュ等と結び付ける仕組みは
/// Phase Eの配布タスクの領域）。このスライスでは呼び出し側が渡す単純な文字列/整数として扱う
/// （`Reducer`のコンストラクタが`coreBuildId`/`policyArtifactHash`を既に単純な文字列として受け取る
/// 既存の割り切りと同じ）。
module Wm.Host.PolicyArtifact

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json.Nodes
open Wm.Contracts.Identifiers
open Wm.Contracts.SharedVocabulary
open Wm.Contracts.JournalTypes
open Wm.Policy.PolicyTypes
open Wm.Policy.PolicyAnalyzer
open Wm.Layouts.LayoutRegistry
open Wm.DebugProtocol.Policy
open Wm.DebugProtocol.PolicyJson
open Wm.DebugProtocol.Deploy
open Wm.Host.ResidentHost

let private sha256Hex (bytes: byte[]) : string =
    Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()

/// canonicalPolicyIrHash（§14）。ユーザー決定2026-11-06：既存の`PolicyJson`（v2.145）の符号化を
/// 再利用する——`JsonObject`は常に同じキー挿入順で書くため（`policyDtoToJson`自身がそう書いている）、
/// 「JSONシリアライザの偶発的なプロパティ順や改行に依存しない」という§14の要求を素のまま満たす。
let canonicalPolicyIrHash (policy: PolicyIR) : string =
    let json = policyDtoToJson (policyDtoOf policy)
    sha256Hex (Encoding.UTF8.GetBytes(json.ToJsonString()))

let private registryManifestEntryToJson (e: RegistryManifestEntry) : JsonNode =
    let (LayoutId lid) = e.LayoutId
    let o = JsonObject()
    o.["layoutId"] <- JsonValue.Create(lid)
    o.["apiVersion"] <- JsonValue.Create(e.ApiVersion)
    o.["implementationDigest"] <- JsonValue.Create(e.ImplementationDigest)
    o :> JsonNode

let private registryManifestEntryOfJson (node: JsonNode) : RegistryManifestEntry =
    { LayoutId = LayoutId(node.["layoutId"].GetValue<string>())
      ApiVersion = node.["apiVersion"].GetValue<int>()
      ImplementationDigest = node.["implementationDigest"].GetValue<string>() }

/// `ArtifactHash`自身を除いた、他の全フィールドのcanonical JSON（`artifactHash`はこのJSONの
/// SHA256として計算するため、自己参照を避ける）。
let private artifactIdentityCoreToJson
    (policySchemaVersion: int)
    (canonicalPolicyIrHashValue: string)
    (manifest: RegistryManifest)
    (coreBuildId: string)
    (normalizerSchemaVersion: int)
    (analyzerVersion: string)
    : JsonObject =
    let o = JsonObject()
    o.["policySchemaVersion"] <- JsonValue.Create(policySchemaVersion)
    o.["canonicalPolicyIrHash"] <- JsonValue.Create(canonicalPolicyIrHashValue)
    let arr = JsonArray()
    for e in manifest do
        arr.Add(registryManifestEntryToJson e)
    o.["layoutRegistryManifest"] <- arr
    o.["coreBuildId"] <- JsonValue.Create(coreBuildId)
    o.["normalizerSchemaVersion"] <- JsonValue.Create(normalizerSchemaVersion)
    o.["analyzerVersion"] <- JsonValue.Create(analyzerVersion)
    o

/// architecture.md §14のPolicyArtifact（`artifactHash`以外のフィールド）。
type ArtifactIdentity = {
    PolicySchemaVersion: int
    CanonicalPolicyIrHash: string
    LayoutRegistryManifest: RegistryManifest
    CoreBuildId: string
    NormalizerSchemaVersion: int
    AnalyzerVersion: string
    ArtifactHash: string
}

/// `policy`から実際に`ArtifactIdentity`を組み立てる。`analyzerVersion`/`normalizerSchemaVersion`は
/// 呼び出し側が渡す単純な値（ファイル冒頭コメント参照、実際のビルド番号との結び付けはPhase E）。
let buildArtifactIdentity
    (policySchemaVersion: int)
    (policy: PolicyIR)
    (manifest: RegistryManifest)
    (coreBuildId: string)
    (normalizerSchemaVersion: int)
    (analyzerVersion: string)
    : ArtifactIdentity =
    let irHash = canonicalPolicyIrHash policy
    let coreJson = artifactIdentityCoreToJson policySchemaVersion irHash manifest coreBuildId normalizerSchemaVersion analyzerVersion
    let artifactHash = sha256Hex (Encoding.UTF8.GetBytes(coreJson.ToJsonString()))
    { PolicySchemaVersion = policySchemaVersion
      CanonicalPolicyIrHash = irHash
      LayoutRegistryManifest = manifest
      CoreBuildId = coreBuildId
      NormalizerSchemaVersion = normalizerSchemaVersion
      AnalyzerVersion = analyzerVersion
      ArtifactHash = artifactHash }

let artifactIdentityToJson (id: ArtifactIdentity) : JsonNode =
    let o =
        artifactIdentityCoreToJson
            id.PolicySchemaVersion
            id.CanonicalPolicyIrHash
            id.LayoutRegistryManifest
            id.CoreBuildId
            id.NormalizerSchemaVersion
            id.AnalyzerVersion
    o.["artifactHash"] <- JsonValue.Create(id.ArtifactHash)
    o :> JsonNode

let artifactIdentityOfJson (node: JsonNode) : ArtifactIdentity =
    { PolicySchemaVersion = node.["policySchemaVersion"].GetValue<int>()
      CanonicalPolicyIrHash = node.["canonicalPolicyIrHash"].GetValue<string>()
      LayoutRegistryManifest = node.["layoutRegistryManifest"].AsArray() |> Seq.map registryManifestEntryOfJson |> List.ofSeq
      CoreBuildId = node.["coreBuildId"].GetValue<string>()
      NormalizerSchemaVersion = node.["normalizerSchemaVersion"].GetValue<int>()
      AnalyzerVersion = node.["analyzerVersion"].GetValue<string>()
      ArtifactHash = node.["artifactHash"].GetValue<string>() }

type IntegrityError =
    | HashMismatch of expected: string * actual: string

/// §14「PolicyArtifactの二分割」。`PinnedPolicy`は「過去にこの内容で存在した」ことだけを保証し、
/// 現行Analyzerでの再分析は要求しない（Replay/Fork/Inspect/Rollbackが読む形）。
type PinnedPolicy = private { PinnedIr: PolicyIR; PinnedIdentity: ArtifactIdentity }

/// 構築条件：現行Analyzerで`Error`なし。新規Deploy・起動時の新規採用が要求する形。
type DeployablePolicy = private { DeployablePinned: PinnedPolicy; DeployableReport: AnalyzerReport }

/// `link`が返す、Registryとのリンク検査まで通った実行可能なPolicy。
type ActivePolicy = private { ActiveIr: PolicyIR }

module PinnedPolicy =
    let ir (p: PinnedPolicy) = p.PinnedIr
    let identity (p: PinnedPolicy) = p.PinnedIdentity

    /// 構築条件：canonical hashの一致のみ（再分析しない）。
    let ofStored (dto: PolicyDto) (identity: ArtifactIdentity) : Result<PinnedPolicy, IntegrityError> =
        let ir = policyIrOf dto
        let actual = canonicalPolicyIrHash ir
        if actual = identity.CanonicalPolicyIrHash then
            Ok { PinnedIr = ir; PinnedIdentity = identity }
        else
            Error(HashMismatch(identity.CanonicalPolicyIrHash, actual))

module DeployablePolicy =
    let pinned (d: DeployablePolicy) = d.DeployablePinned
    let report (d: DeployablePolicy) = d.DeployableReport

    /// 構築条件：現行Analyzerで`Error`なし。
    let analyze (manifest: RegistryManifest) (pinned: PinnedPolicy) : Result<DeployablePolicy, AnalyzerReport> =
        let report = Wm.Policy.PolicyAnalyzer.analyze manifest (PinnedPolicy.ir pinned)
        if isClean report then
            Ok { DeployablePinned = pinned; DeployableReport = report }
        else
            Error report

type LinkError = UnregisteredLayoutInManifest of LayoutId * apiVersion: int

module ActivePolicy =
    let ir (a: ActivePolicy) = a.ActiveIr

/// §14「再分析しないことと『Registryとのリンク検査』は別物」：Analyzerを再実行しなくても、
/// `layoutRegistryManifest`が現在のビルドで解決できるかの検査は起動時・Rollback時・Deploy時
/// すべてで必須。
let link (manifest: RegistryManifest) (pinned: PinnedPolicy) : Result<ActivePolicy, LinkError list> =
    let registered = manifest |> List.map (fun e -> e.LayoutId, e.ApiVersion) |> Set.ofList
    let ir = PinnedPolicy.ir pinned
    let refs = (ir.LayoutRules |> List.map (fun r -> r.Use)) @ [ ir.DefaultLayout ]
    let missing =
        refs
        |> List.filter (fun r -> not (Set.contains (r.Id, r.ApiVersion) registered))
        |> List.distinct
        |> List.map (fun r -> UnregisteredLayoutInManifest(r.Id, r.ApiVersion))
    if List.isEmpty missing then Ok { ActiveIr = ir } else Error missing

/// `artifacts/policy/`ディレクトリ（§14、ユーザー決定2026-11-06：最小限のファイルI/Oを含める）。
let policyArtifactDir (baseDir: string) : string = Path.Combine(baseDir, "artifacts", "policy")

/// `PinnedPolicy`を`<artifactHash>.json`として保存する。`{"policy": <PolicyDto>, "identity": <ArtifactIdentity>}`。
let savePinnedPolicy (baseDir: string) (pinned: PinnedPolicy) : unit =
    let dir = policyArtifactDir baseDir
    Directory.CreateDirectory(dir) |> ignore
    let identity = PinnedPolicy.identity pinned
    let o = JsonObject()
    o.["policy"] <- policyDtoToJson (policyDtoOf (PinnedPolicy.ir pinned))
    o.["identity"] <- artifactIdentityToJson identity
    let path = Path.Combine(dir, sprintf "%s.json" identity.ArtifactHash)
    File.WriteAllText(path, o.ToJsonString())

/// 保存済みの`PinnedPolicy`を`artifactHash`で読み込む。`PinnedPolicy.ofStored`経由なので、
/// ファイルの内容が実際に申告どおりのhashを持つかを必ず検査する——ファイルがどこにあっても
/// 「ファイル自身の申告を信頼の根拠にしない」（§14）。
let tryLoadPinnedPolicy (baseDir: string) (artifactHash: string) : Result<PinnedPolicy, string> =
    let path = Path.Combine(policyArtifactDir baseDir, sprintf "%s.json" artifactHash)
    try
        if not (File.Exists path) then
            Error(sprintf "artifact file not found for hash %s" artifactHash)
        else
            let node = JsonNode.Parse(File.ReadAllText path)
            let dto = policyDtoOfJson node.["policy"]
            let identity = artifactIdentityOfJson node.["identity"]
            match PinnedPolicy.ofStored dto identity with
            | Ok p -> Ok p
            | Error(HashMismatch(expected, actual)) -> Error(sprintf "hash mismatch: identity claims %s but recomputed %s" expected actual)
    with ex ->
        Error ex.Message

/// Journalを走査し、`PolicyActivatedKind`のエントリが記録した`PolicyArtifactHash`（envelope自身の
/// フィールド、`JournalKind.PolicyActivatedKind`型直前のコメント参照）の集合を返す——§14
/// 「採用記録にhashが載っているPinnedPolicyだけを再分析なしで採用する」の実体。
let activatedArtifactHashes (journalPath: string) : Set<string> =
    try
        Wm.Journal.JournalWriter.readAll journalPath
        |> Seq.filter (fun e -> e.Kind = PolicyActivatedKind)
        |> Seq.map (fun e -> e.PolicyArtifactHash)
        |> Set.ofSeq
    with _ ->
        Set.empty

/// Journalへ`PolicyActivatedKind`の採用記録を1件書く。呼び出し側（Deploy/Rollback/起動時の新規
/// 採用）がそれぞれの`source`（"deploy"/"rollback"/"startup"）を渡す。この関数自体は`Reducer`を
/// 経由しない直接のJournal書き込み——Policy採用はCoreの`step`が分岐する入力ではなく、Host自身の
/// ライフサイクル操作のため（10.1「REPL式はクライアント側で実行される」と対になる、「Policyの
/// 採用判断はHost自身が行う」という役割分担）。
let recordPolicyActivated (journalPath: string) (sessionId: string) (coreBuildId: string) (artifactHash: string) (source: string) (now: int64) : unit =
    let entry: JournalEntry =
        { SchemaVersion = 1
          SessionId = sessionId
          Sequence = -1L // Policy採用はReducerの入力シーケンスの外側（Host自身の記録）。Replayの
                         // 入力分岐対象ではないため、負値で「Reducerの処理対象ではない」ことを示す。
          MonotonicTicks = now
          WallTimeUtc = DateTimeOffset.UtcNow
          CorrelationId = None
          CausationId = None
          PolicyArtifactHash = artifactHash
          CoreBuildId = coreBuildId
          Kind = PolicyActivatedKind
          Payload = PolicyActivatedPayload source }
    Wm.Journal.JournalWriter.appendEntry journalPath entry

/// 起動時/Rollback時の「報告のみモード」（§14、v2.4/R2-11）。再分析しないという決定は「採用判定に
/// 使わない」ことだけを意味し、「現行Analyzerを呼ばない」ことまでは意味しない——現行Analyzerを
/// 報告専用で実行し、結果を返す。採用自体はこの戻り値を見てもブロックしない。
let reportOnlyAnalysis (manifest: RegistryManifest) (pinned: PinnedPolicy) : AnalyzerReport =
    Wm.Policy.PolicyAnalyzer.analyze manifest (PinnedPolicy.ir pinned)

/// `reportOnlyAnalysis`が`Error`を返した場合にJournalへ`PolicyStaleAnalysisKind`を書く
/// （ファイル冒頭コメント参照：トレイ通知は`Wm.Host`にUIが無いため対象外、Journal記録＋Inspectの
/// `Journal`操作（v2.147）での可視化に留める）。
let recordPolicyStaleAnalysis (journalPath: string) (sessionId: string) (coreBuildId: string) (artifactHash: string) (report: AnalyzerReport) (now: int64) : unit =
    if not (isClean report) then
        let entry: JournalEntry =
            { SchemaVersion = 1
              SessionId = sessionId
              Sequence = -1L
              MonotonicTicks = now
              WallTimeUtc = DateTimeOffset.UtcNow
              CorrelationId = None
              CausationId = None
              PolicyArtifactHash = artifactHash
              CoreBuildId = coreBuildId
              Kind = PolicyStaleAnalysisKind
              Payload = PolicyStaleAnalysisPayload(report.Errors |> List.map (sprintf "%A")) }
        Wm.Journal.JournalWriter.appendEntry journalPath entry

type DeployError =
    | DeployIntegrityFailed of IntegrityError
    | DeployAnalysisFailed of AnalyzerReport

/// Deploy（§10.2「検証済みPolicyArtifactを切り替える」）。受信した`PolicyDto`は必ずHost自身の
/// Analyzerで再分析する——クライアントの自己申告（「もう分析済みです」）を信用しない（§14）。
/// 成功したら呼び出し側が`savePinnedPolicy`＋`recordPolicyActivated(source="deploy")`を行う
/// （この関数自体は副作用を持たない判定のみ、呼び出し側の責務を明確に分ける）。
let deploy (manifest: RegistryManifest) (dto: PolicyDto) (identity: ArtifactIdentity) : Result<DeployablePolicy, DeployError> =
    match PinnedPolicy.ofStored dto identity with
    | Error e -> Error(DeployIntegrityFailed e)
    | Ok pinned ->
        match DeployablePolicy.analyze manifest pinned with
        | Ok deployable -> Ok deployable
        | Error report -> Error(DeployAnalysisFailed report)

type RollbackError =
    | NotPreviouslyActivated of artifactHash: string
    | RollbackLoadFailed of string
    | RollbackLinkFailed of LinkError list

/// Rollback（§10.2「既知良好版へのRollback経路」）。`PolicyDto`は受け付けず、`artifactHash`の参照
/// だけを受け付ける（10.2「Rollbackは`artifactHash`の参照だけを受け付け、Host自身のストアから
/// 読む」）。Host自身の採用記録に載っているhashだけを再分析なしで採用する——採用記録に無いhashは、
/// ファイルがどこにあっても常に`Error`にする（ファイルを直接置いて「最後の正常版」を騙る迂回を
/// 塞ぐ、§14の設計そのもの）。
let rollback (baseDir: string) (journalPath: string) (manifest: RegistryManifest) (artifactHash: string) : Result<PinnedPolicy, RollbackError> =
    let trusted = activatedArtifactHashes journalPath
    if not (Set.contains artifactHash trusted) then
        Error(NotPreviouslyActivated artifactHash)
    else
        match tryLoadPinnedPolicy baseDir artifactHash with
        | Error e -> Error(RollbackLoadFailed e)
        | Ok pinned ->
            match link manifest pinned with
            | Error errs -> Error(RollbackLinkFailed errs)
            | Ok _ -> Ok pinned

/// §14自身が明記する「analyzerVersion」/「normalizerSchemaVersion」の実際のバージョニング規約は
/// まだコードに無い（ファイル冒頭コメント参照、Phase Eの配布タスクの領域）。Round 24のテストで
/// 使った値と揃えた固定値として扱う。
let private deployPolicySchemaVersion = 1
let private deployNormalizerSchemaVersion = 1
let private deployAnalyzerVersion = "v1"

/// `op=deploy`（architecture.md §10.2、v2.150）。クライアントは候補`PolicyDto`だけを送り、
/// `ArtifactIdentity`はHost自身が自分の採用中Registry/CoreBuildIdから`buildArtifactIdentity`で
/// 組み立てる（§14「クライアントの自己申告を信用しない」、v2.149のAnalyzerと同じ原則）。成功時は
/// `savePinnedPolicy`で`artifacts/policy/`へ保存し、`journalPath`があれば`recordPolicyActivated`で
/// `PolicyActivatedKind`を記録する。ユーザー決定2026-11-06：このスライスはここまでで、Reducerの
/// 実行中Policyはswapしない（ファイル冒頭コメント参照、ライブ切替には現時点でHost再起動が必要）。
let deployCandidateOf
    (baseDir: string)
    (registry: Registry)
    (journalPath: string option)
    (sessionId: string)
    (coreBuildId: string)
    (now: int64)
    (candidate: PolicyDto)
    : DeployResultDto =
    let manifest = Registry.toManifest registry
    let ir = policyIrOf candidate
    let identity =
        buildArtifactIdentity deployPolicySchemaVersion ir manifest coreBuildId deployNormalizerSchemaVersion deployAnalyzerVersion
    match deploy manifest candidate identity with
    | Error(DeployIntegrityFailed(HashMismatch(expected, actual))) -> DeployIntegrityFailedDto(expected, actual)
    | Error(DeployAnalysisFailed report) -> DeployAnalysisFailedDto(analyzerReportDtoOf report)
    | Ok deployable ->
        savePinnedPolicy baseDir (DeployablePolicy.pinned deployable)
        journalPath |> Option.iter (fun path -> recordPolicyActivated path sessionId coreBuildId identity.ArtifactHash "deploy" now)
        DeploySucceededDto identity.ArtifactHash
