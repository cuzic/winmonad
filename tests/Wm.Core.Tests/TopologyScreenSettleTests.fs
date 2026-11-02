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

/// 常にモニター0を含む（空にならない）。これらのテストは`initial Map.empty`（NoMonitors）から出発するため、
/// 観測したトポロジが空だと「変化が無い」と判定されてSettlingへ入らなくなる（Opusレビュー3-2の修正で
/// 意図した新しい挙動）。各テストはトポロジが実際に変化する前提で書かれているので、非空を保証する。
let private mkSnapshots (rawIds: int list) : MonitorSnapshot list =
    0 :: (rawIds |> List.map (fun i -> abs i % 8))
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
    && (response.TimerCommands = [ StartTimer (TimerId.Topology ScreenSettle, config.SettleMs) ])
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
        let state2, response, events, trace = update config firedAt (TimerElapsed (TimerId.Topology ScreenSettle)) state1
        (state2 = state1)
        && (response.Actions = [])
        && (response.TimerCommands = [])
        && (events = [])
        && (trace = [])
    | _ -> false // MonitorTopologyObservedの直後はSettlingのはず

[<Property>]
let ``#21後半 待ちが存在しない(Stable)間のTimerElapsedも状態を変えない`` (firedAt: int) =
    let state = initial Map.empty
    let state', response, events, trace = update config (int64 firedAt) (TimerElapsed (TimerId.Topology ScreenSettle)) state
    (state' = state) && (response.Actions = []) && (response.TimerCommands = []) && (events = []) && (trace = [])

/// Opusレビュー3-2の回帰テスト：Stable/Graceで、現在のDisplaysと同じ内容のMonitorTopologyObservedを
/// 受けてもSettlingへ入らない（これが無いと、settleが出すObserveAllへの応答が同じトポロジを含むたびに
/// Settling→Grace→Settling…を無限に繰り返す）。
[<Property>]
let ``#21前半 変化していないトポロジ観測ではStableからSettlingへ入らない`` (now: int) =
    let state = initial Map.empty // NoMonitors
    let unchanged = [] // currentMonitors state = [] と一致する観測
    let state', response, events, trace = update config (int64 now) (MonitorTopologyObserved unchanged) state
    (phase state' = Stable) && (response.Actions = []) && (response.TimerCommands = []) && (events = []) && (trace = [])

/// Settling→genuine発火→Graceまで進める（テストの土台）。
let private settleFully (t0: int64) (snapshots: MonitorSnapshot list) (state: TopologyState) : int64 * TopologyState =
    let state1, _, _, _ = update config t0 (MonitorTopologyObserved snapshots) state
    match phase state1 with
    | Settling (_, deadline, _) ->
        let state2, _, _, _ = update config deadline (TimerElapsed (TimerId.Topology ScreenSettle)) state1
        deadline, state2
    | _ -> t0, state1

/// round2レビューT-2：3-1（blocking）の回帰を実際に捕まえるため、Reconfiguring区間を実際に通す。
/// 「Stable/Settlingから、ScreenReconfigurationInProgress trueに入り、0回以上のMonitorTopologyObserved
/// を経て、false／UserCommand／ReconfigurationCap満了のいずれかで解除し、ScreenSettleの発火まで進めたとき、
/// 確定したDisplaysのモニター集合は、Reconfiguring中に最後に観測したトポロジ（無ければ解除前のDisplays）に
/// 等しい」ことを検査する。元のバグ（`latestForForcedSettle`が`[]`を返す）では、観測を何もしていないのに
/// `NoMonitors`へ確定してしまい、この性質が破れる。
[<Property>]
let ``T-2 Reconfiguring解除後は、最後に観測したトポロジ（無ければ解除前のDisplays）で確定する``
    (initialRawIds: int list)
    (observedDuring: int list list)
    (releaseKind: int)
    =
    let initialSnapshots = mkSnapshots initialRawIds
    let t0, state0 = settleFully 0L initialSnapshots (initial Map.empty) // Stable、Displays=initialSnapshots

    let t1, stateReconfiguring, _, _ =
        let s, r, e, tr = update config (t0 + 1L) (ScreenReconfigurationInProgress true) state0
        (t0 + 1L, s, r, e)

    let observedList = observedDuring |> List.truncate 5 |> List.map mkSnapshots

    let tN, stateAfterObserved, lastObserved =
        observedList
        |> List.fold
            (fun (t, s, _lastObs) snaps ->
                let s', _, _, _ = update config (t + 1L) (MonitorTopologyObserved snaps) s
                (t + 1L, s', Some snaps))
            (t1, stateReconfiguring, None)

    let expectedMonitorIds =
        (lastObserved |> Option.defaultValue initialSnapshots) |> List.map (fun m -> m.Id) |> Set.ofList

    let stateReleased, _, _, _ =
        match (abs releaseKind) % 3 with
        | 0 -> update config (tN + 1L) (ScreenReconfigurationInProgress false) stateAfterObserved
        | 1 -> update config (tN + 1L) (UserCommand FocusNext) stateAfterObserved
        | _ ->
            match phase stateAfterObserved with
            | Reconfiguring since -> update config (since + config.ReconfigurationCapMs) (TimerElapsed (TimerId.Topology ReconfigurationCap)) stateAfterObserved
            | _ -> update config (tN + 1L) (ScreenReconfigurationInProgress false) stateAfterObserved

    match phase stateReleased with
    | Settling (_, deadline, _) ->
        let stateSettled, _, _, _ = update config deadline (TimerElapsed (TimerId.Topology ScreenSettle)) stateReleased
        match displays stateSettled with
        | Displays (current, visible) ->
            let actualIds = (current :: visible) |> List.map (fun sc -> sc.Monitor.Id) |> Set.ofList
            actualIds = expectedMonitorIds
        | NoMonitors -> Set.isEmpty expectedMonitorIds
    | _ -> false // Reconfiguring中は常に解除されるので到達しないはず

[<Property>]
let ``deadline到達ちょうどのTimerElapsedは本物の発火としてGraceへ進む`` (settlingStart: int) (rawIds: int list) =
    let state0 = initial Map.empty
    let snapshots = mkSnapshots rawIds
    let state1, _, _, _ = update config (int64 settlingStart) (MonitorTopologyObserved snapshots) state0

    match phase state1 with
    | Settling (_, deadline, _) ->
        let state2, response, _events, _trace = update config deadline (TimerElapsed (TimerId.Topology ScreenSettle)) state1
        match phase state2 with
        | Grace _ ->
            (response.Actions = [ ObserveAll ])
            && (response.TimerCommands
                |> List.contains (CancelTimer (TimerId.Topology ScreenSettle)))
        | _ -> false
    | _ -> false
