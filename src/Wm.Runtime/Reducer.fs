/// Wm.Runtime（ADR-019実装順16）：入力キューの処理とJournalへの書込み順序。Windows非依存。
/// architecture.md §3.1/§7.1/§9.4「入力をJournalに書いてから`step`を呼ぶ」。
module Wm.Runtime.Reducer

open System
open Wm.Contracts.CoreCommand
open Wm.Contracts.JournalTypes
open Wm.Core.WmState
open Wm.Runtime.TimerExecutor

/// 最小のReducer：単一の入力キューを順に処理し、各ステップの前に入力をJournalへ書く。
/// Simulation版のTimer実行器を内包し、外部入力が無い間は最も早い期限のタイマーを仮想時計で発火できる。
type Reducer(runtimePolicy: RuntimePolicy, sessionId: string, coreBuildId: string, policyArtifactHash: string, journalPath: string option) =
    let mutable state = initial runtimePolicy
    let mutable sequence = 0L
    let timers = SimulationTimerExecutor()

    member _.State = state
    member _.Timers = timers

    /// §7.1固定順序：「入力をJournalに書いてから`step`を呼ぶ」。これにより`step`実行中に例外を起こした
    /// 入力（poison input）が必ずJournalに残り、Replayで再現できる。
    member _.Submit (now: int64) (source: string) (input: InputEvent) : StepResult =
        let envelope: EventEnvelope<InputEvent> =
            { Sequence = sequence; MonotonicTime = now; Source = source; SessionId = sessionId; Input = input }
        sequence <- sequence + 1L

        journalPath
        |> Option.iter (fun path ->
            let entry =
                { SchemaVersion = 1
                  SessionId = sessionId
                  Sequence = envelope.Sequence
                  MonotonicTicks = now
                  WallTimeUtc = DateTimeOffset.UtcNow
                  CorrelationId = None
                  CausationId = None
                  PolicyArtifactHash = policyArtifactHash
                  CoreBuildId = coreBuildId
                  Kind = InputReceived
                  Payload = InputPayload input }
            Wm.Journal.JournalWriter.appendEntry path entry)

        let result = step runtimePolicy state envelope
        state <- result.Next
        timers.Apply now result.Timers
        result

    /// Simulation駆動：次に発火すべきタイマーがあれば、仮想時計をその時刻へ進めてTimerElapsedを投入する。
    /// 外部入力が尽きたとき（§3.3の規約2「ランタイムがTimerCommandを実行し、発火をTimerElapsed入力として
    /// Reducerのキューへ戻す」）に呼ぶ。
    member this.AdvanceToNextTimer () : StepResult option =
        match timers.PopNext () with
        | None -> None
        | Some (id, fireAt) -> Some (this.Submit fireAt "simulation-timer" (TimerElapsed id))
