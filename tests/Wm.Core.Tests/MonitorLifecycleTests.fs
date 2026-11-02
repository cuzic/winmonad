/// architecture.md §15.1 #18「モニターの切断・再接続で各Workspaceは最初の所有者へ戻る」
/// （Hyprlandの`cascadedDisconnectsReturnEachWorkspaceToItsFirstOwner`と同型）を、
/// `Core.Topology.MonitorLifecycle.reconcile`に対して直接検査する。
/// 元々は「実装済みだが性質テストが無い」状態だった（Opusレビュー3-3）。3-3の修正
/// （`bySmallestUnshown`がReturns予約済みのWorkspaceを奪わないようにした）の回帰テストでもある。
///
/// round2レビューT-4：最初のテストはモニター0,1,2の部分集合しか生成せず、「一度も見たことが無い
/// 新しいモニター」が現れなかった（3-3の失敗シナリオの本質はまさにそれだった）。モニター3,4を
/// 「最初の接続には含めず、途中の列にだけ現れ、最後にはまた外れる」よう生成し、`Returns`予約を
/// 実際に横取りできる入力空間にした（M1で旧実装がこの入力空間なら検出できることを確認済み）。
module Wm.Core.Tests.MonitorLifecycleTests

open FsCheck.Xunit
open Xunit
open Wm.Contracts.Identifiers
open Wm.Contracts.SharedVocabulary
open Wm.Policy.MonitorSelector
open Wm.Core.Topology

let private mkMonitor (id: int) : MonitorSnapshot =
    { Id = MonitorId (int64 id)
      StableKey = sprintf "TEST:%d" id
      Bounds = { X = 0; Y = 0; Width = 1920; Height = 1080 }
      WorkArea = { X = 0; Y = 0; Width = 1920; Height = 1040 }
      Dpi = 96
      IsPrimary = (id = 0) }

/// 最初に接続する3台（0,1,2 → ws0,ws1,ws2に確定する）。
let private originalMonitors = [ 0; 1; 2 ] |> List.map mkMonitor

/// モニターidをそのSetに含めるかどうかの5つ組から、現在接続中のモニター集合を作る。
/// 3,4は「最初の接続(initial0)には含めず、最後の再接続(final)にも含めない」——これにより、
/// 途中の列でだけ現れる「一度も見たことが無い新しいモニター」として振る舞う（T-4）。
let private subsetOf (incl0: bool, incl1: bool, incl2: bool, incl3: bool, incl4: bool) : MonitorSnapshot list =
    [ if incl0 then yield mkMonitor 0
      if incl1 then yield mkMonitor 1
      if incl2 then yield mkMonitor 2
      if incl3 then yield mkMonitor 3
      if incl4 then yield mkMonitor 4 ]

[<Property>]
let ``#18 新規モニターが途中で現れても、全モニターが揃えば各Workspaceは最初の所有者へ戻る``
    (steps: (bool * bool * bool * bool * bool) list)
    =
    // 最初の接続（モニター0,1,2 → ws0,ws1,ws2。bySmallestUnshownが昇順に割り当てる）。モニター3,4はまだ無い。
    let initial0, _ = MonitorLifecycle.reconcile (Wm.Core.Topology.initial Map.empty) originalMonitors

    // 任意の切断・再接続列（モニター3,4が途中で現れることがある。ユーザーのSwitchWorkspaceは挟まない）。
    let afterSteps =
        steps
        |> List.truncate 20
        |> List.fold (fun state incl -> MonitorLifecycle.reconcile state (subsetOf incl) |> fst) initial0

    // 最後に元の3台だけを再接続する（3,4は外れている）。
    let final, _ = MonitorLifecycle.reconcile afterSteps originalMonitors

    match displays final with
    | NoMonitors -> false // originalMonitorsは非空なので到達しない
    | Displays (current, visible) ->
        (current :: visible)
        |> List.forall (fun sc ->
            match sc.Monitor.Id with
            | MonitorId 0L -> sc.Workspace = WorkspaceId 0
            | MonitorId 1L -> sc.Workspace = WorkspaceId 1
            | MonitorId 2L -> sc.Workspace = WorkspaceId 2
            | _ -> false)

