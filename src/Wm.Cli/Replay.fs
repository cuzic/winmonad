/// architecture.md §9.1「判断は永続化せずReplay再構成」：Journalの入力列を同じ`WmState.step`へ
/// 同じ順序で通すだけで、当時の`StepResult`（Effects/DecisionTrace）を再構成する純粋な駆動部分。
/// `Program.fs`の表示（`printfn`）とはあえて分離してある——ここはテストから直接呼べる形にする。
module Wm.Cli.Replay

open System
open Wm.Contracts.Identifiers
open Wm.Contracts.Beliefs
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

    // `settle`は収束するまでEffect→観測の往復を繰り返すが、2つの独立した仮想時計
    // ——Reducerの`Timers`（`PendingDeadline`等）と`VirtualWorld.Scheduled`（`DelayedByMs`等が
    // 積む未発火の観測、`advanceClock`で進める）——は自分では進めない（ScenarioRunner.fs冒頭コメント・
    // VirtualWorld.fs冒頭コメントが明示的に「シナリオ側の責務」としている）。ここが`simulateFromJournal`
    // 自身の責務：どちらの時計が次に早く発火するかを比べ、早い方だけを1つ進めてから再度`settle`する、
    // を両方尽きるまで繰り返す。
    //
    // 当初の実装は`Reducer.Timers`側だけを進めていて`advanceClock`を一度も呼んでいなかった——
    // `DelayedByMs`/`SelfRepositionAfterMs`の遅延観測が永遠に発火しないバグで、`calibrate`の
    // 候補グリッドを実際に試すまで（`Immediate`だけを使う既存の`simulate`では症状が出ない経路だった
    // ため）発覚しなかった。`PendingDeadline`のリトライ上限（既定3回）が偶然どちらの経路でも
    // 最終的にPendingを空にしてしまうため、既存の決定論性テストだけでは検出できなかった
    // （CalibrateTests.fsのミューテーションテストで実際に確認済み）。
    let settleFully (now: int64) (initialEffects: GatedEffect list) =
        world <- settle now reducer world initialEffects
        let mutable more = true
        while more do
            let nextScheduled = world.Scheduled |> List.map (fun s -> s.FireAtMs) |> List.sort |> List.tryHead
            let nextTimerDeadline = reducer.Timers.NextDeadline
            let scheduledFiresFirst =
                match nextScheduled, nextTimerDeadline with
                | Some schedAt, Some timerAt -> schedAt <= timerAt
                | Some _, None -> true
                | None, _ -> false
            if nextScheduled.IsNone && nextTimerDeadline.IsNone then
                more <- false
            elif scheduledFiresFirst then
                let schedAt = nextScheduled.Value
                let world', events = advanceClock schedAt world
                let effects = events |> List.collect (fun ev -> (reducer.Submit schedAt "virtual-world-clock" ev).Effects)
                for ev in events do
                    sb.AppendLine(sprintf "  -> (virtual-clock@%dms) Observed: %A" schedAt ev) |> ignore
                world <- settle schedAt reducer world' effects
            else
                let timerAt = nextTimerDeadline.Value
                match reducer.AdvanceToNextTimer() with
                | Some sr ->
                    for t in sr.Trace do
                        sb.AppendLine(sprintf "  -> (timer@%dms) DecisionTrace: %A" timerAt t) |> ignore
                    world <- settle timerAt reducer world sr.Effects
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

/// architecture.md §12.3「実測モデルの育成」・外部フレームワーク構想§10.3 Model Calibrationの
/// 最小実装：「シミュレーターを実機検証の結果でどんどん精度を上げていくサイクル」という直接の
/// 要望に応える核。
///
/// 当初は`simulateFromJournal`の出力テキストを候補ごとに作り、実機の`golden-save`出力（`formatReplay`
/// 形式）と`diffLines`で比較する案で実装したが、実際に試して2つの問題を発見し（本書の規律：
/// 「ground truth from testing, not assumption」）、この数値直接比較の案へ置き換えた：
/// (1) 2つの関数はそもそも異なるテキスト形式（`formatReplay`は実機の生Journalエントリを逐次ログ、
///     `simulateFromJournal`はbootstrap/exogenousだけを選択的にログ）なので、行diffは形式の違いで
///     即座に全行ズレてしまい、意味のある比較にならない。
/// (2) `settleFully`が両方の仮想時計を尽くすまで回す以上、最終収束状態（分類・Pending件数）は
///     遅延の長さに関わらず同じになりがちで、行diffという「一致/不一致」の二値指標では「どの程度
///     近いか」という連続値（遅延ms）を順位付けできない。
/// 正しい指標は「実測の遅延（ms）」そのものを実機Journalから直接抽出し、候補`WindowBehavior`の
/// 遅延値とどれが一番近いかを数値で比べること——`measuredLatenciesMs`がこの抽出を行う。
let candidateBehaviors : WindowBehavior list =
    [ Immediate
      DelayedByMs 50L
      DelayedByMs 100L
      DelayedByMs 200L
      DelayedByMs 500L
      DelayedByMs 1000L ]

