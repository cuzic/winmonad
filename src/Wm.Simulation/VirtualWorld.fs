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
///
/// **v2.51で`Reprobe`/`ObserveAll`/`RequestClose`の3つを実装**：v2.50の直後に`src/Wm.Core/*.fs`を
/// 再調査したところ、この3つも実際にはCoreが発行していた（`Reprobe (w, KTargetIntegrity)`は
/// `Placement.fs`のTargetIntegrity ProbeOnce規則が発行し続けている、`ObserveAll`は`Topology.fs`が
/// Grace期間突入時に発行する、`RequestClose`は`CloseFocused`コマンドが発行する）——「各Effectの対応が
/// BeliefKeyごとに違うので設計が要る」という上記の懸念は、実際には`Reprobe`が今日まで`KTargetIntegrity`
/// 以外で一度も呼ばれていないため当てはまらなかった（他のキーはまだ呼び出し元が存在しない、R4-24と
/// 同じ「まだ無い消費者向けに作り込まない」判断でそのまま`[]`のまま残す）。`VirtualWindow`に
/// `Integrity: IntegrityRelation`（既定`NotHigher`）を追加し、`Reprobe (w, KTargetIntegrity)`は
/// `TargetIntegrity`だけを運ぶ部分観測（§4.2「今回問い合わせた属性だけを運ぶ」に忠実：`VisibleFrameRect`
/// 等は含めない）を返す。`ObserveAll`は全窓についてRect/Presence/TargetIntegrityを運ぶ完全な
/// `WindowObserved`を返す（全体再列挙を模すため、こちらは部分観測ではない）。`RequestClose`は
/// 常に成功する（窓を取り除き`WindowRemoved`を返す）という最も単純な形だけを実装する——アプリが
/// 拒否する側（S17で実機確認済みの挙動）を模す`WindowBehavior`はまだ無いため、この既定だけを置く。
/// `ApplyBatch`/`ObserveWindow`は依然`[]`のまま（前者は子Effectの集約規則が要る、後者はCoreがまだ
/// 一度も発行していないため消費者が無い）。
///
/// **v2.74で`RestoreBeforeMove`を実装**：`Placement.dispatchPlacements`が最大化中のTiled窓への新しい
/// `SetRect`の直前に発行するようになった（§6.2/§7.1の`NeedsRestore`、CIスパイクS6bで実機確認した
/// `ShowWindowAsync(SW_RESTORE)`の効果）。`VirtualWindow`に`IsMaximized: bool`（既定`false`）を追加し、
/// `RestoreBeforeMove`はこれを`false`へ戻す（対象が疑似OS側に無ければ`SetRect`と同じ
/// `ERROR_INVALID_WINDOW_HANDLE`）。`ObserveAll`の完全観測にも`IsMaximized`を含めた。
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
open Wm.Contracts.Beliefs
open Wm.Contracts.CoreCommand
open Wm.Contracts.GatedEffect
open Wm.Simulation.WindowBehaviors

/// `Shown`はWMが`SetPresence`で最後に要求した表示状態（v2.50追加）。発見時は常に`true`
/// （`addWindow`が疑似OS上に置く時点で既に見えている窓を模しているため）。`Integrity`はWMと対象
/// プロセスの整合性レベルの関係（§4.5 TargetIntegrityのProbeOnce規則、v2.51追加）。発見時は常に
/// `NotHigher`（通常の非昇格テスト窓を模す既定——昇格窓を模すシナリオは`addWindow`呼び出し後に
/// `{ world with Windows = Map.add w { vw with Integrity = Higher } world.Windows }`で上書きする）。
/// `IsMaximized`（v2.74、§6.2/§7.1の`NeedsRestore`/`RestoreBeforeMove`）は発見時は常に`false`
/// （既定は非最大化窓——最大化窓を模すシナリオは`addWindow`呼び出し後に同じ要領で上書きする）。
/// `RestoreBeforeMove`を適用すると`false`へ戻る（CIスパイクS6bで確認した`ShowWindowAsync(SW_RESTORE)`
/// の実際の効果）。`FrameInset`（v2.113、architecture.md §4.2/§13「不可視リサイズ枠による恒常的
/// Mismatch」）は発見時は常に`noFrameInset`（既定は枠なし——不可視枠を持つ窓を模すシナリオは
/// `addWindow`呼び出し後に同じ要領で上書きする）。pad2py-pad-host実機（v2.112）で実測した
/// 「X+7/Width-14/Height-7/Y不変」をそのまま再現できる値。

