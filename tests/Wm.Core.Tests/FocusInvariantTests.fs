/// architecture.md §4.5のLatestWins規則（Foreground）、§4.4のHistory更新規則、
/// §4.4のSwitchSettle待ち（§15.1 #21と同型の「古いTimerの発火の無害性」）を検査する。
module Wm.Core.Tests.FocusInvariantTests

open FsCheck.Xunit
open Wm.Contracts.Identifiers
open Wm.Contracts.ErrorTypes
open Wm.Contracts.Observation
open Wm.Contracts.Beliefs
open Wm.Contracts.SharedVocabulary
open Wm.Contracts.Timers
open Wm.Core.Focus

let private mkObservation (sequence: int64) (value: Result<WindowId option, NativeCallRecord>) =
    { Value = value; SampleSequence = sequence; MonotonicTime = sequence; Source = "test" }

let private sampleErr: NativeCallRecord = { Api = "test"; Space = Win32LastError; Code = 1; ElapsedUs = 0L }

[<Property>]
let ``reviseForeground: より新しいSampleSequenceのOk観測は常に採用される`` (seq1: int) (seq2: int) (w1: int) (w2: int) =
    let s1, s2 = int64 (abs seq1), int64 (abs seq1) + int64 (abs seq2) + 1L // s2は厳密にs1より新しい
    let belief1 = reviseForeground None (mkObservation s1 (Ok (Some (WindowId (int64 w1)))))
    let belief2 = reviseForeground (Some belief1) (mkObservation s2 (Ok (Some (WindowId (int64 w2)))))
    match belief2 with
    | Believed b -> b.Value = Some (WindowId (int64 w2)) && b.Confidence = Confirmed && b.LastRevisedAt = s2
    | Unformed _ -> false

[<Property>]
let ``reviseForeground: 古いSampleSequenceの観測は新しい信念を上書きしない`` (seqNewer: int) (seqOlderGap: int) (wNewer: int) (wOlder: int) =
    let sNewer = int64 (abs seqNewer) + int64 (abs seqOlderGap) + 1L
    let sOlder = int64 (abs seqNewer) // sOlder < sNewer
    let afterNewer = reviseForeground None (mkObservation sNewer (Ok (Some (WindowId (int64 wNewer)))))
    let afterOlder = reviseForeground (Some afterNewer) (mkObservation sOlder (Ok (Some (WindowId (int64 wOlder)))))
    afterOlder = afterNewer // 一切変化しない

[<Property>]
let ``reviseForeground: Errorの観測は値を保ったままStaleにする`` (sequence: int) (w: int) =
    let s = int64 (abs sequence)
    let afterOk = reviseForeground None (mkObservation s (Ok (Some (WindowId (int64 w)))))
    let afterErr = reviseForeground (Some afterOk) (mkObservation (s + 1L) (Error sampleErr))
    match afterOk, afterErr with
    | Believed okBelief, Believed errBelief ->
        errBelief.Value = okBelief.Value
        && (match errBelief.Confidence with
            | Stale _ -> true
            | _ -> false)
    | _ -> false

[<Property>]
let ``updateHistory: 任意の遷移列をfoldしてもHistoryに重複が生じない`` (rawWindowIds: int list) =
    let targets = rawWindowIds |> List.truncate 60 |> List.map (fun i -> ManagedWindow (WindowId (int64 (abs i % 8))))
    let allFocused = Elsewhere :: targets // 初期状態はElsewhere
    let finalHistory =
        allFocused
        |> List.pairwise
        |> List.fold (fun history (before, after) -> updateHistory before after history) []
    (finalHistory |> List.distinct |> List.length) = finalHistory.Length

[<Property>]
let ``SwitchSettle: 記録したdeadline未到達のTimerElapsedは状態を一切変えない`` (settleStart: int) (settleMs: int) (focusWindowId: int option) (earlyOffsetRaw: int) =
    let focusTarget = focusWindowId |> Option.map (fun i -> WindowId (int64 (abs i)))
    let settling, _cmds = startSwitchSettle (int64 settleStart) (int64 (abs settleMs) + 1L) focusTarget
    let state0 = { initial with SwitchSettle = Some settling }
    let (_, deadline) = settling
    let offset = (int64 (abs earlyOffsetRaw)) + 1L
    let firedAt = deadline - offset
    let state1, reRequest, cmds1 = onSwitchSettleTimerFired firedAt state0
    (state1 = state0) && (reRequest = None) && (cmds1 = [])

[<Property>]
let ``SwitchSettle: deadline到達ちょうどのTimerElapsedは本物の発火として待ちを解消する`` (settleStart: int) (settleMs: int) (focusWindowId: int option) =
    let focusTarget = focusWindowId |> Option.map (fun i -> WindowId (int64 (abs i)))
    let settling, _cmds = startSwitchSettle (int64 settleStart) (int64 (abs settleMs) + 1L) focusTarget
    let state0 = { initial with SwitchSettle = Some settling }
    let (_, deadline) = settling
    let state1, reRequest, cmds1 = onSwitchSettleTimerFired deadline state0
    (state1.SwitchSettle = None) && (reRequest = focusTarget) && (cmds1 = [ CancelTimer (Focus WorkspaceSwitchSettle) ])
