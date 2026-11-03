/// architecture.md §5.3/§5.6/§6.1：Columnsレイアウト（v2.67で追加）の幾何的な正しさを検査する。
module Wm.Layouts.Tests.ColumnsTests

open FsCheck
open FsCheck.Xunit
open Wm.Contracts.SharedVocabulary
open Wm.Layouts
open Wm.Layouts.LayoutRegistry
open Wm.Layouts.Tests.TestHelpers

[<Property>]
let ``TiledWindowsが0件なら空のPlacementを返す`` (x: int) (y: int) (w: PositiveInt) (h: PositiveInt) =
    let ctx = mkContext (mkWorkArea x y w h) []
    Columns.calculate NoArgs ctx = Ok []

/// 全ての窓がちょうど1回ずつ配置される(欠落・重複が無い)——PlanValidatorの#2/#6が守る不変条件を
/// LayoutFunction自身も満たしていることの確認（MasterStackTests.fsと同じ観点）。
[<Property>]
let ``N件のTiledWindowsは欠落も重複も無くちょうどN件のPlacementになる``
    (x: int)
    (y: int)
    (w: PositiveInt)
    (h: PositiveInt)
    (count: int)
    (colsRaw: int)
    =
    let tiled = mkTiledWindows count
    let ctx = mkContext (mkWorkArea x y w h) tiled
    let cols = 1 + (abs colsRaw % 5) // 1..5
    match Columns.calculate (ColumnCount cols) ctx with
    | Ok placements ->
        let expected = tiled |> List.map (fun t -> t.Id) |> Set.ofList
        let actual = placements |> List.map (fun p -> p.Window) |> Set.ofList
        (List.length placements = List.length tiled) && (expected = actual)
    | Error _ -> tiled.IsEmpty // 0件は常にOk(上のテストで確認済み)、到達しない

/// 各列のPlacementの幅の合計がWorkAreaの幅と一致し、各列内部の高さの合計がWorkAreaの高さと一致する
/// （横方向の列分割・縦方向の列内分割がどちらも隙間/overlapを作らないことの確認）。
[<Property>]
let ``列数ぶんのPlacementがWorkAreaの幅をちょうど分割し、各列内の高さはWorkAreaの高さに一致する``
    (x: int)
    (y: int)
    (w: PositiveInt)
    (h: PositiveInt)
    (countRaw: int)
    (colsRaw: int)
    =
    let count = 2 + (abs countRaw % 8) // 2..9
    // `mkTiledWindows`自身が`count % 9`で窓数を決めるため（count=9だと0件になる、境界の取り違えに
    // 一度実際に踏んだ）、要求した`count`ではなく実際に生成された`tiled.Length`を基準にする。
    let tiled = mkTiledWindows count
    let area = mkWorkArea x y w h
    let cols = 1 + (abs colsRaw % 5) // 1..5
    let ctx = mkContext area tiled
    match Columns.calculate (ColumnCount cols) ctx with
    | Ok _ when tiled.IsEmpty -> true // 窓が無ければ検査対象外(上の「0件なら空」テストの範囲)
    | Ok placements when not placements.IsEmpty ->
        // 列ごとにグループ化(X座標で識別——同じ列のPlacementは同じXを持つ)。
        let byColumn = placements |> List.groupBy (fun p -> p.Target.X)
        let totalWidth = byColumn |> List.map (fun (_, ps) -> (List.head ps).Target.Width) |> List.sum
        let heightsOk =
            byColumn
            |> List.forall (fun (_, ps) -> (ps |> List.sumBy (fun p -> p.Target.Height)) = area.Height)
        totalWidth = area.Width && heightsOk
    | Ok _ -> false // countが2以上ならPlacementは空にならないはず
    | Error _ -> false

[<Property>]
let ``ColumnCountが0以下ならInvalidInputを返す`` (x: int) (y: int) (w: PositiveInt) (h: PositiveInt) (badCols: int) =
    (badCols <= 0) ==>
        lazy
            let ctx = mkContext (mkWorkArea x y w h) (mkTiledWindows 3)
            match Columns.calculate (ColumnCount badCols) ctx with
            | Error (InvalidInput _) -> true
            | _ -> false

[<Property>]
let ``MasterRatioは常にInvalidInputで拒否する`` (x: int) (y: int) (w: PositiveInt) (h: PositiveInt) (ratio: decimal) =
    let ctx = mkContext (mkWorkArea x y w h) (mkTiledWindows 3)
    match Columns.calculate (MasterRatio ratio) ctx with
    | Error (InvalidInput _) -> true
    | _ -> false

[<Property>]
let ``NoArgsは既定の列数(2)として受け付ける`` (x: int) (y: int) (w: PositiveInt) (h: PositiveInt) =
    let ctx = mkContext (mkWorkArea x y w h) (mkTiledWindows 4)
    match Columns.calculate NoArgs ctx, Columns.calculate (ColumnCount 2) ctx with
    | Ok a, Ok b -> a = b
    | _ -> false

[<Property>]
let ``窓数より列数が多い場合は空列を作らず、実際の窓数ぶんの列だけになる`` (x: int) (y: int) (w: PositiveInt) (h: PositiveInt) =
    let area = mkWorkArea x y w h
    let tiled = mkTiledWindows 3
    let ctx = mkContext area tiled
    match Columns.calculate (ColumnCount 10) ctx with
    | Ok placements ->
        let distinctXs = placements |> List.map (fun p -> p.Target.X) |> List.distinct
        List.length distinctXs = List.length tiled // 列数=窓数(3)まで減らされている
    | Error _ -> tiled.IsEmpty
