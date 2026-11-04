/// ADR-019実装順10：Windows Event Normalizer（ADR-013）。architecture.md §5/§12.1、ADR-013。
///
/// Raw観測（WinEvent種別＋その時点でShellが問い合わせた属性集合）を、Core（`WmState.step`）が
/// 消費する正規化済み`InputEvent`へ変換する純粋関数。Windows Shell（Phase B）とWm.Simulationの
/// 両方がこの同じ関数を呼ぶことで、分類・正規化起因の不具合（ADR-013/C-2）を疑似OS上で再現できる。
///
/// **architecture.md §21の未決事項「Normalizerの『候補条件』判定とCoreの状態
/// （`ClassificationState`）の関係」（v2.10, R3-2）をこのファイルの着手時点で解決する**：
/// Normalizerは「可視・非ツール窓・非Cloak等」という候補条件を一切判定しない。候補条件の評価と
/// `NotCandidate`からの状態遷移（§4.4、一度`Classified`になった窓は候補条件を外れても戻らない、
/// というスティッキーな規則を含む）は、将来のCore.Windowsドメイン（`ClassificationState`、まだ
/// 実装されていない）だけが行う。Normalizerは受け取った生の属性集合を無条件・無加工で
/// 対応する`InputEvent`へ詰め替えるだけであり、`ClassificationState`はおろかCore側の型を
/// 一切参照しない（Wm.NormalizationはWm.Contractsにしか依存しない、§17の依存方向表）。
/// これにより、R3-2が懸念した「NormalizerをCore非依存の純粋関数に保てない可能性」は生じない。
module Wm.Normalization.WindowEventNormalizer

open Wm.Contracts.Identifiers
open Wm.Contracts.Observation
open Wm.Contracts.CoreCommand

/// Shellが実際に問い合わせた属性集合とともに受け取る、1件のRaw観測。
/// HWND初見判定（「このHWNDを見るのは初めてか」）はShell/Simulation側が持つ非純粋な寿命管理
/// （HWNDごとの既知集合）の結果であり、Normalizerはそれを判定するための隠れた状態を一切持たない
/// （ADR-013「純粋関数として切り出す」の要件）——呼び出し側が`RawFirstSeen`と`RawVisibility`等を
/// 区別して渡す。
type RawWindowEvent =
    /// EnumWindowsによる全体Snapshot、またはShellが初見と判定したHWNDの最初のSnapshot。
    | RawFirstSeen of WindowSnapshot
    /// 既知のHWNDについて、発見後に取り直したSnapshot（Phase B ラウンド4：`EVENT_OBJECT_LOCATIONCHANGE`
    /// による再観測）。`RawFirstSeen`と同じ`InputEvent`（`WindowObserved`）へ詰め替わるが、タグを
    /// 分けることで`RawFirstSeen`の「最初のSnapshot」という意味を保つ（Phase Bラウンド3の計画
    /// 「Pending→Satisfied閉路はこのラウンドでは閉じない」で、`RawFirstSeen`の再利用は意味的に
    /// 不整合だと指摘した論点への対応）。Core側（`Placement.onWindowObserved`の`checkPendingSatisfied`）
    /// は既にどの`WindowObserved`でもPending一致判定を行うので、Core側の変更は不要。
    | RawReobserved of WindowSnapshot
    /// EVENT_OBJECT_DESTROY。
    | RawDestroyed of WindowId
    /// EVENT_SYSTEM_FOREGROUND。
    | RawForeground of Observation<WindowId option>
    /// EVENT_OBJECT_SHOW/HIDE等に応じてShellがIsVisibleを再問合せした結果。
    | RawVisibility of WindowId * Observation<bool>
    /// 最小化/復元に応じてShellがIsMinimizedを再問合せした結果。
    | RawMinimized of WindowId * Observation<bool>
    /// DWMWA_CLOAKEDの変化に応じてShellがCloak理由ビットを再問合せした結果。
    | RawCloak of WindowId * Observation<CloakState>
    /// EVENT_OBJECT_NAMECHANGE等のスタイル変化通知（値そのものは次のSnapshotで取り直す、§4.5）。
    | RawStyleChanged of WindowId
    /// EVENT_SYSTEM_MOVESIZESTART。
    | RawMoveSizeStart of WindowId
    /// EVENT_SYSTEM_MOVESIZEEND。最終矩形・モニターの観測を伴う。
    | RawMoveSizeEnd of WindowId * Observation<PixelRect> * Observation<MonitorId>

/// Raw観測1件を、対応する正規化済み`InputEvent`へ1件変換する。`RawWindowEvent`の全ケースに
/// `InputEvent`の既存ケース（Windowsドメインがまだ無いため`step`はまだ分岐しないが、型としては
/// 既に`Wm.Contracts.CoreCommand.InputEvent`に揃っている）が1つずつ対応するので、変換は事実の
/// 詰め替えだけであり、判断（候補条件の評価、分類等）を一切加えない。
///
/// 戻り値を`InputEvent list`にしているのは、将来Raw側の1イベントが複数の正規化イベントに
/// 分かれるケース（例：全体Snapshotの再構築時に複数窓を一度に報告する）に備えるためで、
/// 現時点のケースはいずれも単一要素のリストを返す。
let normalize (raw: RawWindowEvent) : InputEvent list =
    match raw with
    | RawFirstSeen snapshot -> [ WindowObserved snapshot ]
    | RawReobserved snapshot -> [ WindowObserved snapshot ]
    | RawDestroyed w -> [ WindowRemoved w ]
    | RawForeground obs -> [ ForegroundObserved obs ]
    | RawVisibility (w, obs) -> [ VisibilityChanged (w, obs) ]
    | RawMinimized (w, obs) -> [ MinimizedStateChanged (w, obs) ]
    | RawCloak (w, obs) -> [ CloakChanged (w, obs) ]
    | RawStyleChanged w -> [ StyleChanged w ]
    | RawMoveSizeStart w -> [ MoveSizeStarted w ]
    | RawMoveSizeEnd (w, rect, mon) -> [ MoveSizeEnded (w, rect, mon) ]
