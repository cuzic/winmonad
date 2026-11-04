/// architecture.md §12.3「実測モデルの育成」・外部フレームワーク構想§10.3 Model Calibrationから
/// 持ち込んだ`Wm.Cli.Replay.measuredLatenciesMs`/`calibrate`を検査する：実機Journalに記録された
/// 「SetRectの発行→目標どおりの矩形の観測」という実際の往復だけから実測遅延(ms)を正しく抽出し、
/// それに数値的に最も近い候補`WindowBehavior`を選べることを確認する。
module Wm.Cli.Tests.CalibrateTests

open System
open Xunit
open Wm.Contracts.Identifiers
open Wm.Contracts.Observation
open Wm.Contracts.CoreCommand
open Wm.Contracts.GatedEffect
open Wm.Core.WmState
open Wm.Runtime.Reducer
open Wm.Simulation.WindowBehaviors
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

let private sampleWindow (w: WindowId) (rect: PixelRect) : WindowSnapshot =
    { Id = w
      Process = None
      AppIdentity = { Exe = None; HostExe = None; Aumid = None; CoreWindowExe = None }
      Owner = None
      IsDialog = None
      NativeRect = Some (obs 1L 0L rect)
      VisibleFrameRect = Some (obs 1L 1L rect)
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

/// 実機を模した「生のJournal」を1本作る：モニター1台＋初期矩形がWorkAreaと違う窓1つ（必ず
/// `SetRect`が発行される条件）。`SetRect`自体はWindowObserved直後ではなくTopology Grace期間満了の
/// タイマー経由で発行されることがあるため（`SimulateTests.buildJournal`と同様、タイマーを尽きるまで
/// 進めながら探す）、実際に発行された tick を`reducer.Timers.NextDeadline`で追いながら特定する。
/// 見つかった時点で、`delayMs`だけ後のtickに、目標どおりの矩形を運ぶ`WindowObserved`（実機の
/// WinEventフックが遅れて矩形変化を検知した、という体）を1件だけ追加する——これが
/// `measuredLatenciesMs`が抽出すべき「実測遅延」そのものになる。
let private buildJournalWithMeasuredDelay (w: WindowId) (initialRect: PixelRect) (delayMs: int64) : string =
    let path = IO.Path.Combine(IO.Path.GetTempPath(), sprintf "wm-cli-calibrate-test-%s.ndjson" (Guid.NewGuid().ToString("N")))
    let reducer = Reducer(RuntimePolicy.defaults, "test", "test-build", "no-policy-artifact", Some path)
    reducer.Submit 0L "test" (MonitorTopologyObserved [ sampleMonitor ]) |> ignore
    let sr0 = reducer.Submit 1L "test" (WindowObserved (sampleWindow w initialRect))
    let mutable setRectInfo : (int64 * PixelRect) option = None
    let tryCapture (tick: int64) (effects: GatedEffect list) =
        if setRectInfo.IsNone then
            effects
            |> List.tryPick (fun g -> match GatedEffect.effect g with SetRect (_, _, rect) -> Some rect | _ -> None)
            |> Option.iter (fun rect -> setRectInfo <- Some (tick, rect))
    tryCapture 1L sr0.Effects
    let mutable more = true
    while more && setRectInfo.IsNone do
        match reducer.Timers.NextDeadline with
        | Some fireAt ->
            match reducer.AdvanceToNextTimer() with
            | Some sr -> tryCapture fireAt sr.Effects
            | None -> more <- false
        | None -> more <- false
    match setRectInfo with
    | Some (issuedAt, targetRect) -> reducer.Submit (issuedAt + delayMs) "test" (WindowObserved (sampleWindow w targetRect)) |> ignore
    | None -> failwith "fixture did not produce a SetRect to measure against"
    path

[<Fact>]
let ``measuredLatenciesMsはSetRect発行から目標どおりの矩形が観測されるまでの実測tick差をそのまま返す`` () =
    let w = WindowId 1L
    let path = buildJournalWithMeasuredDelay w { X = 0; Y = 0; Width = 300; Height = 200 } 237L
    try
        let entries = Wm.Journal.JournalWriter.readAll path |> List.ofSeq
        match measuredLatenciesMs entries with
        | [ (measuredWindow, latencyMs) ] ->
            Assert.Equal(w, measuredWindow)
            Assert.Equal(237L, latencyMs)
        | other -> failwithf "expected exactly one measured latency, got %A" other
    finally
        if IO.File.Exists path then IO.File.Delete path

[<Theory>]
[<InlineData(0L, "Immediate")>]
[<InlineData(50L, "DelayedByMs 50L")>]
[<InlineData(200L, "DelayedByMs 200L")>]
[<InlineData(180L, "DelayedByMs 200L")>] // 180msはグリッド上200msに一番近い(|100-180|=80 > |200-180|=20)
let ``calibrateは実測遅延に数値的に最も近い候補を選ぶ`` (delayMs: int64, expectedBehaviorText: string) =
    let w = WindowId 1L
    let path = buildJournalWithMeasuredDelay w { X = 0; Y = 0; Width = 300; Height = 200 } delayMs
    try
        let entries = Wm.Journal.JournalWriter.readAll path |> List.ofSeq
        match calibrate entries with
        | [ (measuredWindow, bestFit, measuredMs) ] ->
            Assert.Equal(w, measuredWindow)
            Assert.Equal(delayMs, measuredMs)
            Assert.Equal(expectedBehaviorText, sprintf "%A" bestFit)
        | other -> failwithf "expected exactly one calibration result, got %A" other
    finally
        if IO.File.Exists path then IO.File.Delete path

[<Fact>]
let ``calibrateは同じJournalに対して常に同じ結果を返す(決定論性)`` () =
    let w = WindowId 1L
    let path = buildJournalWithMeasuredDelay w { X = 0; Y = 0; Width = 300; Height = 200 } 333L
    try
        let entries = Wm.Journal.JournalWriter.readAll path |> List.ofSeq
        Assert.Equal<(WindowId * WindowBehavior * int64) list>(calibrate entries, calibrate entries)
    finally
        if IO.File.Exists path then IO.File.Delete path

[<Fact>]
let ``SetRectが一度も発行されない(矩形が既にWorkAreaと一致)Journalではcalibrateは空を返す`` () =
    let w = WindowId 1L
    let path =
        let p = IO.Path.Combine(IO.Path.GetTempPath(), sprintf "wm-cli-calibrate-test-%s.ndjson" (Guid.NewGuid().ToString("N")))
        let reducer = Reducer(RuntimePolicy.defaults, "test", "test-build", "no-policy-artifact", Some p)
        reducer.Submit 0L "test" (MonitorTopologyObserved [ sampleMonitor ]) |> ignore
        reducer.Submit 1L "test" (WindowObserved (sampleWindow w { X = 0; Y = 0; Width = 1920; Height = 1080 })) |> ignore
        p
    try
        let entries = Wm.Journal.JournalWriter.readAll path |> List.ofSeq
        Assert.Empty(calibrate entries)
    finally
        if IO.File.Exists path then IO.File.Delete path
