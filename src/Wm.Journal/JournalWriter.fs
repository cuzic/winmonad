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
    | TimerId.Placement (PresenceDeadline _) -> "placement-presence-deadline"
    | TimerId.Windows (ClassificationDeadline _) -> "windows-classification-deadline"
    | TimerId.Topology ScreenSettle -> "topology-screen-settle"
    | TimerId.Topology ScreenGrace -> "topology-screen-grace"
    | TimerId.Topology ReconfigurationCap -> "topology-reconfiguration-cap"
    | TimerId.Focus WorkspaceSwitchSettle -> "focus-workspace-switch-settle"
    | TimerId.Health (ResponsivenessReprobe _) -> "health-responsiveness-reprobe"

let private windowIdOf (id: TimerId) : JsonNode option =
    match id with
    | TimerId.Placement (PendingDeadline (RequestId rid))
    | TimerId.Placement (PresenceDeadline (RequestId rid)) -> Some (JsonValue.Create(rid) :> JsonNode)
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
    | "placement-presence-deadline" -> TimerId.Placement (PresenceDeadline (RequestId (refVal ())))
    | "windows-classification-deadline" -> TimerId.Windows (ClassificationDeadline (WindowId (refVal ())))
    | "topology-screen-settle" -> TimerId.Topology ScreenSettle
    | "topology-screen-grace" -> TimerId.Topology ScreenGrace
    | "topology-reconfiguration-cap" -> TimerId.Topology ReconfigurationCap
    | "focus-workspace-switch-settle" -> TimerId.Focus WorkspaceSwitchSettle
    | "health-responsiveness-reprobe" -> TimerId.Health (ResponsivenessReprobe (WindowId (refVal ())))
    | other -> failwithf "Replay: unrecognized TimerId tag '%s' — このJournalはより新しいスキーマで書かれている" other

/// `System.Text.Json.Nodes.JsonValue.Create<string>(null)`は（正しく）JSONの`null`を表す`null`な
/// `JsonNode`参照を返すが、読み出し側で`node.GetValue<string>()`をそのまま呼ぶと`NullReferenceException`
/// になる（`GetValue`はインスタンスメソッドで、`this`がnullだと呼べない）。`string`は.NET/F#では既定で
/// null許容なので、`Observation<'T>.Source`や`NativeCallRecord.Api`のような文字列フィールドにJSON側で
/// nullが来ても安全に往復させる必要がある——v2.57でFsCheckの`WindowSnapshot`往復テストが
/// `Source = null`を実際に生成して検出した（null文字列を本当に送ってくるコードは今の実装には無いが、
/// 型がnullを許す以上、Journalの往復は「来たら安全に扱う」側であるべき、という判断）。
let private stringOfJsonAllowNull (node: JsonNode) : string =
    if isNull node then null else node.GetValue<string>()

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
    { Api = stringOfJsonAllowNull node.["api"]
      Space = errorSpaceOfJson (node.["space"].GetValue<string>())
      Code = node.["code"].GetValue<int>()
      ElapsedUs = node.["elapsedUs"].GetValue<int64>() }

let private windowIdOptionToJson (w: WindowId option) : JsonNode =
    match w with
    | Some (WindowId v) -> JsonValue.Create(v) :> JsonNode
    | None -> null

let private windowIdOptionOfJson (node: JsonNode) : WindowId option =
    if isNull node then None else Some (WindowId (node.GetValue<int64>()))

