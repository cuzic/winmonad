/// `ExplainDto`のJSON往復。`SnapshotJson.fs`/`PolicyJson.fs`と同じ`{"tag": ..., ...}`規約。
module Wm.DebugProtocol.ExplainJson

open System.Text.Json.Nodes
open Wm.Contracts.Identifiers
open Wm.DebugProtocol.Explain
open Wm.DebugProtocol.SnapshotJson

let private windowIdToJson (WindowId w: WindowId) : JsonNode = JsonValue.Create(w) :> JsonNode
let private windowIdOfJson (node: JsonNode) : WindowId = WindowId(node.GetValue<int64>())

let private pixelRectToJson (r: PixelRect) : JsonNode =
    let o = JsonObject()
    o.["x"] <- JsonValue.Create(r.X)
    o.["y"] <- JsonValue.Create(r.Y)
    o.["width"] <- JsonValue.Create(r.Width)
    o.["height"] <- JsonValue.Create(r.Height)
    o :> JsonNode

let private pixelRectOfJson (node: JsonNode) : PixelRect =
    { X = node.["x"].GetValue<int>()
      Y = node.["y"].GetValue<int>()
      Width = node.["width"].GetValue<int>()
      Height = node.["height"].GetValue<int>() }

let private layoutErrorDtoToJson (e: LayoutErrorDto) : JsonNode =
    let o = JsonObject()
    match e with
    | InvalidInputDto v ->
        o.["tag"] <- JsonValue.Create("invalid-input")
        o.["value"] <- JsonValue.Create(v)
    | UnsatisfiableDto v ->
        o.["tag"] <- JsonValue.Create("unsatisfiable")
        o.["value"] <- JsonValue.Create(v)
    | LayoutCrashedDto exnType ->
        o.["tag"] <- JsonValue.Create("layout-crashed")
        o.["exnType"] <- JsonValue.Create(exnType)
    o :> JsonNode

let private layoutErrorDtoOfJson (node: JsonNode) : LayoutErrorDto =
    match node.["tag"].GetValue<string>() with
    | "invalid-input" -> InvalidInputDto(node.["value"].GetValue<string>())
    | "unsatisfiable" -> UnsatisfiableDto(node.["value"].GetValue<string>())
    | "layout-crashed" -> LayoutCrashedDto(node.["exnType"].GetValue<string>())
    | other -> failwithf "ExplainJson: unrecognized LayoutErrorDto tag '%s'" other

let private validationErrorDtoToJson (e: ValidationErrorDto) : JsonNode =
    let o = JsonObject()
    match e with
    | UnmanagedOrDeadWindowDto w ->
        o.["tag"] <- JsonValue.Create("unmanaged-or-dead-window")
        o.["window"] <- windowIdToJson w
    | DuplicateInPlanDto w ->
        o.["tag"] <- JsonValue.Create("duplicate-in-plan")
        o.["window"] <- windowIdToJson w
    | NonPositiveSizeDto (w, width, height) ->
        o.["tag"] <- JsonValue.Create("non-positive-size")
        o.["window"] <- windowIdToJson w
        o.["width"] <- JsonValue.Create(width)
        o.["height"] <- JsonValue.Create(height)
    | ArithmeticOverflowDto w ->
        o.["tag"] <- JsonValue.Create("arithmetic-overflow")
        o.["window"] <- windowIdToJson w
    | OutsideWorkAreaDto (w, rect) ->
        o.["tag"] <- JsonValue.Create("outside-work-area")
        o.["window"] <- windowIdToJson w
        o.["rect"] <- pixelRectToJson rect
    | ExcludedTargetDto w ->
        o.["tag"] <- JsonValue.Create("excluded-target")
        o.["window"] <- windowIdToJson w
    | MissingPlacementDto w ->
        o.["tag"] <- JsonValue.Create("missing-placement")
        o.["window"] <- windowIdToJson w
    o :> JsonNode

let private validationErrorDtoOfJson (node: JsonNode) : ValidationErrorDto =
    let w () = windowIdOfJson node.["window"]
    match node.["tag"].GetValue<string>() with
    | "unmanaged-or-dead-window" -> UnmanagedOrDeadWindowDto(w ())
    | "duplicate-in-plan" -> DuplicateInPlanDto(w ())
    | "non-positive-size" -> NonPositiveSizeDto(w (), node.["width"].GetValue<int>(), node.["height"].GetValue<int>())
    | "arithmetic-overflow" -> ArithmeticOverflowDto(w ())
    | "outside-work-area" -> OutsideWorkAreaDto(w (), pixelRectOfJson node.["rect"])
    | "excluded-target" -> ExcludedTargetDto(w ())
    | "missing-placement" -> MissingPlacementDto(w ())
    | other -> failwithf "ExplainJson: unrecognized ValidationErrorDto tag '%s'" other

