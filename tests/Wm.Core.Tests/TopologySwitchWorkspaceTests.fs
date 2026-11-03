/// architecture.md §4.4「SwitchWorkspaceの意味」（xmonadのview semantics、v2.32）を検査する。
module Wm.Core.Tests.TopologySwitchWorkspaceTests

open Xunit
open Wm.Contracts.Identifiers
open Wm.Contracts.Observation
open Wm.Contracts.SharedVocabulary
open Wm.Contracts.Timers
open Wm.Contracts.CoreCommand
open Wm.Core.DomainEvents
open Wm.Core.Topology

let private config = TopologyConfig.defaults

let private mkMonitor (id: int) (primary: bool) : MonitorSnapshot =
    { Id = MonitorId (int64 id)
      StableKey = sprintf "TEST:%d" id
      Bounds = { X = 0; Y = 0; Width = 1920; Height = 1080 }
      WorkArea = { X = 0; Y = 0; Width = 1920; Height = 1040 }
      Dpi = 96
      IsPrimary = primary }

let private settleFully (t0: int64) (snapshots: MonitorSnapshot list) (state: TopologyState) : int64 * TopologyState =
    let state1, _, _, _ = update config t0 (MonitorTopologyObserved snapshots) state
    match phase state1 with
    | Settling (_, deadline, _) -> deadline, (update config deadline (TimerElapsed (TimerId.Topology ScreenSettle)) state1 |> fun (s, _, _, _) -> s)
    | _ -> t0, state1

[<Fact>]
let ``SwitchWorkspaceは既にvisibleなWorkspaceへなら、currentを入れ替えるだけでCurrentChangedを出す`` () =
    let t0, state0 = settleFully 0L [ mkMonitor 0 true; mkMonitor 1 false ] (initial Map.empty)
    let ws0, ws1 =
        match displays state0 with
        | Displays (current, [ visible1 ]) -> current.Workspace, visible1.Workspace
        | other -> failwithf "expected 2 monitors to settle, got %A" other

    let state1, response, events, _ = update config (t0 + 1L) (UserCommand (SwitchWorkspace ws1)) state0
    match displays state1 with
    | Displays (current, [ visible1 ]) ->
        Assert.Equal(ws1, current.Workspace)
        Assert.Equal(ws0, visible1.Workspace)
    | other -> failwithf "expected current/visible to swap, got %A" other
    Assert.Equal<DomainEvent list>([ CurrentChanged ws1 ], events)
    Assert.Equal({ Actions = []; TimerCommands = [] }, response)

[<Fact>]
let ``SwitchWorkspaceで既にcurrentなWorkspaceを指定しても何もしない`` () =
    let t0, state0 = settleFully 0L [ mkMonitor 0 true ] (initial Map.empty)
    let ws0 = match displays state0 with Displays (current, _) -> current.Workspace | NoMonitors -> failwith "expected a monitor"
    let state1, response, events, trace = update config (t0 + 1L) (UserCommand (SwitchWorkspace ws0)) state0
    Assert.Equal(state0, state1)
    Assert.Equal({ Actions = []; TimerCommands = [] }, response)
    Assert.Empty(events)
    Assert.Empty(trace)

[<Fact>]
let ``SwitchWorkspaceでhiddenな(未知の)Workspaceへ切り替えると、古いcurrentはhiddenになり新しいWorkspaceがcurrentになる`` () =
    let t0, state0 = settleFully 0L [ mkMonitor 0 true ] (initial Map.empty)
    let ws0 = match displays state0 with Displays (current, _) -> current.Workspace | NoMonitors -> failwith "expected a monitor"
    let unknownWs = WorkspaceId 999

    let state1, response, events, _ = update config (t0 + 1L) (UserCommand (SwitchWorkspace unknownWs)) state0
    match displays state1 with
    | Displays (current, []) -> Assert.Equal(unknownWs, current.Workspace)
    | other -> failwithf "expected the unknown workspace to become current on the same monitor, got %A" other
    Assert.Equal<DomainEvent list>([ WorkspaceHidden ws0; WorkspaceShown (unknownWs, UserView) ], events)
    Assert.Equal({ Actions = []; TimerCommands = [] }, response)