/// `Observation<'T>`の汎用DTO（§4.2）。値そのものの符号化（`valueToJson`/`valueOfJson`）だけを
/// 型ごとに渡す——v2.42までは`ForegroundObserved`用の`Observation<WindowId option>`専用版しか無かった
/// （「このスライスで必要なのはこの1つだけ」という理由で汎用化を避けていた）。v2.57で`WindowObserved`
/// 等がJournal往復対象になり、`WindowSnapshot`の各フィールドごとに同じ形を繰り返す必要が出たため、
/// ここで一般化した（`ForegroundObserved`側は`observationToJson windowIdOptionToJson`という適用に
/// 置き換えるだけで、JSON形状自体は変えていない）。
let private observationToJson (valueToJson: 'T -> JsonNode) (obs: Observation<'T>) : JsonNode =
    let o = JsonObject()
    o.["sampleSequence"] <- JsonValue.Create(obs.SampleSequence)
    o.["monotonicTime"] <- JsonValue.Create(obs.MonotonicTime)
    o.["source"] <- JsonValue.Create(obs.Source)
    match obs.Value with
    | Ok v ->
        o.["outcome"] <- JsonValue.Create("ok")
        o.["value"] <- valueToJson v
    | Error rec_ ->
        o.["outcome"] <- JsonValue.Create("failed")
        o.["failure"] <- nativeCallRecordToJson rec_
    o :> JsonNode

