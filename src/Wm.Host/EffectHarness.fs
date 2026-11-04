/// architecture.md §7.1/ADR-012「Effect適用は専用スレッドで行う」の最小限の共通実装。
///
/// ラウンド3〜12で5回ほぼ同じ形（専用Effect適用スレッド・`RequestId`↔結果の往復・実時間ゲート付き
/// `pump`）に複製されていたボイラープレートを1つにまとめる（コード品質の簡素化ラウンドで自己選定、
/// v2.89「`WmState.step`内の重複配線を`allocateAndEmit`ヘルパーへ集約した」と同じ判断）。挙動は
/// 変えない——各ラウンドの個別のカウンタ・ログ・副作用追跡（ラウンド12の`lastHiddenHwnds`等）は
/// `onDispatch`/`onSkip`フックで引き続き呼び出し側が持つ。
module Wm.Host.EffectHarness

open System.Collections.Concurrent
open System.Threading
open System.Windows.Forms
open Wm.Contracts.Identifiers
open Wm.Contracts.ErrorTypes
open Wm.Contracts.CoreCommand
open Wm.Contracts.GatedEffect
open Wm.Core.WmState
open Wm.Runtime.Reducer

type EffectHarness(reducer: Reducer, liveNow: unit -> int64, ?onDispatch: Effect -> Result<unit, NativeCallRecord> -> unit, ?onSkip: unit -> unit) =
    let effectQueue = ConcurrentQueue<GatedEffect>()
    let resultQueue = ConcurrentQueue<RequestId * Result<unit, NativeCallRecord>>()
    let mutable keepRunning = true

    /// `EffectDispatch.toDispatch`が実際に発行する5種だけが`RequestId`を持つ（Native.fs/EffectDispatch.fs
    /// 参照）。これ以外（`Reprobe`/`ForgetWindow`/`ObserveAll`/`ApplyBatch`）は`apply`自体が`None`を
    /// 返すので、ここには到達しない。
    let ridOf (effect: Effect) : RequestId option =
        match effect with
        | SetRect (rid, _, _) | RequestForeground (rid, _) | RestoreBeforeMove (rid, _)
        | RequestClose (rid, _) | SetPresence (rid, _, _) -> Some rid
        | _ -> None

    let thread =
        Thread(fun () ->
            while keepRunning do
                match effectQueue.TryDequeue() with
                | true, gated ->
                    let effect = GatedEffect.effect gated
                    match Wm.Windows.EffectDispatch.apply effect with
                    | Some result ->
                        onDispatch |> Option.iter (fun f -> f effect result)
                        match ridOf effect with
                        | Some rid -> resultQueue.Enqueue(rid, result)
                        | None -> ()
                    | None -> onSkip |> Option.iter (fun f -> f ())
                | false, _ -> Thread.Sleep(1))

    do
        thread.IsBackground <- true
        thread.Start()

    member private _.Enqueue(effects: GatedEffect list) = for g in effects do effectQueue.Enqueue(g)

    /// `Reducer.Submit`を呼び、生まれたEffectsを適用スレッドへ渡す。Reducerへの`Submit`呼出は
    /// このメインスレッドからだけ行う（Coreの単一スレッドアクセスの不変条件、ADR-012）。
    member this.Submit (now: int64) (source: string) (input: InputEvent) : StepResult =
        let sr = reducer.Submit now source input
        this.Enqueue sr.Effects
        sr

    member this.Advance() : bool =
        match reducer.AdvanceToNextTimer() with
        | Some sr -> this.Enqueue sr.Effects; true
        | None -> false

    member this.DrainResults(now: int64) =
        let mutable more = true
        while more do
            match resultQueue.TryDequeue() with
            | true, (rid, result) -> this.Submit now "wm-host-effect-thread" (EffectCallReturned (rid, result, None)) |> ignore
            | false, _ -> more <- false

    /// ラウンド2の`pump`以来の規律（ラウンド8の実機ログ調査で実際に破られていたと判明した、
    /// `pump3`導入時の既知のバグの修正点）：実時間でまだ来ていないタイマーは発火させない。
    member this.Pump(ms: int) =
        let deadline = liveNow () + int64 ms
        while liveNow () < deadline do
            Application.DoEvents()
            this.DrainResults(liveNow ())
            while reducer.Timers.NextDeadline |> Option.exists (fun d -> d <= liveNow ()) do
                this.Advance () |> ignore
            Thread.Sleep(1)

    member _.Stop() =
        keepRunning <- false
        thread.Join(2000) |> ignore
