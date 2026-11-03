/// ADR-019実装順17：WindowBehaviors.fs。architecture.md §12.1。
///
/// §12.1の`WindowBehavior`（机上で考案するのではなく、Phase 0の失敗カタログ（18章）で実際に観測された
/// ものから選ぶ部品）のうち、`Immediate`（`SetRect`が即座に成功し、要求どおりの矩形がそのまま観測
/// される）に加え、`DelayedByMs`（`SetRect`の呼び出し自体は即座に成功するが、要求どおりの矩形の観測は
/// 指定ミリ秒だけ遅れて届く——`VirtualWorld.Scheduled`、§12.1の`ScheduledSimEvent`に相当）を実装する。
/// ~~`HungAfterMs`は対象外（Shellと共有する`executeEffectProtocol`のprobe差し替えが前提）~~：
/// **v2.60で実装**。`Wm.Contracts.ErrorTypes.executeEffectProtocol`自体がv2.59でテストされ実際に呼べる
/// 状態になったので、前提が満たされた（`VirtualWorld.applyEffect`の`SetRect`が実際にこの手順を
/// 経由するようになった経緯はそちらのファイル冒頭コメント参照）。§12.1の型どおり
/// `startMs以降メッセージを処理せず、start+5000ms以降はOSのハング判定が成立する`（feasibility.md §5の
/// OS内部基準と一致、architecture.md 2069行目）：`now < startMs`は`Responsive`、
/// `startMs <= now < startMs+5000`は`HungPending`（`dispatch`はまだ呼ばれる）、
/// `now >= startMs+5000`は`HungConfirmed`（`dispatch`は呼ばれず即座に`TargetHung`相当の
/// `NativeCallRecord`を返す）。
/// ~~`MinimumWidth`は対象外（まだ`VirtualWorld`側に対応する信念更新の受け口が無い）~~：**v2.61で実装**。
/// `Placement.reviseMinWidth`（v2.43）・`MinWidth`のPolicy/Layout消費側（v2.48）は既にテスト済みだが、
/// この信念が実際に蓄積する根拠（`SetRect`で幅`r`を要求して幅`o > r`が観測された`Mismatch`）を
/// 実際のパイプラインで生む経路が無かった——`HungAfterMs`がv2.59の`executeEffectProtocol`テストで
/// 満たされたのと同じ構図（消費側は既にあるが、証拠を生む側のシミュレーションが無い）。要求幅が
/// `minWidthPx`未満なら観測される幅を`minWidthPx`へ丸める（Windowsの`WM_GETMINMAXINFO`の
/// `MinTrackSize`相当、Phase 0スパイクS23で実機確認した「丸め済みの最終幅だけが1回のLOCATIONCHANGEで
/// 届く」挙動と整合）。
/// ~~`SelfRepositionAfterMs`は対象外~~：**v2.62で実装**。`Mismatch → Reevaluate`（v2.42、Opus敵対的
/// レビューで設計、`PendingRequest.DispatchSeq`/`Baseline`/`PlacementState.InFlightTargets`）は
/// これまで一度も実際のパイプラインを通して検証されていなかった——既存のテスト
/// （`PlacementReconcileTests.fs`）は全て手で組み立てた観測を直接`onPendingDeadlineTimerFired`等へ
/// 渡していた。「要求とは無関係にアプリが自分で選んだ矩形へ動く」という、Mismatch→Reevaluateが
/// 本来想定しているシナリオそのものを疑似OSで再現する——`SetRect`の呼出自体は即座に成功するが
/// （`DelayedByMs`と同じ`Scheduled`機構を使う）、観測として届くのは要求された矩形ではなく
/// 挙動に固定された`selfRect`（`DelayedByMs`は要求どおりの矩形を遅らせて返すだけなのに対し、
/// こちらは要求を無視して別の矩形を返す点が異なる）。`IgnoreMove`/`DisappearOnMove`/`Compose`は依然
/// 対象外（1ラウンドで実装するには判断点が多すぎる——特に`Compose`は複数挙動の確率的な組合せ規則が
/// 要る）。
module Wm.Simulation.WindowBehaviors

open Wm.Contracts.Identifiers

type WindowBehavior =
    | Immediate
    | DelayedByMs of int64
    /// §12.1「`startMs`以降メッセージを処理せず、`start+5000ms`以降はOSのハング判定が成立する」。
    /// 時刻は`VirtualWorld`の他の時刻と同じ`int64`ミリ秒（架空のpseudocodeの`int`注記はここでは
    /// `VirtualWorld.ClockMs`/`now`との型の一貫性を優先して`int64`にする）。
    | HungAfterMs of startMs: int64
    /// §12.1「`MinimumWidth(700)`」。要求された幅が`minWidthPx`未満なら、観測される幅は
    /// `minWidthPx`に丸められる（高さ・X/Yは要求どおり）。物理ピクセル（`SetRect`の矩形と同じ単位）。
    | MinimumWidth of minWidthPx: int
    /// §12.1「`SelfRepositionAfterMs(ms, rect)`」。`SetRect`の呼出自体は即座に成功するが、`delayMs`後に
    /// 要求とは無関係な`selfRect`へ自己移動したという観測だけが届く（`DelayedByMs`と同じ
    /// `VirtualWorld.Scheduled`機構を使うが、積む矩形が要求された`rect`ではなく`selfRect`になる点が
    /// 唯一の違い）。
    | SelfRepositionAfterMs of delayMs: int64 * selfRect: PixelRect
