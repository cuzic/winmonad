/// architecture.md §12.1：VirtualWorld.applyEffectの個別の分岐を、実際のReducerパイプラインが生成した
/// `GatedEffect`を使って検査する。
///
/// `GatedEffect`のコンストラクタは`internal`で`Wm.Core`/`Wm.Core.Tests`だけに構築を許している
/// （§6.2、Opusレビュー2-9：「誰でも偽のGatedEffectを作れると、internalコンストラクタが実質何も
/// 守らなくなる」）。`Wm.Simulation`はGatedEffectを消費するだけの側であり、この制約の対象に
/// 意図的に含まれている（§17「GatedEffectを消費するだけのWm.Windows/Wm.SimulationがWm.Coreの
/// アセンブリ全体を参照することになり依存方向が曖昧になる」）。したがってこのファイルのテストは、
/// 必ず実際の`Reducer.Submit`が返す`StepResult.Effects`（ゲートを実際に通過した本物のGatedEffect）
/// だけを使う——`applyEffect`を手作りのGatedEffectで単体テストすることは、この設計上できない
/// （意図的な制約であり、見落としではない）。
module Wm.Simulation.Tests.VirtualWorldScenarioTests

open Xunit
open Wm.Contracts.Identifiers
open Wm.Contracts.Observation
open Wm.Contracts.Beliefs
open Wm.Contracts.SharedVocabulary
open Wm.Contracts.CoreCommand
open Wm.Contracts.GatedEffect
open Wm.Core.Topology
open Wm.Core.WmState
open Wm.Runtime.Reducer
open Wm.Simulation.WindowBehaviors
open Wm.Simulation.VirtualWorld
open Wm.Simulation.ScenarioRunner

let private mkMonitor (id: int) : MonitorSnapshot =
    { Id = MonitorId (int64 id)
      StableKey = sprintf "TEST:%d" id
      Bounds = { X = 0; Y = 0; Width = 1920; Height = 1080 }
      WorkArea = { X = 0; Y = 0; Width = 1920; Height = 1080 }
      Dpi = 96
      IsPrimary = (id = 0) }

let private emptyAppIdentity: AppIdentity = { Exe = None; HostExe = None; Aumid = None; CoreWindowExe = None }

let private visibleWindowObserved (w: WindowId) : InputEvent =
    WindowObserved
        { Id = w
          Process = None
          AppIdentity = emptyAppIdentity
          Owner = None
          IsDialog = None
          NativeRect = None
          VisibleFrameRect = None
          IsVisible = Some { Value = Ok true; SampleSequence = 0L; MonotonicTime = 0L; Source = "virtual-world" }
          IsMinimized = None
          Cloak = None
          Monitor = None
          MinWidthHint = None
          TargetIntegrity = None
          Responsiveness = None
          Presence = None
          DpiAwareness = None
          TitleMatches = None }

let private settleTopology (reducer: Reducer) (monitors: MonitorSnapshot list) : unit =
    reducer.Submit 0L "virtual-world" (MonitorTopologyObserved monitors) |> ignore
    reducer.AdvanceToNextTimer () |> ignore

