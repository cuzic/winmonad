/// architecture.md §15.1 #16(F2)を、WmState.stepを実際に通して検査する。
/// (F2) focused s = ManagedWindow w ならば、wのWorkspaceのzipperのFocus=w かつ head History = w。
/// Windowsドメインの分類がまだ無いため、「管理下の窓」はテスト側でWorkspaceSet.addWindowを使って
/// 事前に配置したものに限定する（Windows実装後は分類確定イベントが同じ役割を果たす）。
module Wm.Core.Tests.WmStateFocusTests

open FsCheck.Xunit
open Wm.Contracts.Identifiers
open Wm.Contracts.SharedVocabulary
open Wm.Contracts.CoreCommand
open Wm.Core.Workspaces
open Wm.Core.Topology
open Wm.Core.WmState

let private mkMonitor (id: int) : MonitorSnapshot =
    { Id = MonitorId (int64 id)
      StableKey = sprintf "TEST:%d" id
      Bounds = { X = 0; Y = 0; Width = 1920; Height = 1080 }
      WorkArea = { X = 0; Y = 0; Width = 1920; Height = 1040 }
      Dpi = 96
      IsPrimary = (id = 0) }

/// 3つのWorkspace(0,1,2)に窓0..5を分配して配置し、同じ3つをTopologyが表示中にした初期状態。
/// Workspace/Topologyの双方を実際に結線しないとfocusedが常にElsewhereになってしまうため、
/// `MonitorLifecycle.reconcile`を直接呼んで3モニターを表示状態にする（3つのWorkspaceが割り当たる）。
let private seedState (placements: (int * int) list) : WmState =
    let workspaces =
        placements
        |> List.fold
            (fun ws (wsRaw, wRaw) ->
                WorkspaceSet.addWindow (WorkspaceId (abs wsRaw % 3)) (WindowId (int64 (abs wRaw % 6))) false ws)
            WorkspaceSet.empty

    let topology, _events =
        MonitorLifecycle.reconcile (Wm.Core.Topology.initial Map.empty) [ mkMonitor 0; mkMonitor 1; mkMonitor 2 ]

    { initial RuntimePolicy.defaults with Workspaces = workspaces; Topology = topology }

let private mkForegroundObserved (sequence: int64) (w: WindowId) : InputEvent =
    ForegroundObserved { Value = Ok (Some w); SampleSequence = sequence; MonotonicTime = sequence; Source = "test" }

/// 現在のfocusedについてF2が成り立つかを検査する（Elsewhereの場合はF2の前提を満たさないので無条件に真）。
let private checkF2 (s: WmState) : bool =
    match focused s with
    | Elsewhere -> true
    | ManagedWindow w ->
        match WorkspaceSet.workspaceOf w s.Workspaces with
        | None -> false // 管理下にあるはずなのに所属が無い:矛盾
        | Some ws ->
            let st = WorkspaceSet.stateOf ws s.Workspaces
            let zipperFocusOk = match st.Stack with Some z -> z.Focus = w | None -> false
            let historyHeadOk = match s.Focus.History with head :: _ -> head = w | [] -> false
            zipperFocusOk && historyHeadOk

/// 性質テストが常にElsewhereへ落ちて自明に成立しているだけでないことを示すための決定的な健全性チェック。
[<Xunit.Fact>]
let ``健全性チェック: seedStateとForegroundObservedだけで実際にManagedWindowへ到達する`` () =
    let seeded = seedState [ (0, 0); (1, 1) ]
    let envelope: EventEnvelope<InputEvent> =
        { Sequence = 1L; MonotonicTime = 1L; Source = "test"; SessionId = "test-session"
          Input = mkForegroundObserved 1L (WindowId 0L) }
    let next = (step RuntimePolicy.defaults seeded envelope).Next
    match focused next with
    | ManagedWindow w -> Xunit.Assert.Equal(WindowId 0L, w)
    | Elsewhere -> Xunit.Assert.Fail("expected ManagedWindow, got Elsewhere — focused()/step() wiring is broken")

[<Property>]
let ``F2: focused=ManagedWindow wならwのWorkspaceのzipper.Focus=wかつhead History=w`` (placements: (int * int) list) (focusSeq: int list) =
    let placements = placements |> List.truncate 10
    let seeded = seedState placements

    let managedWindows =
        [ 0 .. 2 ]
        |> List.map WorkspaceId
        |> List.collect (fun ws -> (WorkspaceSet.stateOf ws seeded.Workspaces).Stack |> Option.map Zipper.toList |> Option.defaultValue [])
        |> List.distinct

    if List.isEmpty managedWindows then
        true // 配置が無ければ検査対象が無い(自明に成立)
    else
        let focusTargets = focusSeq |> List.truncate 30 |> List.map (fun i -> managedWindows.[abs i % managedWindows.Length])

        let statesIncludingSeed =
            focusTargets
            |> List.indexed
            |> List.scan
                (fun (state: WmState) (idx, w) ->
                    let sequence = int64 (idx + 1)
                    let envelope: EventEnvelope<InputEvent> =
                        { Sequence = sequence; MonotonicTime = sequence; Source = "test"; SessionId = "test-session"
                          Input = mkForegroundObserved sequence w }
                    (step RuntimePolicy.defaults state envelope).Next)
                seeded

        statesIncludingSeed |> List.forall checkF2
