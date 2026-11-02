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
open Wm.Contracts.Observation
open Wm.Contracts.SharedVocabulary
open Wm.Contracts.Timers
open Wm.Contracts.CoreCommand
open Wm.Contracts.JournalTypes

// ===== DTOのタグ付け規約（§9.2）：既知のタグは安定文字列コードで管理する =====
// このスライスで実際に書き込む必要のあるInputEventのケースだけを明示的に符号化する。
// 未対応のケースは、未知の将来スキーマと区別するためのUnrecognizedではなく、
// 「このビルドではまだ対応していない」を明示する専用タグで書き出す（表示/Explainのみ想定。Replayはfail-fastの対象）。

let private timerIdTag (id: TimerId) : string =
    match id with
    | TimerId.Placement (PendingDeadline _) -> "placement-pending-deadline"
    | TimerId.Placement (IntegrityReprobe _) -> "placement-integrity-reprobe"
    | TimerId.Windows (ClassificationDeadline _) -> "windows-classification-deadline"
    | TimerId.Topology ScreenSettle -> "topology-screen-settle"
    | TimerId.Topology ScreenGrace -> "topology-screen-grace"
    | TimerId.Topology ReconfigurationCap -> "topology-reconfiguration-cap"
    | TimerId.Focus WorkspaceSwitchSettle -> "focus-workspace-switch-settle"
    | TimerId.Health (ResponsivenessReprobe _) -> "health-responsiveness-reprobe"

let private windowIdOf (id: TimerId) : JsonNode option =
    match id with
    | TimerId.Placement (PendingDeadline (RequestId rid)) -> Some (JsonValue.Create(rid) :> JsonNode)
    | TimerId.Placement (IntegrityReprobe (WindowId w))
    | TimerId.Windows (ClassificationDeadline (WindowId w))
    | TimerId.Health (ResponsivenessReprobe (WindowId w)) -> Some (JsonValue.Create(w) :> JsonNode)
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
    | "placement-pending-deadline" -> TimerId.Placement (PendingDeadline (RequestId (refVal ())))
    | "placement-integrity-reprobe" -> TimerId.Placement (IntegrityReprobe (WindowId (refVal ())))
    | "windows-classification-deadline" -> TimerId.Windows (ClassificationDeadline (WindowId (refVal ())))
    | "topology-screen-settle" -> TimerId.Topology ScreenSettle
    | "topology-screen-grace" -> TimerId.Topology ScreenGrace
    | "topology-reconfiguration-cap" -> TimerId.Topology ReconfigurationCap
    | "focus-workspace-switch-settle" -> TimerId.Focus WorkspaceSwitchSettle
    | "health-responsiveness-reprobe" -> TimerId.Health (ResponsivenessReprobe (WindowId (refVal ())))
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

let private errorSpaceToJson (space: ErrorSpace) : string =
    match space with
    | Win32LastError -> "win32-last-error"
    | HResult -> "hresult"
    | BoolFalseNoCode -> "bool-false-no-code"

let private errorSpaceOfJson (tag: string) : ErrorSpace =
    match tag with
    | "win32-last-error" -> Win32LastError
    | "hresult" -> HResult
    | "bool-false-no-code" -> BoolFalseNoCode
    | other -> failwithf "Replay: unrecognized ErrorSpace tag '%s'" other

let private nativeCallRecordToJson (r: NativeCallRecord) : JsonNode =
    let o = JsonObject()
    o.["api"] <- JsonValue.Create(r.Api)
    o.["space"] <- JsonValue.Create(errorSpaceToJson r.Space)
    o.["code"] <- JsonValue.Create(r.Code)
    o.["elapsedUs"] <- JsonValue.Create(r.ElapsedUs)
    o :> JsonNode

let private nativeCallRecordOfJson (node: JsonNode) : NativeCallRecord =
    { Api = node.["api"].GetValue<string>()
      Space = errorSpaceOfJson (node.["space"].GetValue<string>())
      Code = node.["code"].GetValue<int>()
      ElapsedUs = node.["elapsedUs"].GetValue<int64>() }

let private windowIdOptionToJson (w: WindowId option) : JsonNode =
    match w with
    | Some (WindowId v) -> JsonValue.Create(v) :> JsonNode
    | None -> null

