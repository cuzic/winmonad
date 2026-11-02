/// Core.Topology（ADR-019実装順14）。architecture.md §4.4/§7.3。
/// モニター構成、各モニターに表示中のWorkspace、アクティブなモニター、画面構成変化の待ち。
/// `MonitorLifecycle.reconcile`を同梱する（§19）。
///
/// `SwitchWorkspace`のview semantics（v2.32追加）：xmonadのviewと同じ意味論（§4.4）。
/// `WorkspaceShown`/`WorkspaceHidden`/`CurrentChanged`はこれで初めて`UserCommand`からも発行される
/// ようになった（これまでは`MonitorLifecycle.reconcile`、つまりモニター接続・切断だけが発行元だった）。
/// `WmState.step`はv2.31でこれらのうち`WorkspaceShown`/`WorkspaceHidden`の消費（Placement側の
/// `HiddenByWm`/`Shown None`反映）を始めており、`CurrentChanged`の消費（フォーカスを渡す窓への
/// `RequestForeground`）はこのラウンドで追加する（WmState.fsのコメント参照）。`SwitchSettle`
/// （切替直後の揺れの吸収、§4.4「Workspace切替は「待ち」を伴う」）はこのラウンドの対象外のまま——
/// `Focus.startSwitchSettle`/`onSwitchSettleTimerFired`は既に実装されているが、実際のOS側の
/// タイミング挙動（揺れの幅・頻度）の実測がまだ無く、適切なSettleMsや再要求の発火条件を決める
/// 根拠が無いため、専用のラウンドへ切り出す。
///
/// `FocusMonitor`（v2.36追加）：対象モニターが現在表示中（visible）ならcurrentをそのモニターへ入れ替える
/// だけで、どのモニターがどのWorkspaceを表示しているかは変えない。`SwitchWorkspace`の「既にvisible」
/// 分岐と同じ`CurrentChanged`を出すので、`WmState.step`のv2.32のRequestForeground受け口をそのまま
/// 再利用できる。対象外：「Foregroundの信念が変わったらcurrentが追従する」仕組み（§4.4、R5-1）は
/// まだ存在しない（`ForegroundChanged`DomainEventは出すが誰も消費していない）。R5-1はこの追従と
/// `FocusMonitor`の順序（追従が同じstepでFocusMonitorを打ち消さないこと）を定めているが、追従自体が
/// 無い今は単に無関係——追従を実装する専用のラウンドで一緒に検討する。
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
          Returns: Map<WorkspaceId, MonitorId>
          /// 最後に観測したモニター構成（§7.1段①「全入力について無条件に最初に行う」の具体化）。
          /// `ScreenPhase`はPhaseをまたいで値を保持する場所を持たないため、Reconfiguringの解除や
          /// ReconfigurationCapの満了でSettlingへ入るときに使う「latest」をここから取る
          /// （Opusレビュー3-1：これが無いと、観測を伴わない遷移が`latest=[]`で確定しNoMonitorsになる）。
          LastObserved: MonitorSnapshot list option }

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
      Returns = Map.empty
      LastObserved = None }

let displays (s: TopologyState) : Displays = s.Displays
let phase (s: TopologyState) : ScreenPhase = s.Phase

/// 切断時の復帰先の予約（§4.4の`Returns`）。性質テストがN-3（予約の漏れ）を検査するための読み取り専用アクセサ。
let returns (s: TopologyState) : Map<WorkspaceId, MonitorId> = s.Returns

let private shownScreens (d: Displays) : Screen list =
    match d with
    | NoMonitors -> []
    | Displays (current, visible) -> current :: visible

let isShown (ws: WorkspaceId) (s: TopologyState) : bool =
    shownScreens s.Displays |> List.exists (fun sc -> sc.Workspace = ws)

