/// ADR-019実装順9：PlanValidator.fs。architecture.md §6.2。
///
/// `accumulate`する`validate`（全違反を一度に収集、ADR-014/2-1・2-2対応）と、診断から判断を分離した
/// `decide`（ADR-014/2-3対応）。`ApplyBatch`/`RestoreBeforeMove`の前置契約検査はGatedEffect.fs側
/// （Wm.Contracts）が持ち、ここには統合しない（v2.4、R2-8対応）。
module Wm.Layouts.PlanValidator

open Wm.Contracts.Identifiers
open Wm.Contracts.SharedVocabulary
open Wm.Layouts.LayoutRegistry

type ValidationInput = {
    /// WorkAreaを含む（#4対応）。
    Context: LayoutContext
    /// #1/#5対応。UnplacedはTiledWindowsに入らない（§4.4）。
    Management: Map<WindowId, EffectiveManagement>
    /// #1対応。
    Alive: Set<WindowId>
}

type ValidationError =
    | UnmanagedOrDeadWindow of WindowId
    | DuplicateInPlan of WindowId
    | NonPositiveSize of WindowId * width: int * height: int
    /// #3対応。
    | ArithmeticOverflow of WindowId
    /// #4対応（UnknownMonitorより具体的）。
    | OutsideWorkArea of WindowId * PixelRect
    /// #5対応。
    | ExcludedTarget of WindowId
    /// #6対応。
    | MissingPlacement of WindowId

/// private constructor：PlanValidatorの外からは構築できない。
type ValidatedPlan = private { Placements: Placement list }

module ValidatedPlan =
    let placements (p: ValidatedPlan) = p.Placements

let private checkManagedAndAlive (input: ValidationInput) (placements: Placement list) : ValidationError list =
    placements
    |> List.choose (fun p ->
        let isAlive = Set.contains p.Window input.Alive
        let isManaged = Map.containsKey p.Window input.Management
        if not isAlive || not isManaged then Some (UnmanagedOrDeadWindow p.Window) else None)

let private checkNoDuplicates (placements: Placement list) : ValidationError list =
    placements
    |> List.map (fun p -> p.Window)
    |> List.countBy id
    |> List.filter (fun (_, count) -> count > 1)
    |> List.map (fun (w, _) -> DuplicateInPlan w)

let private checkPositiveSize (placements: Placement list) : ValidationError list =
    placements
    |> List.choose (fun p ->
        if p.Target.Width <= 0 || p.Target.Height <= 0 then
            Some (NonPositiveSize (p.Window, p.Target.Width, p.Target.Height))
        else
            None)

/// オーバーフロー対策でint64で計算する（GatedEffect.fsの`intersectsAnyMonitor`と同じ注意、§6.2 #3）。
let private checkNoOverflow (placements: Placement list) : ValidationError list =
    placements
    |> List.choose (fun p ->
        let r = p.Target
        let right = int64 r.X + int64 r.Width
        let bottom = int64 r.Y + int64 r.Height
        let inRange (v: int64) = v >= int64 System.Int32.MinValue && v <= int64 System.Int32.MaxValue
        if not (inRange right) || not (inRange bottom) then Some (ArithmeticOverflow p.Window) else None)

/// モニター/WorkArea参照の整合性（#4）：目標矩形がWorkAreaと一切交差しなければ違反とする
/// （GatedEffect.fsの`OutsideAnyMonitor`と同じ「交差」基準。部分的な画面外はソフト制約の範疇）。
let private checkWithinWorkArea (input: ValidationInput) (placements: Placement list) : ValidationError list =
    let intersects (a: PixelRect) (b: PixelRect) =
        let ax, ay, aw, ah = int64 a.X, int64 a.Y, int64 a.Width, int64 a.Height
        let bx, by, bw, bh = int64 b.X, int64 b.Y, int64 b.Width, int64 b.Height
        ax < bx + bw && ax + aw > bx && ay < by + bh && ay + ah > by
    placements
    |> List.choose (fun p ->
        if intersects p.Target input.Context.WorkArea then None else Some (OutsideWorkArea (p.Window, p.Target)))

let private checkNotExcluded (input: ValidationInput) (placements: Placement list) : ValidationError list =
    placements
    |> List.choose (fun p ->
        match Map.tryFind p.Window input.Management with
        | Some EffExcluded -> Some (ExcludedTarget p.Window)
        | _ -> None)

/// 配置されるべき対象（Alive ∩ Managed(Tiled)）のうち、Planに含まれない窓を検出する（#6）。
/// この期待集合の導出がR2-21で訂正された「`TiledWindows ⊆ Alive ∩ Managed(Tiled)`」不変条件の
/// 根拠そのものである。
let private checkNoMissingPlacement (input: ValidationInput) (placements: Placement list) : ValidationError list =
    let placed = placements |> List.map (fun p -> p.Window) |> Set.ofList
    input.Management
    |> Map.toList
    |> List.choose (fun (w, m) ->
        if m = EffTiled && Set.contains w input.Alive && not (Set.contains w placed) then
            Some (MissingPlacement w)
        else
            None)

let validate (input: ValidationInput) (placements: Placement list) : Result<ValidatedPlan, ValidationError list> =
    let errors =
        [ yield! checkManagedAndAlive input placements
          yield! checkNoDuplicates placements
          yield! checkPositiveSize placements
          yield! checkNoOverflow placements
          yield! checkWithinWorkArea input placements
          yield! checkNotExcluded input placements
          yield! checkNoMissingPlacement input placements ]
    match errors with
    | [] -> Ok { Placements = placements }
    | es -> Error es

type Disposition =
    | RejectWholePlan
    /// 検証時点で死亡/Excluded化していた窓を除いて1回だけ再計算する。
    | RelayoutWithout of Set<WindowId>

/// 「集める」診断と「決める」判断を分離する（ADR-014/2-3対応）。窓の生死に関係しない構造的な違反
/// （`NonPositiveSize`/`ArithmeticOverflow`/`OutsideWorkArea`/`MissingPlacement`等）が1件でも
/// 混ざっていれば再計算の対象にならず`RejectWholePlan`になる。
let decide (errors: ValidationError list) : Disposition =
    let removable =
        errors
        |> List.forall (function
            | UnmanagedOrDeadWindow _
            | ExcludedTarget _ -> true
            | _ -> false)
    let targets =
        errors
        |> List.choose (function
            | UnmanagedOrDeadWindow w
            | ExcludedTarget w -> Some w
            | _ -> None)
        |> Set.ofList
    if removable && not (Set.isEmpty targets) then RelayoutWithout targets else RejectWholePlan
