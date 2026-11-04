/// architecture.md §9.1「判断は永続化せずReplay再構成」を、`Wm.Cli.Replay`に対して直接検査する：
/// Journalに書いた入力列を新しい`Reducer`へ`replayAll`で通すと、ライブ実行とまったく同じ
/// Effects・最終状態が再構成されることを確認する——この等価性こそが9.1の方針の中身であり、
/// `Wm.Cli`が実際に正しく動く根拠になる。
module Wm.Cli.Tests.ReplayTests

open System
open Xunit
open Wm.Contracts.Identifiers
open Wm.Contracts.Observation
open Wm.Contracts.CoreCommand
open Wm.Contracts.GatedEffect
open Wm.Core.WmState
open Wm.Runtime.Reducer
open Wm.Cli.Replay

let private sampleMonitor : MonitorSnapshot =
    { Id = MonitorId 0L
      StableKey = "m0"
      Bounds = { X = 0; Y = 0; Width = 1920; Height = 1080 }
      WorkArea = { X = 0; Y = 0; Width = 1920; Height = 1080 }
      Dpi = 96
      IsPrimary = true }

let private obs (now: int64) (seq: int64) (value: 'T) : Observation<'T> =
    { Value = Ok value; SampleSequence = seq; MonotonicTime = now; Source = "test" }

let private sampleWindow : WindowSnapshot =
    { Id = WindowId 1L
      Process = None
      AppIdentity = { Exe = None; HostExe = None; Aumid = None; CoreWindowExe = None }
      Owner = None
      IsDialog = None
      NativeRect = Some (obs 1L 0L { X = 0; Y = 0; Width = 300; Height = 200 })
      VisibleFrameRect = Some (obs 1L 1L { X = 0; Y = 0; Width = 300; Height = 200 })
      IsVisible = Some (obs 1L 2L true)
      IsMinimized = Some (obs 1L 3L false)
      IsMaximized = Some (obs 1L 4L false)
      Cloak = Some (obs 1L 5L { App = false; Shell = false; Inherited = false })
      Monitor = Some (obs 1L 6L (MonitorId 0L))
      MinWidthHint = None
      TargetIntegrity = None
      Responsiveness = None
      Presence = None
      DpiAwareness = None
      TitleMatches = None }

/// タイマーを尽きるまで仮想時計で進め、発火したStepResultのEffectsも集めて返す
/// （Program.fs/Wm.Hostの`drainTimers`と同じ考え方）。
let private drainTimers (reducer: Reducer) : GatedEffect list =
    let mutable effects = []
    let mutable more = true
    while more do
        match reducer.AdvanceToNextTimer() with
        | Some sr -> effects <- effects @ sr.Effects
        | None -> more <- false
    effects

[<Fact>]
let ``Replayは、Journalに書いたライブ実行と同じEffectsと最終状態を再構成する`` () =
    let path = IO.Path.Combine(IO.Path.GetTempPath(), sprintf "wm-cli-replay-test-%s.ndjson" (Guid.NewGuid().ToString("N")))
    try
        // ライブ実行：実際にJournalへ書きながら駆動する。
        let live = Reducer(RuntimePolicy.defaults, "live", "test-build", "no-policy-artifact", Some path)
        let liveEffects = ResizeArray<GatedEffect>()
        liveEffects.AddRange((live.Submit 0L "test" (MonitorTopologyObserved [ sampleMonitor ])).Effects)
        liveEffects.AddRange(drainTimers live)
        liveEffects.AddRange((live.Submit 1L "test" (WindowObserved sampleWindow)).Effects)
        liveEffects.AddRange(drainTimers live)
        let liveClassification = live.State.Windows.Records |> Map.map (fun _ r -> r.Classification)
        let livePendingCount = Map.count live.State.Placement.Pending

        // Replay：同じJournalを読み、新しいReducerへ`replayAll`で通すだけ。
        let entries = Wm.Journal.JournalWriter.readAll path |> List.ofSeq
        let replayReducer = Reducer(RuntimePolicy.defaults, "replay", "test-build", "no-policy-artifact", None)
        let replayEffects =
            replayAll replayReducer entries
            |> List.collect (fun (_, srOpt) -> srOpt |> Option.map (fun sr -> sr.Effects) |> Option.defaultValue [])
        let replayClassification = replayReducer.State.Windows.Records |> Map.map (fun _ r -> r.Classification)
        let replayPendingCount = Map.count replayReducer.State.Placement.Pending

        Assert.True((List.ofSeq liveEffects = replayEffects), "replayed Effects must equal the live-run Effects")
        Assert.True((liveClassification = replayClassification), "replayed classification must equal the live-run classification")
        Assert.Equal(livePendingCount, replayPendingCount)
    finally
        if IO.File.Exists path then IO.File.Delete path

[<Fact>]
let ``非InputPayloadのエントリはNoneを返しCoreへ入力されない`` () =
    let reducer = Reducer(RuntimePolicy.defaults, "test", "test-build", "no-policy-artifact", None)
    let entry: Wm.Contracts.JournalTypes.JournalEntry =
        { SchemaVersion = 1
          SessionId = "test"
          Sequence = 0L
          MonotonicTicks = 0L
          WallTimeUtc = DateTimeOffset.UtcNow
          CorrelationId = None
          CausationId = None
          PolicyArtifactHash = "no-policy-artifact"
          CoreBuildId = "test-build"
          Kind = Wm.Contracts.JournalTypes.SafetyRecoveryAction
          Payload = Wm.Contracts.JournalTypes.SafetyRecoveryActionPayload "restored hwnd=0 after heartbeat gap" }
    Assert.True(Option.isNone (replayEntry reducer entry))