let private observationOfJson (valueOfJson: JsonNode -> 'T) (node: JsonNode) : Observation<'T> =
    let value =
        match node.["outcome"].GetValue<string>() with
        | "ok" -> Ok (valueOfJson node.["value"])
        | "failed" -> Error (nativeCallRecordOfJson node.["failure"])
        | other -> failwithf "Replay: unrecognized Observation outcome '%s'" other
    { Value = value
      SampleSequence = node.["sampleSequence"].GetValue<int64>()
      MonotonicTime = node.["monotonicTime"].GetValue<int64>()
      Source = stringOfJsonAllowNull node.["source"] }

let private observationOptionToJson (valueToJson: 'T -> JsonNode) (obs: Observation<'T> option) : JsonNode =
    match obs with
    | Some o -> observationToJson valueToJson o
    | None -> null

let private observationOptionOfJson (valueOfJson: JsonNode -> 'T) (node: JsonNode) : Observation<'T> option =
    if isNull node then None else Some (observationOfJson valueOfJson node)

let private boolToJson (b: bool) : JsonNode = JsonValue.Create(b) :> JsonNode
let private boolOfJson (node: JsonNode) : bool = node.GetValue<bool>()

let private intToJson (i: int) : JsonNode = JsonValue.Create(i) :> JsonNode
let private intOfJson (node: JsonNode) : int = node.GetValue<int>()

let private stringOptionObservationToJson (obs: Observation<string> option) : JsonNode =
    observationOptionToJson (fun (s: string) -> JsonValue.Create(s) :> JsonNode) obs

let private stringOptionObservationOfJson (node: JsonNode) : Observation<string> option =
    observationOptionOfJson stringOfJsonAllowNull node

let private appIdentityToJson (a: AppIdentity) : JsonNode =
    let o = JsonObject()
    o.["exe"] <- stringOptionObservationToJson a.Exe
    o.["hostExe"] <- stringOptionObservationToJson a.HostExe
    o.["aumid"] <- stringOptionObservationToJson a.Aumid
    o.["coreWindowExe"] <- stringOptionObservationToJson a.CoreWindowExe
    o :> JsonNode

let private appIdentityOfJson (node: JsonNode) : AppIdentity =
    { Exe = stringOptionObservationOfJson node.["exe"]
      HostExe = stringOptionObservationOfJson node.["hostExe"]
      Aumid = stringOptionObservationOfJson node.["aumid"]
      CoreWindowExe = stringOptionObservationOfJson node.["coreWindowExe"] }

let private processKeyToJson (p: ProcessKey) : JsonNode =
    let o = JsonObject()
    o.["pid"] <- JsonValue.Create(p.Pid)
    o.["creationTime"] <- JsonValue.Create(p.CreationTime)
    o :> JsonNode

let private processKeyOfJson (node: JsonNode) : ProcessKey =
    { Pid = node.["pid"].GetValue<int>(); CreationTime = node.["creationTime"].GetValue<int64>() }

let private cloakStateToJson (c: CloakState) : JsonNode =
    let o = JsonObject()
    o.["app"] <- JsonValue.Create(c.App)
    o.["shell"] <- JsonValue.Create(c.Shell)
    o.["inherited"] <- JsonValue.Create(c.Inherited)
    o :> JsonNode

let private cloakStateOfJson (node: JsonNode) : CloakState =
    { App = node.["app"].GetValue<bool>(); Shell = node.["shell"].GetValue<bool>(); Inherited = node.["inherited"].GetValue<bool>() }

let private monitorIdToJson (MonitorId mid: MonitorId) : JsonNode = JsonValue.Create(mid) :> JsonNode
let private monitorIdOfJson (node: JsonNode) : MonitorId = MonitorId (node.GetValue<int64>())

let private integrityRelationToJson (r: IntegrityRelation) : JsonNode =
    JsonValue.Create(match r with NotHigher -> "not-higher" | Higher -> "higher") :> JsonNode

let private integrityRelationOfJson (node: JsonNode) : IntegrityRelation =
    match node.GetValue<string>() with
    | "not-higher" -> NotHigher
    | "higher" -> Higher
    | other -> failwithf "Replay: unrecognized IntegrityRelation tag '%s'" other

let private responsivenessSampleToJson (r: ResponsivenessSample) : JsonNode =
    let o = JsonObject()
    match r with
    | PumpResponded -> o.["tag"] <- JsonValue.Create("pump-responded")
    | PumpTimedOut elapsedUs ->
        o.["tag"] <- JsonValue.Create("pump-timed-out")
        o.["elapsedUs"] <- JsonValue.Create(elapsedUs)
    | IsHungAppWindowTrue -> o.["tag"] <- JsonValue.Create("is-hung-app-window-true")
    o :> JsonNode

let private responsivenessSampleOfJson (node: JsonNode) : ResponsivenessSample =
    match node.["tag"].GetValue<string>() with
    | "pump-responded" -> PumpResponded
    | "pump-timed-out" -> PumpTimedOut (node.["elapsedUs"].GetValue<int64>())
    | "is-hung-app-window-true" -> IsHungAppWindowTrue
    | other -> failwithf "Replay: unrecognized ResponsivenessSample tag '%s'" other

let private observedPresenceToJson (p: ObservedPresence) : JsonNode =
    JsonValue.Create(match p with VisibleToUser -> "visible-to-user" | HiddenByWmMethod -> "hidden-by-wm-method" | HiddenOther -> "hidden-other") :> JsonNode

let private observedPresenceOfJson (node: JsonNode) : ObservedPresence =
    match node.GetValue<string>() with
    | "visible-to-user" -> VisibleToUser
    | "hidden-by-wm-method" -> HiddenByWmMethod
    | "hidden-other" -> HiddenOther
    | other -> failwithf "Replay: unrecognized ObservedPresence tag '%s'" other

let private dpiAwarenessDeclaredToJson (d: DpiAwarenessDeclared) : JsonNode =
    JsonValue.Create(match d with Unaware -> "unaware" | SystemAware -> "system-aware" | PerMonitor -> "per-monitor" | PerMonitorV2 -> "per-monitor-v2") :> JsonNode

let private dpiAwarenessDeclaredOfJson (node: JsonNode) : DpiAwarenessDeclared =
    match node.GetValue<string>() with
    | "unaware" -> Unaware
    | "system-aware" -> SystemAware
    | "per-monitor" -> PerMonitor
    | "per-monitor-v2" -> PerMonitorV2
    | other -> failwithf "Replay: unrecognized DpiAwarenessDeclared tag '%s'" other

let private titleMatchVectorToJson (t: TitleMatchVector) : JsonNode =
    let o = JsonObject()
    o.["patternSetHash"] <- JsonValue.Create(t.PatternSetHash)
    let arr = JsonArray()
    for i in t.Matches do arr.Add(JsonValue.Create(i))
    o.["matches"] <- arr
    o :> JsonNode

let private titleMatchVectorOfJson (node: JsonNode) : TitleMatchVector =
    { PatternSetHash = stringOfJsonAllowNull node.["patternSetHash"]
      Matches = node.["matches"].AsArray() |> Seq.map (fun n -> n.GetValue<int>()) |> Set.ofSeq }

/// `WindowSnapshot`（§4.2「今回Shellが問い合わせた属性だけを運ぶ部分観測」）のDTO。各フィールドは
/// `Observation<'T> option`で、問い合わせなかったフィールドは`null`として書く（v2.57追加）。
let private windowSnapshotToJson (s: WindowSnapshot) : JsonNode =
    let o = JsonObject()
    let (WindowId wid) = s.Id
    o.["id"] <- JsonValue.Create(wid)
    o.["process"] <- observationOptionToJson processKeyToJson s.Process
    o.["appIdentity"] <- appIdentityToJson s.AppIdentity
    o.["owner"] <- observationOptionToJson windowIdOptionToJson s.Owner
    o.["isDialog"] <- observationOptionToJson boolToJson s.IsDialog
    o.["nativeRect"] <- observationOptionToJson pixelRectToJson s.NativeRect
    o.["visibleFrameRect"] <- observationOptionToJson pixelRectToJson s.VisibleFrameRect
    o.["isVisible"] <- observationOptionToJson boolToJson s.IsVisible
    o.["isMinimized"] <- observationOptionToJson boolToJson s.IsMinimized
    o.["isMaximized"] <- observationOptionToJson boolToJson s.IsMaximized
    o.["cloak"] <- observationOptionToJson cloakStateToJson s.Cloak
    o.["monitor"] <- observationOptionToJson monitorIdToJson s.Monitor
    o.["minWidthHint"] <- observationOptionToJson intToJson s.MinWidthHint
    o.["targetIntegrity"] <- observationOptionToJson integrityRelationToJson s.TargetIntegrity
    o.["responsiveness"] <- observationOptionToJson responsivenessSampleToJson s.Responsiveness
    o.["presence"] <- observationOptionToJson observedPresenceToJson s.Presence
    o.["dpiAwareness"] <- observationOptionToJson dpiAwarenessDeclaredToJson s.DpiAwareness
    o.["titleMatches"] <- observationOptionToJson titleMatchVectorToJson s.TitleMatches
    o :> JsonNode

let private windowSnapshotOfJson (node: JsonNode) : WindowSnapshot =
    { Id = WindowId (node.["id"].GetValue<int64>())
      Process = observationOptionOfJson processKeyOfJson node.["process"]
      AppIdentity = appIdentityOfJson node.["appIdentity"]
      Owner = observationOptionOfJson windowIdOptionOfJson node.["owner"]
      IsDialog = observationOptionOfJson boolOfJson node.["isDialog"]
      NativeRect = observationOptionOfJson pixelRectOfJson node.["nativeRect"]
      VisibleFrameRect = observationOptionOfJson pixelRectOfJson node.["visibleFrameRect"]
      IsVisible = observationOptionOfJson boolOfJson node.["isVisible"]
      IsMinimized = observationOptionOfJson boolOfJson node.["isMinimized"]
      IsMaximized = observationOptionOfJson boolOfJson node.["isMaximized"]
      Cloak = observationOptionOfJson cloakStateOfJson node.["cloak"]
      Monitor = observationOptionOfJson monitorIdOfJson node.["monitor"]
      MinWidthHint = observationOptionOfJson intOfJson node.["minWidthHint"]
      TargetIntegrity = observationOptionOfJson integrityRelationOfJson node.["targetIntegrity"]
      Responsiveness = observationOptionOfJson responsivenessSampleOfJson node.["responsiveness"]
      Presence = observationOptionOfJson observedPresenceOfJson node.["presence"]
      DpiAwareness = observationOptionOfJson dpiAwarenessDeclaredOfJson node.["dpiAwareness"]
      TitleMatches = observationOptionOfJson titleMatchVectorOfJson node.["titleMatches"] }

let private unitResultToJson (r: Result<unit, NativeCallRecord>) : JsonNode =
    let o = JsonObject()
    match r with
    | Ok () -> o.["outcome"] <- JsonValue.Create("ok")
    | Error rec_ ->
        o.["outcome"] <- JsonValue.Create("failed")
        o.["failure"] <- nativeCallRecordToJson rec_
    o :> JsonNode

let private unitResultOfJson (node: JsonNode) : Result<unit, NativeCallRecord> =
    match node.["outcome"].GetValue<string>() with
    | "ok" -> Ok ()
    | "failed" -> Error (nativeCallRecordOfJson node.["failure"])
    | other -> failwithf "Replay: unrecognized Result outcome '%s'" other

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

/// `Management option`（`MembershipHintsLoaded`、v2.64）。`windowIdOptionToJson`と同じ
/// null-as-None規約。
let private managementOptionToJson (m: Management option) : JsonNode =
    match m with
    | Some mgmt -> JsonValue.Create(managementToJson mgmt) :> JsonNode
    | None -> null

let private managementOptionOfJson (node: JsonNode) : Management option =
    if isNull node then None else Some (managementOfJson (node.GetValue<string>()))

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
/// WindowRemoved/UserCommand）は全て対応する（Opusレビュー5-4）。**v2.57で`WindowObserved`/
/// `VisibilityChanged`/`CloakChanged`/`MinimizedStateChanged`/`EffectCallReturned`も追加**：
/// `WmState.step`が実際に分岐対象にしている入力のうち、この5つが未対応のまま（`unsupported-
/// input-event`へ落ちてReplayがfail-fastする）残っていた——§9.2自身が要求する「`step`の入力分岐 ⊆
/// Journalの往復可能集合」という不変条件への違反であり、かつこの5つは現実のシナリオで最も頻繁に
/// 届く入力（`WindowObserved`は窓の発見・全ての信念更新の起点）なので、実機ReplayがほぼどのJournalも
/// 読めない状態だった。`EffectBatchReturned`は`ApplyBatch`自体がまだ発行されないため（VirtualWorld.fs
/// ファイル冒頭コメント参照）、依然対象外のまま。**v2.64で`MembershipHintsLoaded`も追加**：
/// `Windows.onMembershipHintsLoaded`の実装（§4.5 R4-3）で`WmState.step`がこの入力を初めて分岐対象に
/// したので、同じ不変条件を保つため同じラウンドでJournal往復対応も追加した（前回のような「後から
/// 気付く」ギャップを繰り返さないため）。**v2.65で`MoveSizeStarted`/`MoveSizeEnded`も追加**：
/// `Placement.onMoveSizeStarted`/`onMoveSizeEnded`の実装で`WmState.step`がこの2つを初めて分岐対象に
/// したので、同じ理由で同じラウンドに含めた。
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
        o.["observation"] <- observationToJson windowIdOptionToJson obs
    | WindowRemoved (WindowId w) ->
        o.["tag"] <- JsonValue.Create("window-removed")
        o.["windowId"] <- JsonValue.Create(w)
    | UserCommand cmd ->
        o.["tag"] <- JsonValue.Create("user-command")
        o.["command"] <- coreCommandToJson cmd
    | WindowObserved snapshot ->
        o.["tag"] <- JsonValue.Create("window-observed")
        o.["snapshot"] <- windowSnapshotToJson snapshot
    | VisibilityChanged (WindowId w, obs) ->
        o.["tag"] <- JsonValue.Create("visibility-changed")
        o.["windowId"] <- JsonValue.Create(w)
        o.["observation"] <- observationToJson boolToJson obs
    | CloakChanged (WindowId w, obs) ->
        o.["tag"] <- JsonValue.Create("cloak-changed")
        o.["windowId"] <- JsonValue.Create(w)
        o.["observation"] <- observationToJson cloakStateToJson obs
    | MinimizedStateChanged (WindowId w, obs) ->
        o.["tag"] <- JsonValue.Create("minimized-state-changed")
        o.["windowId"] <- JsonValue.Create(w)
        o.["observation"] <- observationToJson boolToJson obs
    | EffectCallReturned (RequestId rid, result, dispatchSeq) ->
        o.["tag"] <- JsonValue.Create("effect-call-returned")
        o.["requestId"] <- JsonValue.Create(rid)
        o.["result"] <- unitResultToJson result
        o.["dispatchSeq"] <- (dispatchSeq |> Option.map JsonValue.Create<int64> |> Option.defaultValue null)
    | MoveSizeStarted (WindowId w) ->
        o.["tag"] <- JsonValue.Create("move-size-started")
        o.["windowId"] <- JsonValue.Create(w)
    | MoveSizeEnded (WindowId w, rectObs, monitorObs) ->
        o.["tag"] <- JsonValue.Create("move-size-ended")
        o.["windowId"] <- JsonValue.Create(w)
        o.["rect"] <- observationToJson pixelRectToJson rectObs
        o.["monitor"] <- observationToJson monitorIdToJson monitorObs
    | MembershipHintsLoaded hints ->
        o.["tag"] <- JsonValue.Create("membership-hints-loaded")
        let arr = JsonArray()
        for (WindowId w, WorkspaceId ws, mgmt) in hints do
            let h = JsonObject()
            h.["windowId"] <- JsonValue.Create(w)
            h.["workspaceId"] <- JsonValue.Create(ws)
            h.["management"] <- managementOptionToJson mgmt
            arr.Add(h)
        o.["hints"] <- arr
    | other ->
        // 本ビルドで未対応のInputEvent（`EffectBatchReturned`等）。表示専用（Replayはこのタグに
        // 出会うとfail-fastする、§9.2）。
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
    | "foreground-observed" -> ForegroundObserved (observationOfJson windowIdOptionOfJson node.["observation"])
    | "window-removed" -> WindowRemoved (WindowId (node.["windowId"].GetValue<int64>()))
    | "user-command" -> UserCommand (coreCommandOfJson node.["command"])
    | "window-observed" -> WindowObserved (windowSnapshotOfJson node.["snapshot"])
    | "visibility-changed" -> VisibilityChanged (WindowId (node.["windowId"].GetValue<int64>()), observationOfJson boolOfJson node.["observation"])
    | "cloak-changed" -> CloakChanged (WindowId (node.["windowId"].GetValue<int64>()), observationOfJson cloakStateOfJson node.["observation"])
    | "minimized-state-changed" -> MinimizedStateChanged (WindowId (node.["windowId"].GetValue<int64>()), observationOfJson boolOfJson node.["observation"])
    | "move-size-started" -> MoveSizeStarted (WindowId (node.["windowId"].GetValue<int64>()))
    | "move-size-ended" ->
        MoveSizeEnded (WindowId (node.["windowId"].GetValue<int64>()), observationOfJson pixelRectOfJson node.["rect"], observationOfJson monitorIdOfJson node.["monitor"])
    | "effect-call-returned" ->
        let dispatchSeq = if isNull node.["dispatchSeq"] then None else Some (node.["dispatchSeq"].GetValue<int64>())
        EffectCallReturned (RequestId (node.["requestId"].GetValue<int64>()), unitResultOfJson node.["result"], dispatchSeq)
    | "membership-hints-loaded" ->
        let hints =
            node.["hints"].AsArray()
            |> Seq.map (fun h -> WindowId (h.["windowId"].GetValue<int64>()), WorkspaceId (h.["workspaceId"].GetValue<int>()), managementOptionOfJson h.["management"])
            |> List.ofSeq
        MembershipHintsLoaded hints
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
