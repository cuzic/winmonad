/// ADR-019実装順17：WindowBehaviors.fs。architecture.md §12.1。
///
/// §12.1の`WindowBehavior`（机上で考案するのではなく、Phase 0の失敗カタログ（18章）で実際に観測された
/// ものから選ぶ部品）のうち、`Immediate`（`SetRect`が即座に成功し、要求どおりの矩形がそのまま観測
/// される）に加え、`DelayedByMs`（`SetRect`の呼び出し自体は即座に成功するが、要求どおりの矩形の観測は
/// 指定ミリ秒だけ遅れて届く——`VirtualWorld.Scheduled`、§12.1の`ScheduledSimEvent`に相当）を実装する。
/// `MinimumWidth`/`IgnoreMove`/`SelfRepositionAfterMs`/`DisappearOnMove`/`HungAfterMs`/`Compose`は
/// 対象外（`HungAfterMs`はShellと共有する`executeEffectProtocol`のprobe差し替えが前提で、他は
/// `MinWidth`/`DpiBehavior`等、まだ`VirtualWorld`側に対応する信念更新の受け口が無い——1ラウンドで
/// 実装するには判断点が多すぎる）。
module Wm.Simulation.WindowBehaviors

type WindowBehavior =
    | Immediate
    | DelayedByMs of int64
