/// 決定6層4（ADR-019）：EvidenceGap（§11.1）→ Belief/BeliefSlot/Confidence/EvidenceRef/BeliefKeyと
/// 各ドメインの信念の値の型・小レコード。architecture.md §4.5/§11.1、ADR-016。
module Wm.Contracts.Beliefs

open Wm.Contracts.Identifiers
open Wm.Contracts.Observation

/// 「何をまだ知らないか」を機械可読に表す（§11.1）。BeliefSlot.Unformedの中身そのもの。
type EvidenceGap =
    | MissingAppIdentity of WindowId
    | MissingDialogRole of WindowId
    | MissingVisibleBounds of WindowId
    | MissingOwnershipGraph of WindowId
    | UnknownMinSize of WindowId
    | UnknownMoveDelay of WindowId
    | UnknownSelfRepositionRule of WindowId
    | MissingTargetIntegrity of WindowId
    | MissingProcessIdentity of WindowId
    | MissingTitleMatch of WindowId

/// 全てのケースが、根拠を運んだ入力のSequenceを持つ（v2.6、R6-9。根拠リストを1つの全順序で並べるため）。
type EvidenceRef =
    | Sample of sequence: int64
    | Outcome of sequence: int64 * RequestId
    | ProbeEvidence of sequence: int64 * EvidenceId

type Confidence =
    | Confirmed
    | Inferred of EvidenceRef list
    | Stale of asOf: int64

type Belief<'T> = { Value: 'T; Confidence: Confidence; LastRevisedAt: int64 }

type BeliefSlot<'T> =
    | Believed of Belief<'T>
    | Unformed of EvidenceGap list

/// 5.4の三値`Truth`はBeliefSlot<bool>の別名（v2.6、ADR-016）。
type Truth = BeliefSlot<bool>

let (|Yes|No|Unknown|) (t: Truth) =
    match t with
    | Believed b when b.Value -> Yes b.Confidence
    | Believed b -> No b.Confidence
    | Unformed gaps -> Unknown gaps

/// 確度の「弱い方」：Confirmed > Inferred > Stale。Inferred同士は根拠の和集合、LastRevisedAtは大きい方。
/// 根拠リストは降順sequence→ケース順(Sample<Outcome<ProbeEvidence)→値順の全順序で並べ重複を除き最大8件に切り詰める（R6-9）。
let private evidenceRefOrderKey (r: EvidenceRef) =
    match r with
    | Sample seq -> seq, 0, 0L, 0L
    | Outcome (seq, RequestId rid) -> seq, 1, rid, 0L
    | ProbeEvidence (seq, EvidenceId eid) -> seq, 2, 0L, int64 (hash eid)

let private normalizeEvidence (refs: EvidenceRef list) =
    refs
    |> List.distinct
    |> List.sortByDescending evidenceRefOrderKey
    |> List.truncate 8

let private weakerConfidence (a: Confidence) (b: Confidence) (newerRevisedAt: int64) : Confidence =
    match a, b with
    | Stale _, _ | _, Stale _ ->
        let asOf =
            match a, b with
            | Stale x, Stale y -> max x y
            | Stale x, _ | _, Stale x -> x
            | _ -> newerRevisedAt
        Stale asOf
    | Inferred ra, Inferred rb -> Inferred (normalizeEvidence (ra @ rb))
    | Inferred ra, Confirmed | Confirmed, Inferred ra -> Inferred (normalizeEvidence ra)
    | Confirmed, Confirmed -> Confirmed

/// 単位元 allIdentity = Believed { Value = true; Confidence = Confirmed; LastRevisedAt = Int64.MinValue }
let allIdentity: Truth = Believed { Value = true; Confidence = Confirmed; LastRevisedAt = System.Int64.MinValue }

/// 単位元 anyOfIdentity = Believed { Value = false; Confidence = Confirmed; LastRevisedAt = Int64.MinValue }
let anyOfIdentity: Truth = Believed { Value = false; Confidence = Confirmed; LastRevisedAt = System.Int64.MinValue }

