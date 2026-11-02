/// architecture.md §5.6/§6.1：MasterStackレイアウトの幾何的な正しさを検査する。
module Wm.Layouts.Tests.MasterStackTests

open FsCheck
open FsCheck.Xunit
open Wm.Contracts.SharedVocabulary
open Wm.Layouts
open Wm.Layouts.LayoutRegistry
open Wm.Layouts.Tests.TestHelpers

[<Property>]
let ``TiledWindowsが0件なら空のPlacementを返す`` (x: int) (y: int) (w: PositiveInt) (h: PositiveInt) =
    let ctx = mkContext (mkWorkArea x y w h) []
    MasterStack.calculate NoArgs ctx = Ok []

[<Property>]
let ``TiledWindowsが1件ならWorkArea全体を1窓へ割り当てる`` (x: int) (y: int) (w: PositiveInt) (h: PositiveInt) =
    let area = mkWorkArea x y w h
    let ctx = mkContext area (mkTiledWindows 1)
    MasterStack.calculate NoArgs ctx = Ok [ { Window = (mkTiledWindows 1).Head.Id; Target = area } ]

/// 全ての窓がちょうど1回ずつ配置される(欠落・重複が無い)——PlanValidatorの#2/#6が守る不変条件を
/// LayoutFunction自身も満たしていることの確認。
[<Property>]
let ``N件のTiledWindowsは欠落も重複も無くちょうどN件のPlacementになる``
    (x: int)
    (y: int)
    (w: PositiveInt)
    (h: PositiveInt)
    (count: int)
    =
    let tiled = mkTiledWindows count
    let ctx = mkContext (mkWorkArea x y w h) tiled
    match MasterStack.calculate NoArgs ctx with
    | Ok placements ->
        let expected = tiled |> List.map (fun t -> t.Id) |> Set.ofList
        let actual = placements |> List.map (fun p -> p.Window) |> Set.ofList
        (List.length placements = List.length tiled) && (expected = actual)
    | Error _ -> tiled.Length < 2 // 0/1件は常にOkのはず(上の2テストで確認済み)、到達しない

/// masterはWorkAreaの左`ratio`幅いっぱいに、残りはstack側に収まる(幅の合計がWorkArea幅と一致する)。
[<Property>]
let ``2件以上では、masterとstackのPlacementがWorkAreaの幅をちょうど分割する``
    (x: int)
    (y: int)
    (w: PositiveInt)
    (h: PositiveInt)
    (countRaw: int)
    (ratioRaw: int)
    =
    let count = 2 + (abs countRaw % 7) // 2..8
    let tiled = mkTiledWindows count |> List.truncate count
    let area = mkWorkArea x y w h
    let ratioPct = 1 + (abs ratioRaw % 98) // 1..98 → (0,1)区間の百分率
    let ratio = decimal ratioPct / 100m
    let ctx = mkContext area tiled
    match MasterStack.calculate (MasterRatio ratio) ctx with
    | Ok placements ->
        let masterRect = (placements |> List.find (fun p -> p.Window = tiled.Head.Id)).Target
        let stackWidths = placements |> List.filter (fun p -> p.Window <> tiled.Head.Id) |> List.map (fun p -> p.Target.Width)
        let expectedMasterWidth = int (decimal area.Width * ratio)
        (masterRect.X = area.X)
        && (masterRect.Height = area.Height)
        && (masterRect.Width = expectedMasterWidth)
        && (stackWidths |> List.forall (fun ww -> ww = area.Width - masterRect.Width))
    | Error _ -> false

[<Property>]
let ``MasterRatioが(0,1)の範囲外ならInvalidInputを返す`` (x: int) (y: int) (w: PositiveInt) (h: PositiveInt) (badRatio: decimal) =
    (badRatio <= 0.0m || badRatio >= 1.0m) ==>
        lazy
            let ctx = mkContext (mkWorkArea x y w h) (mkTiledWindows 3)
            match MasterStack.calculate (MasterRatio badRatio) ctx with
            | Error (InvalidInput _) -> true
            | _ -> false

[<Property>]
let ``ColumnCountは常にInvalidInputで拒否する`` (x: int) (y: int) (w: PositiveInt) (h: PositiveInt) (cols: int) =
    let ctx = mkContext (mkWorkArea x y w h) (mkTiledWindows 3)
    match MasterStack.calculate (ColumnCount cols) ctx with
    | Error (InvalidInput _) -> true
    | _ -> false