let private windowIdOptionOfJson (node: JsonNode) : WindowId option =
    if isNull node then None else Some (WindowId (node.GetValue<int64>()))

/// `ForegroundObserved`が運ぶ`Observation<WindowId option>`のDTO（§4.2。Observation<'T>は`'T`ごとに
/// 個別の符号化が要る——このスライスで実際に必要なのはこの1つだけなので、汎用の符号化基盤は作らない）。
let private observationWindowIdOptionToJson (obs: Observation<WindowId option>) : JsonNode =
    let o = JsonObject()
    o.["sampleSequence"] <- JsonValue.Create(obs.SampleSequence)
    o.["monotonicTime"] <- JsonValue.Create(obs.MonotonicTime)
    o.["source"] <- JsonValue.Create(obs.Source)
    match obs.Value with
    | Ok w ->
        o.["outcome"] <- JsonValue.Create("ok")
        o.["value"] <- windowIdOptionToJson w
    | Error rec_ ->
        o.["outcome"] <- JsonValue.Create("failed")
        o.["failure"] <- nativeCallRecordToJson rec_
    o :> JsonNode

let private observationWindowIdOptionOfJson (node: JsonNode) : Observation<WindowId option> =
    let value =
        match node.["outcome"].GetValue<string>() with
        | "ok" -> Ok (windowIdOptionOfJson node.["value"])
        | "failed" -> Error (nativeCallRecordOfJson node.["failure"])
        | other -> failwithf "Replay: unrecognized Observation outcome '%s'" other
    { Value = value
      SampleSequence = node.["sampleSequence"].GetValue<int64>()
      MonotonicTime = node.["monotonicTime"].GetValue<int64>()
      Source = node.["source"].GetValue<string>() }

let private managementToJson (m: Management) : string =
    match m with
    | Tiled -> "tiled"
    | Floating -> "floating"
    | Excluded -> "excluded"

let private managementOfJson (tag: string) : Management =
    match tag with
    | "tiled" -> Tiled
    | "floating" -> Floating
    | "excluded" -> Excluded
    | other -> failwithf "Replay: unrecognized Management tag '%s'" other

let private layoutArgsToJson (args: LayoutArgs) : JsonNode =
    let o = JsonObject()
    match args with
    | NoArgs -> o.["tag"] <- JsonValue.Create("no-args")
    | MasterRatio r ->
        o.["tag"] <- JsonValue.Create("master-ratio")
        o.["value"] <- JsonValue.Create(r)
    | ColumnCount c ->
        o.["tag"] <- JsonValue.Create("column-count")
        o.["value"] <- JsonValue.Create(c)
    o :> JsonNode

let private layoutArgsOfJson (node: JsonNode) : LayoutArgs =
    match node.["tag"].GetValue<string>() with
    | "no-args" -> NoArgs
    | "master-ratio" -> MasterRatio (node.["value"].GetValue<decimal>())
    | "column-count" -> ColumnCount (node.["value"].GetValue<int>())
    | other -> failwithf "Replay: unrecognized LayoutArgs tag '%s'" other

let private layoutRefToJson (r: LayoutRef) : JsonNode =
    let o = JsonObject()
    let (LayoutId lid) = r.Id
    o.["id"] <- JsonValue.Create(lid)
    o.["apiVersion"] <- JsonValue.Create(r.ApiVersion)
    o.["args"] <- layoutArgsToJson r.Args
    o :> JsonNode

let private layoutRefOfJson (node: JsonNode) : LayoutRef =
    { Id = LayoutId (node.["id"].GetValue<string>())
      ApiVersion = node.["apiVersion"].GetValue<int>()
      Args = layoutArgsOfJson node.["args"] }

