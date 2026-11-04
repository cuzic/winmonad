/// architecture.md §9.1「判断は永続化せずReplay再構成」：Journalの入力列を同じ`WmState.step`へ
/// 同じ順序で通すだけで、当時の`StepResult`（Effects/DecisionTrace）を再構成する純粋な駆動部分。
/// `Program.fs`の表示（`printfn`）とはあえて分離してある——ここはテストから直接呼べる形にする。
module Wm.Cli.Replay

open System
open Wm.Contracts.CoreCommand
open Wm.Contracts.GatedEffect
open Wm.Contracts.JournalTypes
open Wm.Core.WmState
open Wm.Runtime.Reducer

/// 1件のJournalEntryをReplayする。`InputPayload`を運ぶ種別（`InputReceived`/`EffectCallReturnedKind`、
/// Reducer.fsの型直前のコメント参照）だけが実際に`step`へ入力でき、他（`SafetyRecoveryAction`等）は
/// Replay対象外として`None`を返す。
let replayEntry (reducer: Reducer) (entry: JournalEntry) : StepResult option =
    match entry.Payload with
    | InputPayload input -> Some (reducer.Submit entry.MonotonicTicks "replay" input)
    | _ -> None

/// Journal全体を1つの新しい`Reducer`へ順に通す。各エントリと、それをReplayできた場合の
/// `StepResult`を対にして返す——呼び出し側（`Program.fs`の表示、またはテスト）がここから
/// 最終状態・Effects・DecisionTraceを読む。
let replayAll (reducer: Reducer) (entries: JournalEntry list) : (JournalEntry * StepResult option) list =
    entries |> List.map (fun e -> e, replayEntry reducer e)

/// Golden Master用：Replayの結果を決定論的な1本のテキストへ整形する。`Wm.Cli`の`explain`/
/// `golden-save`/`golden-check`が全てこの同じ文字列を使う——保存した参照テキストと、後から
/// 再Replayして作った同じ形式のテキストを素直に比較するだけで「この入力列に対する意味的な
/// 振る舞い」の退行を検出できる（§11 Golden Masterの最小実装、v2.106のEffectHarnessリファクタで
/// 実機CIのログを手で読み比べた検証を、そのまま再利用可能な形に起こした）。`journalPath`や
/// 絶対パス等、実行ごとに変わる値は含めない（実行環境に依存しない比較にするため）。
let formatReplay (entries: JournalEntry list) : string =
    let sb = Text.StringBuilder()
    let reducer = Reducer(RuntimePolicy.defaults, "replay", "wm-cli", "no-policy-artifact", None)
    sb.AppendLine(sprintf "entries=%d" entries.Length) |> ignore
    for entry, srOpt in replayAll reducer entries do
        match srOpt, entry.Payload with
        | Some sr, InputPayload input ->
            sb.AppendLine(sprintf "[seq=%d kind=%A] %A" entry.Sequence entry.Kind input) |> ignore
            for g in sr.Effects do
                sb.AppendLine(sprintf "  -> Effect: %A" (GatedEffect.effect g)) |> ignore
            for t in sr.Trace do
                sb.AppendLine(sprintf "  -> DecisionTrace: %A" t) |> ignore
        | _ ->
            sb.AppendLine(sprintf "[seq=%d kind=%A] (non-replayable payload) %A" entry.Sequence entry.Kind entry.Payload) |> ignore
    sb.AppendLine() |> ignore
    sb.AppendLine("Final classification:") |> ignore
    for KeyValue (w, record) in reducer.State.Windows.Records do
        sb.AppendLine(sprintf "  %A -> %A" w record.Classification) |> ignore
    sb.AppendLine() |> ignore
    sb.AppendLine(sprintf "Final Placement.Pending: %d entries" (Map.count reducer.State.Placement.Pending)) |> ignore
    sb.ToString()

/// 2本のテキスト（`formatReplay`の出力）を行単位で比較し、違う行だけを返す（1始まりの行番号・
/// 期待値・実際の値）。行数が異なる場合は、短い方の末尾以降を全て差分として報告する。
let diffLines (expected: string) (actual: string) : (int * string option * string option) list =
    // `Split('\n')`は末尾が改行で終わる文字列に対し、最後に空文字列の要素を1つ余分に作る
    // （"a\n".Split('\n') = [|"a"; ""|]）。`formatReplay`は常に改行で終えるので、この末尾の
    // 空要素を両辺で同じように落としておかないと、本来1行だけの食い違いが2行分の差分として
    // 報告されてしまう（テストで実際に踏んだ）。
    let splitLines (s: string) =
        let lines = s.Replace("\r\n", "\n").Split('\n')
        if lines.Length > 0 && lines.[lines.Length - 1] = "" then lines.[.. lines.Length - 2] else lines
    let expectedLines = splitLines expected
    let actualLines = splitLines actual
    let maxLen = max expectedLines.Length actualLines.Length
    [ for i in 0 .. maxLen - 1 do
        let e = if i < expectedLines.Length then Some expectedLines.[i] else None
        let a = if i < actualLines.Length then Some actualLines.[i] else None
        if e <> a then yield (i + 1, e, a) ]
