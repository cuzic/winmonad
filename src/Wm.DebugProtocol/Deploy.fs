/// `op=deploy`（architecture.md §14/§10.2、v2.150）の応答DTO。§14の原則どおり、クライアントは
/// 候補`PolicyDto`だけを送り（`ArtifactIdentity`はHost自身が組み立てる）、Hostからの応答はこの3通り
/// のいずれかになる：成功（活性化したhash）／クライアントが送ったPolicyと組み立てたidentityの
/// hashが一致しない（通常は起こらないが型として表現しておく）／Hostの現行Analyzerが拒否した
/// （`AnalyzerReportDto`、v2.149と同じ型を再利用）。
module Wm.DebugProtocol.Deploy

open Wm.DebugProtocol.Analyzer

type DeployResultDto =
    | DeploySucceededDto of artifactHash: string
    | DeployIntegrityFailedDto of expected: string * actual: string
    | DeployAnalysisFailedDto of AnalyzerReportDto
