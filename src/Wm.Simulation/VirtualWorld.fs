/// ADR-019実装順17：VirtualWorld.fs。architecture.md §12.1。
///
/// このラウンドで実装するのは、`SetRect`Effectを`Immediate`挙動（即座に成功し要求どおりの矩形が
/// そのまま観測される）で適用する最小の`applyEffect`だけである。対象外にしたこと：
/// - `RequestForeground`/`SetPresence`/`RequestClose`/`ObserveWindow`/`Reprobe`/`ForgetWindow`/
///   `ApplyBatch`/`ObserveAll`：Reconcileがまだ`SetRect`以外を発行しないため（Placement.fsの
///   ファイル冒頭コメント参照）、今は何もしない（`[]`を返す）。
/// - `ScheduledSimEvent`／時刻のずれを伴う観測（`DelayedByMs`等）：`WindowBehaviors.fs`が`Immediate`
///   だけを持つ現状では、スケジューラーの出口（`ClockMs`を進めて予定イベントを発火する仕組み）自体が
///   不要なので実装しない。
/// - `SimEvent`は本書のまま独立した型にせず、直接`InputEvent`として返す（ファイル冒頭の簡略化）。
///   §12.1「`applyEffect`が生成するSimEventは…Normalizerを通してからInputEventになる」は、WinEvent
///   フック駆動の観測（可視性・最小化・Cloak・スタイル変更）を疑似OS上で再現するための規約であり、
///   `SetRect`完了後の矩形再取得は（実際のShellでも）WinEventコールバックではなくEffect適用スレッド
///   自身の再問い合わせなので、Raw層・Normalizerを経由する対象ではない。`DelayedByMs`等の挙動部品で
///   Raw層駆動の観測（表示直後のスタイル変更等）が必要になった時点で、`SimEvent`を`RawWindowEvent`
///   ベースへ拡張することを検討する。
module Wm.Simulation.VirtualWorld

open Wm.Contracts.Identifiers
open Wm.Contracts.ErrorTypes
open Wm.Contracts.Observation
open Wm.Contracts.CoreCommand
open Wm.Contracts.GatedEffect
open Wm.Simulation.WindowBehaviors

type VirtualWindow = { Id: WindowId; Rect: PixelRect; Behavior: WindowBehavior }

/// §12.1。`Scheduled`/`Seed`は`WindowBehaviors`が`Immediate`だけの間は読み書きする者がいないため、
/// このラウンドでは持たない（ファイル冒頭コメント参照）。
type VirtualWorld = { Windows: Map<WindowId, VirtualWindow>; ClockMs: int64; NextSampleSequence: int64 }

let empty: VirtualWorld = { Windows = Map.empty; ClockMs = 0L; NextSampleSequence = 0L }

let addWindow (w: WindowId) (rect: PixelRect) (behavior: WindowBehavior) (world: VirtualWorld) : VirtualWorld =
    { world with Windows = Map.add w { Id = w; Rect = rect; Behavior = behavior } world.Windows }

let private emptySnapshot (w: WindowId) : WindowSnapshot =
    { Id = w
      Process = None
      AppIdentity = { Exe = None; HostExe = None; Aumid = None; CoreWindowExe = None }
      Owner = None
      IsDialog = None
      NativeRect = None
      VisibleFrameRect = None
      IsVisible = None
      IsMinimized = None
      Cloak = None
      Monitor = None
      MinWidthHint = None
      TargetIntegrity = None
      Responsiveness = None
      Presence = None
      DpiAwareness = None
      TitleMatches = None }

/// §12.1「`applyEffect`」。`GatedEffect`を適用し、新しい`VirtualWorld`と、生じた観測（`InputEvent`、
/// ファイル冒頭コメント参照）、Effect適用スレッドの戻り値（`EffectCallReturned`の元）を返す。
let applyEffect (now: int64) (world: VirtualWorld) (gated: GatedEffect) : VirtualWorld * InputEvent list * (RequestId * Result<unit, NativeCallRecord>) list =
    match GatedEffect.effect gated with
    | SetRect (rid, w, rect) ->
        match Map.tryFind w world.Windows with
        | None ->
            // 対象窓が疑似OS側に存在しない（消滅後の遅延Effect等）。ERROR_INVALID_WINDOW_HANDLEとして返す
            // （8.1のclassify対応表と同じコード、§7.1「TargetGone」）。
            world, [], [ rid, Error { Api = "SetWindowPos"; Space = Win32LastError; Code = 1400; ElapsedUs = 0L } ]
        | Some vw ->
            match vw.Behavior with
            | Immediate ->
                let sampleSeq = world.NextSampleSequence
                let world' =
                    { world with
                        Windows = Map.add w { vw with Rect = rect } world.Windows
                        NextSampleSequence = sampleSeq + 1L }
                let observed =
                    WindowObserved
                        { emptySnapshot w with
                            VisibleFrameRect = Some { Value = Ok rect; SampleSequence = sampleSeq; MonotonicTime = now; Source = "virtual-world" } }
                world', [ observed ], [ rid, Ok () ]
    | _ -> world, [], [] // このラウンドで対象外のEffect種別（ファイル冒頭参照）
