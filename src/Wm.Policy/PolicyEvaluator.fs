/// ADR-019実装順8：PolicyEvaluator.fs。architecture.md §5.4/§6.1。
///
/// `WindowPredicate`/`LayoutPredicate`の三値評価器。述語は生の`WindowSnapshot`ではなく、
/// 4.5の各ドメインの信念から組み立てた読み取り専用の投影（`ClassificationView`）の上で評価する
/// （§5.4）。この投影はPolicyが要求する最小限の形であり、将来のCore.Windows/Core.Placementの
/// 実際の信念レコード（`IdentityBeliefs`/`ClassInputBeliefs`/Placement側のMinWidth/Movability）
/// から射影して渡される想定（§4.5「Policyの評価とLayoutが読む『ビュー』」と同じ位置づけ）。
module Wm.Policy.PolicyEvaluator

open Wm.Contracts.Identifiers
open Wm.Contracts.Beliefs
open Wm.Contracts.Observation
open Wm.Contracts.SharedVocabulary
open Wm.Policy.PolicyTypes

type ClassificationView = {
    Identity: IdentityBeliefs
    IsDialog: BeliefSlot<bool>
    TitleMatches: BeliefSlot<TitleMatchVector>
    MinWidthDip: BeliefSlot<int>
    Movability: BeliefSlot<Movability>
}

/// `TitleContains`の評価に使う、Policy全体のパターン集合（§5.2 R4-8/R5-6）。
/// パターンは`WindowRules`内に最初に現れた順で並べ、重複を除く——この順序が`Matches`（4.2の
/// `TitleMatchVector`）の添字と一致する契約になる。
type TitlePatternSet = {
    Patterns: string list
    PatternSetHash: string
    IndexOf: Map<string, int>
}

let rec private collectTitlePatterns (acc: string list) (p: WindowPredicate) : string list =
    match p with
    | TitleContains s -> if List.contains s acc then acc else acc @ [ s ]
    | All ps | AnyOf ps -> List.fold collectTitlePatterns acc ps
    | Not p -> collectTitlePatterns acc p
    | AnyWindow | AppIs _ | DialogIs _ | MinWidthAtLeast _ | MovableIs _ -> acc

let buildTitlePatternSet (policy: PolicyIR) : TitlePatternSet =
    let patterns = policy.WindowRules |> List.fold (fun acc r -> collectTitlePatterns acc r.When) []
    let hash =
        patterns
        |> String.concat "\u0000"
        |> System.Text.Encoding.UTF8.GetBytes
        |> System.Security.Cryptography.SHA256.HashData
        |> System.Convert.ToHexString
    { Patterns = patterns
      PatternSetHash = hash
      IndexOf = patterns |> List.mapi (fun i s -> s, i) |> Map.ofList }

let private notTruth (t: Truth) : Truth =
    match t with
    | Believed b -> Believed { b with Value = not b.Value }
    | Unformed gaps -> Unformed gaps

let private identityFieldOf (source: AppIdSource) (identity: IdentityBeliefs) : BeliefSlot<string> =
    match source with
    | Exe -> identity.Exe
    | HostExe -> identity.HostExe
    | Aumid -> identity.Aumid
    | CoreWindowExe -> identity.CoreWindowExe

