/// architecture.md §5.3/§5.6/§6.1：Columnsレイアウト（v2.67で追加）の幾何的な正しさを検査する。
module Wm.Layouts.Tests.ColumnsTests

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
let private noMinWidth: Belief<int> option = None

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

// ===== §6.2 MinWidth対応（v2.79、v2.78「兄弟窓を縮めて調整する」のN列への一般化：比例配分） =====

let private columnXsOf (placements: Placement list) : int list =
    placements |> List.map (fun p -> p.Target.X) |> List.distinct |> List.sort

let private widthOfColumnAt (placements: Placement list) (x: int) : int =
    (placements |> List.find (fun p -> p.Target.X = x)).Target.Width

[<Fact>]
let ``不足列1本に対し、複数の余裕列がそれぞれの余裕の大きさに比例して削る`` () =
    let area: PixelRect = { X = 0; Y = 0; Width = 900; Height = 600 }
    let a: LayoutWindow = { Id = WindowId 1L; MinWidthDip = mkMinWidthDip 500 } // 自然な300に対し200不足
    let b: LayoutWindow = { Id = WindowId 2L; MinWidthDip = mkMinWidthDip 200 } // 自然な300に対し余裕100
    let c: LayoutWindow = { Id = WindowId 3L; MinWidthDip = noMinWidth } // 余裕300(制約無し=0とみなす)
    let ctx = mkContext area [ a; b; c ]
    match Columns.calculate (ColumnCount 3) ctx with
    | Ok placements ->
        let xs = columnXsOf placements
        Assert.Equal(3, List.length xs)
        let widths = xs |> List.map (widthOfColumnAt placements)
        // 不足200を、余裕100(b)と余裕300(c)が合計余裕400に対する比率で分担：bは50、cは150削る。
        Assert.Equal<int list>([ 500; 250; 150 ], widths)
        Assert.Equal(900, List.sum widths)
    | Error e -> Assert.True(false, sprintf "unexpected: %A" e)

[<Fact>]
let ``余裕列が全て制約無しなら、不足分を均等な自然な幅から等しい比率で削る`` () =
    let area: PixelRect = { X = 0; Y = 0; Width = 900; Height = 600 }
    let a: LayoutWindow = { Id = WindowId 1L; MinWidthDip = mkMinWidthDip 500 } // 自然な300に対し200不足
    let b: LayoutWindow = { Id = WindowId 2L; MinWidthDip = noMinWidth } // 余裕300
    let c: LayoutWindow = { Id = WindowId 3L; MinWidthDip = noMinWidth } // 余裕300
    let ctx = mkContext area [ a; b; c ]
    match Columns.calculate (ColumnCount 3) ctx with
    | Ok placements ->
        let widths = columnXsOf placements |> List.map (widthOfColumnAt placements)
        // 余裕が同じ(300, 300)なので不足200を均等に(100, 100)分担する。
        Assert.Equal<int list>([ 500; 200; 200 ], widths)
    | Error e -> Assert.True(false, sprintf "unexpected: %A" e)

[<Fact>]
let ``全列のMinWidthの合計がWorkArea幅を超える場合は、これ以上は強制せず自然な等分のまま(打ち止め)`` () =
    let area: PixelRect = { X = 0; Y = 0; Width = 900; Height = 600 }
    let a: LayoutWindow = { Id = WindowId 1L; MinWidthDip = mkMinWidthDip 400 }
    let b: LayoutWindow = { Id = WindowId 2L; MinWidthDip = mkMinWidthDip 400 }
    let c: LayoutWindow = { Id = WindowId 3L; MinWidthDip = mkMinWidthDip 400 } // 合計1200 > 900
    let ctx = mkContext area [ a; b; c ]
    match Columns.calculate (ColumnCount 3) ctx with
    | Ok placements ->
        let widths = columnXsOf placements |> List.map (widthOfColumnAt placements)
        Assert.Equal<int list>([ 300; 300; 300 ], widths) // 自然な3等分のまま
    | Error e -> Assert.True(false, sprintf "unexpected: %A" e)

[<Fact>]
let ``MinWidthDipは画面のDPIで物理ピクセルへ変換される(toDipの逆、px = dip * dpi / 96)`` () =
    let area: PixelRect = { X = 0; Y = 0; Width = 900; Height = 600 }
    let a: LayoutWindow = { Id = WindowId 1L; MinWidthDip = mkMinWidthDip 250 } // 192DPIでは500px相当
    let b: LayoutWindow = { Id = WindowId 2L; MinWidthDip = noMinWidth }
    let c: LayoutWindow = { Id = WindowId 3L; MinWidthDip = noMinWidth }
    let ctx = { mkContext area [ a; b; c ] with Dpi = 192 }
    match Columns.calculate (ColumnCount 3) ctx with
    | Ok placements ->
        let widths = columnXsOf placements |> List.map (widthOfColumnAt placements)
        Assert.Equal<int list>([ 500; 200; 200 ], widths)
    | Error e -> Assert.True(false, sprintf "unexpected: %A" e)

/// MinWidthの値に関わらず、全列の幅の合計はWorkArea幅のまま変わらない
/// （MasterStackTests.fsの同種の性質テストと同じ不変条件、PlanValidatorの前提）。
[<Property>]
let ``MinWidthで調整しても、列幅の合計はWorkArea幅のまま変わらない``
    (x: int)
    (y: int)
    (w: PositiveInt)
    (h: PositiveInt)
    (minsRaw: int list)
    =
    let area = mkWorkArea x y w h
    let mins = minsRaw |> List.truncate 6 |> List.map (fun m -> abs m % 5000)
    let cols = max 1 (List.length mins)
    let tiled = mins |> List.mapi (fun i m -> { Id = WindowId (int64 i); MinWidthDip = mkMinWidthDip m })
    let ctx = mkContext area tiled
    match Columns.calculate (ColumnCount cols) ctx with
    | Ok placements ->
        let totalWidth = columnXsOf placements |> List.sumBy (widthOfColumnAt placements)
        tiled.IsEmpty || totalWidth = area.Width
    | Error _ -> tiled.IsEmpty
