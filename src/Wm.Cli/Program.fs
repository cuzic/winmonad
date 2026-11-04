/// architecture.md §18 Phase A「最小JournalとCLI Explain」：長らく型（`StepResult.Trace`/
/// `DecisionTrace`）だけがあり、実際に読む側のCLIが無かった既知のギャップ（Phase Bで発見・
/// 自己選定）。Replayの駆動そのものは`Replay.fs`（テストから直接呼べる）、ここは表示（`printfn`）と
/// エントリポイントだけを持つ。
module Wm.Cli.Program

open System
open Wm.Contracts.GatedEffect
open Wm.Contracts.JournalTypes
open Wm.Core.WmState
open Wm.Runtime.Reducer
open Wm.Cli.Replay

let private run (path: string) : int =
    let entries = Wm.Journal.JournalWriter.readAll path |> List.ofSeq
    printfn "Journal: %s (%d entries)" path entries.Length
    let reducer = Reducer(RuntimePolicy.defaults, "replay", "wm-cli", "no-policy-artifact", None)
    for entry, srOpt in replayAll reducer entries do
        match srOpt, entry.Payload with
        | Some sr, InputPayload input ->
            printfn "[seq=%d kind=%A t=%d] %A" entry.Sequence entry.Kind entry.MonotonicTicks input
            for g in sr.Effects do
                printfn "  -> Effect: %A" (GatedEffect.effect g)
            for t in sr.Trace do
                printfn "  -> DecisionTrace: %A" t
        | _ ->
            // 9.2の決定6層9（Contractsの型だけで書ける永続化対象）のうち、`InputPayload`以外
            // （`SafetyRecoveryActionPayload`等）はReplayで再現する`step`の入力ではないので、
            // そのまま記録として表示するだけに留める。
            printfn "[seq=%d kind=%A t=%d] (non-replayable payload) %A" entry.Sequence entry.Kind entry.MonotonicTicks entry.Payload

    printfn "\nFinal classification:"
    for KeyValue (w, record) in reducer.State.Windows.Records do
        printfn "  %A -> %A" w record.Classification
    printfn "\nFinal Placement.Pending: %d entries" (Map.count reducer.State.Placement.Pending)
    0

[<EntryPoint>]
let main argv =
    match argv with
    | [| path |] when IO.File.Exists path -> run path
    | [| path |] ->
        eprintfn "File not found: %s" path
        1
    | _ ->
        eprintfn "Usage: wm-cli <journal-file.ndjson>"
        eprintfn "Replays a Wm.Runtime.Reducer journal through Wm.Core.WmState.step, printing each"
        eprintfn "input alongside the Effects/DecisionTrace it produced, plus the final classification."
        1
