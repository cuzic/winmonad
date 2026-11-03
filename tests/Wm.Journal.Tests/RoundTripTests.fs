/// architecture.md §15.1 #10/#19（Replayの一致、信念は入力列の決定的な関数）を実際に検査するには、
/// Journalが`step`の分岐対象になっている入力を欠落無く往復できる必要がある。Opusレビュー5-4：
/// 旧実装は`MonitorTopologyObserved`/`ScreenReconfigurationInProgress`/`TimerElapsed`しか符号化せず、
/// `ForegroundObserved`/`WindowRemoved`/`UserCommand`（いずれも`WmState.step`が分岐に使う）は
/// "unsupported-input-event"として書かれ、Replayがfail-fastしていた。
///
/// round2レビューT-5：当初の往復テストは`Management`/`LayoutArgs`/`ErrorSpace`/`CoreCommand`について
/// 固定の代表値(常に`Win32LastError`・`Code=5`・`MasterRatio 0.5m`・`Tiled`等)しか使っておらず、
/// これらの型の他のケース(`HResult`・`BoolFalseNoCode`・`NoArgs`・`ColumnCount`・`Floating`・
/// `Excluded`、および`MasterRatio`の任意の値)が符号化で正しく往復するかは未検証だった。
/// `Generators.JournalGenerators`で全ケースを覆うArbitraryを定義し、このモジュール全体に適用する。
[<FsCheck.Xunit.Properties(Arbitrary = [| typeof<Wm.Journal.Tests.Generators.JournalGenerators> |])>]
module Wm.Journal.Tests.RoundTripTests

open System
open System.IO
open FsCheck
open FsCheck.Xunit
open Wm.Contracts.Identifiers
open Wm.Contracts.ErrorTypes
open Wm.Contracts.Observation
open Wm.Contracts.SharedVocabulary
open Wm.Contracts.Timers
open Wm.Contracts.CoreCommand
open Wm.Contracts.JournalTypes
open Wm.Journal.JournalWriter

let private mkEntry (payload: JournalPayload) : JournalEntry =
    { SchemaVersion = 1
      SessionId = "test-session"
      Sequence = 1L
      MonotonicTicks = 123L
      WallTimeUtc = DateTimeOffset.UtcNow
      CorrelationId = None
      CausationId = None
      PolicyArtifactHash = "hash"
      CoreBuildId = "build"
      Kind = InputReceived
      Payload = payload }

let private roundTrip (entry: JournalEntry) : JournalEntry =
    let path = Path.Combine(Path.GetTempPath(), sprintf "wm-journal-roundtrip-%s.ndjson" (Guid.NewGuid().ToString("N")))
    try
        appendEntry path entry
        readAll path |> List.ofSeq |> List.exactlyOne
    finally
        if File.Exists path then
            File.Delete path

let private mkMonitor (id: int) : MonitorSnapshot =
    { Id = MonitorId (int64 id)
      StableKey = sprintf "TEST:%d" id
      Bounds = { X = 0; Y = 0; Width = 1920; Height = 1080 }
      WorkArea = { X = 0; Y = 0; Width = 1920; Height = 1040 }
      Dpi = 96
      IsPrimary = (id = 0) }

[<Property>]
let ``MonitorTopologyObservedは往復しても一致する`` (ids: int list) =
    let snapshots = ids |> List.truncate 5 |> List.map (fun i -> mkMonitor (abs i % 8)) |> List.distinctBy (fun m -> m.Id)
    let entry = mkEntry (InputPayload (MonitorTopologyObserved snapshots))
    roundTrip entry = entry

[<Property>]
let ``ScreenReconfigurationInProgressは往復しても一致する`` (b: bool) =
    let entry = mkEntry (InputPayload (ScreenReconfigurationInProgress b))
    roundTrip entry = entry

[<Property>]
let ``TimerElapsed(Topology ScreenSettle)は往復しても一致する`` () =
    let entry = mkEntry (InputPayload (TimerElapsed (TimerId.Topology ScreenSettle)))
    roundTrip entry = entry

