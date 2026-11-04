/// architecture.md §18 Phase A「最小JournalとCLI Explain」：長らく型（`StepResult.Trace`/
/// `DecisionTrace`）だけがあり、実際に読む側のCLIが無かった既知のギャップ（Phase Bで発見・
/// 自己選定、v2.105で解消）。
///
/// `golden-save`/`golden-check`（v2.107で追加）：AIデバッグ・リファクタリング支援フレームワーク
/// 構想（§11 Golden Master）との比較検討から持ち込んだ、Golden Masterの最小実装。v2.106の
/// `EffectHarness`リファクタで実際にやった検証（実機CIのログを再実行し、各ラウンドの正否判定行を
/// 目で読み比べる）を、再利用可能な形に起こした——`formatReplay`が作る決定論的なテキストを
/// 保存・比較するだけで、同じJournal入力列に対する意味的な振る舞いの退行を検出できる。
/// Replayの駆動そのものは`Replay.fs`（テストから直接呼べる）、ここは表示（`printfn`）と
/// エントリポイントだけを持つ。
///
/// `simulate`（v2.109で追加）：外部フレームワーク構想（§10.4 Differential Analysis／§10.5
/// Counterfactual Testing）から持ち込んだ最小実装。実機Journalの「外部刺激」はそのまま再投入し、
/// 各窓の発見だけをSimulationへ引き継いで、以降はCoreが発行したEffectを実際に`VirtualWorld`へ
/// 適用する——「本当に起きたこと」ではなく「単純なSimulationモデルだったら何が起きていたか」を
/// 再構成する。`golden-save`で保存した実機の結果と`diffLines`で比較すれば、モデルの仮定が現実と
/// 食い違う箇所を直接指し示せる。
module Wm.Cli.Program

open System
open Wm.Contracts.JournalTypes
open Wm.Simulation.WindowBehaviors
open Wm.Cli.Replay

let private loadEntries (path: string) : JournalEntry list =
    Wm.Journal.JournalWriter.readAll path |> List.ofSeq

let private explain (path: string) : int =
    let entries = loadEntries path
    printfn "Journal: %s (%d entries)" path entries.Length
    printf "%s" (formatReplay entries)
    0

let private goldenSave (journalPath: string) (goldenPath: string) : int =
    let entries = loadEntries journalPath
    IO.File.WriteAllText(goldenPath, formatReplay entries)
    printfn "Saved golden master (%d entries replayed) to %s" entries.Length goldenPath
    0

let private goldenCheck (journalPath: string) (goldenPath: string) : int =
    let entries = loadEntries journalPath
    let actual = formatReplay entries
    let expected = IO.File.ReadAllText(goldenPath)
    match diffLines expected actual with
    | [] ->
        printfn "OK: replay matches golden master %s (%d entries)" goldenPath entries.Length
        0
    | diffs ->
        printfn "MISMATCH: replay diverged from golden master %s (%d line(s) differ)" goldenPath diffs.Length
        for (lineNo, expectedLine, actualLine) in diffs do
            printfn "  line %d:" lineNo
            printfn "    golden : %s" (expectedLine |> Option.defaultValue "<missing>")
            printfn "    replay : %s" (actualLine |> Option.defaultValue "<missing>")
        1

let private simulate (journalPath: string) (goldenPath: string option) : int =
    let entries = loadEntries journalPath
    let actual = simulateFromJournal (fun _ -> Immediate) entries
    match goldenPath with
    | None ->
        printf "%s" actual
        0
    | Some golden ->
        let expected = IO.File.ReadAllText(golden)
        match diffLines expected actual with
        | [] ->
            printfn "OK: simulated run (Immediate behavior) matches %s (%d entries)" golden entries.Length
            0
        | diffs ->
            printfn "DIVERGED: simulated run (Immediate behavior) differs from %s (%d line(s) differ)" golden diffs.Length
            printfn "This means the real world did NOT behave like the simple 'Immediate' model assumes --"
            printfn "a candidate World Model Defect, not necessarily a Core bug."
            for (lineNo, expectedLine, actualLine) in diffs do
                printfn "  line %d:" lineNo
                printfn "    real (golden) : %s" (expectedLine |> Option.defaultValue "<missing>")
                printfn "    simulated     : %s" (actualLine |> Option.defaultValue "<missing>")
            1

let private usage () =
    eprintfn "Usage:"
    eprintfn "  wm-cli explain <journal-file.ndjson>"
    eprintfn "      Replays a Wm.Runtime.Reducer journal through Wm.Core.WmState.step, printing"
    eprintfn "      each input alongside the Effects/DecisionTrace it produced, plus final state."
    eprintfn "  wm-cli golden-save <journal-file.ndjson> <golden-file.txt>"
    eprintfn "      Replays the journal and saves the formatted result as a reference (Golden Master)."
    eprintfn "  wm-cli golden-check <journal-file.ndjson> <golden-file.txt>"
    eprintfn "      Replays the journal and compares against a previously saved reference,"
    eprintfn "      reporting any semantic divergence (non-zero exit on mismatch)."
    eprintfn "  wm-cli simulate <journal-file.ndjson> [golden-file.txt]"
    eprintfn "      Re-runs the journal's exogenous inputs against a fresh Simulated World"
    eprintfn "      (Immediate/ideal window behavior) instead of replaying the recorded real"
    eprintfn "      Effect results. Without a golden file, prints the simulated result; with one,"
    eprintfn "      diffs against it to reveal where the real world diverged from the simple model."
    1

let private requireFile (label: string) (path: string) : bool =
    if IO.File.Exists path then true
    else
        eprintfn "File not found (%s): %s" label path
        false

[<EntryPoint>]
let main argv =
    match argv with
    | [| "explain"; path |] -> if requireFile "journal" path then explain path else 1
    | [| "golden-save"; journalPath; goldenPath |] -> if requireFile "journal" journalPath then goldenSave journalPath goldenPath else 1
    | [| "golden-check"; journalPath; goldenPath |] ->
        if requireFile "journal" journalPath && requireFile "golden" goldenPath then goldenCheck journalPath goldenPath else 1
    | [| "simulate"; journalPath |] -> if requireFile "journal" journalPath then simulate journalPath None else 1
    | [| "simulate"; journalPath; goldenPath |] ->
        if requireFile "journal" journalPath && requireFile "golden" goldenPath then simulate journalPath (Some goldenPath) else 1
    | [| path |] -> if requireFile "journal" path then explain path else 1 // 後方互換：v2.105時点の既定動作
    | _ -> usage ()
