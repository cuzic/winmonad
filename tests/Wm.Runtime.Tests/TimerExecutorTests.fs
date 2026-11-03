/// `Wm.Contracts.Timers.applyTimerCommands`（StartTimer/CancelTimerの純粋ロジック）と
/// `Wm.Runtime.TimerExecutor.SimulationTimerExecutor`（仮想時計駆動の実行器）には、このラウンドまで
/// 直接のユニットテストが一度も無かった——`ReducerSmokeTests.fs`（唯一の既存テスト）は`Reducer`経由で
/// 1本のタイマーの単純な発火しか通していない。両方とも全てのSimulationシナリオが間接的に依存する
/// 小さいが土台のロジックであり、特に`PopNext`の同時刻タイブレーク（「構造的に最小のTimerIdを選ぶ、
/// Replay決定性のため」、`TimerExecutor.fs`のドキュメンテーションコメント参照）は、このセッションが
/// 繰返し発見してきた種類（§15.1 #11/#25）の決定性バグが潜みやすい箇所である。
module Wm.Core.Tests.TimerExecutorTests

open Xunit
open Wm.Contracts.Identifiers
open Wm.Contracts.Timers
open Wm.Runtime.TimerExecutor

// ===== applyTimerCommands（純粋ロジック） =====

[<Fact>]
let ``StartTimerは新しいdeadline(now+durationMs)で表に追加する`` () =
    let id = TimerId.Topology ScreenSettle
    let table = applyTimerCommands 100L [ StartTimer (id, 50L) ] Map.empty
    Assert.Equal(Some 150L, Map.tryFind id table)

[<Fact>]
let ``同じIDへの2回目のStartTimerは古いdeadlineを上書きする(再始動、debounce)`` () =
    let id = TimerId.Topology ScreenSettle
    let table = applyTimerCommands 100L [ StartTimer (id, 50L) ] Map.empty
    let table' = applyTimerCommands 200L [ StartTimer (id, 50L) ] table
    Assert.Equal(Some 250L, Map.tryFind id table')

[<Fact>]
let ``CancelTimerは表からそのIDを除く`` () =
    let id = TimerId.Topology ScreenSettle
    let table = applyTimerCommands 100L [ StartTimer (id, 50L) ] Map.empty
    let table' = applyTimerCommands 200L [ CancelTimer id ] table
    Assert.Equal(None, Map.tryFind id table')

[<Fact>]
let ``存在しないIDへのCancelTimerは何もしない(§3.3「動いていなければ何もしない」)`` () =
    let id = TimerId.Topology ScreenSettle
    let table = applyTimerCommands 100L [ CancelTimer id ] Map.empty
    Assert.True(Map.isEmpty table)

[<Fact>]
let ``複数のCommandは左から順に1つの表へ畳み込まれる`` () =
    let id1 = TimerId.Topology ScreenSettle
    let id2 = TimerId.Topology ScreenGrace
    let table = applyTimerCommands 0L [ StartTimer (id1, 10L); StartTimer (id2, 20L); CancelTimer id1 ] Map.empty
    Assert.Equal(None, Map.tryFind id1 table)
    Assert.Equal(Some 20L, Map.tryFind id2 table)

// ===== SimulationTimerExecutor（仮想時計駆動の実行器） =====

[<Fact>]
let ``空の実行器ではNextDeadlineがNone、PopNextもNoneを返す`` () =
    let exec = SimulationTimerExecutor()
    Assert.Equal(None, exec.NextDeadline)
    Assert.Equal(None, exec.PopNext ())

[<Fact>]
let ``PopNextは最も早いdeadlineのタイマーを取り除いて返す`` () =
    let exec = SimulationTimerExecutor()
    let idA = TimerId.Topology ScreenSettle
    let idB = TimerId.Topology ScreenGrace
    exec.Apply 0L [ StartTimer (idA, 100L); StartTimer (idB, 50L) ]
    Assert.Equal(Some 50L, exec.NextDeadline)
    Assert.Equal(Some (idB, 50L), exec.PopNext ())
    // 取り除いた後は残りの1本だけが表に残る。
    Assert.Equal(Some 100L, exec.NextDeadline)
    Assert.Equal(Some (idA, 100L), exec.PopNext ())
    Assert.Equal(None, exec.PopNext ())

[<Fact>]
let ``同じdeadlineが複数あればPopNextは構造的に最小のTimerIdを選ぶ(Replay決定性、v2.58)`` () =
    // TimerId.Health/Topology/Windows/Focus/Placement（DU定義順）のうち、同じdeadlineなら
    // 常に同じ1本が選ばれることを確認する——「複数が同時刻なら構造的に最小のTimerIdを選ぶ」という
    // ドキュメンテーションコメントの主張を直接検査する、これまで一度もテストされていなかった規則。
    let exec = SimulationTimerExecutor()
    let idHealth = TimerId.Health (ResponsivenessReprobe (WindowId 1L))
    let idTopology = TimerId.Topology ScreenSettle
    let idWindows = TimerId.Windows (ClassificationDeadline (WindowId 1L))
    let idFocus = TimerId.Focus WorkspaceSwitchSettle
    let idPlacement = TimerId.Placement (IntegrityReprobe (WindowId 1L))
    let allIds = [ idHealth; idTopology; idWindows; idFocus; idPlacement ]
    let expectedMin = allIds |> List.sort |> List.head
    exec.Apply 0L (allIds |> List.map (fun id -> StartTimer (id, 10L)))
    match exec.PopNext () with
    | Some (picked, 10L) -> Assert.Equal(expectedMin, picked)
    | other -> Assert.True(false, sprintf "unexpected: %A" other)
    // 挿入順を変えても同じ結果になる(列挙順ではなく構造的な比較で決まっていることの確認)。
    let exec2 = SimulationTimerExecutor()
    exec2.Apply 0L (List.rev allIds |> List.map (fun id -> StartTimer (id, 10L)))
    match exec2.PopNext () with
    | Some (picked, 10L) -> Assert.Equal(expectedMin, picked)
    | other -> Assert.True(false, sprintf "unexpected: %A" other)

[<Fact>]
let ``PopNextは同時刻の全てを一度に取り除くのではなく1本だけ取り除く`` () =
    let exec = SimulationTimerExecutor()
    let idA = TimerId.Topology ScreenSettle
    let idB = TimerId.Topology ScreenGrace
    exec.Apply 0L [ StartTimer (idA, 10L); StartTimer (idB, 10L) ]
    exec.PopNext () |> ignore
    // もう1本は残り、NextDeadlineは依然10Lのまま。
    Assert.Equal(Some 10L, exec.NextDeadline)
    Assert.True((exec.PopNext ()).IsSome)
    Assert.Equal(None, exec.NextDeadline)
