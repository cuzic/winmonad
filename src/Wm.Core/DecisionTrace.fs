/// ADR-019決定7：`StepResult.Trace`の要素。§4.4の`update`の戻り値、§7.1の「導出不整合の疑い」の記録先。
/// Journalに永続化しない揮発性の値で、ExplainはReplayで再構成する（§18）。
module Wm.Core.DecisionTrace

open Wm.Contracts.Identifiers
open Wm.Layouts.LayoutRegistry
open Wm.Layouts.PlanValidator

/// 「導出不整合の疑い」（§7.1「起こり得ない組合せに出会ったら、例外を投げずに入力を捨て、
/// DecisionTraceに記録する」）に加え、v2.108で`ValidationError`（Wm.Layouts）を運ぶケースを
/// 追加した——本ファイルの型直前のコメントが長らく「それらのドメインの実装が揃った時点で追加する」
/// としていた拡張点そのもの。`Placement.reconcileWorkspace`が「既知のギャップ、Explain未実装」と
/// 自分で3箇所書いていた経路（未登録Layout・Layout呼出失敗・PlanValidator拒否）を初めて記録する。
/// `PolicyRuleId`（Wm.Policy）側は、`AwaitingEvidence`の`blockedBy`も含め既に`WindowRecord`
/// （永続状態、`ClassifiedBy = ByPolicy of PolicyRuleId option`）で追跡済みのため、ここで重複
/// させる必要は無いと判断した——DecisionTraceは「永続状態に残らない、この1stepだけの経緯」が対象。
type DecisionTrace =
    | InconsistencySuspected of domain: string * detail: string
    /// `Policy`/`Registry`の設定ミス（Policyが参照する`LayoutId`/`ApiVersion`が`Registry`に
    /// 登録されていない）。Reconcileはこの窓のSetRectを諦め、既存のDesiredをそのまま残す。
    | LayoutNotRegistered of layoutId: LayoutId * apiVersion: int
    /// Layout関数自体が`Error`を返した（ユーザー提供コードの不具合、またはCrash→`Result`化の経路）。
    | LayoutInvocationFailed of layoutId: LayoutId * error: LayoutError
    /// PlanValidatorが計画を拒否した。`decide`が`RelayoutWithout`で再計算を試みた場合、2回目も
    /// 失敗したときに記録する（1回目の拒否自体は再計算成功時には記録しない——最終的に無害だった
    /// ため、6.3「例外とフォールバック」の精神どおりノイズにしない）。
    | PlanRejected of layoutId: LayoutId * errors: ValidationError list
