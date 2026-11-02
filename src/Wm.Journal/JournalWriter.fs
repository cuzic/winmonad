/// Wm.Journal（ADR-019実装順11）：一次事実の封筒の書込み・読込（I/O）のみ。architecture.md §9.2/§9.4。
/// JournalEntry/JournalKind/JournalPayload自体はWm.Contractsが持つ型（decision4：DTOはそれが写す型と
/// 同じプロジェクトに置く。JournalEntryはsmart constructorを持たない素のレコードなので、
/// 別個のDTO型を介さずにそのままシリアライズする）。
module Wm.Journal.JournalWriter

open System
open System.IO
open System.Text.Json.Nodes
open Wm.Contracts.Identifiers
open Wm.Contracts.ErrorTypes
open Wm.Contracts.Timers
open Wm.Contracts.CoreCommand
open Wm.Contracts.JournalTypes

// ===== DTOのタグ付け規約（§9.2）：既知のタグは安定文字列コードで管理する =====
// このスライスで実際に書き込む必要のあるInputEventのケースだけを明示的に符号化する。
// 未対応のケースは、未知の将来スキーマと区別するためのUnrecognizedではなく、
// 「このビルドではまだ対応していない」を明示する専用タグで書き出す（表示/Explainのみ想定。Replayはfail-fastの対象）。

let private timerIdTag (id: TimerId) : string =
    match id with
    | Placement (PendingDeadline _) -> "placement-pending-deadline"
    | Placement (IntegrityReprobe _) -> "placement-integrity-reprobe"
    | Windows (ClassificationDeadline _) -> "windows-classification-deadline"
    | Topology ScreenSettle -> "topology-screen-settle"
    | Topology ScreenGrace -> "topology-screen-grace"
    | Topology ReconfigurationCap -> "topology-reconfiguration-cap"
    | Focus WorkspaceSwitchSettle -> "focus-workspace-switch-settle"
    | Health (ResponsivenessReprobe _) -> "health-responsiveness-reprobe"

let private windowIdOf (id: TimerId) : JsonNode option =
    match id with
    | Placement (PendingDeadline (RequestId rid)) -> Some (JsonValue.Create(rid) :> JsonNode)
    | Placement (IntegrityReprobe (WindowId w))
    | Windows (ClassificationDeadline (WindowId w))
    | Health (ResponsivenessReprobe (WindowId w)) -> Some (JsonValue.Create(w) :> JsonNode)
    | _ -> None

let private timerIdToJson (id: TimerId) : JsonNode =
    let o = JsonObject()
    o.["tag"] <- JsonValue.Create(timerIdTag id)
    match windowIdOf id with
    | Some v -> o.["ref"] <- v
    | None -> ()
    o :> JsonNode

let private timerIdOfJson (node: JsonNode) : TimerId =
    let tag = node.["tag"].GetValue<string>()
    let refVal () = node.["ref"].GetValue<int64>()
    match tag with
    | "placement-pending-deadline" -> Placement (PendingDeadline (RequestId (refVal ())))
    | "placement-integrity-reprobe" -> Placement (IntegrityReprobe (WindowId (refVal ())))
    | "windows-classification-deadline" -> Windows (ClassificationDeadline (WindowId (refVal ())))
    | "topology-screen-settle" -> Topology ScreenSettle
    | "topology-screen-grace" -> Topology ScreenGrace
    | "topology-reconfiguration-cap" -> Topology ReconfigurationCap
    | "focus-workspace-switch-settle" -> Focus WorkspaceSwitchSettle
    | "health-responsiveness-reprobe" -> Health (ResponsivenessReprobe (WindowId (refVal ())))
    | other -> failwithf "Replay: unrecognized TimerId tag '%s' — このJournalはより新しいスキーマで書かれている" other

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

let private monitorSnapshotToJson (m: MonitorSnapshot) : JsonNode =
    let o = JsonObject()
    let (MonitorId mid) = m.Id
    o.["id"] <- JsonValue.Create(mid)
    o.["stableKey"] <- JsonValue.Create(m.StableKey)
    o.["bounds"] <- pixelRectToJson m.Bounds
    o.["workArea"] <- pixelRectToJson m.WorkArea
    o.["dpi"] <- JsonValue.Create(m.Dpi)
    o.["isPrimary"] <- JsonValue.Create(m.IsPrimary)
    o :> JsonNode

let private monitorSnapshotOfJson (node: JsonNode) : MonitorSnapshot =
    { Id = MonitorId (node.["id"].GetValue<int64>())
      StableKey = node.["stableKey"].GetValue<string>()
      Bounds = pixelRectOfJson node.["bounds"]
      WorkArea = pixelRectOfJson node.["workArea"]
      Dpi = node.["dpi"].GetValue<int>()
      IsPrimary = node.["isPrimary"].GetValue<bool>() }

/// このスライスで対応するInputEventのタグ（§9.2のDTOタグ付け規約）。
let private inputEventToJson (input: InputEvent) : JsonNode =
    let o = JsonObject()
    match input with
    | MonitorTopologyObserved snapshots ->
        o.["tag"] <- JsonValue.Create("monitor-topology-observed")
        let arr = JsonArray()
        for m in snapshots do arr.Add(monitorSnapshotToJson m)
        o.["snapshots"] <- arr
    | ScreenReconfigurationInProgress inProgress ->
        o.["tag"] <- JsonValue.Create("screen-reconfiguration-in-progress")
        o.["value"] <- JsonValue.Create(inProgress)
    | TimerElapsed timerId ->
        o.["tag"] <- JsonValue.Create("timer-elapsed")
        o.["timerId"] <- timerIdToJson timerId
    | other ->
        // 本ビルドで未対応のInputEvent。表示専用（Replayはこのタグに出会うとfail-fastする、§9.2）。
        o.["tag"] <- JsonValue.Create("unsupported-input-event")
        o.["raw"] <- JsonValue.Create(sprintf "%A" other)
    o :> JsonNode