[<Property>]
let ``TimerElapsed(Health ResponsivenessReprobe)は往復しても一致する`` (w: int) =
    let entry = mkEntry (InputPayload (TimerElapsed (TimerId.Health (ResponsivenessReprobe (WindowId (int64 (abs w)))))))
    roundTrip entry = entry

[<Property>]
let ``TimerElapsed(Placement PresenceDeadline)は往復しても一致する(v2.41)`` (rid: int) =
    let entry = mkEntry (InputPayload (TimerElapsed (TimerId.Placement (PresenceDeadline (RequestId (int64 (abs rid)))))))
    roundTrip entry = entry

[<Property>]
let ``ForegroundObservedは往復しても一致する(Ok)`` (sequence: int) (w: int option) =
    let obs =
        { Value = Ok (w |> Option.map (fun i -> WindowId (int64 (abs i))))
          SampleSequence = int64 (abs sequence)
          MonotonicTime = int64 (abs sequence)
          Source = "test" }
    let entry = mkEntry (InputPayload (ForegroundObserved obs))
    roundTrip entry = entry

[<Property>]
let ``ForegroundObservedは往復しても一致する(Error、ErrorSpaceの全ケースと任意のCode/Api/ElapsedUs)``
    (sequence: int)
    (space: ErrorSpace)
    (api: NonNull<string>)
    (code: int)
    (elapsedUs: int64)
    =
    let (NonNull apiStr) = api
    let err: NativeCallRecord = { Api = apiStr; Space = space; Code = code; ElapsedUs = elapsedUs }
    let obs = { Value = Error err; SampleSequence = int64 (abs sequence); MonotonicTime = int64 (abs sequence); Source = "test" }
    let entry = mkEntry (InputPayload (ForegroundObserved obs))
    roundTrip entry = entry

[<Property>]
let ``WindowRemovedは往復しても一致する`` (w: int) =
    let entry = mkEntry (InputPayload (WindowRemoved (WindowId (int64 (abs w)))))
    roundTrip entry = entry

[<Property>]
let ``UserCommandの全13ケースは往復しても一致する(固定値、回帰の土台)`` (ws: int) (w: int) =
    let cmds =
        [ FocusNext
          FocusPrevious
          FocusMostRecent
          SwapWithMaster
          PromoteToMaster
          CloseFocused
          SwitchWorkspace (WorkspaceId (abs ws % 8))
          MoveFocusedToWorkspace (WorkspaceId (abs ws % 8))
          FocusMonitor (MonitorId (int64 (abs ws % 8)))
          ChangeLayout { Id = LayoutId "master-stack"; ApiVersion = 1; Args = MasterRatio 0.5m }
          ToggleFloating (WindowId (int64 (abs w)))
          SetManagement (WindowId (int64 (abs w)), Tiled)
          ClearUserOverride (WindowId (int64 (abs w))) ]

    cmds
    |> List.forall (fun cmd ->
        let entry = mkEntry (InputPayload (UserCommand cmd))
        roundTrip entry = entry)

/// round2レビューT-5の本体：`Generators.JournalGenerators.CoreCommand`が13ケースを一様に選び、
/// `ChangeLayout`/`SetManagement`には`LayoutArgs`(NoArgs/MasterRatio任意精度/ColumnCount)・
/// `Management`(Tiled/Floating/Excluded)の任意の値を載せる。固定値テストでは拾えない、
/// 「`MasterRatio`の特定の値でだけ精度が落ちる」「`Excluded`だけ符号化し忘れる」といった
/// クラス単位の取りこぼしをFsCheckの反復実行(既定100回)で検出する。
[<Property>]
let ``UserCommandは任意のCoreCommand(全ケース×任意のLayoutArgs/Management)でも往復しても一致する`` (cmd: CoreCommand) =
    let entry = mkEntry (InputPayload (UserCommand cmd))
    roundTrip entry = entry

