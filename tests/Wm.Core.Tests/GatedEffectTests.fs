/// architecture.md §6.2「Effect種別ごとのゲートの適用範囲」表を、`GatedEffect.gate`に対して1セルずつ
/// 検査する（Opusレビュー2-11：表が実装からずれても機械的に検出できるようにする。2-1〜2-5のblocking指摘は
/// いずれもこの表と実装の不一致だったため、ここに固定しておく価値が大きい）。
///
/// `gate`はinternal化した（Opusレビュー2-9）が、AssemblyInfo.fsで"Wm.Core.Tests"にも
/// InternalsVisibleToを与えているため、このテストから直接呼べる。
module Wm.Core.Tests.GatedEffectTests

open Xunit
open Wm.Contracts.Identifiers
open Wm.Contracts.SharedVocabulary
open Wm.Contracts.Beliefs
open Wm.Contracts.CoreCommand
open Wm.Contracts.GatedEffect

let private w = WindowId 1L
let private rid = RequestId 1L
let private monitor =
    { Id = MonitorId 0L
      StableKey = "TEST"
      Bounds = { X = 0; Y = 0; Width = 1920; Height = 1080 }
      WorkArea = { X = 0; Y = 0; Width = 1920; Height = 1040 }
      Dpi = 96
      IsPrimary = true }
let private rect = { X = 0; Y = 0; Width = 100; Height = 100 }

/// 「このEffectは何の問題も無く通過すべき」既定のGateInput。各テストはちょうど1つの条件だけを立てる。
let private cleanInput =
    { Alive = Set.singleton w
      Management = Map.empty
      Monitors = [ monitor ]
      NotMovable = Set.empty
      Hung = Set.empty
      HiddenByWm = Set.empty
      NeedsRestore = Set.empty }

let private passes (input: GateInput) (e: Effect) =
    let ok, rejected = GatedEffect.gate input [ e ]
    (not (List.isEmpty ok)) && List.isEmpty rejected

let private rejectedWith (input: GateInput) (e: Effect) (expected: GateRejection) =
    let ok, rejected = GatedEffect.gate input [ e ]
    List.isEmpty ok && rejected = [ (Some rid, expected) ]

// ===== TargetNotAlive：全Effectに適用、ForgetWindowだけ除外（2-4） =====

[<Fact>]
let ``TargetNotAlive: SetRectに適用される`` () =
    Assert.True(rejectedWith { cleanInput with Alive = Set.empty } (SetRect (rid, w, rect)) (TargetNotAlive w))

[<Fact>]
let ``TargetNotAlive: ForgetWindowには適用されない（2-4）`` () =
    let ok, rejected = GatedEffect.gate { cleanInput with Alive = Set.empty } [ ForgetWindow w ]
    Assert.Equal<Effect list>([ ForgetWindow w ], ok |> List.map GatedEffect.effect)
    Assert.Empty(rejected)

[<Fact>]
let ``TargetNotAlive: RequestCloseに適用される`` () =
    Assert.True(rejectedWith { cleanInput with Alive = Set.empty } (RequestClose (rid, w)) (TargetNotAlive w))

// ===== TargetExcluded：ObserveWindow/Reprobe/ForgetWindowには適用しない（2-5） =====

[<Fact>]
let ``TargetExcluded: SetRectに適用される`` () =
    let input = { cleanInput with Management = Map.ofList [ w, EffExcluded ] }
    Assert.True(rejectedWith input (SetRect (rid, w, rect)) (TargetExcluded w))

[<Fact>]
let ``TargetExcluded: ObserveWindowには適用されない（2-5）`` () =
    let input = { cleanInput with Management = Map.ofList [ w, EffExcluded ] }
    Assert.True(passes input (ObserveWindow w))

[<Fact>]
let ``TargetExcluded: Reprobeには適用されない（2-5）`` () =
    let input = { cleanInput with Management = Map.ofList [ w, EffExcluded ] }
    Assert.True(passes input (Reprobe (w, KResponsiveness)))

