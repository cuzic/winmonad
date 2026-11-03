/// architecture.md §15.1 #25「`Truth`の`combineAll`/`combineAnyOf`（4.5）は結合則と単位元の法則を
/// 満たす（v2.6追加）。確度の合成の順序依存のような三値論理の実装のずれを捕まえるため、FsCheckで
/// 任意の`Truth`の3つ組について検査する」。`Beliefs.fs`のドキュメンテーションコメント自身が
/// 「§15.1 #25でFsCheckにより検査する」と明記しているにもかかわらず、`combineAll`/`combineAnyOf`
/// を引用するテストがこれまで一度も無かった（本書が約束していたテストが実際には書かれていない
/// ケース）。
///
/// **FsCheckの自動導出で任意の`Truth`を生成すると見かけ上の反例が出る（既知の罠、実装のバグではない）**：
/// `Confidence = Inferred [重複した根拠…]`や、`normalizeEvidence`（`weakerConfidence`内部、整列・
/// 重複除去・8件切り詰めを行う）が本来適用済みのはずの順序と異なる根拠列は、実際のシステムが
/// `combineAll`/`combineAnyOf`/`weakerConfidence`を通さずに手で組み立てない限り決して生まれない
/// 「非正規形」である。これを雑に生成すると、`combineAll a allIdentity`が`a`を正規化してしまい
/// `a`自身と一致しなくなる、という見かけ上の単位元法則違反が出る（実際に一度この反例を踏んで確認した）。
/// 対処：`Truth`を直接ランダム生成せず、単純な「原子的」信念（単一の根拠・Confirmed・1件のGap）を
/// `combineAll`/`combineAnyOf`自身で何度か畳み込んで作る——これにより生成される`a`/`b`/`c`は、実際の
/// システムが本当に到達し得る値だけになり、正規形の懸念そのものが生じない。
module Wm.Core.Tests.TruthCombinatorLawTests

open Xunit
open FsCheck
open Wm.Contracts.Identifiers
open Wm.Contracts.Beliefs

let private genAtomicTruth : Gen<Truth> =
    Gen.oneof
        [ Gen.map2
            (fun v t -> Believed { Value = v; Confidence = Confirmed; LastRevisedAt = t })
            Arb.generate<bool>
            (Gen.choose (-100, 100) |> Gen.map int64)
          Gen.map3
              (fun v s t -> Believed { Value = v; Confidence = Inferred [ Sample s ]; LastRevisedAt = t })
              Arb.generate<bool>
              (Gen.choose (-100, 100) |> Gen.map int64)
              (Gen.choose (-100, 100) |> Gen.map int64)
          Gen.map3
              (fun v asOf t -> Believed { Value = v; Confidence = Stale asOf; LastRevisedAt = t })
              Arb.generate<bool>
              (Gen.choose (-100, 100) |> Gen.map int64)
              (Gen.choose (-100, 100) |> Gen.map int64)
          Gen.map (fun g -> Unformed [ MissingAppIdentity (WindowId (int64 g)) ]) (Gen.choose (0, 5)) ]

/// `combine`/`identity`自身を使って原子的信念を3〜5件畳み込み、実際に到達可能な（正規形の）`Truth`を作る。
let private genReachableTruth (combine: Truth -> Truth -> Truth) (identity: Truth) : Gen<Truth> =
    gen {
        let! n = Gen.choose (0, 4)
        let! atoms = Gen.listOfLength n genAtomicTruth
        return atoms |> List.fold combine identity
    }

let private checkAssociativity (combine: Truth -> Truth -> Truth) (identity: Truth) =
    let arb = Arb.fromGen (genReachableTruth combine identity)
    let prop = Prop.forAll arb (fun a -> Prop.forAll arb (fun b -> Prop.forAll arb (fun c -> combine (combine a b) c = combine a (combine b c))))
    Check.QuickThrowOnFailure prop

let private checkIdentity (combine: Truth -> Truth -> Truth) (identity: Truth) =
    let arb = Arb.fromGen (genReachableTruth combine identity)
    let prop = Prop.forAll arb (fun a -> combine a identity = a && combine identity a = a)
    Check.QuickThrowOnFailure prop

[<Fact>]
let ``combineAllは結合則を満たす`` () =
    checkAssociativity combineAll allIdentity

[<Fact>]
let ``combineAllはallIdentityを単位元に持つ`` () =
    checkIdentity combineAll allIdentity

[<Fact>]
let ``combineAnyOfは結合則を満たす`` () =
    checkAssociativity combineAnyOf anyOfIdentity

[<Fact>]
let ``combineAnyOfはanyOfIdentityを単位元に持つ`` () =
    checkIdentity combineAnyOf anyOfIdentity