[<Fact>]
let ``VirtualWorldに存在しない窓へのSetRectはERROR_INVALID_WINDOW_HANDLEになり、Pendingは消えずに残る`` () =
    // Coreは窓が生きていると思っているが、疑似OS側にその窓が無い（OSで既に消えたのにCoreがまだ
    // 知らない、という競合を模す）。VirtualWorld.addWindowを呼ばないことでこれを表す。
    let reducer = Reducer(RuntimePolicy.defaults, "scenario-session", "test-build", "test-policy-hash", None)
    settleTopology reducer [ mkMonitor 0 ]
    let w = WindowId 1L
    let discovery = reducer.Submit 1000L "virtual-world" (visibleWindowObserved w)
    Assert.Equal(1, Map.count reducer.State.Placement.Pending) // Reconcileが要求を出している

    // applyEffectの戻り値そのもの（callReturns）を直接検査する。GatedEffectは実際のReducerパイプライン
    // が生成した本物を使う（このファイルの冒頭コメント参照）。discovery.Effectsには
    // WindowsのTargetIntegrity probe要求（Reprobe）も混ざるので、SetRectだけを取り出す。
    let setRectEffects = discovery.Effects |> List.filter (fun g -> match GatedEffect.effect g with SetRect _ -> true | _ -> false)
    match setRectEffects with
    | [ gated ] ->
        let _, events, callReturns = applyEffect 1000L empty gated
        Assert.Empty(events)
        match callReturns with
        | [ (_, Error record, dispatchSeq) ] ->
            Assert.Equal(1400, record.Code)
            Assert.Equal(Wm.Contracts.ErrorTypes.Win32LastError, record.Space)
            Assert.Equal(None, dispatchSeq) // 対象窓が存在しない：発行自体が届いていない
        | other -> Assert.True(false, sprintf "unexpected callReturns: %A" other)
    | other -> Assert.True(false, sprintf "expected exactly 1 SetRect effect from Reconcile, got %A" other)

    let worldWithoutTheWindow = empty // addWindowを呼ばない
    let finalWorld = settle 1000L reducer worldWithoutTheWindow discovery.Effects

    Assert.Empty(finalWorld.Windows)
    // v2.22でEffectCallReturnedを配線した：ERROR_INVALID_WINDOW_HANDLE(1400)はclassifyでTargetGoneに
    // 分類され、Timeoutを待たずに即座にPendingがCancelledになる（§7.2「EffectCallReturned(Error) →
    // classify → TargetGone → Cancelled」）。
    Assert.Empty(reducer.State.Placement.Pending)
    Assert.Equal(None, Map.tryFind w reducer.State.Placement.Desired)

[<Fact>]
let ``SwitchWorkspaceでhidden化された窓はSetPresence(shown=false)がVirtualWorldへ届き、PendingPresenceがSatisfiedになる(v2.50)`` () =
    // v2.17時点のファイル冒頭コメントは「ReconcileがまだSetRect以外を発行しないため`SetPresence`等は
    // 対象外」としていたが、v2.31でReconcileは実際にSetPresenceを発行するようになっていた——
    // VirtualWorld側がそれを黒洞のように無視していた（`applyEffect`の決め打ち`[]`）既知のギャップ。
    let reducer = Reducer(RuntimePolicy.defaults, "scenario-session", "test-build", "test-policy-hash", None)
    settleTopology reducer [ mkMonitor 0 ]
    let w = WindowId 1L
    let world = empty |> addWindow w { X = 0; Y = 0; Width = 10; Height = 10 } Immediate
    let discovery = reducer.Submit 1000L "virtual-world" (visibleWindowObserved w)
    let worldAfterDiscovery = settle 1000L reducer world discovery.Effects
    Assert.Empty(reducer.State.Placement.Pending)
    Assert.True((Map.find w worldAfterDiscovery.Windows).Shown)

    // 別のWorkspaceへ切り替えると、wのいるWorkspace(0)がhiddenになり、SetPresence(shown=false)が出る。
    let switchAway = reducer.Submit 2000L "virtual-world" (UserCommand (SwitchWorkspace (WorkspaceId 1)))
    let setPresenceEffects = switchAway.Effects |> List.filter (fun g -> match GatedEffect.effect g with SetPresence _ -> true | _ -> false)
    Assert.NotEmpty(setPresenceEffects)
    let finalWorld = settle 2000L reducer worldAfterDiscovery switchAway.Effects

    Assert.False((Map.find w finalWorld.Windows).Shown)
    Assert.Empty(reducer.State.Placement.PendingPresence) // 観測が戻ってSatisfiedになっている