[<Fact>]
let ``TargetExcluded: ForgetWindowには適用されない`` () =
    let input = { cleanInput with Management = Map.ofList [ w, EffExcluded ] }
    Assert.True(passes input (ForgetWindow w))

// ===== TargetNotMovable：SetRect/RestoreBeforeMoveだけに適用される =====

[<Fact>]
let ``TargetNotMovable: SetRectに適用される`` () =
    let input = { cleanInput with NotMovable = Set.singleton w }
    Assert.True(rejectedWith input (SetRect (rid, w, rect)) (TargetNotMovable w))

[<Fact>]
let ``TargetNotMovable: RequestForegroundには適用されない`` () =
    let input = { cleanInput with NotMovable = Set.singleton w }
    Assert.True(passes input (RequestForeground (rid, w)))

[<Fact>]
let ``TargetNotMovable: SetPresenceには適用されない`` () =
    let input = { cleanInput with NotMovable = Set.singleton w }
    Assert.True(passes input (SetPresence (rid, w, true)))

// ===== TargetBelievedHung：SetRect/RestoreBeforeMove/RequestForegroundに適用、SetPresenceには適用しない（2-2/2-3） =====

[<Fact>]
let ``TargetBelievedHung: SetRectに適用される（2-3）`` () =
    let input = { cleanInput with Hung = Set.singleton w }
    Assert.True(rejectedWith input (SetRect (rid, w, rect)) (TargetBelievedHung w))

[<Fact>]
let ``TargetBelievedHung: RestoreBeforeMoveに適用される（2-3）`` () =
    let input = { cleanInput with Hung = Set.singleton w }
    Assert.True(rejectedWith input (RestoreBeforeMove (rid, w)) (TargetBelievedHung w))

[<Fact>]
let ``TargetBelievedHung: RequestForegroundに適用される`` () =
    let input = { cleanInput with Hung = Set.singleton w }
    Assert.True(rejectedWith input (RequestForeground (rid, w)) (TargetBelievedHung w))

[<Fact>]
let ``TargetBelievedHung: SetPresenceには適用されない（2-2：ハング窓も隠す必要がある）`` () =
    let input = { cleanInput with Hung = Set.singleton w }
    Assert.True(passes input (SetPresence (rid, w, true)))

[<Fact>]
let ``TargetBelievedHung: Reprobeには適用されない（ハング解除の唯一の経路）`` () =
    let input = { cleanInput with Hung = Set.singleton w }
    Assert.True(passes input (Reprobe (w, KResponsiveness)))

[<Fact>]
let ``TargetBelievedHung: RequestCloseには適用されない（本文の例外）`` () =
    let input = { cleanInput with Hung = Set.singleton w }
    Assert.True(passes input (RequestClose (rid, w)))

// ===== TargetHiddenByWm：SetRect/RestoreBeforeMove/RequestForeground/RequestCloseに適用、SetPresenceには適用しない（2-1/2-3） =====

[<Fact>]
let ``TargetHiddenByWm: SetRectに適用される（2-3）`` () =
    let input = { cleanInput with HiddenByWm = Set.singleton w }
    Assert.True(rejectedWith input (SetRect (rid, w, rect)) (TargetHiddenByWm w))

[<Fact>]
let ``TargetHiddenByWm: RestoreBeforeMoveに適用される（2-3）`` () =
    let input = { cleanInput with HiddenByWm = Set.singleton w }
    Assert.True(rejectedWith input (RestoreBeforeMove (rid, w)) (TargetHiddenByWm w))

[<Fact>]
let ``TargetHiddenByWm: SetPresenceには適用されない（2-1：WMの非表示Effectそのものを弾いてはならない）`` () =
    let input = { cleanInput with HiddenByWm = Set.singleton w }
    Assert.True(passes input (SetPresence (rid, w, false)))

