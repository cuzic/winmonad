/// architecture.md §15.1 #5「破棄・再発見されたHWNDを同一の生きたWindowIdと誤認しない」。
/// v2.47（HWND再利用の検知、`Windows.detectsProcessReuse`/`WindowVanishedByReuse`/`WmState.vanishWindow`）
/// 実装時はWindowsTests.fs/WmStateWindowsTests.fsの決定的なテストだけで検証していた——この性質自体を
/// FsCheckで検査する専用のテストが無かった。他窓の生成・消滅・ForegroundObservedが任意に挟まっても、
/// 一度`WindowVanishedByReuse`で消えた窓が二度と「生きている」状態（`Windows.Records`/`Identity`/
/// `ClassInputs`、`Placement.Geometry`、Workspaces、Focus.History）へ戻らないことを確認する。
///
/// R5-11「`ForgetWindow`が処理されるまでの間に旧`WindowId`で届いた観測は捨てる」はこのテストの対象外
/// （既知のギャップ、Placement.fs/Windows.fsのファイル冒頭コメント・architecture.md §4.5のSticky規則の
/// 節参照）：再利用検知後は同じ`WindowId`へ二度と観測を送らない前提で生成する（他窓の操作だけを挟む）。
module Wm.Core.Tests.HwndReuseInvariantTests

open FsCheck.Xunit
open Wm.Contracts.Identifiers
open Wm.Contracts.Observation
open Wm.Contracts.SharedVocabulary
open Wm.Contracts.CoreCommand
open Wm.Core.Windows
open Wm.Core.Workspaces
open Wm.Core.WmState
open Wm.Policy.PolicyTypes

let private policy: PolicyIR =
    { PolicyIR.defaults with WindowRules = [ { Id = PolicyRuleId "any-tiled"; Priority = 10; When = AnyWindow; Apply = Tiled } ] }

let private rp: RuntimePolicy =
    { RuntimePolicy.defaults with Policy = policy; PatternSet = Wm.Policy.PolicyEvaluator.buildTitlePatternSet policy }

let private emptyAppIdentity: AppIdentity = { Exe = None; HostExe = None; Aumid = None; CoreWindowExe = None }

let private mkEnvelope (sequence: int64) (input: InputEvent) : EventEnvelope<InputEvent> =
    { Sequence = sequence; MonotonicTime = sequence; Source = "test"; SessionId = "test-session"; Input = input }

let private observedWithProcess (sequence: int64) (w: WindowId) (pid: int) : InputEvent =
    WindowObserved
        { Id = w
          Process = Some { Value = Ok { Pid = pid; CreationTime = 1L }; SampleSequence = sequence; MonotonicTime = sequence; Source = "test" }
          AppIdentity = emptyAppIdentity
          Owner = None
          IsDialog = None
          NativeRect = None
          VisibleFrameRect = None
          IsVisible = Some { Value = Ok true; SampleSequence = sequence; MonotonicTime = sequence; Source = "test" }
          IsMinimized = None
          Cloak = None
          Monitor = None
          MinWidthHint = None
          TargetIntegrity = None
          Responsiveness = None
          Presence = None
          DpiAwareness = None
          TitleMatches = None }

/// `w`(= WindowId 1)とは重ならない「他窓」だけを対象にした操作（kind mod 3）：
/// 0=他窓の新規WindowObserved、1=他窓のWindowRemoved、2=w自身へのForegroundObserved
/// （Identity/Processには影響しない信念の反映であり、再利用判定とは無関係なノイズとして混ぜる）。
let private applyOtherOp (idx: int) (state: WmState) (kind: int, otherIdRaw: int) : WmState =
    let other = WindowId (int64 (2 + abs otherIdRaw % 3)) // 2,3,4。wのID(1)とは重ならない
    let sequence = int64 (10_000 + idx)
    match (abs kind) % 3 with
    | 0 -> (step rp state (mkEnvelope sequence (observedWithProcess sequence other (100 + abs otherIdRaw)))).Next
    | 1 -> (step rp state (mkEnvelope sequence (WindowRemoved other))).Next
    | _ ->
        let obs: Observation<WindowId option> = { Value = Ok (Some (WindowId 1L)); SampleSequence = sequence; MonotonicTime = sequence; Source = "test" }
        (step rp state (mkEnvelope sequence (ForegroundObserved obs))).Next

/// `w`が「生きている」とみなせる全ての場所（§4.4「消滅時は全ドメインの信念も除去する」の裏返し）。
let private isLive (w: WindowId) (s: WmState) : bool =
    Map.containsKey w s.Windows.Records
    || Map.containsKey w s.Windows.Identity
    || Map.containsKey w s.Windows.ClassInputs
    || Map.containsKey w s.Placement.Geometry
    || (WorkspaceSet.workspaceOf w s.Workspaces).IsSome
    || List.contains w s.Focus.History

[<Property>]
let ``#5: HWND再利用で消えたWindowIdは、他窓の操作が何回挟まっても二度と生きている状態に戻らない``
    (preOps: (int * int) list)
    (postOps: (int * int) list)
    (pid1: int)
    (pidDelta: int)
    =
    let w = WindowId 1L
    let pid1 = abs pid1 % 1000
    let pid2 = pid1 + 1 + abs pidDelta % 1000 // pid1と常に異なる
    let preOps = preOps |> List.truncate 15
    let postOps = postOps |> List.truncate 15
    // wを最初に発見・分類させる（Process = pid1でConfirmedに固定される）。
    let afterW = (step rp (initial rp) (mkEnvelope 1L (observedWithProcess 1L w pid1))).Next
    // wのIdentityには触れない他窓の操作を任意に挟む。
    let beforeReuse = preOps |> List.indexed |> List.fold (fun st (idx, op) -> applyOtherOp idx st op) afterW
    // Processが異なる値で観測される→HWND再利用の検知→vanishWindow相当の全除去。
    let afterReuse = (step rp beforeReuse (mkEnvelope 5_000L (observedWithProcess 5_000L w pid2))).Next
    // 再利用後はwへ二度と観測を送らない前提（R5-11、ファイル冒頭コメント参照）で、他窓の操作だけを続ける。
    let statesAfterReuse = postOps |> List.indexed |> List.scan (fun st (idx, op) -> applyOtherOp idx st op) afterReuse
    not (isLive w afterReuse) && statesAfterReuse |> List.forall (fun st -> not (isLive w st))
