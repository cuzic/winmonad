/// architecture.md §15.1 #24「ユーザーの明示的な管理指定（UserOverride）は、ClearUserOverrideを
/// 除くその後のどんな信念の修正・再分類の後でも維持される」。この性質はv2.6（R4-20）で確定し、
/// `Windows.reconsider`の`UserOverride = Some _`分岐（§4.4本文「ユーザー指定はPolicyより優先し、
/// 信念の変化を理由には上書きしない」）として実装されているが、本書の性質一覧#24を直接名指しした
/// テストがこれまで一度も無かった（v2.51までの時点で0件、他の多くの番号付き性質はFsCheckテストを
/// 持つのに対しこれだけ抜けていた）。
module Wm.Core.Tests.UserOverridePersistenceTests

open FsCheck.Xunit
open Wm.Contracts.Identifiers
open Wm.Contracts.Observation
open Wm.Contracts.SharedVocabulary
open Wm.Contracts.Timers
open Wm.Contracts.CoreCommand
open Wm.Core.Windows
open Wm.Core.WmState
open Wm.Policy.PolicyTypes

let private policy: PolicyIR =
    { PolicyIR.defaults with WindowRules = [ { Id = PolicyRuleId "any-tiled"; Priority = 10; When = AnyWindow; Apply = Tiled } ] }

let private rp: RuntimePolicy =
    { RuntimePolicy.defaults with Policy = policy; PatternSet = Wm.Policy.PolicyEvaluator.buildTitlePatternSet policy }

let private emptyAppIdentity: AppIdentity = { Exe = None; HostExe = None; Aumid = None; CoreWindowExe = None }

let private mkEnvelope (sequence: int64) (input: InputEvent) : EventEnvelope<InputEvent> =
    { Sequence = sequence; MonotonicTime = sequence; Source = "test"; SessionId = "test-session"; Input = input }

let private w = WindowId 1L

let private windowObserved (sequence: int64) (visible: bool) : InputEvent =
    WindowObserved
        { Id = w
          Process = None
          AppIdentity = emptyAppIdentity
          Owner = None
          IsDialog = None
          NativeRect = None
          VisibleFrameRect = None
          IsVisible = Some { Value = Ok visible; SampleSequence = sequence; MonotonicTime = sequence; Source = "test" }
          IsMinimized = None
          Cloak = None
          Monitor = None
          MinWidthHint = None
          TargetIntegrity = None
          Responsiveness = None
          Presence = None
          DpiAwareness = None
          TitleMatches = None }

let private managementOf (raw: int) : Management =
    match abs raw % 3 with
    | 0 -> Tiled
    | 1 -> Floating
    | _ -> Excluded

/// kind mod 6：0=WindowObserved(可視)、1=WindowObserved(不可視→候補条件を外れる)、2=VisibilityChanged、
/// 3=CloakChanged、4=SetManagement、5=ClearUserOverride。discoveredになるまで(4)(5)はCore側で
/// 無視される（`onUserCommand`の`Map.containsKey`ガード）ので、モデル側も同じガードを再現する。
let private applyOp (idx: int) (discovered: bool, expected: Management option) (state: WmState) (kind: int, raw: int) : WmState * (bool * Management option) =
    let sequence = int64 (idx + 1)
    match (abs kind) % 6 with
    | 0 -> (step rp state (mkEnvelope sequence (windowObserved sequence true))).Next, (true, expected)
    | 1 -> (step rp state (mkEnvelope sequence (windowObserved sequence false))).Next, (true, expected)
    | 2 ->
        let obs: Observation<bool> = { Value = Ok (abs raw % 2 = 0); SampleSequence = sequence; MonotonicTime = sequence; Source = "test" }
        (step rp state (mkEnvelope sequence (VisibilityChanged (w, obs)))).Next, (discovered, expected)
    | 3 ->
        let cloak: CloakState = { App = abs raw % 2 = 0; Shell = false; Inherited = false }
        let obs: Observation<CloakState> = { Value = Ok cloak; SampleSequence = sequence; MonotonicTime = sequence; Source = "test" }
        (step rp state (mkEnvelope sequence (CloakChanged (w, obs)))).Next, (discovered, expected)
    | 4 ->
        let m = managementOf raw
        let next = (step rp state (mkEnvelope sequence (UserCommand (SetManagement (w, m))))).Next
        next, (discovered, if discovered then Some m else expected)
    | _ ->
        let next = (step rp state (mkEnvelope sequence (UserCommand (ClearUserOverride w)))).Next
        next, (discovered, if discovered then None else expected)

let private actualUserOverride (s: WmState) : Management option =
    Map.tryFind w s.Windows.Records |> Option.bind (fun r -> r.UserOverride)

[<Property>]
let ``#24: UserOverrideはClearUserOverride以外のどんな信念の修正・再分類の後でも維持される`` (ops: (int * int) list) =
    let ops = ops |> List.truncate 60
    let trace =
        ops
        |> List.indexed
        |> List.scan
            (fun (discovered, expected, st) (idx, op) ->
                let st', (discovered', expected') = applyOp idx (discovered, expected) st op
                discovered', expected', st')
            (false, None, initial rp)
    trace |> List.forall (fun (_, expected, s) -> actualUserOverride s = expected)
