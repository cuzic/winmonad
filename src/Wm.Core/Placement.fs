/// Core.Placement（ADR-019実装順14、architecture.md §4.4/§4.5）。このラウンドでは`Geometry`の信念
/// （矩形・モニター・最小化・表示状態、LatestWins規則）の修正だけを実装する。
///
/// このラウンドで意図的に対象外にしたこと（§7.2「Desired/Observed/Pending」、§7.1段③④⑦）。いずれも
/// 「できればやる」ではなく、根拠（証拠の供給元）そのものが無いため実装できない、または設計判断を
/// 要する項目であり、専用のラウンドへ切り出す：
/// - `Desired`/`Pending`/`NextRequestId`/`NextBatchId`/`Epochs`/`HeldFocusTarget`：§7.2のPendingの
///   状態機械（Idle→Planned→Dispatched→AwaitingObservation→Satisfied/Mismatch/Timeout/…）全体。
///   `RequestId`/`IntentEpoch`のライフサイクルは、1ラウンドで実装するには判断点が多すぎる。
/// - `MinWidth`/`DpiBehavior`（Accumulating規則）：§4.5の証拠（`SetRect`の`Mismatch`、DPI切替後の
///   2段目の自己リサイズ観測）は、いずれもPendingの産物であり、Pendingが無い今は証拠を一切供給できない。
/// - `Movability`の一部の遷移（`SetRect`の`Denied OtherDenial`→`NotMovable`、`Satisfied`→`Movable`）も
///   同じ理由で対象外。発見時の楽観的既定値（4.5）と、`TargetIntegrity`のprobeで`Higher`が観測された
///   場合の遷移だけを実装する。
/// - `TargetIntegrity`のProbeOnce規則の「発見時に1回probeする」を、Coreが能動的に`Reprobe`Effectを
///   発行して実現するか、Shellが列挙時に自発的に行う前提にするかは本書に明記が無く判断が要る
///   （`Reprobe`Effectの発行はEffect/ゲートの設計に関わるため、専用のラウンドで扱う）。
module Wm.Core.Placement

open Wm.Contracts.Identifiers
open Wm.Contracts.Observation
open Wm.Contracts.Beliefs

let private geometryInitial: GeometryBeliefs =
    { NativeRect = Unformed []
      VisibleFrameRect = LatestWins.unformed
      Monitor = LatestWins.unformed
      IsMinimized = LatestWins.unformed
      Presence = LatestWins.unformed }

/// `WindowSnapshot`から`GeometryBeliefs`をLatestWins規則で修正する（§4.5。`Beliefs.LatestWins.revise`を
/// 直接使う。R4-9/Opusレビュー1-1：ドメインごとに再実装しない）。`NativeRect`は修正規則が本書に
/// 明記されていないため、このラウンドでは手を付けない（`Unformed`のまま、既知のギャップ。Beliefs.fs参照）。
let reviseGeometryFromSnapshot (now: int64) (current: GeometryBeliefs) (snapshot: WindowSnapshot) : GeometryBeliefs =
    { current with
        VisibleFrameRect =
            snapshot.VisibleFrameRect |> Option.map (LatestWins.revise now current.VisibleFrameRect) |> Option.defaultValue current.VisibleFrameRect
        Monitor = snapshot.Monitor |> Option.map (LatestWins.revise now current.Monitor) |> Option.defaultValue current.Monitor
        IsMinimized =
            snapshot.IsMinimized |> Option.map (LatestWins.revise now current.IsMinimized) |> Option.defaultValue current.IsMinimized
        Presence = snapshot.Presence |> Option.map (LatestWins.revise now current.Presence) |> Option.defaultValue current.Presence }

/// `MinimizedStateChanged`（ADR-019実装順10のWm.Normalizationが既に定義していたが、`WmState.step`に
/// 配線先が無かった入力。このラウンドでそれを塞ぐ）。
let reviseMinimized (now: int64) (current: GeometryBeliefs) (obs: Observation<bool>) : GeometryBeliefs =
    { current with IsMinimized = LatestWins.revise now current.IsMinimized obs }

type PlacementState = { Geometry: Map<WindowId, GeometryBeliefs> }

let initial: PlacementState = { Geometry = Map.empty }

let private withGeometry (w: WindowId) (state: PlacementState) (f: GeometryBeliefs -> GeometryBeliefs) : PlacementState =
    let current = Map.tryFind w state.Geometry |> Option.defaultValue geometryInitial
    { state with Geometry = Map.add w (f current) state.Geometry }

/// `WindowObserved`：§7.1段①「Windows→Placement→Health→Focus」のPlacement分。Windowsと同じ入力から
/// 自分の信念（Geometry）だけを修正する（ADR-018：ドメインは自分の状態だけを返す）。
let onWindowObserved (now: int64) (snapshot: WindowSnapshot) (state: PlacementState) : PlacementState =
    withGeometry snapshot.Id state (fun g -> reviseGeometryFromSnapshot now g snapshot)

let onMinimizedStateChanged (now: int64) (w: WindowId) (obs: Observation<bool>) (state: PlacementState) : PlacementState =
    withGeometry w state (fun g -> reviseMinimized now g obs)

/// 窓の消滅時、`Geometry`を除く（§4.4「各ドメインの信念は窓が消滅した時点でのみ除去する」）。
/// `WmState.vanishWindow`から呼ぶ。
let onWindowVanished (w: WindowId) (state: PlacementState) : PlacementState = { state with Geometry = Map.remove w state.Geometry }
