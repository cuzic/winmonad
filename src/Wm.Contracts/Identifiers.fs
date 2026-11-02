/// 決定6層1（ADR-019）：識別子・座標の土台。architecture.md §4.1/§4.2。
module Wm.Contracts.Identifiers

[<Struct>]
type WindowId = WindowId of int64

[<Struct>]
type MonitorId = MonitorId of int64

[<Struct>]
type RequestId = RequestId of int64

[<Struct>]
type IntentEpoch = IntentEpoch of int64

[<Struct>]
type PolicyRuleId = PolicyRuleId of string

[<Struct>]
type LayoutId = LayoutId of string

[<Struct>]
type EvidenceId = EvidenceId of string

[<Struct>]
type ScenarioId = ScenarioId of string

/// ApplyBatchの識別子。子Effectは自分のRequestIdを保つ（v2.4、ADR-014/1-5）。
[<Struct>]
type BatchId = BatchId of int64

/// Coreが扱う矩形は「可視フレーム矩形」（DWM拡張フレーム境界）。§4.2「座標系の契約」。
type PixelRect = {
    X: int
    Y: int
    Width: int
    Height: int
}

/// HMONITORではない安定ID（§4.1）。Shellが安定キー（EDID等）から割り当て、切断・再接続をまたいで維持する。
type MonitorSnapshot = {
    Id: MonitorId
    StableKey: string
    Bounds: PixelRect
    WorkArea: PixelRect
    Dpi: int
    IsPrimary: bool
}
