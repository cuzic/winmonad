/// ADR-013/architecture.md §12.1・§21(R3-2解決)：`normalize`が「Raw観測の事実を無条件・無加工で
/// 正規化済みInputEventへ詰め替えるだけで、候補条件の判定や分類などの判断を一切加えない」ことを
/// 検査する。各`RawWindowEvent`ケースについて、(a) ちょうど1件の`InputEvent`を返し、(b) 運ぶ
/// データ（WindowId/Observation等）が一切変形されず、(c) 対応する`InputEvent`の構築子が
/// 正しいことを、FsCheckの任意値で確認する。
module Wm.Normalization.Tests.WindowEventNormalizerTests

open FsCheck.Xunit
open Wm.Contracts.Identifiers
open Wm.Contracts.Observation
open Wm.Contracts.CoreCommand
open Wm.Normalization.WindowEventNormalizer

[<Property>]
let ``RawFirstSeenはWindowObservedへ、Snapshotを変形せずそのまま詰め替える`` (snapshot: WindowSnapshot) =
    normalize (RawFirstSeen snapshot) = [ WindowObserved snapshot ]

[<Property>]
let ``RawReobservedはWindowObservedへ、Snapshotを変形せずそのまま詰め替える(Phase Bラウンド4)`` (snapshot: WindowSnapshot) =
    normalize (RawReobserved snapshot) = [ WindowObserved snapshot ]

[<Property>]
let ``RawDestroyedはWindowRemovedへ、WindowIdを変形せずそのまま詰め替える`` (w: WindowId) =
    normalize (RawDestroyed w) = [ WindowRemoved w ]

[<Property>]
let ``RawForegroundはForegroundObservedへ、Observationを変形せずそのまま詰め替える`` (obs: Observation<WindowId option>) =
    normalize (RawForeground obs) = [ ForegroundObserved obs ]

[<Property>]
let ``RawVisibilityはVisibilityChangedへ、WindowIdとObservationを変形せずそのまま詰め替える`` (w: WindowId) (obs: Observation<bool>) =
    normalize (RawVisibility (w, obs)) = [ VisibilityChanged (w, obs) ]

[<Property>]
let ``RawMinimizedはMinimizedStateChangedへ、WindowIdとObservationを変形せずそのまま詰め替える`` (w: WindowId) (obs: Observation<bool>) =
    normalize (RawMinimized (w, obs)) = [ MinimizedStateChanged (w, obs) ]

[<Property>]
let ``RawCloakはCloakChangedへ、WindowIdとObservationを変形せずそのまま詰め替える`` (w: WindowId) (obs: Observation<CloakState>) =
    normalize (RawCloak (w, obs)) = [ CloakChanged (w, obs) ]

[<Property>]
let ``RawStyleChangedはStyleChangedへ、WindowIdを変形せずそのまま詰め替える`` (w: WindowId) =
    normalize (RawStyleChanged w) = [ StyleChanged w ]

[<Property>]
let ``RawMoveSizeStartはMoveSizeStartedへ、WindowIdを変形せずそのまま詰め替える`` (w: WindowId) =
    normalize (RawMoveSizeStart w) = [ MoveSizeStarted w ]

[<Property>]
let ``RawMoveSizeEndはMoveSizeEndedへ、全フィールドを変形せずそのまま詰め替える``
    (w: WindowId)
    (rect: Observation<PixelRect>)
    (mon: Observation<MonitorId>)
    =
    normalize (RawMoveSizeEnd (w, rect, mon)) = [ MoveSizeEnded (w, rect, mon) ]

/// 純粋関数であることの直接の確認：同じ入力を2回渡しても同じ結果が返る
/// （隠れた状態・環境依存が無いことの最小限の確認。3.4のDST前提と同種の検査）。
[<Property>]
let ``normalizeは同じ入力について常に同じ結果を返す(純粋性)`` (raw: RawWindowEvent) =
    normalize raw = normalize raw

/// 現時点の全ケースは、Raw側の1イベントがちょうど1件のInputEventに対応する
/// (将来、全体Snapshotの再構築等で1対多になるケースが増えても、既存ケースの意味は変えない)。
[<Property>]
let ``現時点の全ケースはちょうど1件のInputEventを返す`` (raw: RawWindowEvent) =
    (normalize raw |> List.length) = 1
