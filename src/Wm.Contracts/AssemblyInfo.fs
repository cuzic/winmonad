module Wm.Contracts.AssemblyInfo

// GatedEffect（GatedEffect.fs）のコンストラクタをWm.Coreだけに構築させるための許可。
// architecture.md §6.2「GatedEffectはWm.Contractsに型を置き、コンストラクタをinternalにして
// InternalsVisibleTo("Wm.Core")でWm.Coreだけに構築を許す」(ADR-014、v2.4)。
open System.Runtime.CompilerServices

[<assembly: InternalsVisibleTo("Wm.Core")>]
do ()