/// どちらかがNoならNo、そうでなくどちらかがUnknownならUnknown、両方YesならYes（§5.4/§15.1 #25）。
let combineAll (a: Truth) (b: Truth) : Truth =
    match a, b with
    | Believed ba, Believed bb when not ba.Value || not bb.Value ->
        let revisedAt = max ba.LastRevisedAt bb.LastRevisedAt
        let confidence =
            match ba.Value, bb.Value with
            | false, false -> weakerConfidence ba.Confidence bb.Confidence revisedAt
            | false, true -> ba.Confidence
            | true, false -> bb.Confidence
            | true, true -> Confirmed // unreachable here
        Believed { Value = false; Confidence = confidence; LastRevisedAt = revisedAt }
    | Unformed ga, Unformed gb -> Unformed (ga @ gb |> List.distinct)
    | Unformed ga, _ -> Unformed ga
    | _, Unformed gb -> Unformed gb
    | Believed ba, Believed bb ->
        let revisedAt = max ba.LastRevisedAt bb.LastRevisedAt
        Believed { Value = true; Confidence = weakerConfidence ba.Confidence bb.Confidence revisedAt; LastRevisedAt = revisedAt }

/// Yesと Noを入れ替えたcombineAllの双対。
let combineAnyOf (a: Truth) (b: Truth) : Truth =
    match a, b with
    | Believed ba, Believed bb when ba.Value || bb.Value ->
        let revisedAt = max ba.LastRevisedAt bb.LastRevisedAt
        let confidence =
            match ba.Value, bb.Value with
            | true, true -> weakerConfidence ba.Confidence bb.Confidence revisedAt
            | true, false -> ba.Confidence
            | false, true -> bb.Confidence
            | false, false -> Confirmed // unreachable here
        Believed { Value = true; Confidence = confidence; LastRevisedAt = revisedAt }
    | Unformed ga, Unformed gb -> Unformed (ga @ gb |> List.distinct)
    | Unformed ga, _ -> Unformed ga
    | _, Unformed gb -> Unformed gb
    | Believed ba, Believed bb ->
        let revisedAt = max ba.LastRevisedAt bb.LastRevisedAt
        Believed { Value = false; Confidence = weakerConfidence ba.Confidence bb.Confidence revisedAt; LastRevisedAt = revisedAt }

// ===== 各ドメインの信念の値の型（ADR-015/S11） =====

/// yabaiのWINDOW_MOVABLE相当（ADR-015）。
type Movability =
    | Movable
    | NotMovable

type Responsiveness =
    | Responding
    | Hung

/// WM_DPICHANGEDで自ら再配置するか、OSのビットマップ拡大縮小に任せるか（S11）。
type DpiBehavior =
    | RescalesItself
    | OsBitmapScaled

/// 再probeの対象を指す名前。5.5の読み取り集合、Explainで使う（v2.6でケースを列挙、R4-22）。
type BeliefKey =
    | KProcess | KAppExe | KAppHostExe | KAppAumid | KAppCoreWindowExe
    | KOwner | KIsDialog | KNativeRect | KVisibleFrameRect | KIsVisible | KIsMinimized | KCloak | KMonitor | KPresence
    | KMinWidth | KMovability | KTargetIntegrity | KResponsiveness | KDpiBehavior | KTitleMatches

/// Windows `Identity`（Sticky規則）。
type IdentityBeliefs = {
    Exe: BeliefSlot<string>
    HostExe: BeliefSlot<string>
    Aumid: BeliefSlot<string>
    CoreWindowExe: BeliefSlot<string>
    Process: BeliefSlot<ProcessKey>
}

/// Windows `ClassInputs`（LatestWins規則）。
type ClassInputBeliefs = {
    IsDialog: BeliefSlot<bool>
    Owner: BeliefSlot<WindowId option>
    IsVisible: BeliefSlot<bool>
    Cloak: BeliefSlot<CloakState>
    TitleMatches: BeliefSlot<TitleMatchVector>
}

/// Placement `Geometry`（LatestWins規則）。
type GeometryBeliefs = {
    NativeRect: BeliefSlot<PixelRect>
    VisibleFrameRect: BeliefSlot<PixelRect>
    Monitor: BeliefSlot<MonitorId>
    IsMinimized: BeliefSlot<bool>
    Presence: BeliefSlot<ObservedPresence>
}
