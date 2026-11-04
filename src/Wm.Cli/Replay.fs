/// architecture.md §9.1「判断は永続化せずReplay再構成」：Journalの入力列を同じ`WmState.step`へ
/// 同じ順序で通すだけで、当時の`StepResult`（Effects/DecisionTrace）を再構成する純粋な駆動部分。
/// `Program.fs`の表示（`printfn`）とはあえて分離してある——ここはテストから直接呼べる形にする。
module Wm.Cli.Replay

open System
open Wm.Contracts.Identifiers
open Wm.Contracts.Observation
open Wm.Contracts.CoreCommand
open Wm.Contracts.GatedEffect
open Wm.Contracts.JournalTypes
open Wm.Core.WmState
open Wm.Runtime.Reducer
open Wm.Simulation.WindowBehaviors
open Wm.Simulation.VirtualWorld
open Wm.Simulation.ScenarioRunner

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

/// architecture.md §12「疑似窓・疑似OS・モデル合成」・外部フレームワーク構想の§10.4
/// Differential Analysis／§10.5 Counterfactual Testingから持ち込んだ最小実装。実機Journalの
/// 「外部刺激」（`MonitorTopologyObserved`/`UserCommand`/`ForegroundObserved`等）はそのまま
/// 再投入するが、各窓の最初の`WindowObserved`（発見）だけを使って`VirtualWorld`へ窓を1つ追加し、
/// 以降のその窓の再観測と`EffectCallReturned`は実機の記録を再生せず捨てる——代わりに、Coreが
/// 発行したEffectを`ScenarioRunner.settle`で実際に`VirtualWorld`へ適用し、その応答を新しい
/// `Reducer`へ投入する。つまり「実際に何が起きたか」ではなく「`behaviorOf`で与えた単純な
/// Simulationモデルだったら何が起きていたか」を再構成する。これを`golden-save`で保存した実機の
/// `formatReplay`出力と`diffLines`で比較すれば、「モデルの仮定が現実と食い違っている箇所」を
/// 直接指し示せる（§10.4の`World Model Defect`の特定）。`settle`自体は仮想タイマー
/// （`PendingDeadline`等）を進めないため（ScenarioRunner.fsの型直前のコメント参照）、ここでは
/// `AdvanceToNextTimer`も明示的に回す。
let simulateFromJournal (behaviorOf: WindowId -> WindowBehavior) (entries: JournalEntry list) : string =
    let sb = Text.StringBuilder()
    let reducer = Reducer(RuntimePolicy.defaults, "simulate", "wm-cli", "no-policy-artifact", None)
    let mutable world = empty
    let mutable bootstrapped : Set<WindowId> = Set.empty

    let logInputAndEffects (entry: JournalEntry) (input: InputEvent) (sr: StepResult) =
        sb.AppendLine(sprintf "[seq=%d kind=%A] %A" entry.Sequence entry.Kind input) |> ignore
        for g in sr.Effects do
            sb.AppendLine(sprintf "  -> Effect: %A" (GatedEffect.effect g)) |> ignore
        for t in sr.Trace do
            sb.AppendLine(sprintf "  -> DecisionTrace: %A" t) |> ignore

    // `settle`は収束するまでEffect→観測の往復を繰り返すが、仮想タイマー（`PendingDeadline`等、
    // Reconcileが新しいEffectを発行する契機になる）は自分では進めない。タイマーが尽きるまで
    // `AdvanceToNextTimer`→`settle`を交互に回す（固定点まで、Reducer自身の`maxSettleSteps`相当の
    // 上限は`AdvanceToNextTimer`がタイマー表を使い切った時点で自然に止まる）。
    let settleFully (now: int64) (initialEffects: GatedEffect list) =
        world <- settle now reducer world initialEffects
        let mutable more = true
        while more do
            match reducer.AdvanceToNextTimer() with
            | Some sr -> world <- settle now reducer world sr.Effects
            | None -> more <- false

    let bestRect (snapshot: WindowSnapshot) : PixelRect =
        let ok (o: Observation<PixelRect> option) = o |> Option.bind (fun ob -> match ob.Value with Ok r -> Some r | Error _ -> None)
        ok snapshot.VisibleFrameRect
        |> Option.orElse (ok snapshot.NativeRect)
        |> Option.defaultValue { X = 0; Y = 0; Width = 300; Height = 200 }

    for entry in entries do
        match entry.Payload with
        | InputPayload (WindowObserved snapshot) when not (Set.contains snapshot.Id bootstrapped) ->
            bootstrapped <- Set.add snapshot.Id bootstrapped
            let behavior = behaviorOf snapshot.Id
            world <- addWindow snapshot.Id (bestRect snapshot) behavior world
            sb.AppendLine(sprintf "[bootstrap] window=%A rect=%A behavior=%A" snapshot.Id (bestRect snapshot) behavior) |> ignore
            let sr = reducer.Submit entry.MonotonicTicks "simulate-bootstrap" (WindowObserved snapshot)
            logInputAndEffects entry (WindowObserved snapshot) sr
            settleFully entry.MonotonicTicks sr.Effects
        | InputPayload (WindowObserved _) ->
            () // 既知の窓の再観測は実機の記録を再生しない——Simulationが自分で作り直す。
        | InputPayload (EffectCallReturned _) ->
            () // Effectの応答はVirtualWorld経由で再構成するので、実機の記録はそのまま使わない。
        | InputPayload (TimerElapsed _) ->
            // タイマーは「外部刺激」ではなく、各世界（実機／Simulation）が自分の時計とPending表から
            // 独自に生むものなので、ここでも再生しない——`settleFully`が自分の`reducer`の
            // `AdvanceToNextTimer`を直接回して、Simulationの世界線のタイマーを独自に尽くす。
            // 実機の記録をそのまま再生すると、実機側のRequestId/タイミングでSimulation側のPending
            // 表を叩いてしまい（多くは単に無視されるだけだが、意味的には無関係な混入になる）、
            // 「どちらの世界のタイマーか」が食い違う（最初の実装でこの混入を実際に踏んだ）。
            ()
        | InputPayload input ->
            let sr = reducer.Submit entry.MonotonicTicks "simulate-exogenous" input
            logInputAndEffects entry input sr
            settleFully entry.MonotonicTicks sr.Effects
        | other ->
            sb.AppendLine(sprintf "[seq=%d kind=%A] (non-replayable payload) %A" entry.Sequence entry.Kind other) |> ignore

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
