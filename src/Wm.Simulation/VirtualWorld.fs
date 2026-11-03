/// ADR-019実装順17：VirtualWorld.fs。architecture.md §12.1。
///
/// `SetRect`Effectを`Immediate`挙動（即座に成功し要求どおりの矩形がそのまま観測される）と
/// `DelayedByMs`挙動（呼び出しは即座に成功するが、観測は`Scheduled`へ積まれ`advanceClock`で
/// 仮想時計を進めた時だけ発火する——§12.1の`ScheduledSimEvent`に相当）で適用する`applyEffect`を
/// 実装する。~~対象外にしたこと：`RequestForeground`/`SetPresence`/`RequestClose`/`ObserveWindow`/
/// `Reprobe`/`ForgetWindow`/`ApplyBatch`/`ObserveAll`：Reconcileがまだ`SetRect`以外を発行しないため
/// （Placement.fsのファイル冒頭コメント参照）、今は何もしない（`[]`を返す）~~：このコメントはv2.17
/// 時点のまま更新されておらず、実際にはv2.31（`SetPresence`）/v2.27以降（`RequestForeground`）/v2.47
/// （`ForgetWindow`）でCoreが既にこれらを発行するようになっていた——**v2.50で`SetPresence`/
/// `RequestForeground`/`ForgetWindow`の3つを実装**し、この無言のギャップを塞いだ（`VirtualWindow`に
/// `Shown: bool`を追加。`SetPresence`は`ObservedPresence`を運ぶ`WindowObserved`を返し、
/// `RequestForeground`は`ForegroundObserved`を返す。いずれも対象窓が疑似OS側に無ければ`SetRect`と
/// 同じ`ERROR_INVALID_WINDOW_HANDLE`（1400）を返す。`ForgetWindow`は`RequestId`を持たない
/// fire-and-forgetなEffectなので観測も結果も返さず、`world.Windows`からただ除くだけ）。
/// `RequestClose`/`ObserveWindow`/`Reprobe`/`ApplyBatch`/`ObserveAll`は依然`[]`を返す（それぞれ別の
/// 理由で後回し：`RequestClose`は窓の消滅を模す決め打ちが必要、`ObserveWindow`/`Reprobe`は
/// `WindowSnapshot`のどのフィールドを返すべきかの対応がBeliefKeyごとに違うため設計が要る、
/// `ApplyBatch`は子Effectの集約の順序・部分失敗の扱いが要る、`ObserveAll`は全窓への再帰が要る——
/// どれも1ラウンドで一括実装するには判断点が多すぎるため、個別のラウンドへ切り出す）。
/// - `advanceClock`は`Scheduled`の発火だけを行い、`Reducer`のTimerキュー（`PendingDeadline`等）には
///   関与しない——両者を同じ呼び出しで進める結合は`ScenarioRunner.fs`冒頭コメントが明示的にシナリオ側の
///   責務としているため、ここでは2つの独立した時計（疑似OSの`Scheduled`とCoreのTimerキュー）を別々に
///   進める部品だけを提供する。
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

/// `Shown`はWMが`SetPresence`で最後に要求した表示状態（v2.50追加）。発見時は常に`true`
/// （`addWindow`が疑似OS上に置く時点で既に見えている窓を模しているため）。
type VirtualWindow = { Id: WindowId; Rect: PixelRect; Behavior: WindowBehavior; Shown: bool }

/// §12.1「`ScheduledSimEvent`」の最小形：`DelayedByMs`な`SetRect`が、`FireAtMs`に達するまで積まれる
/// 未発火の観測。`Seed`はまだ`WindowBehaviors`のどの挙動も使わないため持たない。
type ScheduledObservation = { FireAtMs: int64; Window: WindowId; Rect: PixelRect }

type VirtualWorld =
    { Windows: Map<WindowId, VirtualWindow>
      ClockMs: int64
      NextSampleSequence: int64
      Scheduled: ScheduledObservation list }

let empty: VirtualWorld = { Windows = Map.empty; ClockMs = 0L; NextSampleSequence = 0L; Scheduled = [] }

