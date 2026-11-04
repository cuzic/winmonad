/// architecture.md §12.3「実測モデルの育成」から持ち込んだ`Wm.Cli.Replay.classifySetRectOutcomes`を
/// 検査する：`calibrate`の数値フィッティングとは別腹に、「要求とどう違う結末になったか」という
/// 離散的な振る舞いの型（`SetRectOutcome`の6ケース）が、実機Journalに実際に記録されたイベントだけ
/// から正しく判定できることを確認する。
module Wm.Cli.Tests.ClassifyTests

open System
open Xunit
open Wm.Contracts.Identifiers
open Wm.Contracts.Observation
open Wm.Contracts.ErrorTypes
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

/// モニター1台＋初期矩形がWorkAreaと違う窓1つ（必ず`SetRect`が発行される）を持つ`Reducer`を
/// 用意し、実際に発行された`SetRect`（タイマー経由でずれて発行される場合も`reducer.Timers.NextDeadline`
/// で追う）の発行tick・RequestId・目標矩形を返す。呼び出し側はこの`reducer`へ続きのイベント
/// （観測/消滅/EffectCallReturned等）を1件足して`path`へ閉じ、`classifySetRectOutcomes`に渡す。
let private buildReducerWithDispatchedSetRect (w: WindowId) (initialRect: PixelRect) : Reducer * string * int64 * RequestId * PixelRect =
    let path = IO.Path.Combine(IO.Path.GetTempPath(), sprintf "wm-cli-classify-test-%s.ndjson" (Guid.NewGuid().ToString("N")))
    let reducer = Reducer(RuntimePolicy.defaults, "test", "test-build", "no-policy-artifact", Some path)
    reducer.Submit 0L "test" (MonitorTopologyObserved [ sampleMonitor ]) |> ignore
    let sr0 = reducer.Submit 1L "test" (WindowObserved (sampleWindow w initialRect))
    let mutable setRectInfo : (int64 * RequestId * PixelRect) option = None
    let tryCapture (tick: int64) (effects: GatedEffect list) =
        if setRectInfo.IsNone then
            effects
            |> List.tryPick (fun g -> match GatedEffect.effect g with SetRect (rid, _, rect) -> Some (rid, rect) | _ -> None)
            |> Option.iter (fun (rid, rect) -> setRectInfo <- Some (tick, rid, rect))
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
    | Some (issuedAt, rid, targetRect) -> reducer, path, issuedAt, rid, targetRect
    | None -> failwith "fixture did not produce a SetRect"

let private runClassify (path: string) : (WindowId * SetRectOutcome) list =
    let entries = Wm.Journal.JournalWriter.readAll path |> List.ofSeq
    classifySetRectOutcomes entries

[<Fact>]
let ``目標どおりの矩形が観測されるとResolvedExactlyへ分類される`` () =
    let w = WindowId 1L
    let reducer, path, issuedAt, _, targetRect = buildReducerWithDispatchedSetRect w { X = 0; Y = 0; Width = 300; Height = 200 }
    try
        reducer.Submit (issuedAt + 150L) "test" (WindowObserved (sampleWindow w targetRect)) |> ignore
        match runClassify path with
        | [ (resultWindow, ResolvedExactly latencyMs) ] ->
            Assert.Equal(w, resultWindow)
            Assert.Equal(150L, latencyMs)
        | other -> failwithf "expected a single ResolvedExactly, got %A" other
    finally
        if IO.File.Exists path then IO.File.Delete path

[<Fact>]
let ``位置と高さは要求どおりだが幅だけ違う観測はWidthFlooredLikeへ分類される`` () =
    let w = WindowId 1L
    let reducer, path, issuedAt, _, targetRect = buildReducerWithDispatchedSetRect w { X = 0; Y = 0; Width = 300; Height = 200 }
    try
        let observedWiderRect = { targetRect with Width = targetRect.Width + 400 }
        reducer.Submit (issuedAt + 50L) "test" (WindowObserved (sampleWindow w observedWiderRect)) |> ignore
        match runClassify path with
        | [ (resultWindow, WidthFlooredLike (requested, observed)) ] ->
            Assert.Equal(w, resultWindow)
            Assert.Equal(targetRect.Width, requested)
            Assert.Equal(observedWiderRect.Width, observed)
        | other -> failwithf "expected a single WidthFlooredLike, got %A" other
    finally
        if IO.File.Exists path then IO.File.Delete path

