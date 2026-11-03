/// `Wm.Contracts.ErrorTypes.executeEffectProtocol`（§7.1「事前確認→ASYNC呼出」、ADR-012のハング
/// 耐性手順）はWindows版・Simulation版が共有する純粋な手順だが、`grep`では`src`/`tests`どちらにも
/// 呼び出し・テストが一度も無かった（ドキュメンテーションコメントで言及されるだけ）。
/// `VirtualWorld.applyEffect`の`SetRect`はこの手順を経由せずに直接成功を返すため、現状は
/// 実際のReducer+VirtualWorldパイプラインを通してハング窓をシミュレートする経路が無い——
/// それ自体を`VirtualWorld`へ配線する（新しい`WindowBehavior`・仮想時計駆動のprobe）のは
/// 判断点が多い別ラウンドへ持ち越し、ここでは`executeEffectProtocol`自身の3分岐（`Responsive`/
/// `HungPending`/`HungConfirmed`）だけを直接検査する。
module Wm.Core.Tests.ExecuteEffectProtocolTests

open Xunit
open Wm.Contracts.Identifiers
open Wm.Contracts.ErrorTypes

let private w = WindowId 1L

[<Fact>]
let ``Responsiveならprobeを確認した上でdispatchを呼び、その結果をそのまま返す(Ok)`` () =
    let mutable dispatchCalled = false
    let dispatch () = dispatchCalled <- true; Ok ()
    let result = executeEffectProtocol (fun _ -> Responsive) dispatch w
    Assert.True(dispatchCalled)
    Assert.Equal(Ok (), result)

[<Fact>]
let ``Responsiveでdispatch自体が失敗したら、その失敗をそのまま返す(probeは原因を偽装しない)`` () =
    let failure: NativeCallRecord = { Api = "SetWindowPos"; Space = Win32LastError; Code = 5; ElapsedUs = 0L }
    let dispatch () = Error failure
    let result = executeEffectProtocol (fun _ -> Responsive) dispatch w
    Assert.Equal(Error failure, result)

[<Fact>]
let ``HungPendingでもdispatchを呼ぶ(判定未確定の間は呼出自体を止めない)`` () =
    let mutable dispatchCalled = false
    let dispatch () = dispatchCalled <- true; Ok ()
    let result = executeEffectProtocol (fun _ -> HungPending 300) dispatch w
    Assert.True(dispatchCalled)
    Assert.Equal(Ok (), result)

[<Fact>]
let ``HungConfirmedならdispatchを一切呼ばず、SendMessageTimeout(1460,ElapsedUs=0)を即座に返す`` () =
    let mutable dispatchCalled = false
    let dispatch () = dispatchCalled <- true; Ok ()
    let result = executeEffectProtocol (fun _ -> HungConfirmed) dispatch w
    Assert.False(dispatchCalled)
    match result with
    | Error record ->
        Assert.Equal("SendMessageTimeout", record.Api)
        Assert.Equal(Win32LastError, record.Space)
        Assert.Equal(1460, record.Code)
        Assert.Equal(0L, record.ElapsedUs)
    | Ok () -> Assert.True(false, "expected Error")

[<Fact>]
let ``HungConfirmedが返すNativeCallRecordはclassifyでTargetHungになる(§8.1対応表との整合)`` () =
    // executeEffectProtocolが事前確認で固めたハング判定が、7.1「結果の分類」の8.1対応表と
    // 食い違わないことを確認する——ElapsedUs=0Lは「判定済み」側のTargetHung分岐(ClassifyTests.fs参照)。
    let dispatch () = Ok ()
    let result = executeEffectProtocol (fun _ -> HungConfirmed) dispatch w
    match result with
    | Error record -> Assert.Equal(TargetHung, classify record)
    | Ok () -> Assert.True(false, "expected Error")
