/// ADR-019実装順9（Columns.fs、v2.67で追加）。architecture.md §5.3/§5.6/§6.1。
///
/// §5.3の設定サンプルが`LayoutId "columns"`/`ColumnCount 3`を使う例を示していた（`TiledCountAtLeast 4`
/// の規則）が、対応するLayoutFunctionが一度も実装されていなかった——MasterStack.fs/Monocle.fsの両方が
/// `ColumnCount`を明示的に`InvalidInput`で拒否するスタブ分岐を持っていたのはこの将来実装を見越した
/// もので、`ColumnCount of int`自体も`Columns`以外に使う消費者が無い。
///
/// `TiledWindows`（zipperの順、§5.6）を`ColumnCount`本の縦列へ順番に振り分け（round-robin：窓`i`は
/// 列`i mod n`へ）、各列の内部はMasterStackのstack側と同じ規則（`splitVertically`：縦に等分、余り
/// ピクセルは最後の窓へ）で縦に等分して並べる。全列が対等（masterの概念が無い）なので`Focused`は
/// 読まない（Monocleと同じ理由）。窓数より列数が多い場合は、空列を作らず実際に窓がある列数まで
/// 減らす（`effectiveColumnCount`）——空のPlacementを持つ列を作る意味が無く、PlanValidatorの
/// 「全てのTiled×Aliveな窓がPlanに現れる」不変条件とも無関係のため。
module Wm.Layouts.Columns

open Wm.Contracts.Identifiers
open Wm.Contracts.SharedVocabulary
open Wm.Layouts.LayoutRegistry

let private defaultColumnCount = 2

let private columnCountOf (args: LayoutArgs) : Result<int, LayoutError> =
    match args with
    | NoArgs -> Ok defaultColumnCount
    | ColumnCount c when c > 0 -> Ok c
    | ColumnCount c -> Error (InvalidInput (sprintf "ColumnCountは正の整数でなければならない: %d" c))
    | MasterRatio _ -> Error (InvalidInput "ColumnsはMasterRatioを受け付けない")

/// `n`窓を1本の縦列`columnArea`に縦等分する（MasterStack.fsの`splitVertically`と同じ「余りは
/// 最後の窓へ」規則。全列が対等でmaster/stackの区別が無いため、縦方向の等分だけがここの役割）。
let private splitVertically (columnArea: PixelRect) (n: int) : PixelRect list =
    if n <= 0 then
        []
    else
        let baseHeight = columnArea.Height / n
        let remainder = columnArea.Height - baseHeight * n
        [ for i in 0 .. n - 1 ->
            let h = if i = n - 1 then baseHeight + remainder else baseHeight
            { X = columnArea.X
              Y = columnArea.Y + i * baseHeight
              Width = columnArea.Width
              Height = h } ]

/// WorkAreaを`n`本の縦列に横方向へ等分する（余りピクセルは最後の列へ、`splitVertically`と同じ規則）。
let private splitHorizontally (area: PixelRect) (n: int) : PixelRect list =
    let baseWidth = area.Width / n
    let remainder = area.Width - baseWidth * n
    [ for i in 0 .. n - 1 ->
        let w = if i = n - 1 then baseWidth + remainder else baseWidth
        { X = area.X + i * baseWidth
          Y = area.Y
          Width = w
          Height = area.Height } ]

let calculate: LayoutFunction =
    fun args ctx ->
        match columnCountOf args with
        | Error e -> Error e
        | Ok n ->
            match ctx.TiledWindows with
            | [] -> Ok []
            | windows ->
                let effectiveColumnCount = min n (List.length windows)
                let columnAreas = splitHorizontally ctx.WorkArea effectiveColumnCount
                let columns =
                    windows
                    |> List.indexed
                    |> List.groupBy (fun (i, _) -> i % effectiveColumnCount)
                    |> List.sortBy fst
                    |> List.map (fun (_, items) -> items |> List.map snd)
                Ok (
                    List.zip columnAreas columns
                    |> List.collect (fun (area, colWindows) ->
                        let rects = splitVertically area (List.length colWindows)
                        List.map2 (fun (w: LayoutWindow) r -> { Window = w.Id; Target = r }) colWindows rects)
                )

let implementation (digest: string) : LayoutImplementation =
    { Id = LayoutId "columns"; ApiVersion = 1; ImplementationDigest = digest; Calculate = calculate }
