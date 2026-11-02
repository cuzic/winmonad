/// ADR-019実装順17：ScenarioRunner.fs。architecture.md §12.1/ADR-002「同じCoreをShell/Simulationが実行」。
///
/// `Wm.Runtime.Reducer`（実行器）と`VirtualWorld`（疑似OS）を結びつけ、`step`が返す`GatedEffect`を
/// 疑似OSへ適用し、生じた観測・`EffectCallReturned`をReducerへ投入する、という往復を固定点まで
/// 繰り返す。収束しないシナリオ（振動、無限ループ）を検出するため、`maxSettleSteps`で上限を設ける。
///
/// このラウンドで対象外にしたこと：タイマー駆動の往復（`Reducer.AdvanceToNextTimer`の呼び出しは
/// シナリオ側の責務のまま、ここでは結合しない——`PendingDeadline`/`IntegrityReprobe`等のリトライが
/// 絡むシナリオは、タイマーを明示的に進める追加の往復が必要になるため、専用のシナリオで扱う）。
module Wm.Simulation.ScenarioRunner

open Wm.Contracts.CoreCommand
open Wm.Contracts.GatedEffect
open Wm.Runtime.Reducer
open Wm.Simulation.VirtualWorld

let maxSettleSteps = 100

/// 1件の`GatedEffect`を疑似OSへ適用し、生じた観測・`EffectCallReturned`をReducerへ投入して、
/// その結果新たに生まれた`GatedEffect`を集める。
let private applyOneAndCollect (now: int64) (reducer: Reducer) (world: VirtualWorld) (effect: GatedEffect) : VirtualWorld * GatedEffect list =
    let world', events, callReturns = applyEffect now world effect
    let fromEvents = events |> List.map (fun ev -> (reducer.Submit now "virtual-world" ev).Effects)
    let fromCallReturns = callReturns |> List.map (fun (rid, result) -> (reducer.Submit now "virtual-world" (EffectCallReturned (rid, result))).Effects)
    world', (fromEvents @ fromCallReturns) |> List.collect id

/// 外部入力が尽きるまで、Effect→観測の折り返しを繰り返す（固定点に収束するか`maxSettleSteps`に達するまで）。
/// `initialEffects`は、シナリオが最初に`Reducer.Submit`した結果の`StepResult.Effects`を渡す。
let rec runToQuiescence (now: int64) (reducer: Reducer) (world: VirtualWorld) (pendingEffects: GatedEffect list) (stepsLeft: int) : VirtualWorld =
    match pendingEffects with
    | [] -> world
    | _ when stepsLeft <= 0 -> world
    | effect :: rest ->
        let world', newEffects = applyOneAndCollect now reducer world effect
        runToQuiescence now reducer world' (rest @ newEffects) (stepsLeft - 1)

/// `maxSettleSteps`を既定値として使う便利版。
let settle (now: int64) (reducer: Reducer) (world: VirtualWorld) (initialEffects: GatedEffect list) : VirtualWorld =
    runToQuiescence now reducer world initialEffects maxSettleSteps
