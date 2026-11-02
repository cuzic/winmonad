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
                let masterWidth = int (decimal area.Width * ratio)
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
