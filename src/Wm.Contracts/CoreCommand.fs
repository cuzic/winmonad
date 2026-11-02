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
    | EffectCallReturned of RequestId * Result<unit, NativeCallRecord>
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

/// Reducerへ投入したSequence、論理/単調時刻、入力の発生元、SessionId等を持つ（§7.1/§9.3）。
type EventEnvelope<'Input> = {
    Sequence: int64
    MonotonicTime: int64
    Source: string
    SessionId: string
    Input: 'Input
}
