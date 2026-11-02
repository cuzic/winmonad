/// Reducerの最小実装と、Simulation版Timer実行器の結線を確認するスモークテスト（ADR-019決定8の前提）。
/// 性質ベーステスト#21そのものはWm.Core.Tests（Topology.updateを直接叩く）が担う。ここでは
/// 「Journalに書く→stepを呼ぶ→TimerCommandを実行器の表へ反映→次のタイマーを仮想時計で発火する」という
/// Reducerの配線が実際に動くことだけを確認する。
module Wm.Core.Tests.ReducerSmokeTests

open System
open System.IO
open Xunit
open Wm.Contracts.Identifiers
open Wm.Contracts.SharedVocabulary
open Wm.Contracts.CoreCommand
open Wm.Core.WmState
open Wm.Runtime.Reducer

let private mkMonitor (id: int) : MonitorSnapshot =
    { Id = MonitorId (int64 id)
      StableKey = sprintf "TEST:%d" id
      Bounds = { X = 0; Y = 0; Width = 1920; Height = 1080 }
      WorkArea = { X = 0; Y = 0; Width = 1920; Height = 1040 }
      Dpi = 96
      IsPrimary = (id = 0) }

[<Fact>]
let ``MonitorTopologyObserved後、タイマーを仮想時計で発火するとGraceへ進みObserveAllが出る`` () =
    let journalPath = Path.Combine(Path.GetTempPath(), sprintf "wm-journal-smoke-%s.ndjson" (Guid.NewGuid().ToString("N")))
    try
        let reducer = Reducer(RuntimePolicy.defaults, "test-session", "test-build", "test-policy-hash", Some journalPath)

        let r1 = reducer.Submit 1000L "test" (MonitorTopologyObserved [ mkMonitor 0 ])
        match Wm.Core.Topology.phase r1.Next.Topology with
        | Settling _ -> ()
        | other -> failwithf "expected Settling, got %A" other

        Assert.True(reducer.Timers.NextDeadline |> Option.isSome)

        let r2 =
            match reducer.AdvanceToNextTimer () with
            | Some r -> r
            | None -> failwith "expected a pending timer to fire"

        match Wm.Core.Topology.phase r2.Next.Topology with
        | Grace _ -> ()
        | other -> failwithf "expected Grace after the settle timer fired, got %A" other

        Assert.Equal<Effect list>([ ObserveAll ], r2.Effects |> List.map Wm.Contracts.GatedEffect.GatedEffect.effect)

        // Journalに入力が書かれ、読み戻せること（9.1 Replay Input）。
        let entries = Wm.Journal.JournalWriter.readAll journalPath |> List.ofSeq
        Assert.Equal(2, entries.Length)
    finally
        if File.Exists journalPath then File.Delete journalPath
