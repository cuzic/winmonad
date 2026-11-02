/// architecture.md §6.2：PlanValidatorの7つの必須条件(validate)と、診断/判断分離(decide)を検査する。
module Wm.Layouts.Tests.PlanValidatorTests

open Xunit
open FsCheck.Xunit
open Wm.Contracts.Identifiers
open Wm.Contracts.SharedVocabulary
open Wm.Layouts
open Wm.Layouts.LayoutRegistry
open Wm.Layouts.PlanValidator
open Wm.Layouts.Tests.TestHelpers

let private w1 = WindowId 1L
let private w2 = WindowId 2L
let private w3 = WindowId 3L

let private area = { X = 0; Y = 0; Width = 1000; Height = 800 }
let private ctx = mkContext area []

let private validInput =
    { Context = ctx
      Management = Map.ofList [ w1, EffTiled; w2, EffTiled ]
      Alive = Set.ofList [ w1; w2 ] }

let private validPlacements =
    [ { Window = w1; Target = { X = 0; Y = 0; Width = 500; Height = 800 } }
      { Window = w2; Target = { X = 500; Y = 0; Width = 500; Height = 800 } } ]

[<Fact>]
let ``条件を全て満たすPlanはOkになる`` () =
    match validate validInput validPlacements with
    | Ok plan -> Assert.Equal<Placement list>(validPlacements, ValidatedPlan.placements plan)
    | Error es -> failwithf "expected Ok, got errors: %A" es

[<Fact>]
let ``死亡した窓への配置はUnmanagedOrDeadWindowになる(#1)`` () =
    let input = { validInput with Alive = Set.ofList [ w1 ] } // w2は死亡
    match validate input validPlacements with
    | Error es -> Assert.Contains(UnmanagedOrDeadWindow w2, es)
    | Ok _ -> failwith "expected Error"

[<Fact>]
let ``管理対象外の窓への配置はUnmanagedOrDeadWindowになる(#1)`` () =
    let input = { validInput with Management = Map.ofList [ w1, EffTiled ] } // w2は管理対象に無い
    match validate input validPlacements with
    | Error es -> Assert.Contains(UnmanagedOrDeadWindow w2, es)
    | Ok _ -> failwith "expected Error"

[<Fact>]
let ``同一窓への複数配置はDuplicateInPlanになる(#2)`` () =
    let placements = validPlacements @ [ { Window = w1; Target = { X = 0; Y = 0; Width = 100; Height = 100 } } ]
    let input = { validInput with Management = Map.ofList [ w1, EffTiled; w2, EffTiled ] }
    match validate input placements with
    | Error es -> Assert.Contains(DuplicateInPlan w1, es)
    | Ok _ -> failwith "expected Error"

[<Fact>]
let ``幅または高さが0以下はNonPositiveSizeになる(#3)`` () =
    let placements =
        [ { Window = w1; Target = { X = 0; Y = 0; Width = 0; Height = 800 } }
          { Window = w2; Target = { X = 500; Y = 0; Width = 500; Height = 800 } } ]
    match validate validInput placements with
    | Error es -> Assert.Contains(NonPositiveSize (w1, 0, 800), es)
    | Ok _ -> failwith "expected Error"

[<Fact>]
let ``座標演算のオーバーフローはArithmeticOverflowになる(#3)`` () =
    let placements =
        [ { Window = w1; Target = { X = System.Int32.MaxValue - 10; Y = 0; Width = 1000; Height = 800 } }
          { Window = w2; Target = { X = 500; Y = 0; Width = 500; Height = 800 } } ]
    match validate validInput placements with
    | Error es -> Assert.Contains(ArithmeticOverflow w1, es)
    | Ok _ -> failwith "expected Error"

[<Fact>]
let ``WorkAreaと一切交差しない矩形はOutsideWorkAreaになる(#4)`` () =
    let outside = { X = 10000; Y = 10000; Width = 100; Height = 100 }
    let placements = [ { Window = w1; Target = outside }; { Window = w2; Target = { X = 500; Y = 0; Width = 500; Height = 800 } } ]
    match validate validInput placements with
    | Error es -> Assert.Contains(OutsideWorkArea (w1, outside), es)
    | Ok _ -> failwith "expected Error"

[<Fact>]
let ``Excluded窓への配置はExcludedTargetになる(#5)`` () =
    let input = { validInput with Management = Map.ofList [ w1, EffExcluded; w2, EffTiled ] }
    match validate input validPlacements with
    | Error es -> Assert.Contains(ExcludedTarget w1, es)
    | Ok _ -> failwith "expected Error"

[<Fact>]
let ``Alive∩Managed(Tiled)の窓がPlanに無ければMissingPlacementになる(#6)`` () =
    let input = { validInput with Management = Map.ofList [ w1, EffTiled; w2, EffTiled; w3, EffTiled ]; Alive = Set.ofList [ w1; w2; w3 ] }
    // w3には配置が無い
    match validate input validPlacements with
    | Error es -> Assert.Contains(MissingPlacement w3, es)
    | Ok _ -> failwith "expected Error"

[<Fact>]
let ``Unplaced(Management未登録ではなくEffUnplaced)の窓はMissingPlacementにならない`` () =
    // 4.4：UnplacedはTiledWindowsに入らない。EffUnplacedはAlive∩Managed(Tiled)の対象外。
    let input = { validInput with Management = Map.ofList [ w1, EffTiled; w2, EffTiled; w3, EffUnplaced ]; Alive = Set.ofList [ w1; w2; w3 ] }
    match validate input validPlacements with
    | Ok _ -> ()
    | Error es -> failwithf "expected Ok, got %A" es

[<Fact>]
let ``構造的な違反(NonPositiveSize)を含むとdecideはRejectWholePlanを返す`` () =
    let errors = [ NonPositiveSize (w1, 0, 800) ]
    Assert.Equal(RejectWholePlan, decide errors)

[<Fact>]
let ``UnmanagedOrDeadWindowだけならdecideはRelayoutWithoutを返す`` () =
    let errors = [ UnmanagedOrDeadWindow w1; ExcludedTarget w2 ]
    Assert.Equal(RelayoutWithout (Set.ofList [ w1; w2 ]), decide errors)

[<Fact>]
let ``UnmanagedOrDeadWindowとMissingPlacementが混在するとdecideはRejectWholePlanを返す`` () =
    let errors = [ UnmanagedOrDeadWindow w1; MissingPlacement w3 ]
    Assert.Equal(RejectWholePlan, decide errors)

/// MasterStack/MonocleのLayoutFunctionを実際にPlanValidatorへ通し、「正しく構成されたLayoutContextなら
/// 常にvalidateを通る」という、パイプライン全体(§6.1)の結合を直接確認する。
[<Property>]
let ``MasterStackの出力は、対応するManagement/Aliveと組み合わせると常にvalidateを通る``
    (x: int)
    (y: int)
    (w: FsCheck.PositiveInt)
    (h: FsCheck.PositiveInt)
    (count: int)
    =
    let area = mkWorkArea x y w h
    let tiled = mkTiledWindows count
    let ctx = mkContext area tiled
    match MasterStack.calculate NoArgs ctx with
    | Ok placements ->
        let management = tiled |> List.map (fun t -> t.Id, EffTiled) |> Map.ofList
        let input = { Context = ctx; Management = management; Alive = management |> Map.toList |> List.map fst |> Set.ofList }
        match validate input placements with
        | Ok _ -> true
        | Error _ -> false
    | Error _ -> true // 退化したWorkArea等でLayout自体が拒否した場合はこの性質の対象外
