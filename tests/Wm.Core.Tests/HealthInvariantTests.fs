/// architecture.md §4.5「応答性（ハング）」のAccumulating規則と、§4.5の再probeバックオフ上限を検査する。
module Wm.Core.Tests.HealthInvariantTests

open Xunit
open FsCheck.Xunit
open Wm.Contracts.Identifiers
open Wm.Contracts.ErrorTypes
open Wm.Contracts.Observation
open Wm.Contracts.Beliefs
open Wm.Contracts.Timers
open Wm.Contracts.CoreCommand
open Wm.Core.Health

let private mkCurrent (value: Responsiveness) (confidence: Confidence) : Belief<Responsiveness> option =
    Some { Value = value; Confidence = confidence; LastRevisedAt = 0L }

[<Property>]
let ``IsHungAppWindowTrueだけでは新たにHung&Confirmedを作らない`` (now: int) (hasCurrent: bool) (currentIsHung: bool) =
    // §4.5「IsHungAppWindowTrueだけならHung（Inferred）に留め、決してConfirmedにしない」。
    // 既に別の強い証拠でHung&Confirmed（またはResponding&Confirmed）だった信念をこの弱い証拠だけで
    // 書き換えない、というreviseResponsivenessの既存信念保持規則は正しい挙動なのでそのまま許容する。
    // 検査すべきは「この証拠だけで新たにHung&Confirmedへ遷移することは無い」という一方向の主張。
    let current =
        if hasCurrent then
            mkCurrent (if currentIsHung then Hung else Responding) Confirmed
        else
            None
    let wasAlreadyHungConfirmed =
        match current with
        | Some b -> b.Value = Hung && b.Confidence = Confirmed
        | None -> false
    let revised = reviseResponsiveness (int64 now) current (FromObservation IsHungAppWindowTrue)
    let becameHungConfirmed =
        match revised with
        | Some b -> b.Value = Hung && b.Confidence = Confirmed
        | None -> false
    wasAlreadyHungConfirmed || not becameHungConfirmed

[<Property>]
let ``PumpRespondedは常にResponding(Confirmed)にする`` (now: int) (currentValue: Responsiveness) =
    let current = mkCurrent currentValue Confirmed
    match reviseResponsiveness (int64 now) current (FromObservation PumpResponded) with
    | Some revised -> revised.Value = Responding && revised.Confidence = Confirmed
    | None -> false

[<Property>]
let ``判定済みのPumpTimedOutとTargetHungは常にHung(Confirmed)にする`` (now: int) (elapsedUsRaw: int) =
    let elapsedUs = int64 (abs elapsedUsRaw % 1000) // judgedHungThresholdUs(1000us)以下に収める
    match
        reviseResponsiveness (int64 now) None (FromObservation (PumpTimedOut elapsedUs)),
        reviseResponsiveness (int64 now) None (FromEffectFailure TargetHung)
    with
    | Some r1, Some r2 -> (r1.Value = Hung && r1.Confidence = Confirmed) && (r2.Value = Hung && r2.Confidence = Confirmed)
    | _ -> false

[<Property>]
let ``scheduleReprobeを繰り返すと試行回数は20回を超えず遅延は30秒を超えない`` (startNow: int) =
    let rec loop (now: int64) (retry: RetryState option) (attempts: int) (sawNone: bool) =
        if attempts > 25 then
            sawNone // 25回呼べば必ず上限(20回)に達してNoneになっているはず
        else
            match scheduleReprobe now (WindowId 1L) retry with
            | Some (retry', deadline, _cmds) ->
                let delay = deadline - now
                if delay > reprobeMaxDelayMs || retry'.AttemptCount > reprobeMaxAttempts then
                    false
                else
                    loop deadline (Some retry') (attempts + 1) sawNone
            | None -> loop now retry (attempts + 1) true
    loop (int64 startNow) None 0 false

[<Property>]
let ``applyEvidenceを任意の証拠列でfoldした結果、HungならReprobesにエントリがありそれ以外は無い`` (rawEvidences: (int * int) list) =
    let window = WindowId 1L
    let toEvidence (kind: int, payload: int) : ResponsivenessEvidence =
        match (abs kind) % 4 with
        | 0 -> FromObservation PumpResponded
        | 1 -> FromObservation (PumpTimedOut (int64 (abs payload % 2000)))
        | 2 -> FromObservation IsHungAppWindowTrue
        | _ -> FromEffectFailure (if payload % 2 = 0 then TargetHung else Transient)

    let evidences = rawEvidences |> List.truncate 50
    let finalState =
        evidences
        |> List.fold
            (fun (state, now) ev ->
                let state', _effects, _cmds = applyEvidence now window (toEvidence ev) state
                state', now + 1L)
            (initial, 0L)
        |> fst

    let isHung =
        match Map.tryFind window finalState.Responsiveness with
        | Some b -> b.Value = Hung
        | None -> false

    let hasReprobe = Map.containsKey window finalState.Reprobes

    isHung = hasReprobe

// ===== Reprobe Effectの発行（v2.84、architecture.md §4.5「Reprobeで再確認を要求する」） =====

[<Fact>]
let ``Hungへ新たに遷移するとReprobe(w, KResponsiveness)が発行される`` () =
    let window = WindowId 1L
    let state', effects, _cmds = applyEvidence 0L window (FromEffectFailure TargetHung) initial
    Assert.Equal<Effect list>([ Reprobe (window, KResponsiveness) ], effects)
    Assert.True(Map.containsKey window state'.Reprobes)

[<Fact>]
let ``既にHungな窓へさらにHungの証拠が来てもReprobeは再発行しない(遷移の瞬間だけ)`` () =
    let window = WindowId 1L
    let state1, _, _ = applyEvidence 0L window (FromEffectFailure TargetHung) initial
    let _state2, effects2, _cmds2 = applyEvidence 100L window (FromObservation (PumpTimedOut 0L)) state1
    Assert.Empty(effects2)

[<Fact>]
let ``Respondingへ回復してもReprobeは発行しない(CancelTimerだけ)`` () =
    let window = WindowId 1L
    let state1, _, _ = applyEvidence 0L window (FromEffectFailure TargetHung) initial
    let _state2, effects2, cmds2 = applyEvidence 100L window (FromObservation PumpResponded) state1
    Assert.Empty(effects2)
    Assert.Equal<TimerCommand<TimerId> list>([ CancelTimer (TimerId.Health (ResponsivenessReprobe window)) ], cmds2)

[<Fact>]
let ``onReprobeTimerFiredが実際に次の再試行を予約するとReprobe(w, KResponsiveness)を再発行する`` () =
    let window = WindowId 1L
    let state1, _, cmds1 = applyEvidence 0L window (FromEffectFailure TargetHung) initial
    let deadline = match Map.find window state1.Reprobes with _, d -> d
    let _state2, effects2, cmds2 = onReprobeTimerFired deadline window state1
    Assert.Equal<Effect list>([ Reprobe (window, KResponsiveness) ], effects2)
    Assert.NotEmpty(cmds1)
    Assert.NotEmpty(cmds2)

[<Fact>]
let ``onReprobeTimerFiredは既に回復済み(Hungでない)なら何もしない`` () =
    let window = WindowId 1L
    let state', effects, cmds = onReprobeTimerFired 0L window initial
    Assert.Empty(effects)
    Assert.Empty(cmds)
    Assert.Equal<HealthState>(initial, state')