[<Fact>]
let ``SwitchWorkspaceはNoMonitorsなら何もしない`` () =
    let state0 = initial Map.empty
    let state1, response, events, trace = update config 10L (UserCommand (SwitchWorkspace (WorkspaceId 1))) state0
    Assert.Equal(state0, state1)
    Assert.Equal({ Actions = []; TimerCommands = [] }, response)
    Assert.Empty(events)
    Assert.Empty(trace)

[<Fact>]
let ``SwitchWorkspaceの明示的な表示は、ReturnMonitorの予約を自動復帰より優先し解除する(§4.4規則3)`` () =
    let t0, state0 = settleFully 0L [ mkMonitor 0 true; mkMonitor 1 false ] (initial Map.empty)
    let ws1 = match displays state0 with Displays (_, [ visible1 ]) -> visible1.Workspace | other -> failwithf "expected 2 monitors, got %A" other

    // monitor1を切断する: ws1がhiddenになりReturns[ws1] = monitor1.Idが記録される(§4.4規則1)。
    let t1, stateDisconnected = settleFully (t0 + 1L) [ mkMonitor 0 true ] state0
    Assert.Equal(Some (MonitorId 1L), Map.tryFind ws1 (returns stateDisconnected))

    // ユーザーが明示的にws1へSwitchWorkspaceする(monitor0しか無いので、monitor0の表示をws1に入れ替える)。
    let stateSwitched, _, events, _ = update config (t1 + 1L) (UserCommand (SwitchWorkspace ws1)) stateDisconnected
    Assert.Equal(None, Map.tryFind ws1 (returns stateSwitched)) // 予約は解除されている
    match displays stateSwitched with
    | Displays (current, []) -> Assert.Equal(ws1, current.Workspace)
    | other -> failwithf "expected ws1 to become current, got %A" other
    Assert.Contains(WorkspaceShown (ws1, UserView), events)

[<Fact>]
let ``抑止区間中(Reconfiguring)のSwitchWorkspaceも、他のUserCommandと同じく強制的にSettlingへ入らせる`` () =
    let t0, state0 = settleFully 0L [ mkMonitor 0 true ] (initial Map.empty)
    let stateReconfiguring, _, _, _ = update config (t0 + 1L) (ScreenReconfigurationInProgress true) state0
    match phase stateReconfiguring with
    | Reconfiguring _ -> ()
    | other -> failwithf "expected Reconfiguring, got %A" other

    let state1, response, _, _ = update config (t0 + 2L) (UserCommand (SwitchWorkspace (WorkspaceId 42))) stateReconfiguring
    match phase state1 with
    | Settling _ -> ()
    | other -> failwithf "expected SwitchWorkspace during Reconfiguring to force Settling, got %A" other
    Assert.Contains(CancelTimer (TimerId.Topology ReconfigurationCap), response.TimerCommands)

// ===== FocusMonitor（§4.4、v2.36） =====

[<Fact>]
let ``FocusMonitorはvisibleな対象モニターへcurrentを入れ替え、どのWorkspaceが表示されているかは変えない`` () =
    let t0, state0 = settleFully 0L [ mkMonitor 0 true; mkMonitor 1 false ] (initial Map.empty)
    let ws0, ws1, monitor1Id =
        match displays state0 with
        | Displays (current, [ visible1 ]) -> current.Workspace, visible1.Workspace, visible1.Monitor.Id
        | other -> failwithf "expected 2 monitors to settle, got %A" other

    let state1, response, events, _ = update config (t0 + 1L) (UserCommand (FocusMonitor monitor1Id)) state0
    match displays state1 with
    | Displays (current, [ visible1 ]) ->
        Assert.Equal(monitor1Id, current.Monitor.Id)
        Assert.Equal(ws1, current.Workspace) // monitor1はws1を表示したまま(所属は変えない)
        Assert.Equal(ws0, visible1.Workspace)
    | other -> failwithf "expected current/visible to swap by monitor, got %A" other
    Assert.Equal<DomainEvent list>([ CurrentChanged ws1 ], events)
    Assert.Equal({ Actions = []; TimerCommands = [] }, response)

