/// `op=trypolicy`（architecture.md §10.2/§10.3、v2.152）の核——`WmState.classifyAllUnder`/
/// `remapTitleMatches`（`private`、ここでは`classifyAllUnder`経由で間接的に検査する）。Opus敵対的
/// 相談`opus-review-fork-design.md`のF-aが指摘した「タイトル証拠は採用中のPatternSetのハッシュに
/// 紐付いており、候補のPatternSetが違うと素朴な再評価では常にAwaitingEvidenceへ落ちる」という
/// 回帰を、実際にPatternSetの要素数を変えて検査する（パターン文字列が同じでも並び/集合が違えば
/// ハッシュも違う、という退化しないケースを選ぶ——`WmStatePlacementTests.fs`等と同じ決定的シナリオ
/// スタイル）。
module Wm.Core.Tests.WmStateTryPolicyTests

open Xunit
open Wm.Contracts.Identifiers
open Wm.Contracts.Beliefs
open Wm.Contracts.Observation
open Wm.Contracts.SharedVocabulary
open Wm.Contracts.CoreCommand
open Wm.Core.Topology
open Wm.Core.WmState
open Wm.Policy.PolicyTypes
open Wm.Policy.PolicyEvaluator

let private mkMonitor (id: int) : MonitorSnapshot =
    { Id = MonitorId(int64 id)
      StableKey = sprintf "TEST:%d" id
      Bounds = { X = 0; Y = 0; Width = 1920; Height = 1080 }
      WorkArea = { X = 0; Y = 0; Width = 1920; Height = 1040 }
      Dpi = 96
      IsPrimary = (id = 0) }

/// "TextEdit"は常にマッチしないダミーパターン——候補が"Notepad"だけを参照する1パターンのPolicyに
/// なることで、候補のPatternSetが要素数2→1へ実際に変わり、ハッシュが本当に異なる（偶然一致する
/// 退化ケースを避ける）。
let private livePolicy: PolicyIR =
    { PolicyIR.defaults with
        WindowRules =
            [ { Id = PolicyRuleId "textedit-rule"; Priority = 2; When = TitleContains "TextEdit"; Apply = Tiled }
              { Id = PolicyRuleId "notepad-rule"; Priority = 1; When = TitleContains "Notepad"; Apply = Floating } ] }

let private liveRuntimePolicy: RuntimePolicy =
    { RuntimePolicy.defaults with Policy = livePolicy; PatternSet = buildTitlePatternSet livePolicy }

let private mkEnvelope (sequence: int64) (input: InputEvent) : EventEnvelope<InputEvent> =
    { Sequence = sequence; MonotonicTime = sequence; Source = "test"; SessionId = "test-session"; Input = input }

let private windowObservedWithTitle (w: WindowId) (titleVector: TitleMatchVector) : InputEvent =
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
          IsMaximized = None
          Style = None
          Cloak = None
          Monitor = None
          MinWidthHint = None
          TargetIntegrity = None
          Responsiveness = None
          Presence = None
          DpiAwareness = None
          TitleMatches = Some { Value = Ok titleVector; SampleSequence = 1L; MonotonicTime = 1L; Source = "test" } }

/// 窓1つが"Notepad"にだけマッチする状態（`livePolicy`の下で`Floating`、`ByPolicy`で確定済み）を作る。
let private seedClassifiedState () : WmState =
    let topology, _ = MonitorLifecycle.reconcile (Wm.Core.Topology.initial Map.empty) [ mkMonitor 0 ]
    let state0 = { initial liveRuntimePolicy with Topology = topology }
    let notepadVector = computeTitleMatchVector liveRuntimePolicy.PatternSet "My Notepad Window"
    (step liveRuntimePolicy state0 (mkEnvelope 1L (windowObservedWithTitle (WindowId 1L) notepadVector))).Next

[<Fact>]
let ``候補が元と同じPolicyなら、観測済みの窓はWouldClassifyAsで元と同じ結果になる`` () =
    let state = seedClassifiedState ()
    let outcomes = classifyAllUnder liveRuntimePolicy.PatternSet liveRuntimePolicy 2L state
    Assert.Equal<(WindowId * ClassifyOutcome) list>(
        [ WindowId 1L, WouldClassifyAs(Floating, Some(PolicyRuleId "notepad-rule")) ],
        outcomes)

[<Fact>]
let ``候補のPatternSetが元と違っても(要素数が変わりハッシュも違う)、共通するタイトルパターンは安全に付け替えられる(F-a回帰テスト)`` () =
    let state = seedClassifiedState ()
    // "Notepad"だけを参照する1パターンのPolicy——liveの2パターンとはハッシュが異なるが、
    // "Notepad"自体はliveにも存在するので、付け替えられてAwaitingEvidenceへ落ちないはず。
    let candidatePolicy =
        { PolicyIR.defaults with
            WindowRules = [ { Id = PolicyRuleId "candidate-notepad"; Priority = 1; When = TitleContains "Notepad"; Apply = Excluded } ] }
    let candidateRuntimePolicy = { liveRuntimePolicy with Policy = candidatePolicy; PatternSet = buildTitlePatternSet candidatePolicy }
    Assert.NotEqual<string>(liveRuntimePolicy.PatternSet.PatternSetHash, candidateRuntimePolicy.PatternSet.PatternSetHash)
    let outcomes = classifyAllUnder liveRuntimePolicy.PatternSet candidateRuntimePolicy 2L state
    Assert.Equal<(WindowId * ClassifyOutcome) list>(
        [ WindowId 1L, WouldClassifyAs(Excluded, Some(PolicyRuleId "candidate-notepad")) ],
        outcomes)

[<Fact>]
let ``候補が一度も測定されていない新しいタイトルパターンを参照するなら、Undeterminableとして報告する`` () =
    let state = seedClassifiedState ()
    let candidatePolicy =
        { PolicyIR.defaults with
            WindowRules = [ { Id = PolicyRuleId "candidate-calculator"; Priority = 1; When = TitleContains "Calculator"; Apply = Excluded } ] }
    let candidateRuntimePolicy = { liveRuntimePolicy with Policy = candidatePolicy; PatternSet = buildTitlePatternSet candidatePolicy }
    let outcomes = classifyAllUnder liveRuntimePolicy.PatternSet candidateRuntimePolicy 2L state
    Assert.Equal<(WindowId * ClassifyOutcome) list>([ WindowId 1L, Undeterminable [ "Calculator" ] ], outcomes)