[<Fact>]
let ``矩形全体が要求と無関係な観測はRelocatedElsewhereLikeへ分類される`` () =
    let w = WindowId 1L
    let reducer, path, issuedAt, _, targetRect = buildReducerWithDispatchedSetRect w { X = 0; Y = 0; Width = 300; Height = 200 }
    try
        let unrelatedRect = { X = targetRect.X + 500; Y = targetRect.Y + 10; Width = 640; Height = 480 }
        reducer.Submit (issuedAt + 50L) "test" (WindowObserved (sampleWindow w unrelatedRect)) |> ignore
        match runClassify path with
        | [ (resultWindow, RelocatedElsewhereLike (requested, observed)) ] ->
            Assert.Equal(w, resultWindow)
            Assert.Equal(targetRect, requested)
            Assert.Equal(unrelatedRect, observed)
        | other -> failwithf "expected a single RelocatedElsewhereLike, got %A" other
    finally
        if IO.File.Exists path then IO.File.Delete path

[<Fact>]
let ``SetRect発行後にWindowRemovedが来るとDisappearedLikeへ分類される`` () =
    let w = WindowId 1L
    let reducer, path, _, _, _ = buildReducerWithDispatchedSetRect w { X = 0; Y = 0; Width = 300; Height = 200 }
    try
        reducer.Submit 999L "test" (WindowRemoved w) |> ignore
        Assert.Equal<(WindowId * SetRectOutcome) list>([ (w, DisappearedLike) ], runClassify path)
    finally
        if IO.File.Exists path then IO.File.Delete path

[<Fact>]
let ``応答が無くResponsivenessがHungへ確定するとHungLikeへ分類される`` () =
    let w = WindowId 1L
    let reducer, path, _, rid, _ = buildReducerWithDispatchedSetRect w { X = 0; Y = 0; Width = 300; Height = 200 }
    try
        // §8.1の対応表：(SendMessageTimeout, Win32LastError, 1460)でElapsedUs<=0は判定済みTargetHung。
        let hungRecord : NativeCallRecord = { Api = "SendMessageTimeout"; Space = Win32LastError; Code = 1460; ElapsedUs = 0L }
        reducer.Submit 999L "test" (EffectCallReturned (rid, Error hungRecord, None)) |> ignore
        Assert.Equal<(WindowId * SetRectOutcome) list>([ (w, HungLike) ], runClassify path)
    finally
        if IO.File.Exists path then IO.File.Delete path

[<Fact>]
let ``応答もHung確定も無いままJournalが終わるとUnknownへ分類される`` () =
    let w = WindowId 1L
    let _, path, _, _, _ = buildReducerWithDispatchedSetRect w { X = 0; Y = 0; Width = 300; Height = 200 }
    try
        Assert.Equal<(WindowId * SetRectOutcome) list>([ (w, Unknown) ], runClassify path)
    finally
        if IO.File.Exists path then IO.File.Delete path

[<Fact>]
let ``classifySetRectOutcomesは同じJournalに対して常に同じ結果を返す(決定論性)`` () =
    let w = WindowId 1L
    let reducer, path, issuedAt, _, targetRect = buildReducerWithDispatchedSetRect w { X = 0; Y = 0; Width = 300; Height = 200 }
    try
        reducer.Submit (issuedAt + 75L) "test" (WindowObserved (sampleWindow w targetRect)) |> ignore
        let entries = Wm.Journal.JournalWriter.readAll path |> List.ofSeq
        Assert.Equal<(WindowId * SetRectOutcome) list>(classifySetRectOutcomes entries, classifySetRectOutcomes entries)
    finally
        if IO.File.Exists path then IO.File.Delete path
