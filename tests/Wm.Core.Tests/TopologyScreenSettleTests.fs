/// architecture.md §15.1 #21「待ちとタイマーの整合」を、ADR-019決定8の最初の縦切り
/// （Core.TopologyのScreenSettle）について検査する。
/// 前半：期限を持つ待ち（ScreenPhase.Settling）について、対応するTimerIdのStartTimerが発行されていること。
/// 後半：待ちが存在しない／Deadlineが入力時刻より後のTimerElapsedは状態を変えないこと（古いTimerの発火の無害性）。
module Wm.Core.Tests.TopologyScreenSettleTests

open FsCheck.Xunit
open Wm.Contracts.Identifiers
open Wm.Contracts.SharedVocabulary
open Wm.Contracts.Timers
open Wm.Contracts.CoreCommand
open Wm.Core.Topology

let private config = TopologyConfig.defaults

let private mkMonitor (id: int) (primary: bool) : MonitorSnapshot =
    { Id = MonitorId (int64 id)
      StableKey = sprintf "TEST:%d" id
      Bounds = { X = 0; Y = 0; Width = 1920; Height = 1080 }
      WorkArea = { X = 0; Y = 0; Width = 1920; Height = 1040 }
      Dpi = 96
      IsPrimary = primary }

let private mkSnapshots (rawIds: int list) : MonitorSnapshot list =
    rawIds
    |> List.map (fun i -> abs i % 8)
    |> List.distinct
    |> List.map (fun i -> mkMonitor i (i = 0))

[<Property>]
let ``#21前半 StableでMonitorTopologyObservedを受けるとSettlingへ入りStartTimerを返す`` (now: int) (rawIds: int list) =
    let now64 = int64 now
    let state = initial Map.empty
    let snapshots = mkSnapshots rawIds
    let state', response, events, trace = update config now64 (MonitorTopologyObserved snapshots) state

    let expectedDeadline = now64 + config.SettleMs
    let expectedHardDeadline = now64 + config.HardDeadlineMs

    (phase state' = Settling (snapshots, expectedDeadline, expectedHardDeadline))
    && (response.TimerCommands = [ StartTimer (Topology ScreenSettle, config.SettleMs) ])
    && (response.Actions = [])
    && (events = [])
    && (trace = [])

[<Property>]
let ``#21後半 deadline未到達のTimerElapsedは状態・応答を一切変えない`` (settlingStart: int) (rawIds: int list) (earlyOffsetRaw: int) =
    let state0 = initial Map.empty
    let snapshots = mkSnapshots rawIds
    let state1, _, _, _ = update config (int64 settlingStart) (MonitorTopologyObserved snapshots) state0

    match phase state1 with
    | Settling (_, deadline, _) ->
        let offset = (int64 (abs earlyOffsetRaw)) + 1L // 1以上。記録したdeadlineより厳密に前の時刻（古い発火）
        let firedAt = deadline - offset
        let state2, response, events, trace = update config firedAt (TimerElapsed (Topology ScreenSettle)) state1
        (state2 = state1)
        && (response.Actions = [])
        && (response.TimerCommands = [])
        && (events = [])
        && (trace = [])
    | _ -> false // MonitorTopologyObservedの直後はSettlingのはず

[<Property>]
let ``#21後半 待ちが存在しない(Stable)間のTimerElapsedも状態を変えない`` (firedAt: int) =
    let state = initial Map.empty
    let state', response, events, trace = update config (int64 firedAt) (TimerElapsed (Topology ScreenSettle)) state
    (state' = state) && (response.Actions = []) && (response.TimerCommands = []) && (events = []) && (trace = [])

[<Property>]
let ``deadline到達ちょうどのTimerElapsedは本物の発火としてGraceへ進む`` (settlingStart: int) (rawIds: int list) =
    let state0 = initial Map.empty
    let snapshots = mkSnapshots rawIds
    let state1, _, _, _ = update config (int64 settlingStart) (MonitorTopologyObserved snapshots) state0

    match phase state1 with
    | Settling (_, deadline, _) ->
        let state2, response, _events, _trace = update config deadline (TimerElapsed (Topology ScreenSettle)) state1
        match phase state2 with
        | Grace _ ->
            (response.Actions = [ ObserveAll ])
            && (response.TimerCommands
                |> List.contains (CancelTimer (Topology ScreenSettle)))
        | _ -> false
    | _ -> false