/// 実機Journalを素の`Reducer`へ通し（`formatReplay`と同じ駆動だが、テキスト整形はしない）、
/// 窓ごとに「直近発行された`SetRect`の発行tickと目標矩形」を追跡し、その後その窓について
/// 目標どおりの矩形を運ぶ`WindowObserved`が実際に届いたtickとの差分（ms、Journalの
/// `MonotonicTicks`はms単位、architecture.md §9参照）を返す——Coreが実際に発行したEffectと、
/// それに対する実機の応答という、Journalに記録された「本当に起きたこと」だけから導く実測値
/// （机上の仮定を含まない）。同じ窓に新しい`SetRect`が発行されたら前回分（未達のまま）は捨て、
/// 最後に発行された`SetRect`への応答だけを測る——Retryで新しい`RequestId`が発行された場合も、
/// Simulationの`DelayedByMs`が「1回の発行→1回の遅延応答」を模すのと対応する単位になる。
///
/// **実機Journal（pad2py-pad-hostでの最初の実機検証、v2.112）で見つかった`classifySetRectOutcomes`
/// の実在のバグに合わせた防御的な一貫性修正**：窓の「発見」（最初の`WindowObserved`）自体が
/// Topology settled後であれば、Reconcileが同じ`Submit`呼び出しの中で即座に`SetRect`を発行する
/// （§4.4の発見→Reconcile→SetRectが同期的に起きる、というCoreの実際の設計どおり）。この関数自体は
/// 不一致時に`pendingByWindow`を消さない作りのため、発見時点の矩形を自分自身の`SetRect`への応答と
/// 誤って「一致」判定することは無く（不一致のまま素通りし、後続の本当の応答が同じエントリを
/// 正しく使う——ミューテーションテストで確認済み、該当テストはこの理由で追加しなかった）、実害は
/// 無かった。ただし`classifySetRectOutcomes`は不一致でも`RelocatedElsewhereLike`等で無条件に
/// 消費してしまうため実害があった（そちらのコメント参照）。両者を同じ順序に揃えておく方が、
/// 「不一致時に消費しない」という暗黙の前提に将来の変更が依存しなくて済む。
let measuredLatenciesMs (entries: JournalEntry list) : (WindowId * int64) list =
    let reducer = Reducer(RuntimePolicy.defaults, "calibrate-measure", "wm-cli", "no-policy-artifact", None)
    let mutable pendingByWindow : Map<WindowId, int64 * PixelRect> = Map.empty
    let mutable measured : (WindowId * int64) list = []
    for entry in entries do
        match entry.Payload with
        | InputPayload input ->
            let sr = reducer.Submit entry.MonotonicTicks "calibrate-measure" input
            match input with
            | WindowObserved snapshot ->
                match Map.tryFind snapshot.Id pendingByWindow, snapshot.VisibleFrameRect with
                | Some (issuedAt, target), Some obs ->
                    match obs.Value with
                    | Ok rect when rect = target ->
                        measured <- (snapshot.Id, entry.MonotonicTicks - issuedAt) :: measured
                        pendingByWindow <- Map.remove snapshot.Id pendingByWindow
                    | _ -> ()
                | _ -> ()
            | _ -> ()
            for g in sr.Effects do
                match GatedEffect.effect g with
                | SetRect (_, w, rect) -> pendingByWindow <- Map.add w (entry.MonotonicTicks, rect) pendingByWindow
                | _ -> ()
        | _ -> ()
    List.rev measured

/// 実測遅延（`measuredLatenciesMs`）に数値的に最も近い候補`WindowBehavior`を窓ごとに選ぶ
/// （`Immediate`からの距離は実測値そのもの、`DelayedByMs d`からの距離は`|d - 実測値|`）。
/// 複数回観測された窓は最後の実測値を使う（直近の振る舞いを優先）。
let calibrate (entries: JournalEntry list) : (WindowId * WindowBehavior * int64) list =
    measuredLatenciesMs entries
    |> List.fold (fun acc (w, latencyMs) -> Map.add w latencyMs acc) Map.empty
    |> Map.toList
    |> List.map (fun (w, latencyMs) ->
        let bestFit =
            candidateBehaviors
            |> List.minBy (fun b ->
                match b with
                | Immediate -> abs latencyMs
                | DelayedByMs d -> abs (d - latencyMs)
                | _ -> Int64.MaxValue)
        w, bestFit, latencyMs)
    |> List.sortBy (fun (WindowId id, _, _) -> id)

