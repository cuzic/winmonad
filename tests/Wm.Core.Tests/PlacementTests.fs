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
