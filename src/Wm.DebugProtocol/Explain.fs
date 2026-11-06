/// Wm.DebugProtocol：Phase CのLive REPL（architecture.md §10.2「Inspect：Explain」、v2.147）が
/// プロセス境界を越えて運ぶExplainの型。`Snapshot.fs`/`Policy.fs`と同じ方針（§17の依存方向：
/// `Wm.Core`/`Wm.Layouts`の内部型をそのまま晒さない）で、`Wm.Core.DecisionTrace.DecisionTrace`
/// （および`Wm.Layouts`の`LayoutError`/`ValidationError`）の写しを専用に定義する。
///
/// **既知の限界（ユーザー向けに明記する必要がある、v2.147実装時に判明）**：`DecisionTrace`自身は
/// ウィンドウIDを運ばないケースが多い（`InconsistencySuspected`/`LayoutNotRegistered`/
/// `LayoutInvocationFailed`は対象ワークスペース/Layout単位、`FrameInsetApplied`は`RequestId`単位）。
/// `PlanRejected`の`ValidationError`だけは全ケースが`WindowId`を運ぶため、真に窓単位で絞り込める。
/// このスライスでは窓単位のフィルタリングは行わず、直近の`DecisionTrace`全件をそのまま返す
/// （`ExplainDto.RecentTrace`）——人間がInspectで見て自分で関連性を判断する前提。真に窓単位の
/// Explainを実現するには、Core側で`DecisionTrace`にWindowIdを持たせる設計変更か、§18の
/// Replayベースの再構成が必要で、どちらもこのスライスの範囲外。
module Wm.DebugProtocol.Explain

open Wm.Contracts.Identifiers
open Wm.Contracts.SharedVocabulary

/// `Wm.Layouts.LayoutRegistry.LayoutError`の写し。
type LayoutErrorDto =
    | InvalidInputDto of string
    | UnsatisfiableDto of string
    | LayoutCrashedDto of exnType: string

/// `Wm.Layouts.PlanValidator.ValidationError`の写し。全ケースが`WindowId`を運ぶ。
type ValidationErrorDto =
    | UnmanagedOrDeadWindowDto of WindowId
    | DuplicateInPlanDto of WindowId
    | NonPositiveSizeDto of WindowId * width: int * height: int
    | ArithmeticOverflowDto of WindowId
    | OutsideWorkAreaDto of WindowId * PixelRect
    | ExcludedTargetDto of WindowId
    | MissingPlacementDto of WindowId

/// `Wm.Core.DecisionTrace.DecisionTrace`の写し。
type DecisionTraceDto =
    | InconsistencySuspectedDto of domain: string * detail: string
    | LayoutNotRegisteredDto of layoutId: LayoutId * apiVersion: int
    | LayoutInvocationFailedDto of layoutId: LayoutId * error: LayoutErrorDto
    | PlanRejectedDto of layoutId: LayoutId * errors: ValidationErrorDto list
    | FrameInsetAppliedDto of requestId: RequestId * desiredVisible: PixelRect * dispatchedNative: PixelRect

/// 対象窓の現在の分類（`WindowSnapshotDto`と同じ情報）＋直近の`DecisionTrace`（上記の限界の通り、
/// 窓単位には絞られていない）。
type ExplainDto = {
    Window: WindowId
    Management: Management option
    MatchedRule: PolicyRuleId option
    RecentTrace: DecisionTraceDto list
}
