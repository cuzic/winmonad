/// ADR-019実装順9：Monocle.fs。architecture.md §5.6/§6.1。
///
/// dwm/bspwm系のmonocleレイアウト：全てのTiled窓をWorkArea全面へ意図的に重ねて配置する
/// （§6.1「重なりを一律に禁止しない：Monocleは意図的に同位置へ重ねる」）。どの窓が最前面に見えるかは
/// Z順序（フォアグラウンド、`RequestForeground`Effect）が決めることであり、`Placement`は矩形しか
/// 運ばないのでLayoutFunctionの関心事ではない——`ctx.Focused`を読まなくても正しい（全窓が同じ
/// 矩形を受け取るため、どれが「focus」かでPlacementは変わらない）。
module Wm.Layouts.Monocle

open Wm.Contracts.Identifiers
open Wm.Contracts.SharedVocabulary
open Wm.Layouts.LayoutRegistry

/// §6.2のMinWidth対応（v2.78）：MasterStackとは違い、Monocleは全窓へ常に`ctx.WorkArea`全体を
/// そのまま渡すため、`LayoutWindow.MinWidthDip`を実際に読んで判定する必要が無い——モニター自体が
/// 窓のMinWidthより狭いという実機ではまず起きない極端な場合を除き、WorkArea全体の幅は常にどの
/// MinWidthも満たす。意図的に`MinWidthDip`を読まない。
let calculate: LayoutFunction =
    fun args ctx ->
        match args with
        | NoArgs -> Ok (ctx.TiledWindows |> List.map (fun w -> { Window = w.Id; Target = ctx.WorkArea }))
        | MasterRatio _ -> Error (InvalidInput "MonocleはMasterRatioを受け付けない")
        | ColumnCount _ -> Error (InvalidInput "MonocleはColumnCountを受け付けない")

let implementation (digest: string) : LayoutImplementation =
    { Id = LayoutId "monocle"; ApiVersion = 1; ImplementationDigest = digest; Calculate = calculate }