[<Fact>]
let ``TargetHiddenByWm: RequestCloseに適用される`` () =
    let input = { cleanInput with HiddenByWm = Set.singleton w }
    Assert.True(rejectedWith input (RequestClose (rid, w)) (TargetHiddenByWm w))

// ===== 矩形の構造的チェック =====

[<Fact>]
let ``NonPositiveRect: 幅0のSetRectは弾かれる`` () =
    Assert.True(rejectedWith cleanInput (SetRect (rid, w, { rect with Width = 0 })) (NonPositiveRect w))

[<Fact>]
let ``OutsideAnyMonitor: どのモニターとも交差しない矩形は弾かれる`` () =
    Assert.True(rejectedWith cleanInput (SetRect (rid, w, { X = 9999; Y = 9999; Width = 10; Height = 10 })) (OutsideAnyMonitor w))

[<Fact>]
let ``MissingRestoreBeforeMove: NeedsRestoreの窓へ先行するRestoreBeforeMove無しのSetRectは弾かれる（2-6）`` () =
    let input = { cleanInput with NeedsRestore = Set.singleton w }
    Assert.True(rejectedWith input (SetRect (rid, w, rect)) (MissingRestoreBeforeMove w))

[<Fact>]
let ``MissingRestoreBeforeMove: 同一バッチ内に先行するRestoreBeforeMoveがあれば通る`` () =
    let input = { cleanInput with NeedsRestore = Set.singleton w }
    let ok, rejected = GatedEffect.gate input [ ApplyBatch (BatchId 1L, [ RestoreBeforeMove (rid, w); SetRect (RequestId 2L, w, rect) ]) ]
    Assert.Empty(rejected)
    match ok with
    | [ g ] ->
        match GatedEffect.effect g with
        | ApplyBatch (_, children) -> Assert.Equal(2, List.length children)
        | _ -> Assert.True(false, "expected ApplyBatch")
    | _ -> Assert.True(false, "expected exactly one passing effect")

[<Fact>]
let ``MissingRestoreBeforeMove: 別バッチのRestoreBeforeMoveは数えない（2-7）`` () =
    let input = { cleanInput with NeedsRestore = Set.singleton w }
    let ok, rejected =
        GatedEffect.gate input
            [ ApplyBatch (BatchId 1L, [ RestoreBeforeMove (rid, w) ])
              ApplyBatch (BatchId 2L, [ SetRect (RequestId 2L, w, rect) ]) ]
    Assert.Equal<(RequestId option * GateRejection) list>([ Some (RequestId 2L), MissingRestoreBeforeMove w ], rejected)
    match ok with
    | [ g ] ->
        match GatedEffect.effect g with
        | ApplyBatch (BatchId 1L, children) -> Assert.Equal(1, List.length children)
        | _ -> Assert.True(false, "expected the first ApplyBatch to pass through")
    | _ -> Assert.True(false, "expected exactly the first batch to pass")

// ===== ApplyBatchの構造的な扱い =====

[<Fact>]
let ``ApplyBatch: 入れ子のApplyBatchも検査される（2-8）`` () =
    let input = { cleanInput with Alive = Set.empty }
    let ok, rejected =
        GatedEffect.gate input [ ApplyBatch (BatchId 1L, [ ApplyBatch (BatchId 2L, [ SetRect (rid, w, rect) ]) ]) ]
    Assert.Empty(ok)
    Assert.Equal<(RequestId option * GateRejection) list>([ Some rid, TargetNotAlive w ], rejected)

[<Fact>]
let ``ApplyBatch: 全ての子が弾かれたら空のApplyBatchを出さない`` () =
    let input = { cleanInput with Alive = Set.empty }
    let ok, _rejected = GatedEffect.gate input [ ApplyBatch (BatchId 1L, [ SetRect (rid, w, rect) ]) ]
    Assert.Empty(ok)