/// round2レビューN-3の回帰：`Assignments`経由（byAssignment）で表示されたWorkspaceの`Returns`予約が
/// 解放されないと、そのWorkspaceが後から正しい「隠れた理由のモニター」へ再登録できなくなる。
/// 具体的な失敗の筋：
///   1. ws1はPrimaryMonitorへの`Assignments`を持つ。P(primary),A,Bを接続→P→ws1, A→ws0, B→ws2。
///   2. Pを切断→ws1はhiddenになり`Returns={ws1→P}`。
///   3. 新しいprimaryのDを接続→`byAssignment`がws1をDに出す。修正前はここで`Returns={ws1→P}`が
///      残り続けた（byReturnが一致しなかったため）。
///   4. Dを切断→ws1は再びhiddenになる。修正前は(2)で記録済みの`Returns[ws1]=P`が
///      「既に値がある」として上書きされず、「ws1はDから隠れた」という正しい事実を記録できない
///      （Dが将来再接続してもws1を取り戻せない、という別の不具合になる）。
/// 修正後は、(3)でws1がDへ表示された時点で`Returns`の予約を解放するため、(4)で正しく`Returns={ws1→D}`
/// に更新される。
///
/// 注記：Pの再接続後にws1がPとDのどちらに戻るべきかは、`Returns`（Dを指す）と`Assignments`
/// （PrimaryMonitorを指す——Pも対象になる）が同じWorkspaceに競合する要求を出す、アーキテクチャ上
/// 未確定の境界ケースになるため、本テストでは検査しない（§4.4の優先順位「Returns→Assignments」は
/// 各モニター単位の判定であり、「別モニターへのReturnsがある場合にAssignmentsを保留する」規則は
/// 明文化されていない）。
[<Fact>]
let ``N-3: Assignments経由で表示されたWorkspaceのReturns予約は解放され、後の隠れた理由を正しく記録する`` () =
    let assignments = Map.ofList [ WorkspaceId 1, PrimaryMonitor ]
    let p = mkMonitor 0 // primary
    let a = mkMonitor 1
    let b = mkMonitor 2
    let d = { mkMonitor 3 with IsPrimary = true } // 新しいprimary

    // 1. P(primary),A,Bを接続。Assignmentsにより P→ws1、A→ws0（最小未使用）、B→ws2。
    let s1, _ = MonitorLifecycle.reconcile (Wm.Core.Topology.initial assignments) [ p; a; b ]

    let workspaceOnMonitor (s: TopologyState) (mid: MonitorId) =
        match displays s with
        | NoMonitors -> None
        | Displays (current, visible) -> (current :: visible) |> List.tryFind (fun sc -> sc.Monitor.Id = mid) |> Option.map (fun sc -> sc.Workspace)

    Assert.Equal(Some (WorkspaceId 1), workspaceOnMonitor s1 (MonitorId 0L))

    // 2. Pを切断。ws1がhiddenになりReturns={ws1→P(0)}。
    let s2, _ = MonitorLifecycle.reconcile s1 [ a; b ]
    Assert.Equal(Some (MonitorId 0L), Map.tryFind (WorkspaceId 1) (returns s2))

    // 3. 新しいprimary Dを接続。byAssignmentがws1をDに出す。修正後はここでReturns予約が解放される。
    let s3, _ = MonitorLifecycle.reconcile s2 [ a; b; d ]
    Assert.Equal(Some (WorkspaceId 1), workspaceOnMonitor s3 (MonitorId 3L))
    Assert.True(Map.isEmpty (returns s3), "ws1がDへ表示された時点でReturns予約は解放されているべき")

    // 4. Dを切断。ws1は再びhiddenになる。正しいReturns先はD(3)であるべき（Pではなく、修正前のまま
    //    残っていたはずの古い予約でもない）。
    let s4, _ = MonitorLifecycle.reconcile s3 [ a; b ]
    Assert.Equal(Some (MonitorId 3L), Map.tryFind (WorkspaceId 1) (returns s4))
