/// 決定6層8（ADR-019）：GateRejection/GateInput → GatedEffect（internal構築子）。architecture.md §6.2（ADR-014）。
///
/// Opusレビュー（2026-11-01）で、§6.2「Effect種別ごとのゲートの適用範囲」表と旧実装（if/elifの手書き分岐）が
/// 11セル食い違っていることが判明した（SetPresenceへのTargetHiddenByWm適用でWMの非表示Effectが全て
/// 弾かれる、ForgetWindowへのTargetNotAlive適用でHWND再利用窓が永久に再発見されない、等、いずれもblocking）。
/// 表をそのままデータ（`appliesTable`）として持ち、`checkOne`はそれを読むだけにすることで、
/// 「表と実装が食い違う」という種類の不一致を構造的に塞ぐ。
module Wm.Contracts.GatedEffect

open Wm.Contracts.Identifiers
open Wm.Contracts.SharedVocabulary
open Wm.Contracts.CoreCommand

type GateInput = {
    Alive: Set<WindowId>
    Management: Map<WindowId, EffectiveManagement>
    Monitors: MonitorSnapshot list
    NotMovable: Set<WindowId>
    Hung: Set<WindowId>
    HiddenByWm: Set<WindowId>
    /// 最大化状態でRestoreBeforeMoveが必要な窓（v2.74でPlacementの`Geometry.IsMaximized`信念から
    /// 実際に組み立てられるようになった。§6.2のMissingRestoreBeforeMove判定に使う。Aero Snap状態は
    /// 検知手段が未調査のため依然対象外のまま）。
    NeedsRestore: Set<WindowId>
    /// 全画面と判定された窓（v2.128、E-4、opus-review-fullscreen-detection-round2.md §2-1）。
    /// `Placement.PlacementState.Fullscreen`からそのまま組み立てる——Draggingの方式（`aliveForScreen`
    /// から外す）ではなく、ここ（ゲート）で`SetRect`/`RestoreBeforeMove`を一律に塞ぐ。理由は
    /// `RejectionKind.RFullscreen`の型直前のコメント参照。
    Fullscreen: Set<WindowId>
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
    | TargetFullscreen of WindowId

/// コンストラクタはinternal。InternalsVisibleTo("Wm.Core"/"Wm.Core.Tests")でそれらだけに構築を許す
/// （R2-20、AssemblyInfo.fs参照）。`gate`自体もinternalにする（Opusレビュー2-9）：
/// 公開関数のままだと、誰でも偽の`GateInput`（例：`Alive`に全窓を含める）を渡して任意のEffectを
/// `GatedEffect`化できてしまい、internalコンストラクタが実質何も守らなくなる。§6.2の「Coreの現在状態との
/// 整合」という約束は、GateInputを作れるのがCoreだけであることに依存している。
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

    /// オーバーフロー対策でint64で計算する（Opusレビュー2-11。ゲートはPlanValidatorを通らない再送経路も
    /// 見るため、§6.2 #3の整数オーバーフロー検査と同じ注意が要る）。
    let private intersectsAnyMonitor (monitors: MonitorSnapshot list) (r: PixelRect) =
        let rx, ry, rw, rh = int64 r.X, int64 r.Y, int64 r.Width, int64 r.Height
        monitors
        |> List.exists (fun m ->
            let b = m.Bounds
            let bx, by, bw, bh = int64 b.X, int64 b.Y, int64 b.Width, int64 b.Height
            rx < bx + bw && rx + rw > bx && ry < by + bh && ry + rh > by)

    /// §6.2の表の列（Effect種別）。矩形を持たないEffect（ApplyBatch/ObserveAll）は対象外（窓単位の検査が無い）。
    type private EffectKind =
        | RectMutating // SetRect | RestoreBeforeMove
        | ForegroundKind // RequestForeground
        | PresenceKind // SetPresence
        | ObserveKind // ObserveWindow | Reprobe
        | ForgetKind // ForgetWindow
        | CloseKind // RequestClose

    /// §6.2の表の行（拒否理由）のうち、窓の信念・生存だけで決まるもの。NonPositiveRect/OutsideAnyMonitor/
    /// MissingRestoreBeforeMoveはRectMutating専用の構造的チェックなので、この表には含めない。
    type private RejectionKind =
        | RNotAlive
        | RExcluded
        | RNotMovable
        | RBelievedHung
        | RHiddenByWm
        /// E-4全画面検出（v2.128、opus-review-fullscreen-detection-round2.md §2-1）：Draggingの方式
        /// （`aliveForScreen`から外す、Reconcile経路だけを止める）ではなくゲートに置く理由は4つ——
        /// (1) `dispatchPlacements`・`retryOrGiveUp`・v2.121自己修復・`RestoreBeforeMove`の
        /// 全`SetRect`経路を1箇所で塞げる、(2) `onGateRejected`が`Desired`も消すので全画面を
        /// 抜けた後の次のReconcileで`desiredChanged`が立ち直り再タイルされる（`aliveForScreen`方式
        /// では`Desired`が古いまま残り取りこぼす）、(3) Layoutが全画面の窓の場所を確保したまま
        /// 残るので、`RelayoutWithout`で兄弟の窓が広がって抜けた後に重なり続ける事故が起きない、
        /// (4) `HiddenByWm`（v2.86）と同じ「Placementの状態からGateInputの集合を作り、RectMutating
        /// を拒否する」という既存パターンに合う。
        | RFullscreen

    let private effectKindOf (e: Effect) : EffectKind option =
        match e with
        | SetRect _
        | RestoreBeforeMove _ -> Some RectMutating
        | RequestForeground _ -> Some ForegroundKind
        | SetPresence _ -> Some PresenceKind
        | ObserveWindow _
        | Reprobe _ -> Some ObserveKind
        | ForgetWindow _ -> Some ForgetKind
        | RequestClose _ -> Some CloseKind
        | ApplyBatch _
        | ObserveAll -> None

    /// §6.2「Effect種別ごとのゲートの適用範囲」表そのもの。表の1セルが実装の1エントリに対応するので、
    /// 表と実装がずれたら片方を読むだけで不一致を見つけられる（Opusレビュー2-11の提案）。
    /// RequestCloseはRequestForegroundの列と同じ扱いにするが、TargetBelievedHungだけ適用しない
    /// （§6.2本文：WM_CLOSEの送信は非同期で呼出側をブロックせず、ハングした窓を閉じたいのは正当な要求）。
    let private appliesTable: Map<RejectionKind * EffectKind, bool> =
        [ (RNotAlive, RectMutating), true
          (RNotAlive, ForegroundKind), true
          (RNotAlive, PresenceKind), true
          (RNotAlive, ObserveKind), true
          (RNotAlive, ForgetKind), false // ForgetWindowの対象はHWND再利用等で既に対応表から外れた旧ID
          (RNotAlive, CloseKind), true

          (RExcluded, RectMutating), true
          (RExcluded, ForegroundKind), true
          (RExcluded, PresenceKind), true
          (RExcluded, ObserveKind), false // Excludedの窓も信念を持ち続け、再probeする（§4.4）
          (RExcluded, ForgetKind), false
          (RExcluded, CloseKind), true

          (RNotMovable, RectMutating), true
          (RNotMovable, ForegroundKind), false
          (RNotMovable, PresenceKind), false
          (RNotMovable, ObserveKind), false
          (RNotMovable, ForgetKind), false
          (RNotMovable, CloseKind), false

          (RBelievedHung, RectMutating), true
          (RBelievedHung, ForegroundKind), true
          (RBelievedHung, PresenceKind), false // ハング窓も隠す必要があり、ShowWindowAsync系で非同期に出す
          (RBelievedHung, ObserveKind), false // Reprobeはハング解除の唯一の経路
          (RBelievedHung, ForgetKind), false
          (RBelievedHung, CloseKind), false // 本文の例外

          (RHiddenByWm, RectMutating), true
          (RHiddenByWm, ForegroundKind), true
          (RHiddenByWm, PresenceKind), false // WMが隠す/見せるためのEffectそのものなので適用しない
          (RHiddenByWm, ObserveKind), false
          (RHiddenByWm, ForgetKind), false
          (RHiddenByWm, CloseKind), true

          (RFullscreen, RectMutating), true
          (RFullscreen, ForegroundKind), false // 全画面の窓を前面に出すのは妨げない
          (RFullscreen, PresenceKind), false // 隠す/見せるのは妨げない（ワークスペース切替等）
          (RFullscreen, ObserveKind), false // 再probeは妨げない
          (RFullscreen, ForgetKind), false
          (RFullscreen, CloseKind), false ] // 閉じる要求は妨げない
        |> Map.ofList

    /// §6.2「拒否理由ごとの適用範囲」の表に従って1つのEffectを検査する。restoreSeenは直前に通過した
    /// Effectが`RestoreBeforeMove w`だった場合の`{w}`、それ以外は空集合（バッチをまたいでは共有しない、
    /// Opusレビュー2-7。「直前」の意味論はv2.88のround2レビューN-4対応で狭めた、`gate`の型直前の
    /// コメント参照）。
    let private checkOne (input: GateInput) (restoreSeen: Set<WindowId>) (e: Effect) : GateRejection option =
        match effectKindOf e, targetOf e with
        | None, _ -> None // ApplyBatch/ObserveAllはここでは検査しない（呼び出し元が子を展開する）
        | Some _, None -> None // 到達しない：効果種別を持つEffectは必ず対象窓を持つ
        | Some kind, Some w ->
            let applies rejection = Map.find (rejection, kind) appliesTable
            let isExcluded =
                match Map.tryFind w input.Management with
                | Some EffExcluded -> true
                | _ -> false
            let isNotMovable = Set.contains w input.NotMovable
            let isHung = Set.contains w input.Hung
            let isHiddenByWm = Set.contains w input.HiddenByWm
            let isFullscreen = Set.contains w input.Fullscreen
            let notAlive = not (Set.contains w input.Alive)

            if notAlive && applies RNotAlive then Some (TargetNotAlive w)
            elif isExcluded && applies RExcluded then Some (TargetExcluded w)
            elif isNotMovable && applies RNotMovable then Some (TargetNotMovable w)
            elif isHung && applies RBelievedHung then Some (TargetBelievedHung w)
            elif isHiddenByWm && applies RHiddenByWm then Some (TargetHiddenByWm w)
            elif isFullscreen && applies RFullscreen then Some (TargetFullscreen w)
            else
                match e with
                | SetRect (_, _, r) ->
                    if r.Width <= 0 || r.Height <= 0 then Some (NonPositiveRect w)
                    elif not (intersectsAnyMonitor input.Monitors r) then Some (OutsideAnyMonitor w)
                    elif Set.contains w input.NeedsRestore && not (Set.contains w restoreSeen) then
                        Some (MissingRestoreBeforeMove w)
                    else
                        None
                | _ -> None

    /// Effectのリスト全体について、1件ずつ§6.2の表で検査し、通過したものと拒否されたもの
    /// （対応するRequestIdと拒否理由）を分けて返す。ApplyBatch（入れ子を含む、Opusレビュー2-8）は
    /// 子を再帰的に展開して検査し、通過した子だけを含む新しいApplyBatchとして再構成する
    /// （全員拒否された場合は空のApplyBatchを出さない、Opusレビュー2-11）。
    /// 各ApplyBatchの`restoreSeen`は空集合から始まり、親の並びや他のバッチとは共有しない（Opusレビュー2-7）。
    /// **v2.88（round2レビューN-4）**：`lastRestore`は「直前に通過したEffectが`RestoreBeforeMove w`
    /// だった場合のその`w`」だけを1件覚える（それより前の、または別の窓への`RestoreBeforeMove`は
    /// 一切数えない）。`Placement.dispatchPlacements`は`RestoreBeforeMove w`の直後に必ず同じ`w`への
    /// `SetRect`を発行するので（型直前のコメント参照）、この狭い意味論でも今日の唯一の発行元には
    /// 影響しない。旧実装は`Set<WindowId>`をリスト全体で単調に増やし続けていたため、ある窓への
    /// `RestoreBeforeMove`が通過した後、全く無関係な別のタイミングで同じ窓へ発行された
    /// （直前にRestoreBeforeMoveを伴わない）`SetRect`まで誤って素通りさせてしまう余地があった
    /// （round2レビューが`NeedsRestore`が常に空だった当時「今は顕在化しない」と指摘した既知のギャップ。
    /// v2.74で`NeedsRestore`が実際に populated されるようになった後も見直されていなかった）。
    let internal gate (input: GateInput) (effects: Effect list) : GatedEffect list * (RequestId option * GateRejection) list =
        let rec go (lastRestore: WindowId option) (es: Effect list) : Effect list * (RequestId option * GateRejection) list =
            let restoreSeen = match lastRestore with Some w -> Set.singleton w | None -> Set.empty
            match es with
            | [] -> [], []
            | ApplyBatch (bid, children) :: rest ->
                let childOk, childRej = go None children
                let restOk, restRej = go None rest
                (if List.isEmpty childOk then restOk else ApplyBatch (bid, childOk) :: restOk), childRej @ restRej
            | e :: rest ->
                match checkOne input restoreSeen e with
                | Some rej ->
                    let restOk, restRej = go None rest
                    restOk, (requestIdOf e, rej) :: restRej
                | None ->
                    let lastRestore' =
                        match e with
                        | RestoreBeforeMove (_, w) -> Some w
                        | _ -> None
                    let restOk, restRej = go lastRestore' rest
                    e :: restOk, restRej

        let ok, rej = go None effects
        ok |> List.map GatedEffect, rej
