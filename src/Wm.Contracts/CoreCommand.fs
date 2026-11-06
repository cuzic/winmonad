/// 決定6層7（ADR-019）：CoreCommand → InputEvent/Effect/EventEnvelope。architecture.md §4.4/§7.1。
module Wm.Contracts.CoreCommand

open Wm.Contracts.Identifiers
open Wm.Contracts.ErrorTypes
open Wm.Contracts.Observation
open Wm.Contracts.Beliefs
open Wm.Contracts.SharedVocabulary

/// ユーザー操作・ホットキーから生じるコマンド（§4.4）。
type CoreCommand =
    | FocusNext
    | FocusPrevious
    | FocusMostRecent
    | SwapWithMaster
    | PromoteToMaster
    | CloseFocused
    | SwitchWorkspace of WorkspaceId
    | MoveFocusedToWorkspace of WorkspaceId
    | FocusMonitor of MonitorId
    | ChangeLayout of LayoutRef
    | ToggleFloating of WindowId
    | SetManagement of WindowId * Management
    | ClearUserOverride of WindowId

/// Intervene（architecture.md §10.2、v2.151）が運ぶ、Named Pipe越しに受け付ける命令の閉じた集合。
/// `CoreCommand`そのものを再利用しない——`CoreCommand`には`CloseFocused`/`FocusNext`等、Opus敵対的
/// 相談`opus-review-intervene-trust-boundary.md`のQ3で「REPLクライアント自身の端末を操作してしまう
/// （`focused`はフォアグラウンド窓の信念であり、コマンド送信時にフォアグラウンドなのはREPLの端末
/// 自身）」「破壊的（`CloseFocused`）でHost UIによる人間の確認が無いまま実行されるのは正当化できない」
/// と指摘された危険なケースを含む——この型自体がワイヤ上で送れる命令の許可リストそのものになる
/// ことで、`CloseFocused`等が決して配線に乗らないことを型で保証する。最初のスライスは同レビューの
/// 推奨どおり、明示的な対象窓を取り・冪等・可逆・無言のフォーカス変更が無い2命令だけに限定する
/// （`ToggleFloating`は非冪等——タイムアウト後の再送が直前の適用を元に戻してしまう、同レビューの
/// Q2-C参照——なので対象外、`SetManagement`で代替できる）。
type InterveneCommand =
    | SetManagementTo of WindowId * Management
    | ClearOverride of WindowId

/// Intervene 1件の要求（v2.151）。`ClientPid`/`ClientImage`はクライアントが送るフィールドではない
/// ——Host自身が接続済みパイプから`GetNamedPipeClientProcessId`/`QueryFullProcessImageName`で
/// 取得する（`InspectPipeServer.fs`参照、§14「クライアントの自己申告を信用しない」と同じ原則）。
/// `CorrelationId`はクライアントが付け、タイムアウト後に結果をJournal/Explainで追跡するためだけに
/// 使う（同レビューQ2-C：5秒の接続タイムアウトが`step`適用後に切れると、クライアントは`Timeout`を見るが
/// コマンド自体は適用済みなので、再送での復元判定にこのIdが要る）。
type InterveneRequest = {
    CorrelationId: string option
    ClientPid: int
    ClientImage: string
    Command: InterveneCommand
}

/// `InterveneCommand`が運ぶ対象`WindowId`（前提検査・Journal記録の両方が使う）。
let targetWindowOf (cmd: InterveneCommand) : WindowId =
    match cmd with
    | SetManagementTo(w, _) -> w
    | ClearOverride w -> w

/// 前提検査（`WmState.step`、F-4）が通った後、既存の`CoreCommand`経路（`Windows.onUserCommand`の
/// `SetManagement`/`ClearUserOverride`分岐、v2.151時点で新規コードを一切要さない）へそのまま
/// 委譲するための変換。
let coreCommandOf (cmd: InterveneCommand) : CoreCommand =
    match cmd with
    | SetManagementTo(w, m) -> SetManagement(w, m)
    | ClearOverride w -> ClearUserOverride w

/// Shellへ渡す意図のIR（§7.1）。GatedEffect.gateを通さないと実際には発行されない。
type Effect =
    | SetRect of RequestId * WindowId * PixelRect
    | RestoreBeforeMove of RequestId * WindowId
    | ApplyBatch of BatchId * Effect list
    | RequestForeground of RequestId * WindowId
    | ObserveWindow of WindowId
    | ObserveAll
    | SetPresence of RequestId * WindowId * shown: bool
    | RequestClose of RequestId * WindowId
    | ForgetWindow of WindowId
    | Reprobe of WindowId * BeliefKey

/// Reducerへの単一の入力（§7.1）。
type InputEvent =
    | WindowObserved of WindowSnapshot
    | WindowRemoved of WindowId
    | ForegroundObserved of Observation<WindowId option>
    | UserCommand of CoreCommand
    /// `dispatchSeq`（v2.42追加）：Shell（またはSimulationの`VirtualWorld`）がこのEffectを実際に
    /// 呼び出した時点で、観測と同じ単調カウンタから採番する値（`Ok`のときだけ`Some`）。Placementの
    /// `Mismatch → Reevaluate`（§7.2）が「この観測は要求の発行後に取得されたか」を判定するために使う
    /// （`EventEnvelope.MonotonicTime`とは番号空間が別なので比較の根拠にならない、Placement.fsの
    /// コメント参照）。
    | EffectCallReturned of RequestId * Result<unit, NativeCallRecord> * dispatchSeq: int64 option
    | EffectBatchReturned of BatchId * (RequestId * Result<unit, NativeCallRecord>) list
    | InternalFault of componentName: string
    | JournalDegraded of since: int64
    | JournalRecovered of gapStart: int64 * gapEnd: int64
    | MoveSizeStarted of WindowId
    | MoveSizeEnded of WindowId * Observation<PixelRect> * Observation<MonitorId>
    | VisibilityChanged of WindowId * Observation<bool>
    | MinimizedStateChanged of WindowId * Observation<bool>
    | CloakChanged of WindowId * Observation<CloakState>
    | StyleChanged of WindowId
    | TimerElapsed of Wm.Contracts.Timers.TimerId
    | MonitorTopologyObserved of MonitorSnapshot list
    | ScreenReconfigurationInProgress of bool
    | PolicyActivated of string
    | MembershipHintsLoaded of (WindowId * WorkspaceId * Management option) list
    /// Intervene（architecture.md §10.2、v2.151）：Named Pipe越しに受け付けた`InterveneRequest`。
    /// `WmState.step`（新設の薄いラッパー、`WmState.fs`の`step`型直前コメント参照）がこれだけを
    /// 特別扱いし、前提検査（F-4）を通れば等価な`UserCommand`へ書き換えて既存経路へ委譲する。
    | InterveneRequested of InterveneRequest

/// Reducerへ投入したSequence、論理/単調時刻、入力の発生元、SessionId等を持つ（§7.1/§9.3）。
type EventEnvelope<'Input> = {
    Sequence: int64
    MonotonicTime: int64
    Source: string
    SessionId: string
    Input: 'Input
}
