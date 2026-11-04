/// architecture.md §4.4/§4.5：Core.PlacementのGeometry（LatestWins）・TargetIntegrity（ProbeOnce）・
/// Movability（発見時既定値とTargetIntegrity probe由来の遷移）の信念修正と消滅時クリーンアップ。
module Wm.Core.Tests.PlacementTests

open Xunit
open Wm.Contracts.Identifiers
open Wm.Contracts.ErrorTypes
open Wm.Contracts.Observation
open Wm.Contracts.Beliefs
open Wm.Contracts.Timers
open Wm.Contracts.CoreCommand
open Wm.Core.Placement

let private obs (sequence: int64) (v: 'T) : Observation<'T> =
    { Value = Ok v; SampleSequence = sequence; MonotonicTime = sequence; Source = "test" }

let private failedObs (sequence: int64) : Observation<'T> =
    { Value = Error { Api = "test"; Space = Win32LastError; Code = 0; ElapsedUs = 0L }; SampleSequence = sequence; MonotonicTime = sequence; Source = "test" }

let private emptyAppIdentity: AppIdentity = { Exe = None; HostExe = None; Aumid = None; CoreWindowExe = None }

let private snapshotWith
    (visibleFrameRect: Observation<PixelRect> option)
    (monitor: Observation<MonitorId> option)
    (isMinimized: Observation<bool> option)
    (presence: Observation<ObservedPresence> option)
    (targetIntegrity: Observation<IntegrityRelation> option)
    : WindowSnapshot =
    { Id = WindowId 1L
      Process = None
      AppIdentity = emptyAppIdentity
      Owner = None
      IsDialog = None
      NativeRect = None
      VisibleFrameRect = visibleFrameRect
      IsVisible = None
      IsMinimized = isMinimized
      IsMaximized = None
      Cloak = None
      Monitor = monitor
      MinWidthHint = None
      TargetIntegrity = targetIntegrity
      Responsiveness = None
      Presence = presence
      DpiAwareness = None
      TitleMatches = None }

let private rect: PixelRect = { X = 0; Y = 0; Width = 800; Height = 600 }

// ===== Geometry（LatestWins） =====

[<Fact>]
let ``onWindowObservedはVisibleFrameRect・Monitor・IsMinimized・Presenceを修正する`` () =
    let snapshot =
        snapshotWith (Some (obs 1L rect)) (Some (obs 1L (MonitorId 0L))) (Some (obs 1L false)) (Some (obs 1L VisibleToUser)) None
    let state', effects, _ = onWindowObserved 10L snapshot initial
    let g = Map.find (WindowId 1L) state'.Geometry
    Assert.Equal(Some rect, LatestWins.value g.VisibleFrameRect)
    Assert.Equal(Some (MonitorId 0L), LatestWins.value g.Monitor)
    Assert.Equal(Some false, LatestWins.value g.IsMinimized)
    Assert.Equal(Some VisibleToUser, LatestWins.value g.Presence)
    // NativeRectはこのラウンドでは修正しない(既知のギャップ)。
    Assert.True(match g.NativeRect with Unformed [] -> true | _ -> false)
    // TargetIntegrityが未観測(None)で発見時なので、probeを要求するEffectが出る(下記の専用テストで別途確認)。
    Assert.Equal<Effect list>([ Reprobe (WindowId 1L, KTargetIntegrity) ], effects)

[<Fact>]
let ``onWindowObservedは観測が無いフィールドを変更しない`` () =
    let snapshot = snapshotWith (Some (obs 1L rect)) None None None None
    let state', _, _ = onWindowObserved 10L snapshot initial
    let g = Map.find (WindowId 1L) state'.Geometry
    Assert.Equal(Some rect, LatestWins.value g.VisibleFrameRect)
    Assert.Equal(None, LatestWins.value g.Monitor)
    Assert.Equal(None, LatestWins.value g.IsMinimized)
    Assert.Equal(None, LatestWins.value g.Presence)

[<Fact>]
let ``onWindowObservedを2回続けて呼ぶと、より新しいSampleSequenceの観測が採用される`` () =
    let s1 = snapshotWith (Some (obs 1L rect)) None None None None
    let largerRect = { rect with Width = 1200 }
    let s2 = snapshotWith (Some (obs 2L largerRect)) None None None None
    let state1, _, _ = onWindowObserved 10L s1 initial
    let state2, _, _ = onWindowObserved 20L s2 state1
    let g = Map.find (WindowId 1L) state2.Geometry
    Assert.Equal(Some largerRect, LatestWins.value g.VisibleFrameRect)

[<Fact>]
let ``onMinimizedStateChangedはIsMinimizedだけを修正する`` () =
    let snapshot = snapshotWith (Some (obs 1L rect)) None None None None
    let afterObserved, _, _ = onWindowObserved 10L snapshot initial
    let afterMinimized = onMinimizedStateChanged 20L (WindowId 1L) (obs 2L true) afterObserved
    let g = Map.find (WindowId 1L) afterMinimized.Geometry
    Assert.Equal(Some true, LatestWins.value g.IsMinimized)
    Assert.Equal(Some rect, LatestWins.value g.VisibleFrameRect) // 他のフィールドは変わらない

// ===== Movability（発見時の既定値、§4.5） =====

[<Fact>]
let ``窓の発見時にMovabilityは楽観的既定値(Movable, Inferred[])になる`` () =
    let snapshot = snapshotWith None None None None None
    let state', _, _ = onWindowObserved 10L snapshot initial
    match Map.find (WindowId 1L) state'.Movability with
    | { Value = Movable; Confidence = Inferred [] } -> ()
    | other -> Assert.True(false, sprintf "unexpected: %A" other)

// ===== Movability（ユーザー操作による1回だけの試し直し、v2.83、architecture.md §4.5 R4-11/R6-2） =====

[<Fact>]
let ``NotMovableな窓にonUserCommandを呼ぶと、Movableへ1回だけ試し直される`` () =
    let state =
        { initial with
            Movability = Map.add (WindowId 1L) { Value = NotMovable; Confidence = Inferred [ Outcome (1L, RequestId 1L) ]; LastRevisedAt = 1L } initial.Movability }
    let state' = onUserCommand 20L (WindowId 1L) state
    match Map.find (WindowId 1L) state'.Movability with
    | { Value = Movable; Confidence = Inferred []; LastRevisedAt = 20L } -> ()
    | other -> Assert.True(false, sprintf "unexpected: %A" other)

[<Fact>]
let ``既にMovableな窓にonUserCommandを呼んでも変化しない(冪等)`` () =
    let state =
        { initial with
            Movability = Map.add (WindowId 1L) { Value = Movable; Confidence = Confirmed; LastRevisedAt = 1L } initial.Movability }
    let state' = onUserCommand 20L (WindowId 1L) state
    Assert.Equal<Belief<Movability>>(Map.find (WindowId 1L) state.Movability, Map.find (WindowId 1L) state'.Movability)

[<Fact>]
let ``Movability信念が未形成の窓にonUserCommandを呼んでも何も作られない`` () =
    let state' = onUserCommand 20L (WindowId 1L) initial
    Assert.False(Map.containsKey (WindowId 1L) state'.Movability)

// ===== TargetIntegrity（ProbeOnce） =====

[<Fact>]
let ``TargetIntegrityが未観測で発見時ならReprobe Effectを発行する`` () =
    let snapshot = snapshotWith None None None None None
    let state', effects, timerCmds = onWindowObserved 10L snapshot initial
    Assert.Equal<Effect list>([ Reprobe (WindowId 1L, KTargetIntegrity) ], effects)
    Assert.Empty(timerCmds)
    Assert.True(match Map.find (WindowId 1L) state'.TargetIntegrity with Unformed _ -> true | _ -> false)

[<Fact>]
let ``TargetIntegrityが既にマップにあれば、再度Noneで届いてもReprobeを出さない`` () =
    let s1 = snapshotWith None None None None None
    let state1, _, _ = onWindowObserved 10L s1 initial
    let s2 = snapshotWith None None None None None
    let state2, effects2, _ = onWindowObserved 20L s2 state1
    Assert.Empty(effects2)
    Assert.True(Map.containsKey (WindowId 1L) state2.TargetIntegrity)

[<Fact>]
let ``TargetIntegrityのOk観測はConfirmedに修正し、再probeの待ちを取り消す`` () =
    let s1 = snapshotWith None None None None (Some (failedObs 1L))
    let state1, _, _ = onWindowObserved 10L s1 initial
    Assert.True(Map.containsKey (WindowId 1L) state1.IntegrityReprobes) // 失敗したので再probeを予約済み
    let s2 = snapshotWith None None None None (Some (obs 2L NotHigher))
    let state2, _, timerCmds2 = onWindowObserved 20L s2 state1
    match Map.find (WindowId 1L) state2.TargetIntegrity with
    | Believed { Value = NotHigher; Confidence = Confirmed } -> ()
    | other -> Assert.True(false, sprintf "unexpected: %A" other)
    Assert.False(Map.containsKey (WindowId 1L) state2.IntegrityReprobes)
    Assert.Equal<TimerCommand<TimerId> list>([ CancelTimer (TimerId.Placement (IntegrityReprobe (WindowId 1L))) ], timerCmds2)

[<Fact>]
let ``TargetIntegrityがHigherと観測されると、その時に限りMovabilityがNotMovableへ修正される`` () =
    let snapshot = snapshotWith None None None None (Some (obs 1L Higher))
    let state', _, _ = onWindowObserved 10L snapshot initial
    match Map.find (WindowId 1L) state'.Movability with
    | { Value = NotMovable } -> ()
    | other -> Assert.True(false, sprintf "unexpected: %A" other)

[<Fact>]
let ``TargetIntegrityのError観測は再probeタイマーを開始する`` () =
    let snapshot = snapshotWith None None None None (Some (failedObs 1L))
    let state', effects, timerCmds = onWindowObserved 10L snapshot initial
    Assert.Empty(effects) // Reprobe自体はタイマー発火時に出す(下記)。この場では予約だけ。
    Assert.Equal<TimerCommand<TimerId> list>(
        [ StartTimer (TimerId.Placement (IntegrityReprobe (WindowId 1L)), integrityReprobeBaseDelayMs) ],
        timerCmds)
    Assert.True(Map.containsKey (WindowId 1L) state'.IntegrityReprobes)

[<Fact>]
let ``onIntegrityReprobeTimerFiredはReprobeを再発行し次の試行を予約する`` () =
    let snapshot = snapshotWith None None None None (Some (failedObs 1L))
    let afterFirstFailure, _, _ = onWindowObserved 10L snapshot initial
    let (_, deadline) = Map.find (WindowId 1L) afterFirstFailure.IntegrityReprobes
    let state', effects, timerCmds = onIntegrityReprobeTimerFired deadline (WindowId 1L) afterFirstFailure
    Assert.Equal<Effect list>([ Reprobe (WindowId 1L, KTargetIntegrity) ], effects)
    Assert.NotEmpty(timerCmds)
    let (retry', _) = Map.find (WindowId 1L) state'.IntegrityReprobes
    Assert.Equal(2, retry'.AttemptCount)

[<Fact>]
let ``onIntegrityReprobeTimerFiredは上限到達後に打ち切る`` () =
    // 初回失敗（onWindowObserved）で1回分予約済み（AttemptCount=1）。integrityReprobeMaxAttempts(3)回の
    // タイマー発火でAttemptCountが2→3→打ち切り(3>=3)まで進む。
    let snapshot = snapshotWith None None None None (Some (failedObs 1L))
    let afterFirstFailure, _, _ = onWindowObserved 10L snapshot initial
    let fireOnce (state: PlacementState) =
        let (_, deadline) = Map.find (WindowId 1L) state.IntegrityReprobes
        let state', _, _ = onIntegrityReprobeTimerFired deadline (WindowId 1L) state
        state'
    let afterAllRetries = afterFirstFailure |> fireOnce |> fireOnce |> fireOnce
    Assert.False(Map.containsKey (WindowId 1L) afterAllRetries.IntegrityReprobes)

// ===== 消滅時クリーンアップ =====

[<Fact>]
let ``onWindowVanishedはGeometry・Movability・TargetIntegrity・IntegrityReprobesを除く`` () =
    let snapshot = snapshotWith (Some (obs 1L rect)) None None None (Some (failedObs 1L))
    let afterObserved, _, _ = onWindowObserved 10L snapshot initial
    Assert.True(Map.containsKey (WindowId 1L) afterObserved.Geometry)
    Assert.True(Map.containsKey (WindowId 1L) afterObserved.Movability)
    Assert.True(Map.containsKey (WindowId 1L) afterObserved.TargetIntegrity)
    Assert.True(Map.containsKey (WindowId 1L) afterObserved.IntegrityReprobes)
    let afterVanished, timerCmds = onWindowVanished (WindowId 1L) afterObserved
    Assert.False(Map.containsKey (WindowId 1L) afterVanished.Geometry)
    Assert.False(Map.containsKey (WindowId 1L) afterVanished.Movability)
    Assert.False(Map.containsKey (WindowId 1L) afterVanished.TargetIntegrity)
    Assert.False(Map.containsKey (WindowId 1L) afterVanished.IntegrityReprobes)
    Assert.Equal<TimerCommand<TimerId> list>([ CancelTimer (TimerId.Placement (IntegrityReprobe (WindowId 1L))) ], timerCmds)

[<Fact>]
let ``onWindowVanishedは再probe待ちが無ければCancelTimerを出さない`` () =
    let snapshot = snapshotWith (Some (obs 1L rect)) None None None None
    let afterObserved, _, _ = onWindowObserved 10L snapshot initial
    let _, timerCmds = onWindowVanished (WindowId 1L) afterObserved
    Assert.Empty(timerCmds)

// ===== DpiBehavior（Accumulating、§4.5 S11、v2.44：発見時の申告による初期値のみ） =====

let private snapshotWithDpiAwareness (declared: Observation<DpiAwarenessDeclared> option) : WindowSnapshot =
    { snapshotWith (Some (obs 1L rect)) None None None None with DpiAwareness = declared }

[<Fact>]
let ``PerMonitorV2の申告はDpiBehaviorをRescalesItselfの初期値にする(v2.44)`` () =
    let state', _, _ = onWindowObserved 10L (snapshotWithDpiAwareness (Some (obs 1L PerMonitorV2))) initial
    match Map.tryFind (WindowId 1L) state'.DpiBehavior with
    | Some { Value = RescalesItself; Confidence = Inferred _ } -> ()
    | other -> Assert.True(false, sprintf "expected Inferred RescalesItself, got %A" other)

[<Theory>]
[<InlineData(0)>] // Unaware
[<InlineData(1)>] // SystemAware
[<InlineData(2)>] // PerMonitor
let ``PerMonitorV2以外の申告はDpiBehaviorをOsBitmapScaledの初期値にする(v2.44)`` (case: int) =
    let declared = match case with 0 -> Unaware | 1 -> SystemAware | _ -> PerMonitor
    let state', _, _ = onWindowObserved 10L (snapshotWithDpiAwareness (Some (obs 1L declared))) initial
    match Map.tryFind (WindowId 1L) state'.DpiBehavior with
    | Some { Value = OsBitmapScaled; Confidence = Inferred _ } -> ()
    | other -> Assert.True(false, sprintf "expected Inferred OsBitmapScaled, got %A" other)

[<Fact>]
let ``DpiAwarenessの申告が無い(未観測)場合はDpiBehaviorの証拠を作らない(v2.44)`` () =
    let state', _, _ = onWindowObserved 10L (snapshotWithDpiAwareness None) initial
    Assert.Equal(None, Map.tryFind (WindowId 1L) state'.DpiBehavior)

[<Fact>]
let ``DpiAwarenessの問い合わせが失敗(Error)した場合もDpiBehaviorの証拠を作らない(v2.44)`` () =
    let state', _, _ = onWindowObserved 10L (snapshotWithDpiAwareness (Some (failedObs 1L))) initial
    Assert.Equal(None, Map.tryFind (WindowId 1L) state'.DpiBehavior)

[<Fact>]
let ``DpiBehaviorは申告を初期値とするだけで、2回目以降の申告では変えない(v2.44)`` () =
    let afterFirst, _, _ = onWindowObserved 10L (snapshotWithDpiAwareness (Some (obs 1L PerMonitorV2))) initial
    // 2回目の観測で(矛盾する)別の申告が届いても、発見時の初期値から変えない
    // (このラウンドでは「実際の2段目リサイズの有無」だけが修正の根拠、型直前のコメント参照)。
    let afterSecond, _, _ = onWindowObserved 20L (snapshotWithDpiAwareness (Some (obs 2L SystemAware))) afterFirst
    match Map.tryFind (WindowId 1L) afterSecond.DpiBehavior with
    | Some { Value = RescalesItself } -> ()
    | other -> Assert.True(false, sprintf "expected the first declaration (RescalesItself) to stick, got %A" other)

[<Fact>]
let ``onWindowVanishedはDpiBehaviorも消す(v2.44)`` () =
    let afterObserved, _, _ = onWindowObserved 10L (snapshotWithDpiAwareness (Some (obs 1L PerMonitorV2))) initial
    Assert.True(Map.containsKey (WindowId 1L) afterObserved.DpiBehavior)
    let afterVanished, _ = onWindowVanished (WindowId 1L) afterObserved
    Assert.False(Map.containsKey (WindowId 1L) afterVanished.DpiBehavior)

// ===== DpiBehaviorの2段目自己リサイズ検出（checkDpiCrossing、v2.45） =====

let private dpi96Then192 (m: MonitorId) : int option =
    if m = MonitorId 0L then Some 96 elif m = MonitorId 1L then Some 192 else None

[<Fact>]
let ``異なるDPIのモニターへ移った直後、矩形がまだ変わらなければDpiCrossingへ記録して待つ(v2.45)`` () =
    let w = WindowId 1L
    let afterFirst, _, _ = onWindowObserved 10L (snapshotWith (Some (obs 1L rect)) (Some (obs 1L (MonitorId 0L))) None None None) initial
    let oldMonitor = LatestWins.value (Map.find w afterFirst.Geometry).Monitor
    let afterSecond, _, _ = onWindowObserved 20L (snapshotWith (Some (obs 2L rect)) (Some (obs 2L (MonitorId 1L))) None None None) afterFirst
    let afterCheck = checkDpiCrossing 20L dpi96Then192 w oldMonitor afterSecond
    Assert.Equal(Some rect, Map.tryFind w afterCheck.DpiCrossing)
    // まだ実際の2段目リサイズを見ていないので、DpiBehaviorは(申告が無ければ)未確定のまま。
    Assert.Equal(None, Map.tryFind w afterCheck.DpiBehavior)

[<Fact>]
let ``2段目の自己リサイズが実際に観測されたら、RescalesItselfをInferredで記録しDpiCrossingを消す(v2.45)`` () =
    let w = WindowId 1L
    let resizedRect = { rect with Width = rect.Width + 50 }
    let afterFirst, _, _ = onWindowObserved 10L (snapshotWith (Some (obs 1L rect)) (Some (obs 1L (MonitorId 0L))) None None None) initial
    let oldMonitor1 = LatestWins.value (Map.find w afterFirst.Geometry).Monitor
    let afterSecond, _, _ = onWindowObserved 20L (snapshotWith (Some (obs 2L rect)) (Some (obs 2L (MonitorId 1L))) None None None) afterFirst
    let afterCrossCheck = checkDpiCrossing 20L dpi96Then192 w oldMonitor1 afterSecond
    // 2段目：自発的に別の矩形へ自己リサイズする(Monitorの再観測は無い、矩形だけ変わる)。
    let afterThird, _, _ = onWindowObserved 30L (snapshotWith (Some (obs 3L resizedRect)) None None None None) afterCrossCheck
    let oldMonitor2 = LatestWins.value (Map.find w afterThird.Geometry).Monitor // 3回目はMonitorを観測していないので不変
    let afterFinalCheck = checkDpiCrossing 30L dpi96Then192 w oldMonitor2 afterThird
    Assert.Equal(None, Map.tryFind w afterFinalCheck.DpiCrossing)
    match Map.tryFind w afterFinalCheck.DpiBehavior with
    | Some { Value = RescalesItself; Confidence = Inferred _ } -> ()
    | other -> Assert.True(false, sprintf "expected an Inferred RescalesItself belief, got %A" other)

[<Fact>]
let ``最小化中の観測はDpiCrossingの判定に使わない(v2.76)`` () =
    // CIスパイクS24/S25が確認した最小化時の挙動(矩形が-32000付近へ移る)が、v2.73
    // (onPendingDeadlineTimerFired)・v2.75(checkUnattributedDrift)と同じ誤検知経路を
    // checkDpiCrossingにも持つ：待機中のDpiCrossingのbaselineと最小化中の矩形を比べて
    // 「2段目の自己リサイズが実際に起きた」と誤判定してはならない。
    let w = WindowId 1L
    let afterFirst, _, _ = onWindowObserved 10L (snapshotWith (Some (obs 1L rect)) (Some (obs 1L (MonitorId 0L))) None None None) initial
    let oldMonitor1 = LatestWins.value (Map.find w afterFirst.Geometry).Monitor
    let afterSecond, _, _ = onWindowObserved 20L (snapshotWith (Some (obs 2L rect)) (Some (obs 2L (MonitorId 1L))) None None None) afterFirst
    let afterCrossCheck = checkDpiCrossing 20L dpi96Then192 w oldMonitor1 afterSecond
    Assert.Equal(Some rect, Map.tryFind w afterCrossCheck.DpiCrossing) // baselineを記録して待っている状態
    // ユーザーが最小化する。観測される矩形が-32000付近へ移る(CIスパイクS24/S25)。
    let minimizedRect = { rect with X = -32000; Y = -32000 }
    let afterMinimized, _, _ =
        onWindowObserved 30L (snapshotWith (Some (obs 3L minimizedRect)) None (Some (obs 3L true)) None None) afterCrossCheck
    let oldMonitor2 = LatestWins.value (Map.find w afterMinimized.Geometry).Monitor
    let afterFinalCheck = checkDpiCrossing 30L dpi96Then192 w oldMonitor2 afterMinimized
    // baselineは消えず、RescalesItselfも確定しない(最小化はアプリの自己リサイズではない)。
    Assert.Equal(Some rect, Map.tryFind w afterFinalCheck.DpiCrossing)
    Assert.Equal(None, Map.tryFind w afterFinalCheck.DpiBehavior)

[<Fact>]
let ``同じDPIの別モニターへ移ってもDpiCrossingを記録しない(v2.45)`` () =
    let w = WindowId 1L
    let sameDpi (_: MonitorId) : int option = Some 96
    let afterFirst, _, _ = onWindowObserved 10L (snapshotWith (Some (obs 1L rect)) (Some (obs 1L (MonitorId 0L))) None None None) initial
    let oldMonitor = LatestWins.value (Map.find w afterFirst.Geometry).Monitor
    let afterSecond, _, _ = onWindowObserved 20L (snapshotWith (Some (obs 2L rect)) (Some (obs 2L (MonitorId 1L))) None None None) afterFirst
    let afterCheck = checkDpiCrossing 20L sameDpi w oldMonitor afterSecond
    Assert.Equal(None, Map.tryFind w afterCheck.DpiCrossing)

[<Fact>]
let ``モニターのDPIが分からない場合はDpiCrossingを記録しない(v2.45)`` () =
    let w = WindowId 1L
    let unknownDpi (_: MonitorId) : int option = None
    let afterFirst, _, _ = onWindowObserved 10L (snapshotWith (Some (obs 1L rect)) (Some (obs 1L (MonitorId 0L))) None None None) initial
    let oldMonitor = LatestWins.value (Map.find w afterFirst.Geometry).Monitor
    let afterSecond, _, _ = onWindowObserved 20L (snapshotWith (Some (obs 2L rect)) (Some (obs 2L (MonitorId 1L))) None None None) afterFirst
    let afterCheck = checkDpiCrossing 20L unknownDpi w oldMonitor afterSecond
    Assert.Equal(None, Map.tryFind w afterCheck.DpiCrossing)

[<Fact>]
let ``onWindowVanishedはDpiCrossingも消す(v2.45)`` () =
    let w = WindowId 1L
    let afterFirst, _, _ = onWindowObserved 10L (snapshotWith (Some (obs 1L rect)) (Some (obs 1L (MonitorId 0L))) None None None) initial
    let oldMonitor = LatestWins.value (Map.find w afterFirst.Geometry).Monitor
    let afterSecond, _, _ = onWindowObserved 20L (snapshotWith (Some (obs 2L rect)) (Some (obs 2L (MonitorId 1L))) None None None) afterFirst
    let afterCheck = checkDpiCrossing 20L dpi96Then192 w oldMonitor afterSecond
    Assert.True(Map.containsKey w afterCheck.DpiCrossing)
    let afterVanished, _ = onWindowVanished w afterCheck
    Assert.False(Map.containsKey w afterVanished.DpiCrossing)

// ===== FrameInset（v2.114、Round 2スライス1の純粋な修正規則；v2.117でモニターIdを使う形に見直し、
// PlacementStateへのキャッシュ配線を追加） =====

let private m0 = Some (MonitorId 0L)

[<Fact>]
let ``pad2py-pad-host実機で実測した値(X+7/Width-14/Height-7/Y不変)をそのまま証拠として採用する(v2.114/v2.117)`` () =
    let native: PixelRect = { X = 0; Y = 0; Width = 1920; Height = 1240 }
    let visible: PixelRect = { X = 7; Y = 0; Width = 1906; Height = 1233 }
    match reviseFrameInset m0 false false native visible with
    | Some inset -> Assert.Equal({ Left = 7; Top = 0; Right = 7; Bottom = 7; Monitor = MonitorId 0L }, inset)
    | None -> Assert.True(false, "expected the realistic inset to be accepted")

[<Fact>]
let ``モニターが分からない観測は採用しない(v2.117)`` () =
    let native: PixelRect = { X = 0; Y = 0; Width = 1920; Height = 1240 }
    let visible: PixelRect = { X = 7; Y = 0; Width = 1906; Height = 1233 }
    Assert.Equal(None, reviseFrameInset None false false native visible)

[<Fact>]
let ``最大化中の観測は採用しない(v2.114、1-B対策)`` () =
    let native: PixelRect = { X = -8; Y = -8; Width = 1936; Height = 1256 }
    let visible: PixelRect = { X = 0; Y = 0; Width = 1920; Height = 1240 }
    Assert.Equal(None, reviseFrameInset m0 true false native visible)

[<Fact>]
let ``最小化中の観測は採用しない(v2.114、1-B対策)`` () =
    let native: PixelRect = { X = -32000; Y = -32000; Width = 160; Height = 28 }
    let visible: PixelRect = { X = -32000; Y = -32000; Width = 160; Height = 28 }
    Assert.Equal(None, reviseFrameInset m0 false true native visible)

[<Fact>]
let ``NativeRectとVisibleFrameRectが完全に一致する(生成直後等)観測は採用しない(v2.114、1-C対策)`` () =
    let rect: PixelRect = { X = 100; Y = 100; Width = 300; Height = 200 }
    Assert.Equal(None, reviseFrameInset m0 false false rect rect)

[<Fact>]
let ``粗い範囲外の異常な差分は採用しない(v2.114/v2.117、1-D/1-G対策)`` () =
    // 150%スケーリングされたプロセスが100%モニターのNativeRectをそのまま読んだ場合等を想定した
    // 極端な値——frameInsetCrudeMaxPx(=64)を大きく超える。
    let native: PixelRect = { X = 0; Y = 0; Width = 1920; Height = 1240 }
    let visible: PixelRect = { X = 200; Y = 0; Width = 1520; Height = 1240 }
    Assert.Equal(None, reviseFrameInset m0 false false native visible)

// ===== PlacementState.FrameInsets への配線（v2.117：onWindowObserved/onWindowVanished） =====

[<Fact>]
let ``onWindowObservedはNativeRect/VisibleFrameRectが両方Okの観測からFrameInsetsを更新する(v2.117)`` () =
    let w = WindowId 1L
    let native: PixelRect = { X = 0; Y = 0; Width = 1920; Height = 1240 }
    let visible: PixelRect = { X = 7; Y = 0; Width = 1906; Height = 1233 }
    let snapshot =
        { snapshotWith (Some (obs 1L visible)) (Some (obs 1L (MonitorId 0L))) None None None with NativeRect = Some (obs 1L native) }
    let state', _, _ = onWindowObserved 10L snapshot initial
    Assert.Equal(Some { Left = 7; Top = 0; Right = 7; Bottom = 7; Monitor = MonitorId 0L }, Map.tryFind w state'.FrameInsets)

[<Fact>]
let ``onWindowObservedはNativeRectが無い観測ではFrameInsetsを変えない(v2.117)`` () =
    let w = WindowId 1L
    let visible: PixelRect = { X = 7; Y = 0; Width = 1906; Height = 1233 }
    let snapshot = snapshotWith (Some (obs 1L visible)) (Some (obs 1L (MonitorId 0L))) None None None
    let state', _, _ = onWindowObserved 10L snapshot initial
    Assert.Equal(None, Map.tryFind w state'.FrameInsets)

[<Fact>]
let ``onWindowVanishedはFrameInsetsも消す(v2.117)`` () =
    let w = WindowId 1L
    let native: PixelRect = { X = 0; Y = 0; Width = 1920; Height = 1240 }
    let visible: PixelRect = { X = 7; Y = 0; Width = 1906; Height = 1233 }
    let snapshot =
        { snapshotWith (Some (obs 1L visible)) (Some (obs 1L (MonitorId 0L))) None None None with NativeRect = Some (obs 1L native) }
    let state', _, _ = onWindowObserved 10L snapshot initial
    Assert.True(Map.containsKey w state'.FrameInsets)
    let afterVanished, _ = onWindowVanished w state'
    Assert.False(Map.containsKey w afterVanished.FrameInsets)