/// §9.2のDTOタグ付け規約：`CoreCommand`の全ケースを安定文字列タグで符号化する（Opusレビュー5-4。
/// `step`が状態遷移に使う入力はJournalが往復できなければならない——`UserCommand`は
/// `Topology.update`がReconfiguring解除の契機として読むので、このスライスでも既に`step`の
/// 分岐対象になっている）。
let private coreCommandToJson (cmd: CoreCommand) : JsonNode =
    let o = JsonObject()
    let simple tag = o.["tag"] <- JsonValue.Create(tag: string)
    match cmd with
    | FocusNext -> simple "focus-next"
    | FocusPrevious -> simple "focus-previous"
    | FocusMostRecent -> simple "focus-most-recent"
    | SwapWithMaster -> simple "swap-with-master"
    | PromoteToMaster -> simple "promote-to-master"
    | CloseFocused -> simple "close-focused"
    | SwitchWorkspace (WorkspaceId ws) ->
        simple "switch-workspace"
        o.["workspaceId"] <- JsonValue.Create(ws)
    | MoveFocusedToWorkspace (WorkspaceId ws) ->
        simple "move-focused-to-workspace"
        o.["workspaceId"] <- JsonValue.Create(ws)
    | FocusMonitor (MonitorId mid) ->
        simple "focus-monitor"
        o.["monitorId"] <- JsonValue.Create(mid)
    | ChangeLayout layoutRef ->
        simple "change-layout"
        o.["layoutRef"] <- layoutRefToJson layoutRef
    | ToggleFloating (WindowId w) ->
        simple "toggle-floating"
        o.["windowId"] <- JsonValue.Create(w)
    | SetManagement (WindowId w, management) ->
        simple "set-management"
        o.["windowId"] <- JsonValue.Create(w)
        o.["management"] <- JsonValue.Create(managementToJson management)
    | ClearUserOverride (WindowId w) ->
        simple "clear-user-override"
        o.["windowId"] <- JsonValue.Create(w)
    o :> JsonNode

let private coreCommandOfJson (node: JsonNode) : CoreCommand =
    match node.["tag"].GetValue<string>() with
    | "focus-next" -> FocusNext
    | "focus-previous" -> FocusPrevious
    | "focus-most-recent" -> FocusMostRecent
    | "swap-with-master" -> SwapWithMaster
    | "promote-to-master" -> PromoteToMaster
    | "close-focused" -> CloseFocused
    | "switch-workspace" -> SwitchWorkspace (WorkspaceId (node.["workspaceId"].GetValue<int>()))
    | "move-focused-to-workspace" -> MoveFocusedToWorkspace (WorkspaceId (node.["workspaceId"].GetValue<int>()))
    | "focus-monitor" -> FocusMonitor (MonitorId (node.["monitorId"].GetValue<int64>()))
    | "change-layout" -> ChangeLayout (layoutRefOfJson node.["layoutRef"])
    | "toggle-floating" -> ToggleFloating (WindowId (node.["windowId"].GetValue<int64>()))
    | "set-management" ->
        SetManagement (WindowId (node.["windowId"].GetValue<int64>()), managementOfJson (node.["management"].GetValue<string>()))
    | "clear-user-override" -> ClearUserOverride (WindowId (node.["windowId"].GetValue<int64>()))
    | other -> failwithf "Replay: unrecognized CoreCommand tag '%s' — このJournalはより新しいスキーマで書かれている" other

/// このスライスで対応するInputEventのタグ（§9.2のDTOタグ付け規約）。`step`が分岐に使う入力
/// （MonitorTopologyObserved/ScreenReconfigurationInProgress/TimerElapsed/ForegroundObserved/
/// WindowRemoved/UserCommand）は全て対応する（Opusレビュー5-4）。
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
    | ForegroundObserved obs ->
        o.["tag"] <- JsonValue.Create("foreground-observed")
        o.["observation"] <- observationWindowIdOptionToJson obs
    | WindowRemoved (WindowId w) ->
        o.["tag"] <- JsonValue.Create("window-removed")
        o.["windowId"] <- JsonValue.Create(w)
    | UserCommand cmd ->
        o.["tag"] <- JsonValue.Create("user-command")
        o.["command"] <- coreCommandToJson cmd
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
    | "foreground-observed" -> ForegroundObserved (observationWindowIdOptionOfJson node.["observation"])
    | "window-removed" -> WindowRemoved (WindowId (node.["windowId"].GetValue<int64>()))
    | "user-command" -> UserCommand (coreCommandOfJson node.["command"])
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
