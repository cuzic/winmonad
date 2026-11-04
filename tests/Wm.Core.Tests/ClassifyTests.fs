/// architecture.md §15.1 #14「`classify`は決定的である：同じ`NativeCallRecord`からは常に同じ
/// `EffectFailure`が得られ、引数は`NativeCallRecord`だけで窓の状態・信念を読まない（v2.4追加、
/// ADR-014対応。Replay時の再計算に必須。v2.6で「窓の状態を読まない」を明記、R3-7：キーは
/// `(Api, Space, Code)`だが一部エントリは`ElapsedUs`も読むので、「3つ組だけの関数」ではない）」。
///
/// `Wm.Contracts.ErrorTypes.classify`はCoreの8.1対応表そのものであり、`EffectCallReturned`の
/// 結果を4種類のEffectFailureへ振り分ける唯一の場所だが、これまで直接のユニットテストが一度も
/// 無かった（`VirtualWorldScenarioTests.fs`が特定のコードを使うシナリオを経由して間接的に
/// 確認しているだけで、対応表そのものをピン留めするテストが無かった）。「窓の状態・信念を読まない」
/// 側は型（`NativeCallRecord -> EffectFailure`、他の引数が無い）で構造的に保証されるので検査しない。
/// 「一部エントリだけがElapsedUsも読む」側（R3-7）は、具体的なfalse-positiveの危険が実在する
/// （`ElapsedUs`を読むべきでないコードで誤って読んでしまう取りこぼし）ので、FsCheckで検査する。
module Wm.Core.Tests.ClassifyTests

open Xunit
open FsCheck
open FsCheck.Xunit
open Wm.Contracts.ErrorTypes

let private rec' (api: string) (space: ErrorSpace) (code: int) (elapsedUs: int64) : NativeCallRecord =
    { Api = api; Space = space; Code = code; ElapsedUs = elapsedUs }

[<Theory>]
[<InlineData(0L, "TargetHung")>] // 判定済み（ElapsedUsが0に近い）
[<InlineData(1L, "Transient")>] // 判定前（ElapsedUsがtimeoutMsに近い）
let ``SendMessageTimeout+1460はElapsedUsでTargetHung/Transientを分ける(8.1対応表)`` (elapsedUs: int64) (expected: string) =
    let result = classify (rec' "SendMessageTimeout" Win32LastError 1460 elapsedUs)
    match expected, result with
    | "TargetHung", TargetHung -> ()
    | "Transient", Transient -> ()
    | _, actual -> Assert.True(false, sprintf "expected %s but got %A" expected actual)

[<Fact>]
let ``ERROR_INVALID_WINDOW_HANDLE(1400)はApi/Spaceを問わずTargetGoneになる(8.1対応表)`` () =
    Assert.Equal(TargetGone, classify (rec' "SetWindowPos" Win32LastError 1400 0L))
    Assert.Equal(TargetGone, classify (rec' "ShowWindowAsync" Win32LastError 1400 999L))

[<Fact>]
let ``ERROR_ACCESS_DENIED(5)はApi/Spaceを問わずDenied OtherDenialになる(8.1対応表)`` () =
    Assert.Equal(Denied OtherDenial, classify (rec' "SetWindowPos" Win32LastError 5 0L))
    Assert.Equal(Denied OtherDenial, classify (rec' "SetForegroundWindow" Win32LastError 5 999L))

/// v2.94（Phase B ラウンド3）：`SetForegroundWindow`は`SetLastError`を立てないため失敗時は
/// `BoolFalseNoCode`/`Code=0`で運ばれる（`callBoolNoCode`）。Apiをワイルドカードにすると
/// `ShowWindowAsync`（同じ`BoolFalseNoCode`/`Code=0`の形だが「前は可視だったか」を返すだけで
/// 失敗シグナルではない）まで誤って`Denied`に分類してしまうため、`SetForegroundWindow`だけに
/// 絞っていることを確認する。
[<Fact>]
let ``SetForegroundWindow+BoolFalseNoCode+0はDenied ForegroundDenialになるが、ShowWindowAsyncは対象外(8.1対応表、v2.94)`` () =
    Assert.Equal(Denied ForegroundDenied, classify (rec' "SetForegroundWindow" BoolFalseNoCode 0 0L))
    Assert.Equal(Unclassified, classify (rec' "ShowWindowAsync" BoolFalseNoCode 0 0L))

[<Fact>]
let ``対応表に無い組合せはUnclassifiedになる(8.1対応表)`` () =
    Assert.Equal(Unclassified, classify (rec' "SomeOtherApi" HResult 42 0L))

[<Property>]
let ``#14: SendMessageTimeout+1460以外ではElapsedUsの値はEffectFailureへ影響しない`` (api: string) (space: ErrorSpace) (code: int) (elapsedUs1: int64) (elapsedUs2: int64) =
    (api <> "SendMessageTimeout" || code <> 1460)
    ==> (classify (rec' api space code elapsedUs1) = classify (rec' api space code elapsedUs2))

[<Property>]
let ``#14: classifyは同じNativeCallRecordから常に同じEffectFailureを返す`` (record: NativeCallRecord) =
    classify record = classify record
