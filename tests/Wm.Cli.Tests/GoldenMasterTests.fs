/// architecture.md §11 Golden Masterとの比較検討（AIデバッグ・リファクタリング支援フレームワーク
/// 構想）から持ち込んだ最小実装を検査する：`formatReplay`が同じ入力に対して毎回同じテキストを
/// 返すこと（Golden Masterとして保存・比較する前提）、`diffLines`が実際に差分を検出できることを
/// 直接確認する。
module Wm.Cli.Tests.GoldenMasterTests

open System
open Xunit
open Wm.Contracts.Identifiers
open Wm.Contracts.Observation
open Wm.Contracts.CoreCommand
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

let private buildSampleJournal () : string =
    let path = IO.Path.Combine(IO.Path.GetTempPath(), sprintf "wm-cli-golden-test-%s.ndjson" (Guid.NewGuid().ToString("N")))
    let reducer = Reducer(RuntimePolicy.defaults, "test", "test-build", "no-policy-artifact", Some path)
    reducer.Submit 0L "test" (MonitorTopologyObserved [ sampleMonitor ]) |> ignore
    let mutable more = true
    while more do
        match reducer.AdvanceToNextTimer() with
        | Some _ -> ()
        | None -> more <- false
    path

[<Fact>]
let ``formatReplayは同じJournalに対して常に同じテキストを返す(決定論性、9.1の前提)`` () =
    let path = buildSampleJournal ()
    try
        let entries = Wm.Journal.JournalWriter.readAll path |> List.ofSeq
        let first = formatReplay entries
        let second = formatReplay entries
        Assert.Equal(first, second)
    finally
        if IO.File.Exists path then IO.File.Delete path

[<Fact>]
let ``diffLinesは完全一致なら空リストを返す`` () =
    let text = "a\nb\nc\n"
    Assert.Empty(diffLines text text)

[<Fact>]
let ``diffLinesは1行だけ違う場合その行番号と両方の値を報告する`` () =
    let expected = "a\nb\nc\n"
    let actual = "a\nX\nc\n"
    match diffLines expected actual with
    | [ (lineNo, Some e, Some a) ] ->
        Assert.Equal(2, lineNo)
        Assert.Equal("b", e)
        Assert.Equal("X", a)
    | other -> failwithf "expected exactly one diff at line 2, got %A" other

[<Fact>]
let ``diffLinesは行数が異なる場合も余分な行を差分として報告する`` () =
    let expected = "a\nb\n"
    let actual = "a\nb\nc\n"
    match diffLines expected actual with
    | [ (3, None, Some "c") ] -> ()
    | other -> failwithf "expected a single trailing-line diff at line 3, got %A" other
