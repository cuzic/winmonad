/// Core.Topology（ADR-019実装順14）。architecture.md §4.4/§7.3。
/// モニター構成、各モニターに表示中のWorkspace、アクティブなモニター、画面構成変化の待ち。
/// `MonitorLifecycle.reconcile`を同梱する（§19）。
module Wm.Core.Topology

open Wm.Contracts.Identifiers
open Wm.Contracts.SharedVocabulary
open Wm.Contracts.Timers
open Wm.Contracts.CoreCommand
open Wm.Policy.MonitorSelector
open Wm.Core.DomainEvents
open Wm.Core.DecisionTrace

/// xmonadのStackSetのcurrent/visibleと同じ形（§4.4、R3-2）。
type Screen = { Monitor: MonitorSnapshot; Workspace: WorkspaceId }

type Displays =
    | NoMonitors
    | Displays of current: Screen * visible: Screen list

/// §17（ADR-019）：この不変条件はWm.Contractsへ公開の値型として置くと保てないため、
/// TopologyStateはCore.Topologyの中にprivateなまま置く。他ドメイン・他段が読む分は
/// Topology自身が公開する読み取り専用の射影（Screen）をstepがその場で組み立てて渡す。
type TopologyState =
    private
        { Displays: Displays
          Phase: ScreenPhase
          Assignments: Map<WorkspaceId, MonitorSelector>
          Returns: Map<WorkspaceId, MonitorId> }

/// Settling/Grace/Reconfigurationの既定値。実測値はPhase 0〜Bの実機Journalから決める（§7.3）。
type TopologyConfig =
    { SettleMs: int64
      GraceMs: int64
      ReconfigurationCapMs: int64
      HardDeadlineMs: int64 }

module TopologyConfig =
    /// HardDeadlineMs=30秒、ReconfigurationCapMs=10分は§7.3の既定値。SettleMs/GraceMsは未確定のため暫定値。
    let defaults =
        { SettleMs = 500L
          GraceMs = 250L
          ReconfigurationCapMs = 600_000L
          HardDeadlineMs = 30_000L }

/// 初期状態（モニター0台）。Assignmentsは採用中PolicyのWorkspaceAssignmentsの写し。
let initial (assignments: Map<WorkspaceId, MonitorSelector>) : TopologyState =
    { Displays = NoMonitors
      Phase = Stable
      Assignments = assignments
      Returns = Map.empty }

let displays (s: TopologyState) : Displays = s.Displays
let phase (s: TopologyState) : ScreenPhase = s.Phase

let private shownScreens (d: Displays) : Screen list =
    match d with
    | NoMonitors -> []
    | Displays (current, visible) -> current :: visible

let isShown (ws: WorkspaceId) (s: TopologyState) : bool =
    shownScreens s.Displays |> List.exists (fun sc -> sc.Workspace = ws)

// ===== MonitorLifecycle.reconcile（§4.4/§7.3、S8） =====

