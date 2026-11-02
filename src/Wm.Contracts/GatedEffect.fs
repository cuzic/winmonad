/// 決定6層8（ADR-019）：GateRejection/GateInput → GatedEffect（internal構築子）。architecture.md §6.2（ADR-014）。
module Wm.Contracts.GatedEffect

open Wm.Contracts.Identifiers
open Wm.Contracts.SharedVocabulary
open Wm.Contracts.CoreCommand

type GateInput = {
    Alive: Set<WindowId>
    Management: Map<WindowId, EffectiveManagement>
    Monitors: MonitorSnapshot list
    RestoreIssuedInBatch: Set<WindowId>
    NotMovable: Set<WindowId>
    Hung: Set<WindowId>
    HiddenByWm: Set<WindowId>
}

type GateRejection =
    | TargetNotAlive of WindowId
    | TargetExcluded of WindowId
    | NonPositiveRect of WindowId
    | OutsideAnyMonitor of WindowId
    | MissingRestoreBeforeMove of WindowId
    | TargetNotMovable of WindowId
    | TargetBelievedHung of WindowId
    | TargetHiddenByWm of WindowId

/// コンストラクタはinternal。InternalsVisibleTo("Wm.Core")でWm.Coreだけに構築を許す（R2-20、AssemblyInfo.fs参照）。
type GatedEffect = internal GatedEffect of Effect

module GatedEffect =
    let effect (g: GatedEffect) = match g with GatedEffect e -> e

    let private rectOf (e: Effect) : (WindowId * PixelRect) option =
        match e with
        | SetRect (_, w, r) -> Some (w, r)
        | _ -> None

    let private targetOf (e: Effect) : WindowId option =
        match e with
        | SetRect (_, w, _)
        | RestoreBeforeMove (_, w)
        | RequestForeground (_, w)
        | ObserveWindow w
        | SetPresence (_, w, _)
        | RequestClose (_, w)
        | ForgetWindow w
        | Reprobe (w, _) -> Some w
        | ApplyBatch _
        | ObserveAll -> None

    let private requestIdOf (e: Effect) : RequestId option =
        match e with
        | SetRect (rid, _, _)
        | RestoreBeforeMove (rid, _)
        | RequestForeground (rid, _)
        | SetPresence (rid, _, _)
        | RequestClose (rid, _) -> Some rid
        | _ -> None

    let private intersectsAnyMonitor (monitors: MonitorSnapshot list) (r: PixelRect) =
        monitors
        |> List.exists (fun m ->
            let b = m.Bounds
            r.X < b.X + b.Width && r.X + r.Width > b.X && r.Y < b.Y + b.Height && r.Y + r.Height > b.Y)

    /// §6.2「拒否理由ごとの適用範囲」の表に従って1つのEffectを検査する。
    let private checkOne (input: GateInput) (restoreTargetsSeen: Set<WindowId>) (e: Effect) : GateRejection option =
        let notAlive w = not (Set.contains w input.Alive)
        let isExcluded w =
            match Map.tryFind w input.Management with
            | Some EffExcluded -> true
            | _ -> false
        let isNotMovable w = Set.contains w input.NotMovable
        let isHung w = Set.contains w input.Hung
        let isHiddenByWm w = Set.contains w input.HiddenByWm

        match targetOf e with
        | Some w when notAlive w -> Some (TargetNotAlive w)
        | _ ->
        match e with
        | SetRect (_, w, _) | RestoreBeforeMove (_, w) ->
            if isExcluded w then Some (TargetExcluded w)
            elif isNotMovable w then Some (TargetNotMovable w)
            else
                match rectOf e with
                | Some (_, r) when r.Width <= 0 || r.Height <= 0 -> Some (NonPositiveRect w)
                | Some (_, r) when not (intersectsAnyMonitor input.Monitors r) -> Some (OutsideAnyMonitor w)
                | _ -> None
        | RequestForeground (_, w) ->
            if isExcluded w then Some (TargetExcluded w)
            elif isNotMovable w then Some (TargetNotMovable w)
            elif isHung w then Some (TargetBelievedHung w)
            elif isHiddenByWm w then Some (TargetHiddenByWm w)
            else None
        | RequestClose (_, w) ->
            if isExcluded w then Some (TargetExcluded w) else None
        | SetPresence (_, w, _) ->
            if isExcluded w then Some (TargetExcluded w)
            elif isNotMovable w then Some (TargetNotMovable w)
            elif isHung w then Some (TargetBelievedHung w)
            elif isHiddenByWm w then Some (TargetHiddenByWm w)
            else None
        | ObserveWindow w | Reprobe (w, _) ->
            if isExcluded w then Some (TargetExcluded w) else None
        | ForgetWindow _ | ApplyBatch _ | ObserveAll -> None

    /// 全ての違反を蓄積し、ゲートを通過したEffectと拒否されたもの（対応するRequestId）を分けて返す。
    /// ApplyBatchは子Effectを展開して個別に検査する（§6.2の表は窓単位の適用範囲なので、バッチ自体は最終的に
    /// 「通過した子だけを含む新しいApplyBatch」として再構成する）。
    let gate (input: GateInput) (effects: Effect list) : GatedEffect list * (RequestId option * GateRejection) list =
        let rec goFlat (restoreSeen: Set<WindowId>) (es: Effect list) =
            es
            |> List.fold
                (fun (seen, accOk, accRej) e ->
                    match e with
                    | RestoreBeforeMove (_, w) ->
                        match checkOne input seen e with
                        | Some rej -> seen, accOk, accRej @ [ (requestIdOf e, rej) ]
                        | None -> Set.add w seen, accOk @ [ e ], accRej
                    | _ ->
                        match checkOne input seen e with
                        | Some rej -> seen, accOk, accRej @ [ (requestIdOf e, rej) ]
                        | None -> seen, accOk @ [ e ], accRej)
                (restoreSeen, [], [])
        let seen0 = input.RestoreIssuedInBatch
        let rec goTop (seen: Set<WindowId>) (es: Effect list) =
            es
            |> List.fold
                (fun (seen, accOk, accRej) e ->
                    match e with
                    | ApplyBatch (bid, children) ->
                        let seen', ok, rej = goFlat seen children
                        seen', accOk @ [ ApplyBatch (bid, ok) ], accRej @ rej
                    | _ ->
                        match checkOne input seen e with
                        | Some rej -> seen, accOk, accRej @ [ (requestIdOf e, rej) ]
                        | None ->
                            let seen' =
                                match e with
                                | RestoreBeforeMove (_, w) -> Set.add w seen
                                | _ -> seen
                            seen', accOk @ [ e ], accRej)
                (seen, [], [])
        let _, ok, rejected = goTop seen0 effects
        ok |> List.map GatedEffect, rejected