/// 1回の`SetRect`発行の結末を、実機Journalに実際に記録されたイベントだけから離散カテゴリへ
/// 分類する——`calibrate`が担う「`Immediate`/`DelayedByMs`のどちらで、何msか」という連続値
/// フィッティングとは別腹に、まず「要求とどう違う結末になったか」という振る舞いの型を見つける
/// （§12.3「実測モデルの育成」、ユーザーとの合意：数値精度よりカテゴリ発見の方が実機の
/// WindowBehaviorの欠落を見つける力が強い）。
type SetRectOutcome =
    /// 要求どおりの矩形がそのまま観測された（`measuredLatenciesMs`と同じ判定）。
    | ResolvedExactly of latencyMs: int64
    /// 位置・高さは要求どおりだが、幅だけが要求より大きい値に揃って観測された
    /// （`WindowBehavior.MinimumWidth`の兆候）。
    | WidthFlooredLike of requestedWidth: int * observedWidth: int
    /// 観測された矩形が要求と無関係（位置・高さも違う）
    /// （`WindowBehavior.SelfRepositionAfterMs`の兆候）。
    | RelocatedElsewhereLike of requested: PixelRect * observed: PixelRect
    /// 観測の代わりに`WindowRemoved`が届いた（`WindowBehavior.DisappearOnMove`の兆候）。
    | DisappearedLike
    /// 観測が一切届かないまま、`Health.Responsiveness`が`Hung`へ確定した
    /// （`WindowBehavior.HungAfterMs`の兆候）。
    | HungLike
    /// 観測が一切届かず、`Responsiveness`もまだ`Hung`に確定していない（証拠不足、Journalが
    /// 途中で終わっている等）——既存の6候補のどれにも当てはめず、素直に「わからない」と報告する。
    | Unknown

/// 窓ごとに「最後に発行された`SetRect`」の結末だけを分類する（`measuredLatenciesMs`と同じ
/// 「最後の発行への応答だけを見る」単純化）。
///
/// `measuredLatenciesMs`と同じ理由（その関数直前のコメント参照、pad2py-pad-hostでの最初の実機
/// 検証で発見、v2.112）で、`input`の判定を、このEntry自身が生んだ`SetRect`を積む*前*の
/// `pendingByWindow`に対して行う——発見と同じEntryでReconcileが即座に`SetRect`を発行する場合、
/// 発見時点のまだ移動していない矩形をその`SetRect`への応答と誤認してはならない。
let classifySetRectOutcomes (entries: JournalEntry list) : (WindowId * SetRectOutcome) list =
    let reducer = Reducer(RuntimePolicy.defaults, "calibrate-classify", "wm-cli", "no-policy-artifact", None)
    let mutable pendingByWindow : Map<WindowId, int64 * PixelRect> = Map.empty
    let mutable outcomes : Map<WindowId, SetRectOutcome> = Map.empty
    for entry in entries do
        match entry.Payload with
        | InputPayload input ->
            let sr = reducer.Submit entry.MonotonicTicks "calibrate-classify" input
            match input with
            | WindowObserved snapshot ->
                match Map.tryFind snapshot.Id pendingByWindow, snapshot.VisibleFrameRect with
                | Some (issuedAt, target), Some obs ->
                    match obs.Value with
                    | Ok rect when rect = target ->
                        outcomes <- Map.add snapshot.Id (ResolvedExactly (entry.MonotonicTicks - issuedAt)) outcomes
                        pendingByWindow <- Map.remove snapshot.Id pendingByWindow
                    | Ok rect when rect.X = target.X && rect.Y = target.Y && rect.Height = target.Height && rect.Width <> target.Width ->
                        outcomes <- Map.add snapshot.Id (WidthFlooredLike (target.Width, rect.Width)) outcomes
                        pendingByWindow <- Map.remove snapshot.Id pendingByWindow
                    | Ok rect ->
                        outcomes <- Map.add snapshot.Id (RelocatedElsewhereLike (target, rect)) outcomes
                        pendingByWindow <- Map.remove snapshot.Id pendingByWindow
                    | Error _ -> ()
                | _ -> ()
            | WindowRemoved w when Map.containsKey w pendingByWindow ->
                outcomes <- Map.add w DisappearedLike outcomes
                pendingByWindow <- Map.remove w pendingByWindow
            | _ -> ()
            for g in sr.Effects do
                match GatedEffect.effect g with
                | SetRect (_, w, rect) ->
                    // 新しい発行があったら、前回分の未確定判定は捨てる——最後の発行への応答だけを見る。
                    pendingByWindow <- Map.add w (entry.MonotonicTicks, rect) pendingByWindow
                    outcomes <- Map.remove w outcomes
                | _ -> ()
        | _ -> ()
    // Journalが終わった時点でまだ応答が無かった窓（pendingByWindowに残っている）は、
    // 最終的なResponsiveness信念でHungLike/Unknownへ振り分ける。
    let unresolved =
        pendingByWindow
        |> Map.toList
        |> List.map (fun (w, _) ->
            let outcome =
                match Map.tryFind w reducer.State.Health.Responsiveness with
                | Some belief when belief.Value = Hung -> HungLike
                | _ -> Unknown
            w, outcome)
    outcomes
    |> Map.toList
    |> (fun resolved -> resolved @ unresolved)
    |> List.sortBy (fun (WindowId id, _) -> id)