/// `ChangeLayout`単体についても、`LayoutArgs`の全ケースと任意精度の`MasterRatio`を直接検査する
/// (decimalのJSON往復は精度を保つはずだが、System.Text.Jsonの数値表現を介するため明示的に確認する)。
[<Property>]
let ``ChangeLayoutはLayoutArgsの全ケース・任意精度のMasterRatioでも往復しても一致する`` (layoutRef: LayoutRef) =
    let entry = mkEntry (InputPayload (UserCommand (ChangeLayout layoutRef)))
    roundTrip entry = entry

/// §4.5 R4-3の所属ヒント（v2.64で`Windows.onMembershipHintsLoaded`を実装し、`WmState.step`が初めて
/// `MembershipHintsLoaded`を分岐対象にした——同じラウンドで追加したJournal往復対応をここで検査する）。
/// `Management option`（ヒント無し/`Tiled`/`Floating`/`Excluded`復元の全ケース）と空リストを含め、
/// 任意の件数のヒントで往復が一致することを確認する。
[<Property>]
let ``MembershipHintsLoadedは任意のヒント一覧(Management optionの全ケース含む)でも往復しても一致する(v2.64)``
    (hints: (int64 * int * Management option) list)
    =
    let hints' = hints |> List.map (fun (w, ws, mgmt) -> WindowId w, WorkspaceId ws, mgmt)
    let entry = mkEntry (InputPayload (MembershipHintsLoaded hints'))
    roundTrip entry = entry

/// `SetManagement`単体についても、`Management`の全ケース(Tiled/Floating/Excluded)を直接検査する。
[<Property>]
let ``SetManagementはManagementの全ケースでも往復しても一致する`` (w: int) (management: Management) =
    let entry = mkEntry (InputPayload (UserCommand (SetManagement (WindowId (int64 (abs w)), management))))
    roundTrip entry = entry

/// v2.57：`WmState.step`が分岐対象にしている入力のうち、ここまで`unsupported-input-event`に
/// 落ちてReplayがfail-fastしていた5ケース（`WindowObserved`/`VisibilityChanged`/`CloakChanged`/
/// `MinimizedStateChanged`/`EffectCallReturned`）を往復させる。`WindowObserved`は最も頻繁に届く
/// 入力（窓の発見・全ての信念更新の起点）であり、§9.2「stepの入力分岐 ⊆ Journalの往復可能集合」を
/// 満たすために欠けていた最大の穴だった。

[<Property>]
let ``WindowObservedは任意のWindowSnapshot(全フィールドNone/Observationを含む)でも往復しても一致する(v2.57)`` (snapshot: WindowSnapshot) =
    let entry = mkEntry (InputPayload (WindowObserved snapshot))
    roundTrip entry = entry

[<Property>]
let ``VisibilityChangedは往復しても一致する(v2.57)`` (w: int) (obs: Observation<bool>) =
    let entry = mkEntry (InputPayload (VisibilityChanged (WindowId (int64 (abs w)), obs)))
    roundTrip entry = entry

[<Property>]
let ``CloakChangedは往復しても一致する(v2.57)`` (w: int) (obs: Observation<CloakState>) =
    let entry = mkEntry (InputPayload (CloakChanged (WindowId (int64 (abs w)), obs)))
    roundTrip entry = entry

[<Property>]
let ``MinimizedStateChangedは往復しても一致する(v2.57)`` (w: int) (obs: Observation<bool>) =
    let entry = mkEntry (InputPayload (MinimizedStateChanged (WindowId (int64 (abs w)), obs)))
    roundTrip entry = entry

[<Property>]
let ``EffectCallReturnedは往復しても一致する(Ok/Error、dispatchSeqのSome/None全て)(v2.57)`` (rid: int) (result: Result<unit, NativeCallRecord>) (dispatchSeq: int64 option) =
    let entry = mkEntry (InputPayload (EffectCallReturned (RequestId (int64 (abs rid)), result, dispatchSeq)))
    roundTrip entry = entry