module MonitorLifecycle =

    let private pickPrimaryOrSmallest (candidates: MonitorSnapshot list) : MonitorSnapshot option =
        match candidates |> List.filter (fun m -> m.IsPrimary) with
        | primary :: _ -> Some primary
        | [] ->
            match candidates with
            | [] -> None
            | _ -> candidates |> List.minBy (fun m -> match m.Id with MonitorId v -> v) |> Some

    let private matchesSelector (m: MonitorSnapshot) (sel: MonitorSelector) : bool =
        match sel with
        | PrimaryMonitor -> m.IsPrimary
        | MonitorKey k -> m.StableKey = k

    /// 任意の切断列・再接続列の後、各Workspaceは最初に退避した時点のモニターへ戻る
    /// （Hyprlandのcascadedテストと同型、§15.1 #18）。1つのstepの中で原子的に行う。
    let reconcile (state: TopologyState) (latest: MonitorSnapshot list) : TopologyState * DomainEvent list =
        let oldScreens = shownScreens state.Displays
        let oldByMonitor = oldScreens |> List.map (fun sc -> sc.Monitor.Id, sc) |> Map.ofList
        let newIds = latest |> List.map (fun m -> m.Id) |> Set.ofList
        let oldIds = oldByMonitor |> Map.toList |> List.map fst |> Set.ofList

        let removedIds = Set.difference oldIds newIds
        let keptIds = Set.intersect oldIds newIds
        let addedIds = Set.difference newIds oldIds

        // (1) 消えたモニターのWorkspaceをhiddenにし、Returnsへ記録する（既に値があれば上書きしない）。
        let hiddenEvents, returnsAfterRemoval =
            removedIds
            |> Set.toList
            |> List.sortBy (fun (MonitorId v) -> v)
            |> List.fold
                (fun (evs, returns) mid ->
                    let sc = oldByMonitor.[mid]
                    let returns' =
                        if Map.containsKey sc.Workspace returns then returns
                        else Map.add sc.Workspace mid returns
                    evs @ [ WorkspaceHidden sc.Workspace ], returns')
                ([], state.Returns)

        // 継続して出ているモニターは、同じWorkspaceを保ちつつ最新のMonitorSnapshotで更新する。
        let keptScreens =
            keptIds
            |> Set.toList
            |> List.map (fun mid ->
                let sc = oldByMonitor.[mid]
                let freshMonitor = latest |> List.find (fun m -> m.Id = mid)
                { sc with Monitor = freshMonitor })

        let latestById = latest |> List.map (fun m -> m.Id, m) |> Map.ofList

        // (2) 新たに現れたモニターへ、Returns→Assignments→最小ID(hidden想定)→最小未使用IDの順で割り当てる。
        let addedScreens, shownEvents, returnsAfterAdd =
            addedIds
            |> Set.toList
            |> List.sortBy (fun (MonitorId v) -> v)
            |> List.fold
                (fun (screens: Screen list, evs, returns: Map<WorkspaceId, MonitorId>) mid ->
                    let alreadyShown =
                        (keptScreens @ screens) |> List.map (fun sc -> sc.Workspace) |> Set.ofList
                    let monitor = latestById.[mid]

                    let byReturn =
                        returns
                        |> Map.toList
                        |> List.filter (fun (ws, m) -> m = mid && not (Set.contains ws alreadyShown))
                        |> List.map fst
                        |> List.sortBy (fun (WorkspaceId v) -> v)
                        |> List.tryHead

                    let byAssignment =
                        state.Assignments
                        |> Map.toList
                        |> List.filter (fun (ws, sel) -> matchesSelector monitor sel && not (Set.contains ws alreadyShown))
                        |> List.map fst
                        |> List.sortBy (fun (WorkspaceId v) -> v)
                        |> List.tryHead

                    // 「hiddenなWorkspace」はTopologyが知る全体では無いが、WorkspaceSetは全域（R6-1）なので
                    // 「表示されていない最小のWorkspaceId」で代用できる（未使用の最小IDと同じ集合になる）。
                    let bySmallestUnshown =
                        Seq.initInfinite id
                        |> Seq.map WorkspaceId
                        |> Seq.find (fun ws -> not (Set.contains ws alreadyShown))

                    let chosen =
                        byReturn
                        |> Option.orElse byAssignment
                        |> Option.defaultValue bySmallestUnshown

                    let returns' =
                        match byReturn with
                        | Some ws -> Map.remove ws returns
                        | None -> returns

                    screens @ [ { Monitor = monitor; Workspace = chosen } ],
                    evs @ [ WorkspaceShown (chosen, Reconcile) ],
                    returns')
                ([], [], returnsAfterRemoval)

        let allScreens = keptScreens @ addedScreens

        let newCurrent =
            match state.Displays with
            | Displays (oldCurrent, _) when Set.contains oldCurrent.Monitor.Id keptIds ->
                allScreens |> List.find (fun sc -> sc.Monitor.Id = oldCurrent.Monitor.Id) |> Some
            | _ ->
                allScreens
                |> List.map (fun sc -> sc.Monitor)
                |> pickPrimaryOrSmallest
                |> Option.bind (fun m -> allScreens |> List.tryFind (fun sc -> sc.Monitor.Id = m.Id))

        let newDisplays =
            match newCurrent with
            | None -> NoMonitors
            | Some cur -> Displays (cur, allScreens |> List.filter (fun sc -> sc.Monitor.Id <> cur.Monitor.Id))

        { state with Displays = newDisplays; Returns = returnsAfterAdd }, hiddenEvents @ shownEvents

// ===== ScreenPhase（§7.3）：抑止区間・安定待ち・grace window =====

/// 段②：ScreenPhaseの遷移。ScreenSettle縦切り（ADR-019決定8、性質テスト#21）の中心。
let update (config: TopologyConfig) (now: int64) (input: InputEvent) (state: TopologyState) : TopologyState * Response<TimerId, Effect> * DomainEvent list * DecisionTrace list =
    let noResponse = { Actions = []; TimerCommands = [] }

    /// 安定を確定し、最新トポロジでreconcileし、全体再観測を発行してGraceへ進む（§7.3）。
    let settle (latest: MonitorSnapshot list) =
        let state', events = MonitorLifecycle.reconcile state latest
        let graceDeadline = now + config.GraceMs
        { state' with Phase = Grace graceDeadline },
        { Actions = [ ObserveAll ]
          TimerCommands = [ CancelTimer (Topology ScreenSettle); StartTimer (Topology ScreenGrace, config.GraceMs) ] },
        events @ [ TopologySettled latest ],
        []

    match input with
    | ScreenReconfigurationInProgress true ->
        match state.Phase with
        | Reconfiguring _ -> state, noResponse, [], []
        | _ ->
            { state with Phase = Reconfiguring now },
            { Actions = []; TimerCommands = [ StartTimer (Topology ReconfigurationCap, config.ReconfigurationCapMs) ] },
            [],
            []
    | ScreenReconfigurationInProgress false ->
        match state.Phase with
        | Reconfiguring _ ->
            let deadline = now + config.SettleMs
            { state with Phase = Settling ([], deadline, now + config.HardDeadlineMs) },
            { Actions = []; TimerCommands = [ CancelTimer (Topology ReconfigurationCap); StartTimer (Topology ScreenSettle, config.SettleMs) ] },
            [],
            []
        | _ -> state, noResponse, [], []
    | UserCommand _ ->
        match state.Phase with
        | Reconfiguring _ ->
            let deadline = now + config.SettleMs
            { state with Phase = Settling ([], deadline, now + config.HardDeadlineMs) },
            { Actions = []; TimerCommands = [ CancelTimer (Topology ReconfigurationCap); StartTimer (Topology ScreenSettle, config.SettleMs) ] },
            [],
            []
        | _ -> state, noResponse, [], []
    | MonitorTopologyObserved snapshots ->
        match state.Phase with
        | Reconfiguring _ -> state, noResponse, [], [] // 抑止区間中は観測を信念へ反映するのみ（段①）。Phaseは変えない
        | Stable | Grace _ ->
            let deadline = now + config.SettleMs
            { state with Phase = Settling (snapshots, deadline, now + config.HardDeadlineMs) },
            { Actions = []; TimerCommands = [ StartTimer (Topology ScreenSettle, config.SettleMs) ] },
            [],
            []
        | Settling (_, _, hardDeadline) when now >= hardDeadline ->
            // hardDeadlineを過ぎたら、イベントが続いていても強制的に確定する（§7.3）。
            settle snapshots
        | Settling (_, _, hardDeadline) ->
            let deadline = now + config.SettleMs
            { state with Phase = Settling (snapshots, deadline, hardDeadline) },
            { Actions = []; TimerCommands = [ StartTimer (Topology ScreenSettle, config.SettleMs) ] },
            [],
            []
    | TimerElapsed (Topology ScreenSettle) ->
        match state.Phase with
        | Settling (latest, deadline, _) when awaitDeadline (Some deadline) now -> settle latest
        | _ -> state, noResponse, [], [] // 古い発火は無害（§15.1 #21後半、#3の具体化）
    | TimerElapsed (Topology ScreenGrace) ->
        match state.Phase with
        | Grace deadline when awaitDeadline (Some deadline) now ->
            { state with Phase = Stable }, noResponse, [], []
        | _ -> state, noResponse, [], []
    | TimerElapsed (Topology ReconfigurationCap) ->
        match state.Phase with
        | Reconfiguring since when awaitDeadline (Some (since + config.ReconfigurationCapMs)) now ->
            let deadline = now + config.SettleMs
            { state with Phase = Settling ([], deadline, now + config.HardDeadlineMs) },
            { Actions = []; TimerCommands = [ StartTimer (Topology ScreenSettle, config.SettleMs) ] },
            [],
            []
        | _ -> state, noResponse, [], []
    | _ -> state, noResponse, [], []