let private inputEventOfJson (node: JsonNode) : InputEvent =
    match node.["tag"].GetValue<string>() with
    | "monitor-topology-observed" ->
        let snapshots = node.["snapshots"].AsArray() |> Seq.map monitorSnapshotOfJson |> List.ofSeq
        MonitorTopologyObserved snapshots
    | "screen-reconfiguration-in-progress" -> ScreenReconfigurationInProgress (node.["value"].GetValue<bool>())
    | "timer-elapsed" -> TimerElapsed (timerIdOfJson node.["timerId"])
    | other -> failwithf "Replay: unrecognized InputEvent tag '%s' — このJournalはより新しいスキーマで書かれている" other

let private payloadToJson (payload: JournalPayload) : JsonNode =
    match payload with
    | InputPayload input -> inputEventToJson input
    | other -> JsonValue.Create(sprintf "%A" other) :> JsonNode

let private kindTag (kind: JournalKind) : string =
    match kind with
    | InputReceived -> "input-received"
    | EffectDispatchStarted -> "effect-dispatch-started"
    | EffectCallReturnedKind -> "effect-call-returned"
    | EffectBatchReturnedKind -> "effect-batch-returned"
    | InternalFaultKind -> "internal-fault"
    | JournalDegradedKind -> "journal-degraded"
    | JournalRecoveredKind -> "journal-recovered"
    | SafetyRecoveryAction -> "safety-recovery-action"
    | ObservationReceived -> "observation-received"
    | PendingSatisfied -> "pending-satisfied"
    | PendingTimedOut -> "pending-timed-out"

let private kindOfTag (tag: string) : JournalKind =
    match tag with
    | "input-received" -> InputReceived
    | "effect-dispatch-started" -> EffectDispatchStarted
    | "effect-call-returned" -> EffectCallReturnedKind
    | "effect-batch-returned" -> EffectBatchReturnedKind
    | "internal-fault" -> InternalFaultKind
    | "journal-degraded" -> JournalDegradedKind
    | "journal-recovered" -> JournalRecoveredKind
    | "safety-recovery-action" -> SafetyRecoveryAction
    | "observation-received" -> ObservationReceived
    | "pending-satisfied" -> PendingSatisfied
    | "pending-timed-out" -> PendingTimedOut
    | other -> failwithf "Replay: unrecognized JournalKind tag '%s'" other

let private entryToJson (entry: JournalEntry) : JsonNode =
    let o = JsonObject()
    o.["schemaVersion"] <- JsonValue.Create(entry.SchemaVersion)
    o.["sessionId"] <- JsonValue.Create(entry.SessionId)
    o.["sequence"] <- JsonValue.Create(entry.Sequence)
    o.["monotonicTicks"] <- JsonValue.Create(entry.MonotonicTicks)
    o.["wallTimeUtc"] <- JsonValue.Create(entry.WallTimeUtc.ToString("o"))
    o.["correlationId"] <- (entry.CorrelationId |> Option.map JsonValue.Create<string> |> Option.defaultValue null)
    o.["causationId"] <- (entry.CausationId |> Option.map JsonValue.Create<string> |> Option.defaultValue null)
    o.["policyArtifactHash"] <- JsonValue.Create(entry.PolicyArtifactHash)
    o.["coreBuildId"] <- JsonValue.Create(entry.CoreBuildId)
    o.["kind"] <- JsonValue.Create(kindTag entry.Kind)
    o.["payload"] <- payloadToJson entry.Payload
    o :> JsonNode

let private entryOfJson (node: JsonNode) : JournalEntry =
    let kind = kindOfTag (node.["kind"].GetValue<string>())
    let payload =
        match kind with
        | InputReceived -> InputPayload (inputEventOfJson node.["payload"])
        | _ -> failwithf "Replay: このビルドはJournalKind '%A' のPayload復元に未対応" kind
    { SchemaVersion = node.["schemaVersion"].GetValue<int>()
      SessionId = node.["sessionId"].GetValue<string>()
      Sequence = node.["sequence"].GetValue<int64>()
      MonotonicTicks = node.["monotonicTicks"].GetValue<int64>()
      WallTimeUtc = DateTimeOffset.Parse(node.["wallTimeUtc"].GetValue<string>())
      CorrelationId = (if isNull node.["correlationId"] then None else Some (node.["correlationId"].GetValue<string>()))
      CausationId = (if isNull node.["causationId"] then None else Some (node.["causationId"].GetValue<string>()))
      PolicyArtifactHash = node.["policyArtifactHash"].GetValue<string>()
      CoreBuildId = node.["coreBuildId"].GetValue<string>()
      Kind = kind
      Payload = payload }

/// 1エントリを1行のNDJSONとして追記する。
let appendEntry (path: string) (entry: JournalEntry) : unit =
    let line = (entryToJson entry).ToJsonString()
    File.AppendAllText(path, line + "\n")

/// ファイル全体を順に読み、JournalEntryのシーケンスを返す（Replay Input、§9.1）。
let readAll (path: string) : JournalEntry seq =
    if not (File.Exists path) then
        Seq.empty
    else
        File.ReadLines(path)
        |> Seq.filter (fun line -> not (String.IsNullOrWhiteSpace line))
        |> Seq.map (fun line -> entryOfJson (JsonNode.Parse(line)))
