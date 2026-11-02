/// ADR-019実装順17：WindowBehaviors.fs。architecture.md §12.1。
///
/// §12.1の`WindowBehavior`（机上で考案するのではなく、Phase 0の失敗カタログ（18章）で実際に観測された
/// ものから選ぶ部品）のうち、このラウンドでは`Immediate`（`SetRect`が即座に成功し、要求どおりの矩形が
/// そのまま観測される）だけを実装する。`DelayedByMs`/`MinimumWidth`/`IgnoreMove`/
/// `SelfRepositionAfterMs`/`DisappearOnMove`/`HungAfterMs`/`Compose`は対象外（`VirtualWorld.fs`の
/// ファイル冒頭コメント参照——いずれもスケジュール済みイベント（`ScheduledSimEvent`、§12.1）か、
/// Shellと共有する`executeEffectProtocol`のprobe差し替え（`HungAfterMs`、§12.1「Shellと同じEffect実行
/// 手順を共有する」）が前提で、1ラウンドで実装するには判断点が多すぎる）。
module Wm.Simulation.WindowBehaviors

type WindowBehavior = Immediate