let addWindow (w: WindowId) (rect: PixelRect) (behavior: WindowBehavior) (world: VirtualWorld) : VirtualWorld =
    { world with Windows = Map.add w { Id = w; Rect = rect; Behavior = behavior; Shown = true } world.Windows }

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
/// ファイル冒頭コメント参照）、Effect適用スレッドの戻り値（`EffectCallReturned`の元。`Ok`の場合は
/// 発行時点のSampleSequence＝`DispatchSeq`も載せる、v2.42。`Placement.fs`の`Mismatch → Reevaluate`
/// コメント参照）を返す。`DispatchSeq`は観測と同じ`NextSampleSequence`カウンタから採番する
/// （Core.Placementの契約「観測スレッドとEffect適用スレッドが同一の単調カウンタを共有する」を、
/// ここでは1つのカウンタで素直に満たす）。
let applyEffect
    (now: int64)
    (world: VirtualWorld)
    (gated: GatedEffect)
    : VirtualWorld * InputEvent list * (RequestId * Result<unit, NativeCallRecord> * int64 option) list =
    match GatedEffect.effect gated with
    | SetRect (rid, w, rect) ->
        match Map.tryFind w world.Windows with
        | None ->
            // 対象窓が疑似OS側に存在しない（消滅後の遅延Effect等）。ERROR_INVALID_WINDOW_HANDLEとして返す
            // （8.1のclassify対応表と同じコード、§7.1「TargetGone」）。dispatchSeqは無し（発行自体が
            // 対象に届いていないので観測との照合対象にならない）。
            world, [], [ rid, Error { Api = "SetWindowPos"; Space = Win32LastError; Code = 1400; ElapsedUs = 0L }, None ]
        | Some vw ->
            match vw.Behavior with
            | Immediate ->
                let dispatchSeq = world.NextSampleSequence
                let sampleSeq = dispatchSeq + 1L
                let world' =
                    { world with
                        Windows = Map.add w { vw with Rect = rect } world.Windows
                        NextSampleSequence = sampleSeq + 1L }
                let observed =
                    WindowObserved
                        { emptySnapshot w with
                            VisibleFrameRect = Some { Value = Ok rect; SampleSequence = sampleSeq; MonotonicTime = now; Source = "virtual-world" } }
                world', [ observed ], [ rid, Ok (), Some dispatchSeq ]
            | DelayedByMs delayMs ->
                // 呼び出し自体は即座に成功する（SetWindowPosはOSに受理された時点で返る）。要求どおりの
                // 矩形の観測だけが`FireAtMs`まで遅れる（`advanceClock`参照）。
                let dispatchSeq = world.NextSampleSequence
                let world' =
                    { world with
                        Windows = Map.add w { vw with Rect = rect } world.Windows
                        NextSampleSequence = dispatchSeq + 1L
                        Scheduled = { FireAtMs = now + delayMs; Window = w; Rect = rect } :: world.Scheduled }
                world', [], [ rid, Ok (), Some dispatchSeq ]
    | SetPresence (rid, w, shown) ->
        match Map.tryFind w world.Windows with
        | None -> world, [], [ rid, Error { Api = "ShowWindow"; Space = Win32LastError; Code = 1400; ElapsedUs = 0L }, None ]
        | Some vw ->
            let dispatchSeq = world.NextSampleSequence
            let sampleSeq = dispatchSeq + 1L
            let world' =
                { world with
                    Windows = Map.add w { vw with Shown = shown } world.Windows
                    NextSampleSequence = sampleSeq + 1L }
            let presence = if shown then VisibleToUser else HiddenByWmMethod
            let observed =
                WindowObserved
                    { emptySnapshot w with
                        Presence = Some { Value = Ok presence; SampleSequence = sampleSeq; MonotonicTime = now; Source = "virtual-world" } }
            world', [ observed ], [ rid, Ok (), Some dispatchSeq ]
    | RequestForeground (rid, w) ->
        match Map.tryFind w world.Windows with
        | None -> world, [], [ rid, Error { Api = "SetForegroundWindow"; Space = Win32LastError; Code = 1400; ElapsedUs = 0L }, None ]
        | Some _ ->
            let dispatchSeq = world.NextSampleSequence
            let world' = { world with NextSampleSequence = dispatchSeq + 1L }
            let observed = ForegroundObserved { Value = Ok (Some w); SampleSequence = dispatchSeq; MonotonicTime = now; Source = "virtual-world" }
            world', [ observed ], [ rid, Ok (), Some dispatchSeq ]
    | ForgetWindow w ->
        // RequestIdを持たないfire-and-forgetなEffect（§7.1「ShellのHWND→WindowId対応表から外させる」）。
        // 疑似OS側にはHWND対応表という概念そのものが無いので、単にその窓を取り除くだけでよい
        // （Coreは`ForgetWindow`を発行した後、このWindowIdへは二度と観測を期待しない、R4-14）。
        { world with Windows = Map.remove w world.Windows }, [], []
    | _ -> world, [], [] // このラウンドで対象外のEffect種別（ファイル冒頭参照）

/// 疑似OSの仮想時計を`toMs`まで進め、`FireAtMs <= toMs`の予定観測を発火時刻の昇順で発火させる
/// （`Reducer`のTimerキューは別物であり、これは関与しない——ファイル冒頭コメント参照）。
let advanceClock (toMs: int64) (world: VirtualWorld) : VirtualWorld * InputEvent list =
    let due, notYet = world.Scheduled |> List.partition (fun s -> s.FireAtMs <= toMs)
    let dueSorted = due |> List.sortBy (fun s -> s.FireAtMs)
    let observed, nextSeq =
        dueSorted
        |> List.fold
            (fun (acc, sampleSeq) s ->
                let ev =
                    WindowObserved
                        { emptySnapshot s.Window with
                            VisibleFrameRect = Some { Value = Ok s.Rect; SampleSequence = sampleSeq; MonotonicTime = s.FireAtMs; Source = "virtual-world" } }
                acc @ [ ev ], sampleSeq + 1L)
            ([], world.NextSampleSequence)
    { world with
        ClockMs = max world.ClockMs toMs
        NextSampleSequence = nextSeq
        Scheduled = notYet },
    observed
