/// Wm.Runtime：Timer実行器のSimulation版（ADR-019決定8の前提）。architecture.md §3.3/§17。
/// 「StartTimer/CancelTimerを処理する小さな表」はContracts.Timers.applyTimerCommandsが純粋ロジックとして持つ。
/// ここでは、その表を保持し、Windows版（Hostのタイマーサービス）の代わりに仮想時計で発火させる。
module Wm.Runtime.TimerExecutor

open Wm.Contracts.Timers

/// 仮想時計駆動のタイマー実行器。発火すると表から取り除かれる（StartTimerで再始動するまで動かない）。
type SimulationTimerExecutor() =
    let mutable table: Map<TimerId, int64> = Map.empty

    member _.Table = table

    member _.Apply (now: int64) (commands: TimerCommand<TimerId> list) : unit =
        table <- applyTimerCommands now commands table

    /// 次に発火すべきタイマーの時刻。表が空ならNone。
    member _.NextDeadline : int64 option =
        if Map.isEmpty table then None else table |> Map.toList |> List.map snd |> List.min |> Some

    /// 最も早い期限のタイマーを表から取り除いて返す（複数が同時刻なら構造的に最小のTimerIdを選ぶ。
    /// Replay決定性のため、暗黙の列挙順ではなく明示的なソートで選ぶ、§7.1）。
    member _.PopNext () : (TimerId * int64) option =
        match table |> Map.toList with
        | [] -> None
        | entries ->
            let minDeadline = entries |> List.map snd |> List.min
            let id =
                entries
                |> List.filter (fun (_, t) -> t = minDeadline)
                |> List.map fst
                |> List.sort
                |> List.head
            table <- Map.remove id table
            Some (id, minDeadline)