/// 現在表示中のモニターの一覧（§15.1 #17のX1が参照する「Topologyが表示するWorkspace」と対になる射影）。
let private currentMonitors (s: TopologyState) : MonitorSnapshot list =
    shownScreens s.Displays |> List.map (fun sc -> sc.Monitor)

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
                    // ただし、他のモニターへの`Returns`を持つWorkspaceは「予約済み」として除外する
                    // （Opusレビュー3-3：除外しないと、cascaded disconnect/reconnectの途中で無関係な
                    // 新規モニターがReturns待ちのWorkspaceを奪い、§15.1 #18「各Workspaceは最初の所有者へ戻る」
                    // が破れる）。
                    let bySmallestUnshown =
                        Seq.initInfinite id
                        |> Seq.map WorkspaceId
                        |> Seq.find (fun ws -> not (Set.contains ws alreadyShown) && not (Map.containsKey ws returns))

                    let chosen =
                        byReturn
                        |> Option.orElse byAssignment
                        |> Option.defaultValue bySmallestUnshown

                    // 選ばれた経路（byReturn/byAssignment/bySmallestUnshown）を問わず、`chosen`の予約は
                    // ここで必ず解除する（round2レビューN-3の修正）：byAssignmentで表示された場合も予約を
                    // 残したままにすると、そのWorkspaceが新しいモニターへの割当候補から永久に外れ続ける
                    // （元のモニターが戻ってきても二度と受け取れない）。「予約より明示的な表示を優先する」。
                    let returns' = Map.remove chosen returns

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

