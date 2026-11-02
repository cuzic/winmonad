/// architecture.md §15.1 #10/#19（Replayの一致、信念は入力列の決定的な関数）を実際に検査するには、
/// Journalが`step`の分岐対象になっている入力を欠落無く往復できる必要がある。Opusレビュー5-4：
/// 旧実装は`MonitorTopologyObserved`/`ScreenReconfigurationInProgress`/`TimerElapsed`しか符号化せず、
/// `ForegroundObserved`/`WindowRemoved`/`UserCommand`（いずれも`WmState.step`が分岐に使う）は
/// "unsupported-input-event"として書かれ、Replayがfail-fastしていた。
module Wm.Journal.Tests.RoundTripTests

open System
open System.IO
open FsCheck.Xunit
open Wm.Contracts.Identifiers
open Wm.Contracts.ErrorTypes
open Wm.Contracts.Observation
open Wm.Contracts.SharedVocabulary
open Wm.Contracts.Timers
open Wm.Contracts.CoreCommand
open Wm.Contracts.JournalTypes
open Wm.Journal.JournalWriter

let private mkEntry (payload: JournalPayload) : JournalEntry =
    { SchemaVersion = 1
      SessionId = "test-session"
      Sequence = 1L
      MonotonicTicks = 123L
      WallTimeUtc = DateTimeOffset.UtcNow
      CorrelationId = None
      CausationId = None
      PolicyArtifactHash = "hash"
      CoreBuildId = "build"
      Kind = InputReceived
      Payload = payload }

let private roundTrip (entry: JournalEntry) : JournalEntry =
    let path = Path.Combine(Path.GetTempPath(), sprintf "wm-journal-roundtrip-%s.ndjson" (Guid.NewGuid().ToString("N")))
    try
        appendEntry path entry
        readAll path |> List.ofSeq |> List.exactlyOne
    finally
        if File.Exists path then
            File.Delete path

let private mkMonitor (id: int) : MonitorSnapshot =
    { Id = MonitorId (int64 id)
      StableKey = sprintf "TEST:%d" id
      Bounds = { X = 0; Y = 0; Width = 1920; Height = 1080 }
      WorkArea = { X = 0; Y = 0; Width = 1920; Height = 1040 }
      Dpi = 96
      IsPrimary = (id = 0) }

[<Property>]
let ``MonitorTopologyObservedは往復しても一致する`` (ids: int list) =
    let snapshots = ids |> List.truncate 5 |> List.map (fun i -> mkMonitor (abs i % 8)) |> List.distinctBy (fun m -> m.Id)
    let entry = mkEntry (InputPayload (MonitorTopologyObserved snapshots))
    roundTrip entry = entry

[<Property>]
let ``ScreenReconfigurationInProgressは往復しても一致する`` (b: bool) =
    let entry = mkEntry (InputPayload (ScreenReconfigurationInProgress b))
    roundTrip entry = entry

[<Property>]
let ``TimerElapsed(Topology ScreenSettle)は往復しても一致する`` () =
    let entry = mkEntry (InputPayload (TimerElapsed (TimerId.Topology ScreenSettle)))
    roundTrip entry = entry

[<Property>]
let ``TimerElapsed(Health ResponsivenessReprobe)は往復しても一致する`` (w: int) =
    let entry = mkEntry (InputPayload (TimerElapsed (TimerId.Health (ResponsivenessReprobe (WindowId (int64 (abs w)))))))
    roundTrip entry = entry

[<Property>]
let ``ForegroundObservedは往復しても一致する(Ok)`` (sequence: int) (w: int option) =
    let obs =
        { Value = Ok (w |> Option.map (fun i -> WindowId (int64 (abs i))))
          SampleSequence = int64 (abs sequence)
          MonotonicTime = int64 (abs sequence)
          Source = "test" }
    let entry = mkEntry (InputPayload (ForegroundObserved obs))
    roundTrip entry = entry

[<Property>]
let ``ForegroundObservedは往復しても一致する(Error)`` (sequence: int) =
    let err: NativeCallRecord = { Api = "test"; Space = Win32LastError; Code = 5; ElapsedUs = 10L }
    let obs = { Value = Error err; SampleSequence = int64 (abs sequence); MonotonicTime = int64 (abs sequence); Source = "test" }
    let entry = mkEntry (InputPayload (ForegroundObserved obs))
    roundTrip entry = entry

[<Property>]
let ``WindowRemovedは往復しても一致する`` (w: int) =
    let entry = mkEntry (InputPayload (WindowRemoved (WindowId (int64 (abs w)))))
    roundTrip entry = entry

[<Property>]
let ``UserCommandの全ケースは往復しても一致する`` (ws: int) (w: int) =
    let cmds =
        [ FocusNext
          FocusPrevious
          FocusMostRecent
          SwapWithMaster
          PromoteToMaster
          CloseFocused
          SwitchWorkspace (WorkspaceId (abs ws % 8))
          MoveFocusedToWorkspace (WorkspaceId (abs ws % 8))
          FocusMonitor (MonitorId (int64 (abs ws % 8)))
          ChangeLayout { Id = LayoutId "master-stack"; ApiVersion = 1; Args = MasterRatio 0.5m }
          ToggleFloating (WindowId (int64 (abs w)))
          SetManagement (WindowId (int64 (abs w)), Tiled)
          ClearUserOverride (WindowId (int64 (abs w))) ]

    cmds
    |> List.forall (fun cmd ->
        let entry = mkEntry (InputPayload (UserCommand cmd))
        roundTrip entry = entry)
