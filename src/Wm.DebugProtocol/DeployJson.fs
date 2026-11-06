/// `DeployResultDto`のJSON往復。`SnapshotJson.fs`/`PolicyJson.fs`/`AnalyzerJson.fs`と同じ
/// `{"tag": ..., ...}`規約。`analysis-failed`のペイロードは`AnalyzerJson`の既存の符号化を再利用する。
module Wm.DebugProtocol.DeployJson

open System.Text.Json.Nodes
open Wm.DebugProtocol.Deploy
open Wm.DebugProtocol.AnalyzerJson

let deployResultDtoToJson (result: DeployResultDto) : JsonNode =
    let o = JsonObject()
    match result with
    | DeploySucceededDto artifactHash ->
        o.["tag"] <- JsonValue.Create("deployed")
        o.["artifactHash"] <- JsonValue.Create(artifactHash)
    | DeployIntegrityFailedDto(expected, actual) ->
        o.["tag"] <- JsonValue.Create("integrity-failed")
        o.["expected"] <- JsonValue.Create(expected)
        o.["actual"] <- JsonValue.Create(actual)
    | DeployAnalysisFailedDto report ->
        o.["tag"] <- JsonValue.Create("analysis-failed")
        o.["report"] <- analyzerReportDtoToJson report
    o :> JsonNode

let deployResultDtoOfJson (node: JsonNode) : DeployResultDto =
    match node.["tag"].GetValue<string>() with
    | "deployed" -> DeploySucceededDto(node.["artifactHash"].GetValue<string>())
    | "integrity-failed" -> DeployIntegrityFailedDto(node.["expected"].GetValue<string>(), node.["actual"].GetValue<string>())
    | "analysis-failed" -> DeployAnalysisFailedDto(analyzerReportDtoOfJson node.["report"])
    | other -> failwithf "DeployJson: unrecognized DeployResultDto tag '%s'" other
