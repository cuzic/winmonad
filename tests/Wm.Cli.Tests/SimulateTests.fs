/// architecture.md §12「疑似窓・疑似OS・モデル合成」・外部フレームワーク構想§10.4/§10.5から
/// 持ち込んだ`Wm.Cli.Replay.simulateFromJournal`を検査する：実機Journalの「外部刺激」から
/// `VirtualWorld`をブートストラップし、以降のEffectを実際にSimulationへ適用した結果が、
/// 同じ初期条件を手で`ScenarioRunner`へ直接渡した場合と一致すること（＝実装が謳っている動作を
/// 実際にしていること）、およびタイマーが実機の記録ではなくSimulation自身の時計から生まれる
/// ことを確認する。
module Wm.Cli.Tests.SimulateTests

open System
open Xunit
open Wm.Contracts.Identifiers
open Wm.Contracts.Observation
open Wm.Contracts.CoreCommand
open Wm.Core.WmState
open Wm.Runtime.Reducer
open Wm.Simulation.WindowBehaviors
open Wm.Simulation.VirtualWorld
open Wm.Simulation.ScenarioRunner
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

/// 実機を模した「生のJournal」を1本作る：モニター1台＋初期矩形がWorkAreaと違う窓1つ
/// （SetRectが実際に発行される条件）。タイマーも実際に進めて、本物のラウンド3〜12と同じ形
/// （複数のTimerElapsedエントリを含む）にする——`simulateFromJournal`がこれらを無視することを
/// 検査するのが目的の1つ。
let private buildJournal (w: WindowId) (initialRect: PixelRect) : string =
    let path = IO.Path.Combine(IO.Path.GetTempPath(), sprintf "wm-cli-simulate-test-%s.ndjson" (Guid.NewGuid().ToString("N")))
    let reducer = Reducer(RuntimePolicy.defaults, "test", "test-build", "no-policy-artifact", Some path)
    reducer.Submit 0L "test" (MonitorTopologyObserved [ sampleMonitor ]) |> ignore
    reducer.Submit 1L "test" (WindowObserved (sampleWindow w initialRect)) |> ignore
    let mutable more = true
    while more do
        match reducer.AdvanceToNextTimer() with
        | Some _ -> ()
        | None -> more <- false
    path

[<Fact>]
let ``simulateFromJournalは同じJournalに対して常に同じテキストを返す(決定論性)`` () =
    let w = WindowId 1L
    let path = buildJournal w { X = 0; Y = 0; Width = 300; Height = 200 }
    try
        let entries = Wm.Journal.JournalWriter.readAll path |> List.ofSeq
        let first = simulateFromJournal (fun _ -> Immediate) entries
        let second = simulateFromJournal (fun _ -> Immediate) entries
        Assert.Equal(first, second)
    finally
        if IO.File.Exists path then IO.File.Delete path

[<Fact>]
let ``simulateFromJournalは実機のEffectCallReturned/TimerElapsedを再生せず、自前のVirtualWorldで実際にSetRectへ応答する`` () =
    // 初期矩形(300x200)はWorkArea(1920x1080)と違うので、Coreは必ずSetRectを発行する。
    let w = WindowId 1L
    let path = buildJournal w { X = 0; Y = 0; Width = 300; Height = 200 }
    try
        let entries = Wm.Journal.JournalWriter.readAll path |> List.ofSeq
        let output = simulateFromJournal (fun _ -> Immediate) entries

        // 実機の`TimerElapsed`（`buildJournal`が実際に記録したPendingDeadlineの再試行・ScreenSettle等、
        // VirtualWorldでは満たされないはずの再試行）が出力に一切現れないことを確認する——これが
        // 無ければ、実機のRequestId/タイミングがSimulation側へ混入してしまう（最初の実装で実際に
        // 踏んだ回帰）。最終状態の数値だけを見る下の検査では、このJournalではたまたま無害
        // （期限切れのRequestIdは`Placement`が素通りするだけ）なため検出できない——出力テキスト
        // 自体を見て初めて検出できる。
        Assert.DoesNotContain("TimerElapsed", output)

        // 参照：手でReducer+VirtualWorldを直接駆動した場合の最終状態（simulateFromJournalが
        // 謳っている動作と一致するはずのもの）。
        let reference = Reducer(RuntimePolicy.defaults, "reference", "test-build", "no-policy-artifact", None)
        let srTopology = reference.Submit 0L "test" (MonitorTopologyObserved [ sampleMonitor ])
        let srWindow = reference.Submit 1L "test" (WindowObserved (sampleWindow w { X = 0; Y = 0; Width = 300; Height = 200 }))
        let world = addWindow w { X = 0; Y = 0; Width = 300; Height = 200 } Immediate empty
        let world = settle 1L reference world (srTopology.Effects @ srWindow.Effects)
        let mutable world = world
        let mutable more = true
        while more do
            match reference.AdvanceToNextTimer() with
            | Some sr -> world <- settle 1L reference world sr.Effects
            | None -> more <- false

        Assert.Equal(0, Map.count reference.State.Placement.Pending)
        Assert.Contains("Final Placement.Pending: 0 entries", output)
        match Map.tryFind w reference.State.Windows.Records with
        | Some record -> Assert.Contains(sprintf "Final classification:\n  %A -> %A" w record.Classification, output.Replace("\r\n", "\n"))
        | None -> failwith "reference window was not classified"
    finally
        if IO.File.Exists path then IO.File.Delete path

[<Fact>]
let ``simulateFromJournalはDelayedByMsのScheduled観測をVirtualWorldの仮想時計(advanceClock)で実際に発火させる`` () =
    // 初期実装は`VirtualWorld.Scheduled`（`DelayedByMs`が積む未発火の観測）を一度も`advanceClock`で
    // 進めておらず、遅延観測が永遠に発火しないバグがあった——`Final Placement.Pending`が偶然
    // どちらの経路でも0に収束してしまうため、最終状態だけを見る検査では検出できなかった
    // （ミューテーションテストで実際に確認: このテストの核心である"virtual-clock@"ログを取り除くと
    // 符合する箇所が無くなり、Assert.Containsが落ちる）。`Immediate`は観測が`settle`内で即座に
    // 返るためこのログ行を一切出さないので、両者の出力が一致しないことも併せて確認する。
    let w = WindowId 1L
    let path = buildJournal w { X = 0; Y = 0; Width = 300; Height = 200 }
    try
        let entries = Wm.Journal.JournalWriter.readAll path |> List.ofSeq
        let delayed = simulateFromJournal (fun _ -> DelayedByMs 100L) entries
        let immediate = simulateFromJournal (fun _ -> Immediate) entries

        Assert.Contains("virtual-clock@", delayed)
        Assert.DoesNotContain("virtual-clock@", immediate)
        Assert.NotEqual<string>(immediate, delayed)
    finally
        if IO.File.Exists path then IO.File.Delete path
