/// architecture.md §15.1 #18「モニターの切断・再接続で各Workspaceは最初の所有者へ戻る」
/// （Hyprlandの`cascadedDisconnectsReturnEachWorkspaceToItsFirstOwner`と同型）を、
/// `Core.Topology.MonitorLifecycle.reconcile`に対して直接検査する。
/// 元々は「実装済みだが性質テストが無い」状態だった（Opusレビュー3-3）。3-3の修正
/// （`bySmallestUnshown`がReturns予約済みのWorkspaceを奪わないようにした）の回帰テストでもある。
module Wm.Core.Tests.MonitorLifecycleTests

open FsCheck.Xunit
open Wm.Contracts.Identifiers
open Wm.Contracts.SharedVocabulary
open Wm.Core.Topology

let private mkMonitor (id: int) : MonitorSnapshot =
    { Id = MonitorId (int64 id)
      StableKey = sprintf "TEST:%d" id
      Bounds = { X = 0; Y = 0; Width = 1920; Height = 1080 }
      WorkArea = { X = 0; Y = 0; Width = 1920; Height = 1040 }
      Dpi = 96
      IsPrimary = (id = 0) }

let private allMonitors = [ 0; 1; 2 ] |> List.map mkMonitor

/// モニターidをそのSetに含めるかどうかの3つ組から、現在接続中のモニター集合を作る。
let private subsetOf (incl0: bool, incl1: bool, incl2: bool) : MonitorSnapshot list =
    [ if incl0 then yield mkMonitor 0
      if incl1 then yield mkMonitor 1
      if incl2 then yield mkMonitor 2 ]

[<Property>]
let ``#18 任意の切断・再接続列の後、全モニターが揃えば各Workspaceは最初の所有者へ戻る`` (steps: (bool * bool * bool) list) =
    // 最初の接続（モニター0,1,2 → ws0,ws1,ws2。bySmallestUnshownが昇順に割り当てる）。
    let initial0, _ = MonitorLifecycle.reconcile (Wm.Core.Topology.initial Map.empty) allMonitors

    // 任意の切断・再接続列（ユーザーのSwitchWorkspaceは挟まない。性質の前提どおり）。
    let afterSteps =
        steps
        |> List.truncate 20
        |> List.fold (fun state incl -> MonitorLifecycle.reconcile state (subsetOf incl) |> fst) initial0

    // 最後に全モニターを再接続する。
    let final, _ = MonitorLifecycle.reconcile afterSteps allMonitors

    match displays final with
    | NoMonitors -> false // allMonitorsは非空なので到達しない
    | Displays (current, visible) ->
        (current :: visible)
        |> List.forall (fun sc ->
            match sc.Monitor.Id with
            | MonitorId 0L -> sc.Workspace = WorkspaceId 0
            | MonitorId 1L -> sc.Workspace = WorkspaceId 1
            | MonitorId 2L -> sc.Workspace = WorkspaceId 2
            | _ -> false)
