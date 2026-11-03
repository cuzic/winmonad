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
        // v2.82：MonitorTopologyObserved/TimerElapsedはどちらもInputReceivedのまま(回帰確認)。
        Assert.True(entries |> List.forall (fun e -> e.Kind = Wm.Contracts.JournalTypes.InputReceived))
    finally
        if File.Exists journalPath then File.Delete journalPath

/// v2.82：`Reducer.Submit`が`EffectCallReturned`を受け取った時だけ`Kind`を`EffectCallReturnedKind`へ
/// 分類し、`Payload`は無損失な`InputPayload`のままにすることを確認する（専用の`EffectCallReturnedPayload`
/// だと`dispatchSeq`を失い`Mismatch → Reevaluate`がReplayで食い違う、`Reducer.fs`の型直前のコメント参照）。
[<Fact>]
let ``EffectCallReturnedを投入すると、JournalのKindがEffectCallReturnedKindになり、dispatchSeqを含めて無損失に往復する(v2.82)`` () =
    let journalPath = Path.Combine(Path.GetTempPath(), sprintf "wm-journal-smoke-%s.ndjson" (Guid.NewGuid().ToString("N")))
    try
        let reducer = Reducer(RuntimePolicy.defaults, "test-session", "test-build", "test-policy-hash", Some journalPath)
        reducer.Submit 1000L "test" (MonitorTopologyObserved [ mkMonitor 0 ]) |> ignore
        reducer.Submit 1001L "test" (EffectCallReturned (RequestId 42L, Ok (), Some 7L)) |> ignore

        let entries = Wm.Journal.JournalWriter.readAll journalPath |> List.ofSeq
        match entries with
        | [ _; effectEntry ] ->
            Assert.Equal(Wm.Contracts.JournalTypes.EffectCallReturnedKind, effectEntry.Kind)
            match effectEntry.Payload with
            | Wm.Contracts.JournalTypes.InputPayload (EffectCallReturned (RequestId rid, Ok (), Some seq)) ->
                Assert.Equal(42L, rid)
                Assert.Equal(7L, seq) // dispatchSeqが欠落していない
            | other -> Assert.True(false, sprintf "unexpected payload: %A" other)
        | other -> Assert.True(false, sprintf "expected exactly 2 entries, got %A" other)
    finally
        if File.Exists journalPath then File.Delete journalPath