let private decisionTraceDtoToJson (t: DecisionTraceDto) : JsonNode =
    let o = JsonObject()
    match t with
    | InconsistencySuspectedDto (domain, detail) ->
        o.["tag"] <- JsonValue.Create("inconsistency-suspected")
        o.["domain"] <- JsonValue.Create(domain)
        o.["detail"] <- JsonValue.Create(detail)
    | LayoutNotRegisteredDto (LayoutId lid, apiVersion) ->
        o.["tag"] <- JsonValue.Create("layout-not-registered")
        o.["layoutId"] <- JsonValue.Create(lid)
        o.["apiVersion"] <- JsonValue.Create(apiVersion)
    | LayoutInvocationFailedDto (LayoutId lid, error) ->
        o.["tag"] <- JsonValue.Create("layout-invocation-failed")
        o.["layoutId"] <- JsonValue.Create(lid)
        o.["error"] <- layoutErrorDtoToJson error
    | PlanRejectedDto (LayoutId lid, errors) ->
        o.["tag"] <- JsonValue.Create("plan-rejected")
        o.["layoutId"] <- JsonValue.Create(lid)
        let arr = JsonArray()
        for e in errors do arr.Add(validationErrorDtoToJson e)
        o.["errors"] <- arr
    | FrameInsetAppliedDto (RequestId rid, desiredVisible, dispatchedNative) ->
        o.["tag"] <- JsonValue.Create("frame-inset-applied")
        o.["requestId"] <- JsonValue.Create(rid)
        o.["desiredVisible"] <- pixelRectToJson desiredVisible
        o.["dispatchedNative"] <- pixelRectToJson dispatchedNative
    o :> JsonNode

let private decisionTraceDtoOfJson (node: JsonNode) : DecisionTraceDto =
    match node.["tag"].GetValue<string>() with
    | "inconsistency-suspected" -> InconsistencySuspectedDto(node.["domain"].GetValue<string>(), node.["detail"].GetValue<string>())
    | "layout-not-registered" -> LayoutNotRegisteredDto(LayoutId(node.["layoutId"].GetValue<string>()), node.["apiVersion"].GetValue<int>())
    | "layout-invocation-failed" -> LayoutInvocationFailedDto(LayoutId(node.["layoutId"].GetValue<string>()), layoutErrorDtoOfJson node.["error"])
    | "plan-rejected" ->
        PlanRejectedDto(
            LayoutId(node.["layoutId"].GetValue<string>()),
            node.["errors"].AsArray() |> Seq.map validationErrorDtoOfJson |> List.ofSeq
        )
    | "frame-inset-applied" ->
        FrameInsetAppliedDto(RequestId(node.["requestId"].GetValue<int64>()), pixelRectOfJson node.["desiredVisible"], pixelRectOfJson node.["dispatchedNative"])
    | other -> failwithf "ExplainJson: unrecognized DecisionTraceDto tag '%s'" other

let explainDtoToJson (e: ExplainDto) : JsonNode =
    let o = JsonObject()
    o.["window"] <- windowIdToJson e.Window
    o.["management"] <- managementOptionToJson e.Management
    o.["matchedRule"] <- policyRuleIdOptionToJson e.MatchedRule
    let arr = JsonArray()
    for t in e.RecentTrace do arr.Add(decisionTraceDtoToJson t)
    o.["recentTrace"] <- arr
    o :> JsonNode

let explainDtoOfJson (node: JsonNode) : ExplainDto =
    { Window = windowIdOfJson node.["window"]
      Management = managementOptionOfJson node.["management"]
      MatchedRule = policyRuleIdOptionOfJson node.["matchedRule"]
      RecentTrace = node.["recentTrace"].AsArray() |> Seq.map decisionTraceDtoOfJson |> List.ofSeq }

/// `op=explain`の応答全体（対象窓が見つからない場合もあるため`ExplainDto option`を運ぶ）。
let explainOptionToJson (e: ExplainDto option) : JsonNode =
    let o = JsonObject()
    match e with
    | Some v ->
        o.["found"] <- JsonValue.Create(true)
        o.["explain"] <- explainDtoToJson v
    | None -> o.["found"] <- JsonValue.Create(false)
    o :> JsonNode

let explainOptionOfJson (node: JsonNode) : ExplainDto option =
    if node.["found"].GetValue<bool>() then
        Some(explainDtoOfJson node.["explain"])
    else
        None
