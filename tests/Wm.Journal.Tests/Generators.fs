/// round2レビューT-5：RoundTripTestsが固定の代表値(例: `MasterRatio 0.5m`、`ErrorSpace`は常に
/// `Win32LastError`)しか見ておらず、「`Management`/`LayoutArgs`/`ErrorSpace`/`CoreCommand`の
/// 全ケースを、FsCheckが生成する任意の値で往復できる」ことは検証できていなかった。各型の全ケースを
/// 覆うGenを書き、`[<Properties(Arbitrary=...)>]`で登録する。
module Wm.Journal.Tests.Generators

open FsCheck
open Wm.Contracts.Identifiers
open Wm.Contracts.ErrorTypes
open Wm.Contracts.SharedVocabulary
open Wm.Contracts.CoreCommand

type JournalGenerators =
    static member Management () : Arbitrary<Management> =
        Gen.elements [ Tiled; Floating; Excluded ] |> Arb.fromGen

    static member ErrorSpace () : Arbitrary<ErrorSpace> =
        Gen.elements [ Win32LastError; HResult; BoolFalseNoCode ] |> Arb.fromGen

    static member LayoutArgs () : Arbitrary<LayoutArgs> =
        gen {
            let! ratio = Arb.generate<decimal>
            let! cols = Arb.generate<int>
            return! Gen.elements [ NoArgs; MasterRatio ratio; ColumnCount cols ]
        }
        |> Arb.fromGen

    static member LayoutRef () : Arbitrary<LayoutRef> =
        gen {
            let! idSuffix = Arb.generate<NonEmptyString>
            let! apiVersion = Arb.generate<int>
            let! args = Arb.generate<LayoutArgs>
            let (NonEmptyString idStr) = idSuffix
            return { Id = LayoutId idStr; ApiVersion = apiVersion; Args = args }
        }
        |> Arb.fromGen

    /// `CoreCommand`の全13ケースを一様に選ぶ(Opusレビュー5-4で全ケース対応に拡張したので、
    /// 生成側も全ケースを対象にして符号化の取りこぼしを検出できるようにする)。
    static member CoreCommand () : Arbitrary<CoreCommand> =
        gen {
            let! ws = Arb.generate<int>
            let! mid = Arb.generate<int64>
            let! w = Arb.generate<int64>
            let! layoutRef = Arb.generate<LayoutRef>
            let! management = Arb.generate<Management>
            return!
                Gen.elements
                    [ FocusNext
                      FocusPrevious
                      FocusMostRecent
                      SwapWithMaster
                      PromoteToMaster
                      CloseFocused
                      SwitchWorkspace (WorkspaceId ws)
                      MoveFocusedToWorkspace (WorkspaceId ws)
                      FocusMonitor (MonitorId mid)
                      ChangeLayout layoutRef
                      ToggleFloating (WindowId w)
                      SetManagement (WindowId w, management)
                      ClearUserOverride (WindowId w) ]
        }
        |> Arb.fromGen
