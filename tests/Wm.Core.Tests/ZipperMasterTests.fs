/// architecture.md §4.4「コマンドの意味」の`PromoteToMaster`/`SwapWithMaster`（xmonadの
/// `shiftMaster`/`swapMaster`と同じ意味論）を、Zipper単体で検査する。
module Wm.Core.Tests.ZipperMasterTests

open Xunit
open Wm.Core.Workspaces

[<Fact>]
let ``promoteToMasterはFocusを先頭へ移し、他の要素の相対順序を変えない`` () =
    let z: Zipper<char> = { Before = [ 'b'; 'a' ]; Focus = 'f'; After = [ 'c'; 'd' ] }
    Assert.Equal<char list>([ 'a'; 'b'; 'f'; 'c'; 'd' ], Zipper.toList z)
    let z' = Zipper.promoteToMaster z
    Assert.Equal<char list>([ 'f'; 'a'; 'b'; 'c'; 'd' ], Zipper.toList z')
    Assert.Equal('f', z'.Focus)

[<Fact>]
let ``promoteToMasterは既にmaster(Before=[])なら何もしない`` () =
    let z: Zipper<char> = { Before = []; Focus = 'f'; After = [ 'c'; 'd' ] }
    Assert.Equal(z, Zipper.promoteToMaster z)

[<Fact>]
let ``swapWithMasterはFocusと先頭(master)だけを入れ替え、他の要素の相対順序を変えない`` () =
    let z: Zipper<char> = { Before = [ 'b'; 'a' ]; Focus = 'f'; After = [ 'c'; 'd' ] }
    Assert.Equal<char list>([ 'a'; 'b'; 'f'; 'c'; 'd' ], Zipper.toList z)
    let z' = Zipper.swapWithMaster z
    Assert.Equal<char list>([ 'f'; 'b'; 'a'; 'c'; 'd' ], Zipper.toList z')
    Assert.Equal('f', z'.Focus)

[<Fact>]
let ``swapWithMasterは既にmaster(Before=[])なら何もしない`` () =
    let z: Zipper<char> = { Before = []; Focus = 'f'; After = [ 'c'; 'd' ] }
    Assert.Equal(z, Zipper.swapWithMaster z)
