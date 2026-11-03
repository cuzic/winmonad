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
/// `NativeCallRecord`を返す）。`MinimumWidth`/`IgnoreMove`/`SelfRepositionAfterMs`/`DisappearOnMove`/
/// `Compose`は依然対象外（`MinWidth`/`DpiBehavior`等、まだ`VirtualWorld`側に対応する信念更新の受け口が
/// 無い——1ラウンドで実装するには判断点が多すぎる）。
module Wm.Simulation.WindowBehaviors

type WindowBehavior =
    | Immediate
    | DelayedByMs of int64
    /// §12.1「`startMs`以降メッセージを処理せず、`start+5000ms`以降はOSのハング判定が成立する」。
    /// 時刻は`VirtualWorld`の他の時刻と同じ`int64`ミリ秒（架空のpseudocodeの`int`注記はここでは
    /// `VirtualWorld.ClockMs`/`now`との型の一貫性を優先して`int64`にする）。
    | HungAfterMs of startMs: int64
