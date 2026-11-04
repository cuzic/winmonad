/// architecture.md §17：Wm.Host、Phase B ラウンド1の実行入口。
///
/// 実際のWin32(`Wm.Windows`)から1回だけ観測を取り、`Wm.Normalization`→`Wm.Runtime.Reducer`へ通して
/// 分類結果を報告する——読み取りだけで、`SetRect`/`RequestForeground`等のEffectは一切実際のOSへ
/// 発行しない(計画の「このラウンドで対象外にしたこと」参照)。`StepResult.Effects`はこの1回の投入で
/// 実際に発生した値をそのまま記録し、表示するだけに留める(再投入はしない——同じ観測を二重に
/// 投入すると状態が壊れるため)。
module Wm.Host.Program

open System
open System.IO
open Wm.Contracts.CoreCommand
open Wm.Contracts.GatedEffect
open Wm.Core.WmState
open Wm.Runtime.Reducer

/// 保留中の全タイマーを仮想時計で発火し切る。発火したStepResultが運んだEffectも集めて返す
/// (Reconcile・分類期限超過等、タイマー発火の結果として生まれるEffectも報告対象にするため)。
let private drainTimers (reducer: Reducer) : Effect list * int =
    let mutable fired = 0
    let mutable effects: Effect list = []
    let mutable more = true
    while more do
        match reducer.AdvanceToNextTimer() with
        | Some stepResult ->
            fired <- fired + 1
            effects <- effects @ (stepResult.Effects |> List.map GatedEffect.effect)
        | None -> more <- false
    effects, fired

[<EntryPoint>]
let main _argv =
    let journalPath = Path.Combine(Path.GetTempPath(), sprintf "wm-host-round1-%s.ndjson" (Guid.NewGuid().ToString("N")))
    printfn "Phase B round 1: real-Windows read-only observation slice"
    printfn "Journal: %s" journalPath

    let reducer = Reducer(RuntimePolicy.defaults, "phase-b-round1", "wm-host-round1", "no-policy-artifact", Some journalPath)

    // 段1：実モニターを列挙し、MonitorTopologyObservedを投入してTopologyを確定させる。
    let monitorHandles = Wm.Windows.RealObservation.enumerateMonitorHandles ()
    let monitors = Wm.Windows.RealObservation.enumerateMonitors monitorHandles
    printfn "\nMonitors observed: %d" monitors.Length
    for m in monitors do
        printfn "  %A primary=%b bounds=%A workArea=%A dpi=%d" m.Id m.IsPrimary m.Bounds m.WorkArea m.Dpi

    let topologyResult = reducer.Submit 0L "wm-host" (MonitorTopologyObserved monitors)
    let topologyEffects = topologyResult.Effects |> List.map GatedEffect.effect
    let settleEffects, settledAfterTopology = drainTimers reducer
    printfn "Timers fired while settling Topology: %d" settledAfterTopology

    // 段2：実トップレベル窓を列挙し、RawFirstSeen→normalize→Reducer.Submitで1回だけ投入する。
    let windows = Wm.Windows.RealObservation.enumerateTopLevelWindows 1L monitorHandles
    printfn "\nTop-level windows observed: %d" windows.Length

    let windowSubmissionResults =
        windows
        |> List.collect (fun snapshot -> Wm.Normalization.WindowEventNormalizer.normalize (Wm.Normalization.WindowEventNormalizer.RawFirstSeen snapshot))
        |> List.map (fun input -> reducer.Submit 1L "wm-host" input)
    let windowEffects = windowSubmissionResults |> List.collect (fun sr -> sr.Effects) |> List.map GatedEffect.effect
    let classifyEffects, settledAfterWindows = drainTimers reducer
    printfn "Timers fired while settling classification: %d" settledAfterWindows

    // 段3：結果の報告。Classificationだけを読み、Effectは一切実行しない(読み取り専用の安全境界)。
    printfn "\nClassification (%d windows):" (Map.count reducer.State.Windows.Records)
    reducer.State.Windows.Records
    |> Map.toSeq
    |> Seq.iter (fun (w, record) -> printfn "  %A -> %A" w record.Classification)

    let allEffects = topologyEffects @ settleEffects @ windowEffects @ classifyEffects
    printfn "\n(diagnostic only, not dispatched to real Win32) Effects the pipeline actually issued during this run: %d" allEffects.Length
    for e in allEffects do
        printfn "  %A" e

    printfn "\nDone."
    0
