/// architecture.md §15.1 #17：ドメインをまたぐ不変条件(X1)(X2)(X3前半)を検査する。
/// (X2)はWindowsドメインの実装（ADR-019実装順14）に伴い追加した。(X3後半)はPlacementドメインが
/// 無いため対象外（既知のギャップ）。
module Wm.Core.Tests.CrossDomainInvariantTests

open FsCheck.Xunit
open Wm.Contracts.Identifiers
open Wm.Contracts.Observation
open Wm.Contracts.SharedVocabulary
open Wm.Contracts.CoreCommand
open Wm.Core.Windows
open Wm.Core.Workspaces
open Wm.Core.Topology
open Wm.Core.WmState
open Wm.Policy.PolicyTypes

let private mkMonitor (id: int) : MonitorSnapshot =
    { Id = MonitorId (int64 id)
      StableKey = sprintf "TEST:%d" id
      Bounds = { X = 0; Y = 0; Width = 1920; Height = 1080 }
      WorkArea = { X = 0; Y = 0; Width = 1920; Height = 1040 }
      Dpi = 96
      IsPrimary = (id = 0) }

/// (X1) Topologyが表示するWorkspaceは全てWorkspacesで参照できる（v2.6、R4-16/R6-1）。
/// WorkspaceSetが全域（SpacesにないWorkspaceIdも空のWorkspaceとして読める）なので構造上常に成り立つ——
/// ここでは「本当にそうなっているか」を、任意のモニター構成で`MonitorLifecycle.reconcile`を通した後、
/// 表示中の全WorkspaceIdについて`WorkspaceSet.stateOf`が例外無く読めることを確認する。
[<Property>]
let ``X1: Topologyが表示する全WorkspaceはWorkspaceSetで例外無く参照できる`` (monitorIds: int list) =
    let monitors = monitorIds |> List.truncate 8 |> List.map (fun i -> abs i % 5) |> List.distinct |> List.map mkMonitor
    let topology, _events = MonitorLifecycle.reconcile (Wm.Core.Topology.initial Map.empty) monitors
    let shown =
        match displays topology with
        | NoMonitors -> []
        | Displays (current, visible) -> (current :: visible) |> List.map (fun sc -> sc.Workspace)
    // WorkspaceSet.emptyには何も実体が無いが、全域なので stateOf は常に(空の)WorkspaceStateを返す。
    shown |> List.forall (fun ws -> (WorkspaceSet.stateOf ws WorkspaceSet.empty).Id = ws)

// ===== (X3前半)：Focus.HistoryはWorkspacesに属する窓だけを含む =====

let private mkForegroundObserved (sequence: int64) (w: WindowId) : InputEvent =
    ForegroundObserved { Value = Ok (Some w); SampleSequence = sequence; MonotonicTime = sequence; Source = "test" }

let private mkEnvelope (sequence: int64) (input: InputEvent) : EventEnvelope<InputEvent> =
    { Sequence = sequence; MonotonicTime = sequence; Source = "test"; SessionId = "test-session"; Input = input }

let private seedState () : WmState =
    let topology, _events = MonitorLifecycle.reconcile (Wm.Core.Topology.initial Map.empty) [ mkMonitor 0; mkMonitor 1; mkMonitor 2 ]
    { initial RuntimePolicy.defaults with Topology = topology }

/// kind: 0=addWindow, 1=moveToWorkspace, 2=ForegroundObserved(step経由), 3=WindowRemoved(step経由)
let private applyOp (idx: int) (state: WmState) (kind: int, wsRaw: int, wRaw: int) : WmState =
    let ws = WorkspaceId (abs wsRaw % 3)
    let w = WindowId (int64 (abs wRaw % 6))
    let sequence = int64 (idx + 1)
    match (abs kind) % 4 with
    | 0 -> { state with Workspaces = WorkspaceSet.addWindow ws w false state.Workspaces }
    | 1 -> { state with Workspaces = WorkspaceSet.moveToWorkspace w ws state.Workspaces }
    | 2 -> (step RuntimePolicy.defaults state (mkEnvelope sequence (mkForegroundObserved sequence w))).Next
    | _ -> (step RuntimePolicy.defaults state (mkEnvelope sequence (WindowRemoved w))).Next

let private allWorkspaceIds = [ 0 .. 2 ] |> List.map WorkspaceId