/// 不可視リサイズ枠の推定（`Wm.Core.Placement`が将来持つ同名の信念と同じ形を想定、§4.2/§13）。
/// `NativeRect`（Win32のネイティブ矩形）が`VisibleFrameRect`（DWMの拡張フレーム矩形）より各辺
/// どれだけ外側に広いかを、辺ごとに独立して保持する——対称とは仮定しない。
type FrameInset = { Left: int; Top: int; Right: int; Bottom: int }

/// 既定値（枠なし）。この値を使う窓は、今までどおり`VisibleFrameRect`だけを運ぶ観測になる
/// （既存のシナリオ・テストへの影響が無いことを保証する既定）。
let noFrameInset : FrameInset = { Left = 0; Top = 0; Right = 0; Bottom = 0 }

/// `native`（要求どおり、またはOSが実際に設定したネイティブ矩形）に`inset`を適用し、
/// 実際に観測される`VisibleFrameRect`を計算する。
let private shrinkByInset (inset: FrameInset) (native: PixelRect) : PixelRect =
    { X = native.X + inset.Left
      Y = native.Y + inset.Top
      Width = native.Width - inset.Left - inset.Right
      Height = native.Height - inset.Top - inset.Bottom }

type VirtualWindow =
    { Id: WindowId
      Rect: PixelRect
      Behavior: WindowBehavior
      Shown: bool
      Integrity: IntegrityRelation
      IsMaximized: bool
      FrameInset: FrameInset }

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
    { world with
        Windows =
            Map.add
                w
                { Id = w; Rect = rect; Behavior = behavior; Shown = true; Integrity = NotHigher; IsMaximized = false; FrameInset = noFrameInset }
                world.Windows }