[<Fact>]
let ``SwitchWorkspaceで表示に戻った窓はSetPresence(shown=true)とRequestForegroundの両方がVirtualWorldで観測を生む(v2.50)`` () =
    let reducer = Reducer(RuntimePolicy.defaults, "scenario-session", "test-build", "test-policy-hash", None)
    settleTopology reducer [ mkMonitor 0 ]
    let w = WindowId 1L
    let world = empty |> addWindow w { X = 0; Y = 0; Width = 10; Height = 10 } Immediate
    let discovery = reducer.Submit 1000L "virtual-world" (visibleWindowObserved w)
    let worldAfterDiscovery = settle 1000L reducer world discovery.Effects
    let switchAway = reducer.Submit 2000L "virtual-world" (UserCommand (SwitchWorkspace (WorkspaceId 1)))
    let worldHidden = settle 2000L reducer worldAfterDiscovery switchAway.Effects
    Assert.False((Map.find w worldHidden.Windows).Shown)

    // ws0へ戻る(UserView)：SetPresence(shown=true)に加え、zipperのFocus(=w)へRequestForegroundが出る。
    let switchBack = reducer.Submit 3000L "virtual-world" (UserCommand (SwitchWorkspace (WorkspaceId 0)))
    let effects = switchBack.Effects |> List.map GatedEffect.effect
    Assert.Contains(effects, (function SetPresence (_, w', true) -> w' = w | _ -> false))
    Assert.Contains(effects, (function RequestForeground (_, w') -> w' = w | _ -> false))
    let finalWorld = settle 3000L reducer worldHidden switchBack.Effects

    Assert.True((Map.find w finalWorld.Windows).Shown)
    Assert.Equal(Some (Some w), Wm.Contracts.Beliefs.LatestWins.value reducer.State.Focus.Foreground)

[<Fact>]
let ``ForgetWindowはVirtualWorldから窓を取り除くだけで、観測もEffectCallReturnedも生まない(v2.50)`` () =
    let w = WindowId 1L
    let world = empty |> addWindow w { X = 0; Y = 0; Width = 10; Height = 10 } Immediate
    // ForgetWindowはRequestIdを持たないfire-and-forgetなEffectなので、本物のGatedEffectを得るために
    // 実際のHWND再利用シナリオ(v2.47)を駆動する必要はない——ここでは直接WindowsテストやWmStateテストが
    // 使っているのと同じ構成でEffectを手に入れる手段が無いため、直接Core.Windowsの性質で保証されている
    // Effectの意味（窓を対応表から外す、7.1）だけを単体で検査する。
    let gated =
        // GatedEffectのコンストラクタはinternalなので、Wm.Core側の実経路（HWND再利用）を駆動して得る。
        let reducer = Reducer(RuntimePolicy.defaults, "scenario-session", "test-build", "test-policy-hash", None)
        let withProcess (sequence: int64) (pid: int) : InputEvent =
            WindowObserved
                { Id = w
                  Process = Some { Value = Ok { Pid = pid; CreationTime = 1L }; SampleSequence = sequence; MonotonicTime = sequence; Source = "test" }
                  AppIdentity = emptyAppIdentity
                  Owner = None; IsDialog = None; NativeRect = None; VisibleFrameRect = None
                  IsVisible = Some { Value = Ok true; SampleSequence = sequence; MonotonicTime = sequence; Source = "test" }
                  IsMinimized = None; Cloak = None; Monitor = None; MinWidthHint = None
                  TargetIntegrity = None; Responsiveness = None; Presence = None; DpiAwareness = None; TitleMatches = None }
        reducer.Submit 1L "virtual-world" (withProcess 1L 100) |> ignore
        let result = reducer.Submit 2L "virtual-world" (withProcess 2L 200)
        result.Effects |> List.filter (fun g -> match GatedEffect.effect g with ForgetWindow w' -> w' = w | _ -> false) |> List.exactlyOne
    let world', events, callReturns = applyEffect 3L world gated
    Assert.Empty(events)
    Assert.Empty(callReturns)
    Assert.False(Map.containsKey w world'.Windows)

[<Fact>]
let ``窓の発見時にPlacementが出すReprobe(KTargetIntegrity)は、TargetIntegrityだけを運ぶ部分観測を返す(v2.51)`` () =
    let reducer = Reducer(RuntimePolicy.defaults, "scenario-session", "test-build", "test-policy-hash", None)
    settleTopology reducer [ mkMonitor 0 ]
    let w = WindowId 1L
    let world = empty |> addWindow w { X = 0; Y = 0; Width = 10; Height = 10 } Immediate
    // 発見時の観測にTargetIntegrityが含まれていないので、Placementが能動的にReprobeを発行する(§21)。
    let discovery = reducer.Submit 1000L "virtual-world" (visibleWindowObserved w)
    let reprobeEffects = discovery.Effects |> List.filter (fun g -> match GatedEffect.effect g with Reprobe (w', KTargetIntegrity) -> w' = w | _ -> false)
    Assert.Equal(1, List.length reprobeEffects)
    let world', events, callReturns = applyEffect 1000L world (List.exactlyOne reprobeEffects)
    Assert.Empty(callReturns) // ReprobeはRequestIdを持たないEffectなので結果は無い
    match events with
    | [ WindowObserved snapshot ] ->
        Assert.Equal(w, snapshot.Id)
        Assert.Equal(Some (Ok NotHigher), snapshot.TargetIntegrity |> Option.map (fun o -> o.Value))
        // 部分観測であることの確認：TargetIntegrity以外のフィールドは一切運ばない。
        Assert.Equal(None, snapshot.VisibleFrameRect)
        Assert.Equal(None, snapshot.Presence)
    | other -> Assert.True(false, sprintf "unexpected events: %A" other)
    // この観測をCoreへ戻すと、Placement.TargetIntegrityの信念が実際にBelieved NotHigherへ確定する。
    let afterObserved = events |> List.fold (fun (r: Reducer) ev -> r.Submit 1001L "virtual-world" ev |> ignore; r) reducer
    match Map.tryFind w afterObserved.State.Placement.TargetIntegrity with
    | Some (Believed { Value = NotHigher; Confidence = Confirmed }) -> ()
    | other -> Assert.True(false, sprintf "unexpected: %A" other)

    // 昇格窓(Integrity = Higher)を模した場合は、Reprobeの観測もHigherを運ぶ——決め打ちのNotHigherで
    // なく実際にVirtualWindow.Integrityを読んでいることを確認する。
    let elevatedWindow = { Map.find w world.Windows with Integrity = Higher }
    let elevatedWorld = { world with Windows = Map.add w elevatedWindow world.Windows }
    let _, elevatedEvents, _ = applyEffect 1000L elevatedWorld (List.exactlyOne reprobeEffects)
    match elevatedEvents with
    | [ WindowObserved snapshot ] -> Assert.Equal(Some (Ok Higher), snapshot.TargetIntegrity |> Option.map (fun o -> o.Value))
    | other -> Assert.True(false, sprintf "unexpected events: %A" other)

[<Fact>]
let ``ScreenSettle確定でTopologyが発行するObserveAllは、VirtualWorld上の全窓を完全なWindowObservedとして運ぶ(v2.51)`` () =
    let reducer = Reducer(RuntimePolicy.defaults, "scenario-session", "test-build", "test-policy-hash", None)
    let w1, w2 = WindowId 1L, WindowId 2L
    let world =
        empty
        |> addWindow w1 { X = 0; Y = 0; Width = 10; Height = 10 } Immediate
        |> addWindow w2 { X = 5; Y = 5; Width = 20; Height = 20 } Immediate
    reducer.Submit 0L "virtual-world" (MonitorTopologyObserved [ mkMonitor 0 ]) |> ignore
    let settleResult = (reducer.AdvanceToNextTimer ()).Value // ScreenSettle満了：ObserveAllを発行してGraceへ
    let observeAllEffects = settleResult.Effects |> List.filter (fun g -> match GatedEffect.effect g with ObserveAll -> true | _ -> false)
    Assert.Equal(1, List.length observeAllEffects)
    let _, events, callReturns = applyEffect 100L world (List.exactlyOne observeAllEffects)
    Assert.Empty(callReturns)
    let observedIds = events |> List.choose (function WindowObserved s -> Some s.Id | _ -> None) |> Set.ofList
    Assert.Equal<Set<WindowId>>(Set.ofList [ w1; w2 ], observedIds)
    // 部分観測ではなく全体再列挙なので、Rect/Presence/TargetIntegrityを全て運ぶ。
    for ev in events do
        match ev with
        | WindowObserved s ->
            Assert.True(s.VisibleFrameRect.IsSome)
            Assert.True(s.Presence.IsSome)
            Assert.True(s.TargetIntegrity.IsSome)
        | _ -> ()

[<Fact>]
let ``CloseFocusedが出すRequestCloseはVirtualWorldから窓を取り除きWindowRemovedを生む(v2.51)`` () =
    let reducer = Reducer(RuntimePolicy.defaults, "scenario-session", "test-build", "test-policy-hash", None)
    settleTopology reducer [ mkMonitor 0 ]
    let w = WindowId 1L
    let world = empty |> addWindow w { X = 0; Y = 0; Width = 10; Height = 10 } Immediate
    let discovery = reducer.Submit 1000L "virtual-world" (visibleWindowObserved w)
    let world' = settle 1000L reducer world discovery.Effects
    let fg: Observation<WindowId option> = { Value = Ok (Some w); SampleSequence = 100L; MonotonicTime = 1500L; Source = "test" }
    reducer.Submit 1500L "virtual-world" (ForegroundObserved fg) |> ignore

    let closeResult = reducer.Submit 1600L "virtual-world" (UserCommand CloseFocused)
    let requestCloseEffects = closeResult.Effects |> List.filter (fun g -> match GatedEffect.effect g with RequestClose (_, w') -> w' = w | _ -> false)
    Assert.Equal(1, List.length requestCloseEffects)

    let finalWorld = settle 1600L reducer world' closeResult.Effects
    Assert.False(Map.containsKey w finalWorld.Windows)
    // VirtualWorldが返したWindowRemovedが実際にCoreへ戻り、消滅のクリーンアップが走っている。
    Assert.False(Map.containsKey w reducer.State.Windows.Records)

[<Fact>]
let ``2つ目の窓が現れても、両方がMonocleで全画面に配置され両方Satisfiedになる`` () =
    let reducer = Reducer(RuntimePolicy.defaults, "scenario-session", "test-build", "test-policy-hash", None)
    settleTopology reducer [ mkMonitor 0 ]
    let w1, w2 = WindowId 1L, WindowId 2L
    let world1 = empty |> addWindow w1 { X = 0; Y = 0; Width = 10; Height = 10 } Immediate
    let d1 = reducer.Submit 1000L "virtual-world" (visibleWindowObserved w1)
    let world1' = settle 1000L reducer world1 d1.Effects
    Assert.Empty(reducer.State.Placement.Pending)

    let world2 = world1' |> addWindow w2 { X = 0; Y = 0; Width = 10; Height = 10 } Immediate
    let d2 = reducer.Submit 2000L "virtual-world" (visibleWindowObserved w2)
    let finalWorld = settle 2000L reducer world2 d2.Effects

    let workArea: PixelRect = (mkMonitor 0).WorkArea
    Assert.Equal(workArea, (Map.find w1 finalWorld.Windows).Rect)
    Assert.Equal(workArea, (Map.find w2 finalWorld.Windows).Rect)
    Assert.Empty(reducer.State.Placement.Pending)

/// v2.60：`VirtualWorld.applyEffect`の`SetRect`が`executeEffectProtocol`（v2.59でテスト済み）を実際に
/// 通るようになったので、`HungAfterMs`挙動を初めてここで検査する。

[<Fact>]
let ``HungAfterMsの窓はstartMs+5000ms以降、SetRectがHungConfirmedとして即座に失敗しTargetHungになる(v2.60)`` () =
    let reducer = Reducer(RuntimePolicy.defaults, "scenario-session", "test-build", "test-policy-hash", None)
    settleTopology reducer [ mkMonitor 0 ]
    let w = WindowId 1L
    // startMs=0なので、now>=5000なら常にHungConfirmed。
    let world = empty |> addWindow w { X = 0; Y = 0; Width = 10; Height = 10 } (HungAfterMs 0L)
    let discovery = reducer.Submit 1000L "virtual-world" (visibleWindowObserved w)
    let setRectEffects = discovery.Effects |> List.filter (fun g -> match GatedEffect.effect g with SetRect _ -> true | _ -> false)
    let gated = List.exactlyOne setRectEffects

    // 発行自体はnow=1000だが、hangが確定しているnow=6000の時点でEffect適用スレッドがこれを処理した、
    // という想定で`applyEffect`を直接呼ぶ(`settle`経由だと`now`は発行と同じ値に固定されてしまうため)。
    let world', events, callReturns = applyEffect 6000L world gated
    Assert.Empty(events) // dispatchは呼ばれていないので観測も無い
    Assert.Equal<Map<WindowId, VirtualWindow>>(world.Windows, world'.Windows) // 矩形は変わっていない

    let rid, errorRecord =
        match callReturns with
        | [ (rid, Error record, dispatchSeq) ] ->
            Assert.Equal("SendMessageTimeout", record.Api)
            Assert.Equal(1460, record.Code)
            Assert.Equal(None, dispatchSeq)
            Assert.Equal(Wm.Contracts.ErrorTypes.TargetHung, Wm.Contracts.ErrorTypes.classify record)
            rid, record
        | other -> failwithf "unexpected callReturns: %A" other

    // このEffectCallReturnedを実際にCoreへ戻すと、Health.Responsivenessが確定でHungになる。
    reducer.Submit 6000L "virtual-world" (EffectCallReturned (rid, Error errorRecord, None)) |> ignore
    match Map.tryFind w reducer.State.Health.Responsiveness with
    | Some { Value = Hung; Confidence = Confirmed } -> ()
    | other -> Assert.True(false, sprintf "unexpected: %A" other)

[<Fact>]
let ``HungAfterMsの窓もstartMs+5000ms未満(HungPending)ならSetRectは普通に成功する(v2.60)`` () =
    let reducer = Reducer(RuntimePolicy.defaults, "scenario-session", "test-build", "test-policy-hash", None)
    settleTopology reducer [ mkMonitor 0 ]
    let w = WindowId 1L
    let world = empty |> addWindow w { X = 0; Y = 0; Width = 10; Height = 10 } (HungAfterMs 1000L)
    let discovery = reducer.Submit 1000L "virtual-world" (visibleWindowObserved w)
    let gated = discovery.Effects |> List.filter (fun g -> match GatedEffect.effect g with SetRect _ -> true | _ -> false) |> List.exactlyOne

    // now=2000：startMs(1000) <= now < startMs+5000(6000) なのでHungPending——判定未確定の間は
    // dispatch自体は止めない(executeEffectProtocolのHungPending分岐、v2.59で確認済み)ので成功する。
    let world', events, callReturns = applyEffect 2000L world gated
    Assert.NotEmpty(events)
    let workArea: PixelRect = (mkMonitor 0).WorkArea
    Assert.Equal(workArea, (Map.find w world'.Windows).Rect)
    match callReturns with
    | [ (_, Ok (), Some _) ] -> ()
    | other -> Assert.True(false, sprintf "unexpected callReturns: %A" other)

[<Fact>]
let ``HungAfterMsの窓もstartMs未満(Responsive)ならSetRectは普通に成功する(v2.60)`` () =
    let reducer = Reducer(RuntimePolicy.defaults, "scenario-session", "test-build", "test-policy-hash", None)
    settleTopology reducer [ mkMonitor 0 ]
    let w = WindowId 1L
    let world = empty |> addWindow w { X = 0; Y = 0; Width = 10; Height = 10 } (HungAfterMs 5000L)
    let discovery = reducer.Submit 1000L "virtual-world" (visibleWindowObserved w)
    let gated = discovery.Effects |> List.filter (fun g -> match GatedEffect.effect g with SetRect _ -> true | _ -> false) |> List.exactlyOne
    let finalWorld = settle 1000L reducer world discovery.Effects
    let workArea: PixelRect = (mkMonitor 0).WorkArea
    Assert.Equal(workArea, (Map.find w finalWorld.Windows).Rect)
    Assert.Empty(reducer.State.Placement.Pending)

/// v2.61：`VirtualWorld.applyEffect`の`SetRect`が`MinimumWidth`挙動を実際に模すようになったので、
/// 初めてここで検査する。`§12.1「MinimumWidth(700)」`が実際に要求幅を丸めることを直接確認する——
/// Mismatch（要求r・観測o>r）を経由した`Placement.MinWidth`信念の蓄積自体はv2.43のテスト
/// （`PlacementReconcileTests.fs`、hand-builtな観測）で既に検証済みなので、ここでは「証拠を生む側」
/// （疑似OSが実際に丸めた幅を観測として返す）だけを検査する。

[<Fact>]
let ``MinimumWidthより狭いSetRect要求は、観測される幅がMinimumWidthへ丸められる(v2.61)`` () =
    let reducer = Reducer(RuntimePolicy.defaults, "scenario-session", "test-build", "test-policy-hash", None)
    settleTopology reducer [ mkMonitor 0 ]
    let w = WindowId 1L
    // MonocleはモニターのWorkArea幅(1920)をそのまま要求するので、MinimumWidthを2000にすれば
    // 「要求(1920) < MinimumWidth(2000)」が必ず成り立つ。
    let world = empty |> addWindow w { X = 0; Y = 0; Width = 10; Height = 10 } (MinimumWidth 2000)
    let discovery = reducer.Submit 1000L "virtual-world" (visibleWindowObserved w)
    let gated = discovery.Effects |> List.filter (fun g -> match GatedEffect.effect g with SetRect _ -> true | _ -> false) |> List.exactlyOne
    match GatedEffect.effect gated with
    | SetRect (_, _, requested) -> Assert.Equal(1920, requested.Width) // 要求自体はモニター幅そのまま
    | other -> Assert.True(false, sprintf "unexpected: %A" other)

    let world', events, callReturns = applyEffect 1000L world gated
    Assert.Equal(2000, (Map.find w world'.Windows).Rect.Width) // 疑似OS側が実際に丸めた
    match events with
    | [ WindowObserved snapshot ] ->
        match snapshot.VisibleFrameRect with
        | Some { Value = Ok observed } -> Assert.Equal(2000, observed.Width)
        | other -> Assert.True(false, sprintf "unexpected: %A" other)
    | other -> Assert.True(false, sprintf "unexpected events: %A" other)
    match callReturns with
    | [ (_, Ok (), Some _) ] -> ()
    | other -> Assert.True(false, sprintf "unexpected callReturns: %A" other)

[<Fact>]
let ``MinimumWidth以上の要求幅はそのまま観測される(丸めは「未満の時だけ」)(v2.61)`` () =
    let reducer = Reducer(RuntimePolicy.defaults, "scenario-session", "test-build", "test-policy-hash", None)
    settleTopology reducer [ mkMonitor 0 ]
    let w = WindowId 1L
    // MinimumWidth(100) < WorkArea幅(1920)なので丸めは発生しない。
    let world = empty |> addWindow w { X = 0; Y = 0; Width = 10; Height = 10 } (MinimumWidth 100)
    let discovery = reducer.Submit 1000L "virtual-world" (visibleWindowObserved w)
    let finalWorld = settle 1000L reducer world discovery.Effects
    let workArea: PixelRect = (mkMonitor 0).WorkArea
    Assert.Equal(workArea, (Map.find w finalWorld.Windows).Rect)
    Assert.Empty(reducer.State.Placement.Pending) // 要求どおりに観測されたのでSatisfied
