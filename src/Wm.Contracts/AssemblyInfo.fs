module Wm.Contracts.AssemblyInfo

// GatedEffect（GatedEffect.fs）のコンストラクタをWm.Coreだけに構築させるための許可。
// architecture.md §6.2「GatedEffectはWm.Contractsに型を置き、コンストラクタをinternalにして
// InternalsVisibleTo("Wm.Core")でWm.Coreだけに構築を許す」(ADR-014、v2.4)。
open System.Runtime.CompilerServices

[<assembly: InternalsVisibleTo("Wm.Core")>]
// GatedEffect.gate自体もinternal化した（Opusレビュー2-9）。表駆動の実装を網羅的に単体テストするため、
// テストアセンブリにも同じ許可を与える（gateを呼べるのは「Coreの現在状態からGateInputを作る」経路に
// 限られるべきで、テストはその不変条件を直接検査する側なので例外として許す）。
[<assembly: InternalsVisibleTo("Wm.Core.Tests")>]
do ()