/// `addWindow`呼び出し後に不可視リサイズ枠を模す窓へ上書きする（`Integrity`/`IsMaximized`と同じ要領、
/// `VirtualWindow`の型直前のコメント参照）。対象窓が無ければ何もしない。
let withFrameInset (w: WindowId) (inset: FrameInset) (world: VirtualWorld) : VirtualWorld =
    match Map.tryFind w world.Windows with
    | Some vw -> { world with Windows = Map.add w { vw with FrameInset = inset } world.Windows }
    | None -> world

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
      IsMaximized = None
      Style = None
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
            // §7.1「事前確認→ASYNC呼出」をShellと共有する`executeEffectProtocol`（v2.59でテスト済み）
            // を通す（v2.60）。`probe`は`HungAfterMs`挙動だけを実際に判定し（§12.1：`startMs`以降は
            // `HungPending`、`start+5000ms`以降は`HungConfirmed`）、他の挙動は常に`Responsive`
            // （事前確認では引っかからない）。`dispatch`は既存の`Immediate`/`DelayedByMs`の適用ロジック
            // をそのまま持つ——`HungAfterMs`はハングが確定するまでの間（Responsive/HungPending）は
            // `Immediate`と同じ即座反映で模す（`executeEffectProtocol`は`HungPending`でも`dispatch`を
            // 呼ぶ、§7.1「判定未確定の間は呼出自体を止めない」）。`dispatch`自身は`Result`しか返せない
            // ので、更新後の`world`/観測/`dispatchSeq`はこの関数のローカルな可変変数で持ち出す
            // （呼び出し元には見えない、この関数内に閉じた実装都合）。
            let probe (_: WindowId) : ProbeOutcome =
                match vw.Behavior with
                | HungAfterMs (startMs, _) when now >= startMs + 5000L -> HungConfirmed
                | HungAfterMs (startMs, _) when now >= startMs -> HungPending (int (now - startMs))
                | _ -> Responsive
            let mutable dispatchedWorld = world
            let mutable dispatchedEvents: InputEvent list = []
            let mutable dispatchedSeq: int64 option = None
            let dispatch () : Result<unit, NativeCallRecord> =
                match vw.Behavior with
                | DelayedByMs delayMs ->
                    // 呼び出し自体は即座に成功する（SetWindowPosはOSに受理された時点で返る）。要求
                    // どおりの矩形の観測だけが`FireAtMs`まで遅れる（`advanceClock`参照）。
                    let dispatchSeq = world.NextSampleSequence
                    dispatchedWorld <-
                        { world with
                            Windows = Map.add w { vw with Rect = rect } world.Windows
                            NextSampleSequence = dispatchSeq + 1L
                            Scheduled = { FireAtMs = now + delayMs; Window = w; Rect = rect } :: world.Scheduled }
                    dispatchedSeq <- Some dispatchSeq
                    Ok ()
                | Immediate
                | HungAfterMs (_, _) when rect = vw.Rect ->
                    // Phase 0 CIスパイクS22/S23の設計コメントで実機確認済みの事実（これまでPlacementの
                    // 設計には結び付けられていなかった）：`SetWindowPos`の要求が現在のネイティブ矩形と
                    // 完全に同じ場合、`WM_WINDOWPOSCHANGED`/`EVENT_OBJECT_LOCATIONCHANGE`は発火しない
                    // （OSが冗長なレイアウト処理を省く——S22/S23が初期矩形をどのtargetとも異ならせている
                    // のは、まさにこの無反応を「遅延」と誤認しないための回避策だった）。Opus敵対的レビュー
                    // （opus-review-frame-inset.md論点3-B、v2.113）が指摘した「既に目標どおりの位置に
                    // いる窓へ同じ`SetRect`を再発行すると観測が一切来ずTimeout→Retry→諦めへ進み、次の
                    // Reconcileで同じ目標をまた再発行する」というループを再現するには、この無反応を
                    // 模す必要がある——`dispatchedEvents`を空のままにする（観測を一切生まない）。
                    dispatchedSeq <- Some world.NextSampleSequence
                    Ok ()
                | Immediate
                | HungAfterMs (_, _) ->
                    let dispatchSeq = world.NextSampleSequence
                    let sampleSeq = dispatchSeq + 1L
                    dispatchedWorld <-
                        { world with
                            Windows = Map.add w { vw with Rect = rect } world.Windows
                            NextSampleSequence = sampleSeq + 1L }
                    // `FrameInset`（v2.113、`VirtualWindow`の型直前のコメント参照）：既定(noFrameInset)
                    // の窓は今までどおりVisibleFrameRectだけを運ぶ観測になる（既存シナリオへの影響なし）。
                    // 枠を持つ窓は、要求どおりのネイティブ矩形（`rect`、実機でSetWindowPosがそのまま
                    // 適用した値に対応）と、それを縮めたVisibleFrameRectの両方を運ぶ——pad2py-pad-host
                    // 実機（v2.112）で実際に観測した「要求どおりのNativeRect・縮んだVisibleFrameRect」
                    // という形をそのまま再現する。
                    let visibleRect = shrinkByInset vw.FrameInset rect
                    let visibleObs = Some { Value = Ok visibleRect; SampleSequence = sampleSeq; MonotonicTime = now; Source = "virtual-world" }
                    let nativeObs =
                        if vw.FrameInset = noFrameInset then
                            None
                        else
                            Some { Value = Ok rect; SampleSequence = sampleSeq; MonotonicTime = now; Source = "virtual-world" }
                    dispatchedEvents <-
                        [ WindowObserved { emptySnapshot w with NativeRect = nativeObs; VisibleFrameRect = visibleObs } ]
                    dispatchedSeq <- Some dispatchSeq
                    Ok ()
                | MinimumWidth minWidthPx ->
                    // 要求幅が`minWidthPx`未満なら、観測される幅はそこへ丸められる（§12.1
                    // 「MinimumWidth(700)」、Phase 0スパイクS23で確認した「丸め済みの最終幅だけが
                    // 1回のLOCATIONCHANGEで届く」挙動と整合——中間の未丸め矩形の観測は無い）。
                    // このMismatch（要求r・観測o>r）が`Placement.reviseMinWidth`（v2.43）の証拠になる。
                    let enforcedRect = if rect.Width < minWidthPx then { rect with Width = minWidthPx } else rect
                    let dispatchSeq = world.NextSampleSequence
                    let sampleSeq = dispatchSeq + 1L
                    dispatchedWorld <-
                        { world with
                            Windows = Map.add w { vw with Rect = enforcedRect } world.Windows
                            NextSampleSequence = sampleSeq + 1L }
                    dispatchedEvents <-
                        [ WindowObserved
                            { emptySnapshot w with
                                VisibleFrameRect = Some { Value = Ok enforcedRect; SampleSequence = sampleSeq; MonotonicTime = now; Source = "virtual-world" } } ]
                    dispatchedSeq <- Some dispatchSeq
                    Ok ()
                | SelfRepositionAfterMs (delayMs, selfRect) ->
                    // 呼び出し自体は即座に成功するが、要求された`rect`は無視し、`delayMs`後に
                    // アプリが自分で選んだ`selfRect`へ動いたという観測だけが届く（§12.1
                    // 「SelfRepositionAfterMs(ms, rect)」。`Mismatch → Reevaluate`、v2.42の
                    // DispatchSeq/Baselineが実際に正しく働くかを確かめる現実のシナリオ）。
                    let dispatchSeq = world.NextSampleSequence
                    dispatchedWorld <-
                        { world with
                            Windows = Map.add w { vw with Rect = selfRect } world.Windows
                            NextSampleSequence = dispatchSeq + 1L
                            Scheduled = { FireAtMs = now + delayMs; Window = w; Rect = selfRect } :: world.Scheduled }
                    dispatchedSeq <- Some dispatchSeq
                    Ok ()
                | DisappearOnMove ->
                    // §12.1「DisappearOnMove」。`SetRect`の呼出自体は成功するが（OS受理）、ウィンドウは
                    // その要求に応えず即座に消滅する——観測としては`WindowObserved`ではなく
                    // `WindowRemoved`が1件届く。`DispatchSeq`を進める必要はない（この窓は以後
                    // 観測を生まない）。
                    dispatchedWorld <- { world with Windows = Map.remove w world.Windows }
                    dispatchedEvents <- [ WindowRemoved w ]
                    Ok ()
            match executeEffectProtocol probe dispatch w with
            | Ok () -> dispatchedWorld, dispatchedEvents, [ rid, Ok (), dispatchedSeq ]
            | Error record -> world, [], [ rid, Error record, None ]
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
    | RestoreBeforeMove (rid, w) ->
        // v2.74、§6.2/§7.1。`ShowWindowAsync(SW_RESTORE)`相当（CIスパイクS6bで確認済み：最大化窓を
        // 実際に非最大化状態へ戻す効果を持つ）。`SetRect`と同じ`callBoolNoCode`なので結果コードは
        // 無く、対象が疑似OS側に無ければ`SetRect`と同じ`ERROR_INVALID_WINDOW_HANDLE`(1400)を返す。
        match Map.tryFind w world.Windows with
        | None -> world, [], [ rid, Error { Api = "ShowWindowAsync"; Space = Win32LastError; Code = 1400; ElapsedUs = 0L }, None ]
        | Some vw -> { world with Windows = Map.add w { vw with IsMaximized = false } world.Windows }, [], [ rid, Ok (), None ]
    | Reprobe (w, KTargetIntegrity) ->
        match Map.tryFind w world.Windows with
        | None -> world, [], [] // Reprobeも`[]`返却に結果の通知先が無い（元の発行元はタイマーで再試行する）
        | Some vw ->
            let sampleSeq = world.NextSampleSequence
            let world' = { world with NextSampleSequence = sampleSeq + 1L }
            // §4.2「今回問い合わせた属性だけを運ぶ部分観測」に忠実に、`TargetIntegrity`だけを運ぶ
            // （`VisibleFrameRect`等は含めない——`ObserveAll`の全体再列挙とはここが違う）。
            let observed =
                WindowObserved
                    { emptySnapshot w with
                        TargetIntegrity = Some { Value = Ok vw.Integrity; SampleSequence = sampleSeq; MonotonicTime = now; Source = "virtual-world" } }
            world', [ observed ], []
    | Reprobe (w, KResponsiveness) ->
        // v2.85、§4.5 Responsivenessの再probeループ（v2.84で`Health`がこのEffectを発行するように
        // なった）に対する疑似OSの応答。`HungAfterMs`の`recoversAtMs`が無い（恒久的にハング）か
        // `now`がまだそれに達していなければ`PumpTimedOut 0L`（`Health.judgedHungThresholdUs`以下
        // なので`Hung`が再確認されるだけ）、`recoversAtMs`に達していれば`PumpResponded`で応じる。
        // `TargetIntegrity`と同じ部分観測（`Responsiveness`だけを運ぶ）。
        match Map.tryFind w world.Windows with
        | None -> world, [], []
        | Some vw ->
            let sampleSeq = world.NextSampleSequence
            let world' = { world with NextSampleSequence = sampleSeq + 1L }
            let sample =
                match vw.Behavior with
                | HungAfterMs (_, Some recoversAtMs) when now >= recoversAtMs -> PumpResponded
                | HungAfterMs (_, _) -> PumpTimedOut 0L
                | _ -> PumpResponded
            let observed =
                WindowObserved
                    { emptySnapshot w with
                        Responsiveness = Some { Value = Ok sample; SampleSequence = sampleSeq; MonotonicTime = now; Source = "virtual-world" } }
            world', [ observed ], []
    | Reprobe (_, _) ->
        // Coreは今のところ`KTargetIntegrity`/`KResponsiveness`以外のReprobeを一度も発行しない
        // （ファイル冒頭コメント参照）。対応する消費者が無いまま実装すると検証不能な死んだコードに
        // なるため、他のキーは対象外のまま。
        world, [], []
    | ObserveAll ->
        // 全窓の全体再列挙（§7.3、`ScreenPhase = Grace`突入時にTopologyが発行する）。`Reprobe`の部分観測
        // とは異なり、ここでは疑似OSが持つ属性を全て運ぶ完全な`WindowObserved`を返す。
        let observed, nextSeq =
            world.Windows
            |> Map.toList
            |> List.fold
                (fun (acc, sampleSeq) (w, vw) ->
                    let presence = if vw.Shown then VisibleToUser else HiddenByWmMethod
                    let ev =
                        WindowObserved
                            { emptySnapshot w with
                                VisibleFrameRect = Some { Value = Ok vw.Rect; SampleSequence = sampleSeq; MonotonicTime = now; Source = "virtual-world" }
                                Presence = Some { Value = Ok presence; SampleSequence = sampleSeq; MonotonicTime = now; Source = "virtual-world" }
                                TargetIntegrity = Some { Value = Ok vw.Integrity; SampleSequence = sampleSeq; MonotonicTime = now; Source = "virtual-world" }
                                IsMaximized = Some { Value = Ok vw.IsMaximized; SampleSequence = sampleSeq; MonotonicTime = now; Source = "virtual-world" } }
                    acc @ [ ev ], sampleSeq + 1L)
                ([], world.NextSampleSequence)
        { world with NextSampleSequence = nextSeq }, observed, []
    | RequestClose (rid, w) ->
        match Map.tryFind w world.Windows with
        | None -> world, [], [ rid, Error { Api = "PostMessage"; Space = Win32LastError; Code = 1400; ElapsedUs = 0L }, None ]
        | Some _ ->
            // 既定は常に成功する（窓が閉じる）という最も単純な形だけを実装する。アプリが`WM_CLOSE`を
            // 拒否する側（S17で実機確認済みの非blocking拒否）を模す`WindowBehavior`はまだ無いため、
            // 拒否のシナリオは専用のラウンドへ持ち越す（ファイル冒頭コメント参照）。
            { world with Windows = Map.remove w world.Windows }, [ WindowRemoved w ], [ rid, Ok (), None ]
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