/// Focus.Historyに含まれる全ての窓は、いずれかのWorkspaceのStackに実在する（§4.4、(X3)前半）。
let private checkX3FirstHalf (s: WmState) : bool =
    let managedWindows =
        allWorkspaceIds
        |> List.collect (fun ws -> (WorkspaceSet.stateOf ws s.Workspaces).Stack |> Option.map Zipper.toList |> Option.defaultValue [])
        |> Set.ofList
    s.Focus.History |> List.forall (fun w -> Set.contains w managedWindows)

[<Property>]
let ``X3前半: 任意の操作列の後、各ステップでFocus.Historyの要素は全てWorkspacesに実在する`` (ops: (int * int * int) list) =
    let ops = ops |> List.truncate 60
    let statesIncludingSeed = ops |> List.indexed |> List.scan (fun state (idx, op) -> applyOp idx state op) (seedState ())
    statesIncludingSeed |> List.forall checkX3FirstHalf

// ===== (X2)：Workspacesに属する窓 ⟺ Windowsで Classified (Tiled|Floating, _) =====

/// 全ての窓を即座にTiledへ分類する（AwaitingEvidenceを経由させず、Policyの評価を決定的にする）。
let private x2Policy: PolicyIR =
    { PolicyIR.defaults with WindowRules = [ { Id = PolicyRuleId "any-tiled"; Priority = 10; When = AnyWindow; Apply = Tiled } ] }

let private x2RuntimePolicy: RuntimePolicy =
    { RuntimePolicy.defaults with
        Policy = x2Policy
        PatternSet = Wm.Policy.PolicyEvaluator.buildTitlePatternSet x2Policy }

let private x2SeedState () : WmState =
    let topology, _events = MonitorLifecycle.reconcile (Wm.Core.Topology.initial Map.empty) [ mkMonitor 0; mkMonitor 1; mkMonitor 2 ]
    { initial x2RuntimePolicy with Topology = topology }

let private mkWindowObserved (w: WindowId) : InputEvent =
    WindowObserved
        { Id = w
          Process = None
          AppIdentity = { Exe = None; HostExe = None; Aumid = None; CoreWindowExe = None }
          Owner = None
          IsDialog = None
          NativeRect = None
          VisibleFrameRect = None
          IsVisible = Some { Value = Ok true; SampleSequence = 1L; MonotonicTime = 1L; Source = "test" }
          IsMinimized = None
          Cloak = None
          Monitor = None
          MinWidthHint = None
          TargetIntegrity = None
          Responsiveness = None
          Presence = None
          DpiAwareness = None
          TitleMatches = None }

/// kind: 0=WindowObserved(候補条件を満たす), 1=WindowRemoved, 2=ToggleFloating, 3=SetManagement Excluded,
/// 4=ClearUserOverride（全てstep経由。いずれも0..5の窓に対して行う）。
let private applyX2Op (idx: int) (state: WmState) (kind: int, wRaw: int) : WmState =
    let w = WindowId (int64 (abs wRaw % 6))
    let sequence = int64 (idx + 1)
    let input =
        match (abs kind) % 5 with
        | 0 -> mkWindowObserved w
        | 1 -> WindowRemoved w
        | 2 -> UserCommand (ToggleFloating w)
        | 3 -> UserCommand (SetManagement (w, Excluded))
        | _ -> UserCommand (ClearUserOverride w)
    (step x2RuntimePolicy state (mkEnvelope sequence input)).Next

/// Workspacesに属する窓は全てWindowsで`Classified (EffTiled|EffFloating, _)`であり、その逆も成り立つ
/// （§4.4、(X2)）。
let private checkX2 (s: WmState) : bool =
    let inWorkspaces =
        allWorkspaceIds
        |> List.collect (fun ws -> (WorkspaceSet.stateOf ws s.Workspaces).Stack |> Option.map Zipper.toList |> Option.defaultValue [])
        |> Set.ofList
    let classifiedManaged =
        s.Windows.Records
        |> Map.toSeq
        |> Seq.choose (fun (w, r) -> match r.Classification with Classified (EffTiled, _) | Classified (EffFloating, _) -> Some w | _ -> None)
        |> Set.ofSeq
    inWorkspaces = classifiedManaged

[<Property>]
let ``X2: 任意の操作列の後、各ステップでWorkspacesに属する窓とWindowsでClassified(Tiled|Floating)の窓は一致する``
    (ops: (int * int) list)
    =
    let ops = ops |> List.truncate 60
    let statesIncludingSeed = ops |> List.indexed |> List.scan (fun state (idx, op) -> applyX2Op idx state op) (x2SeedState ())
    statesIncludingSeed |> List.forall checkX2
