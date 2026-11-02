/// architecture.md §5.6/§6.1：Monocleレイアウトが「全窓を意図的にWorkArea全面へ重ねる」ことを検査する。
module Wm.Layouts.Tests.MonocleTests

open FsCheck
open FsCheck.Xunit
open Wm.Contracts.SharedVocabulary
open Wm.Layouts
open Wm.Layouts.LayoutRegistry
open Wm.Layouts.Tests.TestHelpers

[<Property>]
let ``全てのTiled窓がWorkArea全面へ、欠落も重複も無く割り当てられる``
    (x: int)
    (y: int)
    (w: PositiveInt)
    (h: PositiveInt)
    (count: int)
    =
    let area = mkWorkArea x y w h
    let tiled = mkTiledWindows count
    let ctx = mkContext area tiled
    match Monocle.calculate NoArgs ctx with
    | Ok placements ->
        let expected = tiled |> List.map (fun t -> t.Id) |> Set.ofList
        let actual = placements |> List.map (fun p -> p.Window) |> Set.ofList
        (List.length placements = List.length tiled)
        && (expected = actual)
        && (placements |> List.forall (fun p -> p.Target = area))
    | Error _ -> false

[<Property>]
let ``MasterRatio/ColumnCountは常にInvalidInputで拒否する`` (x: int) (y: int) (w: PositiveInt) (h: PositiveInt) (ratio: decimal) (cols: int) =
    let ctx = mkContext (mkWorkArea x y w h) (mkTiledWindows 3)
    let ratioRejected =
        match Monocle.calculate (MasterRatio ratio) ctx with
        | Error (InvalidInput _) -> true
        | _ -> false
    let colsRejected =
        match Monocle.calculate (ColumnCount cols) ctx with
        | Error (InvalidInput _) -> true
        | _ -> false
    ratioRejected && colsRejected
