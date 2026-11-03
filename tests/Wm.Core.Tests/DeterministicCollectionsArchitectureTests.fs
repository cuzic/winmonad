/// architecture.md §15.1 #11「Coreの判断がプロセス間で再現可能である（`string.GetHashCode`の
/// ランダム化に依存する`HashSet`/`Dictionary`の列挙順にCoreの判断が依存しない。v2.1追加、B-7対応）」。
///
/// F#の`Map`/`Set`は赤黒木であり比較演算子で全順序を持つため、.NET Coreが既定で有効にする文字列
/// ハッシュのプロセスごとのランダム化（`GetHashCode`のソルト）の影響を受けない。これに対し
/// `System.Collections.Generic.Dictionary`/`HashSet`はハッシュバケットの内部順で列挙するため、同じ
/// 要素を同じ順で挿入しても別プロセスでは列挙順が変わり得る。ADR-004/#4（Core状態とEffect列は同じ
/// 入力で一致する）・#10（Replay）・#11はこれに依存するので、Core/Contracts/Policy/Layoutsの
/// 状態を表す型が内部でこの2つのBCLコレクションを使っていないことを、個々のドメインファイルを
/// 読んで確認する代わりに、アセンブリ全体をリフレクションで検査する1本のテストで構造的に保証する
/// （手で見落とす心配を無くす、R2-5/R2-20と同じ「検査可能な単一の場所に集める」考え方）。
///
/// 検査範囲は各アセンブリの`GetTypes()`が返す全ての型（F#のDUの各ケースが生成する入れ子のクラスも
/// 含む）の、宣言されているフィールドの型（ジェネリック型引数も1段分展開して検査する）。BCLの
/// 型そのものの内部実装までは追わない（Coreが書いた型の宣言だけを検査対象にする）。
module Wm.Core.Tests.DeterministicCollectionsArchitectureTests

open System
open System.Reflection
open Xunit

let private assembliesToCheck : Assembly list =
    [ typeof<Wm.Core.WmState.WmState>.Assembly
      typeof<Wm.Contracts.Identifiers.WindowId>.Assembly
      typeof<Wm.Policy.PolicyTypes.PolicyIR>.Assembly
      typeof<Wm.Layouts.LayoutRegistry.LayoutContext>.Assembly ]
    |> List.distinct

let private bannedGenericDefs : Type list =
    [ typedefof<System.Collections.Generic.Dictionary<_, _>>
      typedefof<System.Collections.Generic.HashSet<_>> ]

let private bannedNonGeneric : Type list =
    [ typeof<System.Collections.Hashtable> ]

/// `t`自身、またはジェネリック型引数のいずれかが禁止コレクションなら`Some`で理由を返す。
/// 1段分だけ型引数を見る（`Map<WindowId, Dictionary<int,int>>`のような入れ子も検出するため）。
let private offendingReason (t: Type) : string option =
    let isBanned (x: Type) =
        (x.IsGenericType && bannedGenericDefs |> List.contains (x.GetGenericTypeDefinition()))
        || bannedNonGeneric |> List.contains x
    if isBanned t then
        Some (sprintf "%s" t.FullName)
    elif t.IsGenericType then
        t.GetGenericArguments()
        |> Array.tryPick (fun arg -> if isBanned arg then Some (sprintf "%s (型引数 %s)" t.FullName arg.FullName) else None)
    else
        None

[<Fact>]
let ``Core/Contracts/Policy/Layoutsのどの型のフィールドもDictionary/HashSet/Hashtableを使わない(§15.1 #11)`` () =
    let flags = BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.Instance ||| BindingFlags.Static ||| BindingFlags.DeclaredOnly
    let offenders =
        assembliesToCheck
        |> List.collect (fun asm -> asm.GetTypes() |> List.ofArray)
        |> List.collect (fun t ->
            t.GetFields(flags)
            |> List.ofArray
            |> List.choose (fun f ->
                offendingReason f.FieldType
                |> Option.map (fun reason -> sprintf "%s.%s : %s" t.FullName f.Name reason)))
    Assert.Equal<string list>([], offenders)
