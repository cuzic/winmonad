/// architecture.md §5.6/§6.1：MasterStackレイアウトの幾何的な正しさを検査する。
module Wm.Layouts.Tests.MasterStackTests

open Xunit
open FsCheck
open FsCheck.Xunit
open Wm.Contracts.Identifiers
open Wm.Contracts.Beliefs
open Wm.Contracts.SharedVocabulary
open Wm.Layouts
open Wm.Layouts.LayoutRegistry
open Wm.Layouts.Tests.TestHelpers

let private mkMinWidthDip (dip: int) : Belief<int> option = Some { Value = dip; Confidence = Inferred []; LastRevisedAt = 0L }

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

// ===== §6.2 MinWidth対応（v2.78、プロジェクトオーナー確認済み：「兄弟窓を縮めて調整する」） =====

[<Fact>]
let ``masterのMinWidthが比率どおりの幅を上回る場合、stack側を削ってmasterへ充てる`` () =
    let area: PixelRect = { X = 0; Y = 0; Width = 1000; Height = 600 }
    let master: LayoutWindow = { Id = WindowId 1L; MinWidthDip = mkMinWidthDip 700 }
    let stackWin: LayoutWindow = { Id = WindowId 2L; MinWidthDip = None }
    let ctx = mkContext area [ master; stackWin ]
    match MasterStack.calculate (MasterRatio 0.3m) ctx with
    | Ok placements ->
        let masterRect = (placements |> List.find (fun p -> p.Window = master.Id)).Target
        let stackRect = (placements |> List.find (fun p -> p.Window = stackWin.Id)).Target
        Assert.Equal(700, masterRect.Width) // 比率どおりなら300だが、MinWidthの700へ広げる
        Assert.Equal(300, stackRect.Width) // 残りをstackへ(1000-700)
    | Error e -> Assert.True(false, sprintf "unexpected: %A" e)

[<Fact>]
let ``stack側のMinWidthが比率どおりの幅を上回る場合、master側を削ってstackへ充てる`` () =
    let area: PixelRect = { X = 0; Y = 0; Width = 1000; Height = 600 }
    let master: LayoutWindow = { Id = WindowId 1L; MinWidthDip = None }
    let stackWin: LayoutWindow = { Id = WindowId 2L; MinWidthDip = mkMinWidthDip 800 }
    let ctx = mkContext area [ master; stackWin ]
    match MasterStack.calculate (MasterRatio 0.7m) ctx with
    | Ok placements ->
        let masterRect = (placements |> List.find (fun p -> p.Window = master.Id)).Target
        let stackRect = (placements |> List.find (fun p -> p.Window = stackWin.Id)).Target
        Assert.Equal(200, masterRect.Width) // 比率どおりなら700だが、stackのMinWidth 800を確保するため削られる
        Assert.Equal(800, stackRect.Width)
    | Error e -> Assert.True(false, sprintf "unexpected: %A" e)

[<Fact>]
let ``stack側のMinWidthは構成窓のうち最大値を使う(全員が同じstackArea.Widthを共有するため)`` () =
    let area: PixelRect = { X = 0; Y = 0; Width = 1000; Height = 600 }
    let master: LayoutWindow = { Id = WindowId 1L; MinWidthDip = None }
    let stackA: LayoutWindow = { Id = WindowId 2L; MinWidthDip = mkMinWidthDip 300 }
    let stackB: LayoutWindow = { Id = WindowId 3L; MinWidthDip = mkMinWidthDip 800 }
    let ctx = mkContext area [ master; stackA; stackB ]
    match MasterStack.calculate (MasterRatio 0.7m) ctx with
    | Ok placements ->
        let stackWidths = placements |> List.filter (fun p -> p.Window <> master.Id) |> List.map (fun p -> p.Target.Width)
        Assert.All<int>(stackWidths, fun ww -> Assert.Equal(800, ww)) // stackBの800が両方のstack窓の幅になる
    | Error e -> Assert.True(false, sprintf "unexpected: %A" e)

[<Fact>]
let ``両者のMinWidthの合計がWorkArea幅を超える場合は、これ以上は強制せず元の比率のまま(打ち止め)`` () =
    let area: PixelRect = { X = 0; Y = 0; Width = 1000; Height = 600 }
    let master: LayoutWindow = { Id = WindowId 1L; MinWidthDip = mkMinWidthDip 700 }
    let stackWin: LayoutWindow = { Id = WindowId 2L; MinWidthDip = mkMinWidthDip 700 }
    let ctx = mkContext area [ master; stackWin ]
    match MasterStack.calculate (MasterRatio 0.5m) ctx with
    | Ok placements ->
        let masterRect = (placements |> List.find (fun p -> p.Window = master.Id)).Target
        Assert.Equal(500, masterRect.Width) // 700+700 > 1000なので満たせない。比率どおりの500のまま
    | Error e -> Assert.True(false, sprintf "unexpected: %A" e)

[<Fact>]
let ``MinWidthDipは画面のDPIで物理ピクセルへ変換される(toDipの逆、px = dip * dpi / 96)`` () =
    let area: PixelRect = { X = 0; Y = 0; Width = 1000; Height = 600 }
    let master: LayoutWindow = { Id = WindowId 1L; MinWidthDip = mkMinWidthDip 400 } // 192DPIでは800px相当
    let stackWin: LayoutWindow = { Id = WindowId 2L; MinWidthDip = None }
    let ctx = { mkContext area [ master; stackWin ] with Dpi = 192 }
    match MasterStack.calculate (MasterRatio 0.3m) ctx with
    | Ok placements ->
        let masterRect = (placements |> List.find (fun p -> p.Window = master.Id)).Target
        Assert.Equal(800, masterRect.Width)
    | Error e -> Assert.True(false, sprintf "unexpected: %A" e)

/// MinWidthの値に関わらず、master幅+stack幅は常にWorkArea幅をちょうど分割する
/// （PlanValidatorの前提・既存の性質テストと同じ不変条件が、MinWidth調整後も崩れないことの確認）。
[<Property>]
let ``MinWidthで調整しても、masterとstackの幅の合計はWorkArea幅のまま変わらない``
    (x: int)
    (y: int)
    (w: PositiveInt)
    (h: PositiveInt)
    (masterMinRaw: int)
    (stackMinRaw: int)
    =
    let area = mkWorkArea x y w h
    let master: LayoutWindow = { Id = WindowId 1L; MinWidthDip = mkMinWidthDip (abs masterMinRaw % 5000) }
    let stackWin: LayoutWindow = { Id = WindowId 2L; MinWidthDip = mkMinWidthDip (abs stackMinRaw % 5000) }
    let ctx = mkContext area [ master; stackWin ]
    match MasterStack.calculate NoArgs ctx with
    | Ok placements ->
        let totalWidth = placements |> List.sumBy (fun p -> p.Target.Width)
        totalWidth = area.Width
    | Error _ -> false
