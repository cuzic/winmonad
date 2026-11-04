/// architecture.md §9.1「判断は永続化せずReplay再構成」：Journalの入力列を同じ`WmState.step`へ
/// 同じ順序で通すだけで、当時の`StepResult`（Effects/DecisionTrace）を再構成する純粋な駆動部分。
/// `Program.fs`の表示（`printfn`）とはあえて分離してある——ここはテストから直接呼べる形にする。
module Wm.Cli.Replay

open Wm.Contracts.CoreCommand
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