let rec evaluateWindowPredicate (patternSet: TitlePatternSet) (view: ClassificationView) (predicate: WindowPredicate) : Truth =
    match predicate with
    | AnyWindow -> allIdentity
    | AppIs (source, name) ->
        match identityFieldOf source view.Identity with
        | Unformed gaps -> Unformed gaps
        | Believed b -> Believed { Value = (b.Value = name); Confidence = b.Confidence; LastRevisedAt = b.LastRevisedAt }
    | TitleContains pattern ->
        match view.TitleMatches with
        | Unformed gaps -> Unformed gaps
        | Believed b ->
            if b.Value.PatternSetHash <> patternSet.PatternSetHash then
                Unformed [ MissingTitleMatch (WindowId 0L) ] // §5.2：Policy切替直後、旧パターン集合の観測はUnknownとして扱う
            else
                match Map.tryFind pattern patternSet.IndexOf with
                | Some idx -> Believed { Value = Set.contains idx b.Value.Matches; Confidence = b.Confidence; LastRevisedAt = b.LastRevisedAt }
                | None -> Unformed [ MissingTitleMatch (WindowId 0L) ] // patternSetの構築元に無いパターン(呼び出し側の不整合)
    | DialogIs expected ->
        match view.IsDialog with
        | Unformed gaps -> Unformed gaps
        | Believed b -> Believed { Value = (b.Value = expected); Confidence = b.Confidence; LastRevisedAt = b.LastRevisedAt }
    | MinWidthAtLeast minDip ->
        match view.MinWidthDip with
        | Unformed gaps -> Unformed gaps
        | Believed b -> Believed { Value = (b.Value >= minDip); Confidence = b.Confidence; LastRevisedAt = b.LastRevisedAt }
    | MovableIs expected ->
        match view.Movability with
        | Unformed gaps -> Unformed gaps
        | Believed b ->
            Believed
                { Value = (b.Value = (if expected then Movable else NotMovable))
                  Confidence = b.Confidence
                  LastRevisedAt = b.LastRevisedAt }
    | All ps -> ps |> List.fold (fun acc p -> combineAll acc (evaluateWindowPredicate patternSet view p)) allIdentity
    | AnyOf ps -> ps |> List.fold (fun acc p -> combineAnyOf acc (evaluateWindowPredicate patternSet view p)) anyOfIdentity
    | Not p -> notTruth (evaluateWindowPredicate patternSet view p)

let rec evaluateLayoutPredicate (tiledCount: int) (monitorWidthDip: int) (predicate: LayoutPredicate) : bool =
    match predicate with
    | Always -> true
    | TiledCountAtLeast n -> tiledCount >= n
    | MonitorWidthAtLeast n -> monitorWidthDip >= n
    | Both ps -> ps |> List.forall (evaluateLayoutPredicate tiledCount monitorWidthDip)

type ClassificationResult =
    | Resolved of Management
    /// 優先度の高いルールがUnknownであり、低優先のルールへ進むと振動・誤判定の恐れがあるため保留する
    /// （§5.4「Unknownの高優先ルールがある場合、低優先のTiledをすぐ適用すると危険なら保留」）。
    /// どのルールが保留の原因かは呼び出し元のExplainが`WindowRule.Id`から特定できる。
    | AwaitingEvidence of blockedBy: PolicyRuleId

/// `WindowRules`をPriority降順・PolicyRuleId昇順で評価し、最初にYesとなったルールの`Apply`を返す。
/// 途中でUnknownに出会ったら即座に`AwaitingEvidence`で止まる（低優先のルールが先に確定していても、
/// それを採用しない——高優先ルールが後からYes/Noに確定したときに結果が変わる余地を残さないため）。
/// 全てNoならDefaultManagementを返す。
let selectManagement (patternSet: TitlePatternSet) (view: ClassificationView) (policy: PolicyIR) : ClassificationResult =
    let ordered =
        policy.WindowRules
        |> List.sortBy (fun r -> let (PolicyRuleId rid) = r.Id in (-r.Priority, rid))
    let rec go rules =
        match rules with
        | [] -> Resolved policy.DefaultManagement
        | (rule: WindowRule) :: rest ->
            match evaluateWindowPredicate patternSet view rule.When with
            | Believed b when b.Value -> Resolved rule.Apply
            | Believed _ -> go rest
            | Unformed _ -> AwaitingEvidence rule.Id
    go ordered

/// `LayoutRules`をPriority降順・PolicyRuleId昇順で評価し、最初に真となったルールの`Use`を返す。
/// `LayoutPredicate`はタイル数・モニター幅という常に既知の事実だけを読むため、三値ではなく
/// 決定的なboolで評価できる（§5.2、信念を読まない）。
let selectLayout (tiledCount: int) (monitorWidthDip: int) (policy: PolicyIR) : LayoutRef =
    let ordered =
        policy.LayoutRules
        |> List.sortBy (fun r -> let (PolicyRuleId rid) = r.Id in (-r.Priority, rid))
    ordered
    |> List.tryFind (fun r -> evaluateLayoutPredicate tiledCount monitorWidthDip r.When)
    |> Option.map (fun r -> r.Use)
    |> Option.defaultValue policy.DefaultLayout
