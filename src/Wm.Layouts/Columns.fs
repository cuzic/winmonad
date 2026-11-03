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

/// WorkAreaを`n`本の縦列に横方向へ等分する幅だけを求める（余りピクセルは最後の列へ、
/// `splitVertically`と同じ規則）。矩形化（Xオフセットの確定）は`adjustForMinWidth`適用後に行う。
let private naturalWidthsOf (totalWidth: int) (n: int) : int list =
    let baseWidth = totalWidth / n
    let remainder = totalWidth - baseWidth * n
    [ for i in 0 .. n - 1 -> if i = n - 1 then baseWidth + remainder else baseWidth ]

/// §6.2のMinWidth対応（v2.79、プロジェクトオーナー確認済みのv2.78「兄弟窓を縮めて調整する」方針を
/// N列へ一般化）：`LayoutWindow.MinWidthDip`をこの画面のDPIで物理ピクセルへ戻す（`Placement.
/// reviseMinWidth`の`toDip = px * 96 / dpi`の逆、`px = dip * dpi / 96`）。信念が無い窓は制約無し（0）。
/// 列の最小幅は構成窓のうち最大値（列内の全窓が同じ列幅を共有するため、MasterStack.fsの
/// `minWidthPxOf`と同じ考え方）。
let private minWidthPxOf (dpi: int) (w: LayoutWindow) : int =
    w.MinWidthDip |> Option.map (fun b -> b.Value * dpi / 96) |> Option.defaultValue 0

/// N列の自然な等分幅を、各列のMinWidthを尊重できるよう再配分する（v2.79）。v2.78のMasterStack
/// （master/stackの2者だけ）では「不足している側に、もう一方が丸ごと充てる」で済んだが、列が
/// 3本以上あると「不足分をどの余裕列からどれだけ削るか」という2者には無かった新しい選択が生じる。
/// ここでは「各余裕列（自然な幅がMinWidthを上回る列）の余裕（natural - min）の大きさに比例して
/// 削る」規則を採る——この規則はN=2のときv2.78の「もう一方が全額負担する」と完全に一致する
/// （余裕列が1本しか無いので、その1本が比例配分の100%を負担することになる）ため、新しい方針を
/// 持ち込むのではなく、オーナーが既に選んだ方針の自然な拡張として選んだ。全列のMinWidthの合計が
/// WorkArea幅を超える場合はどの列からも削りきれないので、v2.78と同じく「打ち止め」——自然な等分の
/// ままにする（削る側を決める根拠が無い状態で無理に押し込むと、むしろ任意の判断を混入させてしまう、
/// という理由もv2.78から変わらない）。各列のMinWidthの合計がWorkArea幅以下である限り、必ず
/// 「余裕列の余裕の合計 ≧ 不足列の不足の合計」が成り立つ（自然な幅の合計＝WorkArea幅であることから
/// 導ける）ので、比例配分で不足側を必ず満たせる——`totalSlack = 0`なのに`totalDeficit > 0`という
/// 割り算不能な状態には構造的に陥らない。
let private adjustForMinWidth (dpi: int) (naturalWidths: int list) (columns: LayoutWindow list list) : int list =
    let minPxByColumn = columns |> List.map (fun col -> col |> List.map (minWidthPxOf dpi) |> List.fold max 0)
    let totalWidth = List.sum naturalWidths
    let totalMinPx = List.sum minPxByColumn
    if totalMinPx > totalWidth then
        naturalWidths
    else
        let slacks = List.map2 (fun nat minPx -> nat - minPx) naturalWidths minPxByColumn
        let totalDeficit = slacks |> List.filter (fun s -> s < 0) |> List.sumBy (fun s -> -s)
        if totalDeficit = 0 then
            naturalWidths
        else
            let totalSlack = slacks |> List.filter (fun s -> s > 0) |> List.sum
            let adjusted =
                List.map2
                    (fun nat slack ->
                        if slack < 0 then
                            nat - slack // 不足列：自然な幅をMinWidthまで引き上げる(nat + (-slack) = minPx)
                        elif slack > 0 then
                            nat - int (float slack * float totalDeficit / float totalSlack) // 余裕に比例して削る
                        else
                            nat)
                    naturalWidths
                    slacks
            // 浮動小数の丸め誤差で合計がWorkArea幅と数px ずれることがあるため、最後の列で吸収する
            // （`naturalWidthsOf`/`splitVertically`と同じ「余りは最後へ」規則）。
            let diff = totalWidth - List.sum adjusted
            adjusted |> List.mapi (fun i w -> if i = List.length adjusted - 1 then w + diff else w)

let calculate: LayoutFunction =
    fun args ctx ->
        match columnCountOf args with
        | Error e -> Error e
        | Ok n ->
            match ctx.TiledWindows with
            | [] -> Ok []
            | windows ->
                let effectiveColumnCount = min n (List.length windows)
                let columns =
                    windows
                    |> List.indexed
                    |> List.groupBy (fun (i, _) -> i % effectiveColumnCount)
                    |> List.sortBy fst
                    |> List.map (fun (_, items) -> items |> List.map snd)
                let naturalWidths = naturalWidthsOf ctx.WorkArea.Width effectiveColumnCount
                let widths = adjustForMinWidth ctx.Dpi naturalWidths columns
                let columnAreas =
                    widths
                    |> List.fold
                        (fun (areas, x) w ->
                            areas @ [ { X = x; Y = ctx.WorkArea.Y; Width = w; Height = ctx.WorkArea.Height } ], x + w)
                        ([], ctx.WorkArea.X)
                    |> fst
                Ok (
                    List.zip columnAreas columns
                    |> List.collect (fun (area, colWindows) ->
                        let rects = splitVertically area (List.length colWindows)
                        List.map2 (fun (w: LayoutWindow) r -> { Window = w.Id; Target = r }) colWindows rects)
                )

let implementation (digest: string) : LayoutImplementation =
    { Id = LayoutId "columns"; ApiVersion = 1; ImplementationDigest = digest; Calculate = calculate }