/// `ws`をcurrentへ表示する（xmonadのview）内部実装。`shownBy`/`clearReturn`を呼び出し側が選べる
/// （v2.39、R5-1の`ForegroundFollow`との共有のためパラメータ化した）。呼び出し側は`state.Phase`が
/// `Reconfiguring`でないことを保証すること（Displaysが未確定の間はview自体が無意味）。
let private showWorkspaceAs (shownBy: ShownBy) (clearReturn: bool) (ws: WorkspaceId) (state: TopologyState) : TopologyState * DomainEvent list =
    match state.Displays with
    | NoMonitors -> state, []
    | Displays (current, _) when current.Workspace = ws -> state, [] // 既にcurrent
    | Displays (current, visible) ->
        let returns' = if clearReturn then Map.remove ws state.Returns else state.Returns
        match visible |> List.tryFind (fun sc -> sc.Workspace = ws) with
        | Some targetScreen ->
            let visible' = current :: (visible |> List.filter (fun sc -> sc.Workspace <> ws))
            { state with Displays = Displays (targetScreen, visible'); Returns = returns' },
            [ CurrentChanged ws ]
        | None ->
            let oldWorkspace = current.Workspace
            let newCurrent = { current with Workspace = ws }
            { state with Displays = Displays (newCurrent, visible); Returns = returns' },
            [ WorkspaceHidden oldWorkspace; WorkspaceShown (ws, shownBy) ]

/// `ws`をcurrentへ表示する（xmonadのview、§4.4「SwitchWorkspaceの意味」）。`UserCommand (SwitchWorkspace ws)`の
/// 非`Reconfiguring`分岐そのもの（v2.32）をここへ抽出した（v2.38）。ユーザーの明示操作なので
/// `ShownBy = UserView`、`ReturnMonitor`は解除する。`FocusMostRecent`（`WmState.step`）が行き先の窓の
/// Workspaceがhiddenな場合に同じ遷移を再利用するために公開する。
let showWorkspace (ws: WorkspaceId) (state: TopologyState) : TopologyState * DomainEvent list =
    showWorkspaceAs UserView true ws state

/// R5-1「hiddenなWorkspaceの窓がフォアグラウンドになった」（§4.4、v2.39）：`SwitchWorkspace`と同じview
/// 遷移だが`ShownBy = ForegroundFollow`で、`ReturnMonitor`は保持する（ユーザーの明示操作ではないため）。
/// 呼び出し側（`WmState.step`）はこの後`RequestForeground`を発行しない——`WorkspaceShown (_, UserView)`
/// だけを拾う既存の受け口（v2.32）が`ForegroundFollow`には反応しないので、自然にそうなる。
let showWorkspaceAsForegroundFollow (ws: WorkspaceId) (state: TopologyState) : TopologyState * DomainEvent list =
    showWorkspaceAs ForegroundFollow false ws state

/// R5-1「アクティブなモニターの追従」（§4.4、v2.39）：`ws`が既にvisible（current以外）なモニターに
/// 表示されていれば、そのモニターをcurrentにする。`FocusMonitor`の「既にvisible」分岐と同じ操作だが、
/// 意図的にDomainEventを出さない——`CurrentChanged`を出すと既存の受け口（v2.32）がそのWorkspaceの
/// zipperのFocusへ`RequestForeground`してしまうが、この追従はOS側で既にフォーカスが済んでいる窓を
/// 内部のcurrent帳簿に反映するだけの処理であり、フォーカスの再要求は不要（かつ有害：別の窓の
/// zipperのFocusへ要求してしまう可能性がある）。
let followForegroundToVisibleMonitor (ws: WorkspaceId) (state: TopologyState) : TopologyState =
    match state.Displays with
    | Displays (current, visible) when current.Workspace <> ws ->
        match visible |> List.tryFind (fun sc -> sc.Workspace = ws) with
        | Some targetScreen ->
            let visible' = current :: (visible |> List.filter (fun sc -> sc.Workspace <> ws))
            { state with Displays = Displays (targetScreen, visible') }
        | None -> state
    | _ -> state

// ===== ScreenPhase（§7.3）：抑止区間・安定待ち・grace window =====

/// 段②：ScreenPhaseの遷移。ScreenSettle縦切り（ADR-019決定8、性質テスト#21）の中心。
let update (config: TopologyConfig) (now: int64) (input: InputEvent) (state: TopologyState) : TopologyState * Response<TimerId, Effect> * DomainEvent list * DecisionTrace list =
    let noResponse = { Actions = []; TimerCommands = [] }

    /// 安定を確定し、最新トポロジでreconcileし、全体再観測を発行してGraceへ進む（§7.3）。
    let settle (st: TopologyState) (latest: MonitorSnapshot list) =
        let state', events = MonitorLifecycle.reconcile st latest
        let graceDeadline = now + config.GraceMs
        { state' with Phase = Grace graceDeadline },
        { Actions = [ ObserveAll ]
          TimerCommands =
            [ CancelTimer (TimerId.Topology ScreenSettle)
              StartTimer (TimerId.Topology ScreenGrace, config.GraceMs) ] },
        events @ [ TopologySettled latest ],
        []

    let enterSettling (st: TopologyState) (latest: MonitorSnapshot list) (hardDeadline: int64) =
        let deadline = now + config.SettleMs
        { st with Phase = Settling (latest, deadline, hardDeadline) },
        { Actions = []; TimerCommands = [ StartTimer (TimerId.Topology ScreenSettle, config.SettleMs) ] },
        [],
        []

    /// Reconfiguringの解除／ReconfigurationCap満了でSettlingへ入るときに使う最新トポロジ。これらの入力
    /// 自身はモニター構成を運ばないため、段①で無条件に更新している`LastObserved`を使う（無ければ現在の
    /// Displaysのモニターをそのまま使い、reconcileを実質no-opにする。Opusレビュー3-1：これが無いと、
    /// 観測を伴わない遷移が`latest=[]`で確定してしまい、ロック→アンロックだけでNoMonitorsになる）。
    let latestForForcedSettle () = state.LastObserved |> Option.defaultValue (currentMonitors state)

    match input with
    | MonitorTopologyObserved snapshots ->
        // 段①相当：全入力について無条件に最初に行う。抑止区間中も観測そのものは記録する（Opusレビュー3-1）。
        let state1 = { state with LastObserved = Some snapshots }
        match state1.Phase with
        | Reconfiguring _ -> state1, noResponse, [], [] // 抑止区間中はPhaseを変えない
        | Stable
        | Grace _ when Set.ofList snapshots = Set.ofList (currentMonitors state1) ->
            // 変化が無いトポロジ観測ではSettlingへ入らない（Opusレビュー3-2：入ってしまうと、settleが出す
            // ObserveAllの応答が同じトポロジを含むたびにSettling→Grace→Settling…を無限に繰り返す）。
            state1, noResponse, [], []
        | Stable
        | Grace _ -> enterSettling state1 snapshots (now + config.HardDeadlineMs)
        | Settling (_, _, hardDeadline) when now >= hardDeadline ->
            // hardDeadlineを過ぎたら、イベントが続いていても強制的に確定する（§7.3）。
            settle state1 snapshots
        | Settling (_, _, hardDeadline) -> enterSettling state1 snapshots hardDeadline
    | ScreenReconfigurationInProgress true ->
        match state.Phase with
        | Reconfiguring _ -> state, noResponse, [], []
        | _ ->
            { state with Phase = Reconfiguring now },
            { Actions = []; TimerCommands = [ StartTimer (TimerId.Topology ReconfigurationCap, config.ReconfigurationCapMs) ] },
            [],
            []
    | ScreenReconfigurationInProgress false ->
        match state.Phase with
        | Reconfiguring _ ->
            let state', response, events, trace = enterSettling state (latestForForcedSettle ()) (now + config.HardDeadlineMs)
            state',
            { response with TimerCommands = CancelTimer (TimerId.Topology ReconfigurationCap) :: response.TimerCommands },
            events,
            trace
        | _ -> state, noResponse, [], []
    | UserCommand (SwitchWorkspace ws) ->
        match state.Phase with
        | Reconfiguring _ ->
            // 抑止区間中はSwitchWorkspaceを含むどのUserCommandも、強制的にSettlingへ入らせる
            // （下の`UserCommand _`と同じ規則。抑止区間中は安定したDisplaysが無いのでview自体が無意味）。
            let state', response, events, trace = enterSettling state (latestForForcedSettle ()) (now + config.HardDeadlineMs)
            state',
            { response with TimerCommands = CancelTimer (TimerId.Topology ReconfigurationCap) :: response.TimerCommands },
            events,
            trace
        | _ ->
            // §4.4「SwitchWorkspaceの意味」：xmonadのview。`ws`が既にvisibleなモニターnに表示されていれば
            // currentをnへ入れ替えるだけ（画面の中身は変わらない）。hiddenなら（未知のwsも含む——
            // WorkspaceSetは全域なので空のWorkspaceとして扱える）、currentの画面の表示をwsへ入れ替え、
            // それまで表示していたWorkspaceがhiddenになる。NoMonitorsなら何もしない。greedyView
            // （2画面の表示を交換する）は既定にしない（§4.4、ユーザーが見ていない側まで入れ替わるため）。
            // v2.38で`showWorkspace`へ抽出した（`FocusMostRecent`との共有）。
            let state', events = showWorkspace ws state
            state', noResponse, events, []
    | UserCommand (FocusMonitor m) ->
        match state.Phase with
        | Reconfiguring _ ->
            let state', response, events, trace = enterSettling state (latestForForcedSettle ()) (now + config.HardDeadlineMs)
            state',
            { response with TimerCommands = CancelTimer (TimerId.Topology ReconfigurationCap) :: response.TimerCommands },
            events,
            trace
        | _ ->
            // §4.4「アクティブなモニターを切り替える」：対象モニターが現在visible（接続済み）なら
            // currentをそのモニターへ入れ替えるだけ（どのモニターがどのWorkspaceを表示しているかは
            // 変えない）。SwitchWorkspaceの「既にvisible」分岐と同じ`CurrentChanged`を出すので、
            // 切替先WorkspaceのzipperのFocusへのRequestForegroundは`WmState.step`のv2.32の受け口を
            // そのまま再利用できる。「既にcurrent」を明示的なガードにはしない——`current`と`visible`は
            // 互いに素という不変条件があるため、`m = current.Monitor.Id`のときvisibleをいくら探しても
            // 絶対に見つからず、下の`None`分岐（無害な何もしない）へ自然に落ちる（SwitchWorkspaceの
            // 同じ形の分岐とは違い、ここでは「既にcurrent」と「接続されていないMonitorId」が完全に
            // 同じ結果になるため、分けて書く意味が無い）。
            match state.Displays with
            | NoMonitors -> state, noResponse, [], []
            | Displays (current, visible) ->
                match visible |> List.tryFind (fun sc -> sc.Monitor.Id = m) with
                | Some targetScreen ->
                    let visible' = current :: (visible |> List.filter (fun sc -> sc.Monitor.Id <> m))
                    { state with Displays = Displays (targetScreen, visible') }, noResponse, [ CurrentChanged targetScreen.Workspace ], []
                | None -> state, noResponse, [], [] // そのMonitorIdは現在表示されていない
    | UserCommand _ ->
        match state.Phase with
        | Reconfiguring _ ->
            let state', response, events, trace = enterSettling state (latestForForcedSettle ()) (now + config.HardDeadlineMs)
            state',
            { response with TimerCommands = CancelTimer (TimerId.Topology ReconfigurationCap) :: response.TimerCommands },
            events,
            trace
        | _ -> state, noResponse, [], []
    | TimerElapsed (TimerId.Topology ScreenSettle) ->
        match state.Phase with
        | Settling (latest, deadline, _) when awaitDeadline (Some deadline) now -> settle state latest
        | _ -> state, noResponse, [], [] // 古い発火は無害（§15.1 #21後半、#3の具体化）
    | TimerElapsed (TimerId.Topology ScreenGrace) ->
        match state.Phase with
        | Grace deadline when awaitDeadline (Some deadline) now ->
            { state with Phase = Stable }, noResponse, [], []
        | _ -> state, noResponse, [], []
    | TimerElapsed (TimerId.Topology ReconfigurationCap) ->
        match state.Phase with
        | Reconfiguring since when awaitDeadline (Some (since + config.ReconfigurationCapMs)) now ->
            enterSettling state (latestForForcedSettle ()) (now + config.HardDeadlineMs)
        | _ -> state, noResponse, [], []
    | _ -> state, noResponse, [], []