[<Fact>]
let ``FocusMonitorで既にcurrentなモニターを指定しても何もしない`` () =
    let t0, state0 = settleFully 0L [ mkMonitor 0 true ] (initial Map.empty)
    let state1, response, events, trace = update config (t0 + 1L) (UserCommand (FocusMonitor (MonitorId 0L))) state0
    Assert.Equal(state0, state1)
    Assert.Equal({ Actions = []; TimerCommands = [] }, response)
    Assert.Empty(events)
    Assert.Empty(trace)

[<Fact>]
let ``FocusMonitorは接続されていないMonitorIdなら何もしない`` () =
    let t0, state0 = settleFully 0L [ mkMonitor 0 true ] (initial Map.empty)
    let state1, response, events, trace = update config (t0 + 1L) (UserCommand (FocusMonitor (MonitorId 999L))) state0
    Assert.Equal(state0, state1)
    Assert.Equal({ Actions = []; TimerCommands = [] }, response)
    Assert.Empty(events)
    Assert.Empty(trace)

[<Fact>]
let ``FocusMonitorはNoMonitorsなら何もしない`` () =
    let state0 = initial Map.empty
    let state1, response, events, trace = update config 10L (UserCommand (FocusMonitor (MonitorId 0L))) state0
    Assert.Equal(state0, state1)
    Assert.Equal({ Actions = []; TimerCommands = [] }, response)
    Assert.Empty(events)
    Assert.Empty(trace)

[<Fact>]
let ``抑止区間中(Reconfiguring)のFocusMonitorも、他のUserCommandと同じく強制的にSettlingへ入らせる`` () =
    let t0, state0 = settleFully 0L [ mkMonitor 0 true ] (initial Map.empty)
    let stateReconfiguring, _, _, _ = update config (t0 + 1L) (ScreenReconfigurationInProgress true) state0
    let state1, response, _, _ = update config (t0 + 2L) (UserCommand (FocusMonitor (MonitorId 0L))) stateReconfiguring
    match phase state1 with
    | Settling _ -> ()
    | other -> failwithf "expected FocusMonitor during Reconfiguring to force Settling, got %A" other
    Assert.Contains(CancelTimer (TimerId.Topology ReconfigurationCap), response.TimerCommands)

// ===== MoveSizeEnded → MoveAttributed(w, UserDrag)（§4.4「観測されたモニターと意図されたモニターが
// 食い違うとき」のUserDrag分岐、R3-4/R5-9、v2.66） =====

let private moveSizeEnded (w: WindowId) : InputEvent =
    let rectObs: Observation<PixelRect> = { Value = Ok { X = 0; Y = 0; Width = 300; Height = 300 }; SampleSequence = 1L; MonotonicTime = 1L; Source = "test" }
    let monitorObs: Observation<MonitorId> = { Value = Ok (MonitorId 0L); SampleSequence = 1L; MonotonicTime = 1L; Source = "test" }
    MoveSizeEnded (w, rectObs, monitorObs)

[<Fact>]
let ``MoveSizeEndedはMoveAttributed(w, UserDrag)を出し、ScreenPhase・状態は一切変えない(v2.66)`` () =
    let t0, state0 = settleFully 0L [ mkMonitor 0 true ] (initial Map.empty)
    let w = WindowId 1L
    let state1, response, events, trace = update config (t0 + 1L) (moveSizeEnded w) state0
    Assert.Equal(state0, state1)
    Assert.Equal({ Actions = []; TimerCommands = [] }, response)
    Assert.Equal<DomainEvent list>([ MoveAttributed (w, UserDrag) ], events)
    Assert.Empty(trace)

[<Fact>]
let ``MoveSizeEndedは抑止区間(Reconfiguring)中でもUserDragを優先する(R5-9、v2.66)`` () =
    let t0, state0 = settleFully 0L [ mkMonitor 0 true ] (initial Map.empty)
    let stateReconfiguring, _, _, _ = update config (t0 + 1L) (ScreenReconfigurationInProgress true) state0
    match phase stateReconfiguring with
    | Reconfiguring _ -> ()
    | other -> failwithf "expected Reconfiguring, got %A" other
    let w = WindowId 1L
    let _, _, events, _ = update config (t0 + 2L) (moveSizeEnded w) stateReconfiguring
    Assert.Equal<DomainEvent list>([ MoveAttributed (w, UserDrag) ], events)
