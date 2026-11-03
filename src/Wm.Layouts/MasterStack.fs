/// ADR-019実装順9：MasterStack.fs。architecture.md §5.6/§6.1。
///
/// i3/dwm系のmaster-stackレイアウト：`TiledWindows`の先頭（zipperの順、§5.6）を master として
/// WorkAreaの左`MasterRatio`の幅いっぱいに置き、残りを右側に縦に等分して stack する。
/// masterの選択はCore（Workspacesのzipperの順序）の責務であり、LayoutFunctionは順序だけを読む
/// （`Focused`は使わない——「どれをmasterにするか」はPromoteToMaster/SwapWithMaster等の
/// CoreCommandでzipperの順序そのものを変えることで表現する、Coreの責務）。
module Wm.Layouts.MasterStack

open Wm.Contracts.Identifiers
open Wm.Contracts.SharedVocabulary
open Wm.Layouts.LayoutRegistry

let private defaultRatio = 0.5m

let private ratioOf (args: LayoutArgs) : Result<decimal, LayoutError> =
    match args with
    | NoArgs -> Ok defaultRatio
    | MasterRatio r when r > 0.0m && r < 1.0m -> Ok r
    | MasterRatio r -> Error (InvalidInput (sprintf "MasterRatioは(0,1)の範囲でなければならない: %M" r))
    | ColumnCount _ -> Error (InvalidInput "MasterStackはColumnCountを受け付けない")

/// §6.2「ソフト制約・劣化」のMinWidth対応（v2.78）：`LayoutWindow.MinWidthDip`をこの画面のDPIで
/// 物理ピクセルへ戻す（`Placement.reviseMinWidth`が記録する際の正規化`toDip = px * 96 / dpi`の逆、
/// `px = dip * dpi / 96`）。信念が無い窓は制約無し（0）として扱う。
let private minWidthPxOf (dpi: int) (w: LayoutWindow) : int =
    w.MinWidthDip |> Option.map (fun b -> b.Value * dpi / 96) |> Option.defaultValue 0

/// stack側の`n`窓をWorkAreaの右側`stackArea`に縦に等分する。余りのピクセルは隙間を残さないため
/// 最後の窓へ割り当てる（ギャップの厳密さはソフト制約、§6.1）。
let private splitVertically (stackArea: PixelRect) (n: int) : PixelRect list =
    if n <= 0 then
        []
    else
        let baseHeight = stackArea.Height / n
        let remainder = stackArea.Height - baseHeight * n
        [ for i in 0 .. n - 1 ->
            let h = if i = n - 1 then baseHeight + remainder else baseHeight
            { X = stackArea.X
              Y = stackArea.Y + i * baseHeight
              Width = stackArea.Width
              Height = h } ]

let calculate: LayoutFunction =
    fun args ctx ->
        match ratioOf args with
        | Error e -> Error e
        | Ok ratio ->
            match ctx.TiledWindows with
            | [] -> Ok []
            | [ only ] -> Ok [ { Window = only.Id; Target = ctx.WorkArea } ]
            | master :: stack ->
                let area = ctx.WorkArea
                let naturalMasterWidth = int (decimal area.Width * ratio)
                // §6.2のMinWidth対応（v2.78、プロジェクトオーナー確認済み：「兄弟窓を縮めて調整する」）：
                // masterとstack列はこの2者だけで幅を取り合う（stack内の各窓は同じstackArea.Widthを
                // 共有するので、stack列が必要とする最小幅は構成窓のうち最大のMinWidth）。どちらかの
                // 比率どおりの幅が自分のMinWidthを下回るなら、もう一方から幅を削って充てる——
                // 両方のMinWidthの合計がWorkArea全体を超える場合は満たしきれないので、それ以上は
                // 強制せず元の比率のまま（ソフト制約、打ち止め。削る側を決める根拠が無い状態で
                // 無理に押し込むと、むしろ任意の判断を混入させてしまう）。
                let masterMinPx = minWidthPxOf ctx.Dpi master
                let stackMinPx = stack |> List.map (minWidthPxOf ctx.Dpi) |> List.fold max 0
                let masterWidth =
                    if masterMinPx + stackMinPx > area.Width then
                        naturalMasterWidth
                    elif naturalMasterWidth < masterMinPx then
                        masterMinPx
                    elif area.Width - naturalMasterWidth < stackMinPx then
                        area.Width - stackMinPx
                    else
                        naturalMasterWidth
                let masterRect = { X = area.X; Y = area.Y; Width = masterWidth; Height = area.Height }
                let stackArea =
                    { X = area.X + masterWidth
                      Y = area.Y
                      Width = area.Width - masterWidth
                      Height = area.Height }
                let stackRects = splitVertically stackArea (List.length stack)
                Ok (
                    { Window = master.Id; Target = masterRect }
                    :: List.map2 (fun (w: LayoutWindow) r -> { Window = w.Id; Target = r }) stack stackRects
                )

let implementation (digest: string) : LayoutImplementation =
    { Id = LayoutId "master-stack"; ApiVersion = 1; ImplementationDigest = digest; Calculate = calculate }
