/// テスト共通のヘルパー：妥当な(非退化)なWorkAreaと重複の無いTiledWindows列を作る。
module Wm.Layouts.Tests.TestHelpers

open Wm.Contracts.Identifiers
open Wm.Layouts.LayoutRegistry

/// FsCheckの`PositiveInt`から、現実的な範囲(50〜4000px)のWorkAreaを作る。
let mkWorkArea (x: int) (y: int) (w: FsCheck.PositiveInt) (h: FsCheck.PositiveInt) : PixelRect =
    let (FsCheck.PositiveInt wRaw) = w
    let (FsCheck.PositiveInt hRaw) = h
    { X = x; Y = y; Width = 50 + (wRaw % 3950); Height = 50 + (hRaw % 3950) }

/// 0〜count個の、重複しないWindowIdを持つTiledWindowsを作る(MinWidthDipはv2.78以降MasterStackが読むが、
/// 既存のFsCheck性質テストは制約の無い窓だけを対象にするためNoneのままにする——MinWidth対応自体の
/// テストはMasterStackTests.fsが別途、明示的にMinWidthDipを設定して検査する)。
let mkTiledWindows (count: int) : LayoutWindow list =
    [ for i in 0 .. (abs count % 9) - 1 -> { Id = WindowId (int64 i); MinWidthDip = None } ]

let mkContext (workArea: PixelRect) (tiled: LayoutWindow list) : LayoutContext =
    { WorkArea = workArea; Dpi = 96; TiledWindows = tiled; Focused = None }
