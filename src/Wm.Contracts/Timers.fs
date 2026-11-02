/// 決定6層6（ADR-019）：TimerId（各ドメインTimerサブDU含む）/TimerCommand/Response/RetryState → Timer実行器。
/// architecture.md §3.3（ADR-017）。combinator（awaitDeadline/nextRetry/debounce）は決定5のゲート拡大の対象として
/// ここに置く（ADR-019決定8）。
module Wm.Contracts.Timers

open Wm.Contracts.Identifiers

/// 再試行の記録（試行回数、最終試行時刻）。方針（何回まで、どの間隔で）は各信念の修正関数側に残す（v2.6）。
type RetryState = { AttemptCount: int; LastAttemptAt: int64 }

/// 同じIDのタイマーが動いていれば再始動する（debounceはこれで書ける）。CancelTimerは動いていなければ何もしない。
type TimerCommand<'TimerId> =
    | StartTimer of 'TimerId * durationMs: int64
    | CancelTimer of 'TimerId

/// Coreの部分状態機械が1入力ごとに返す形。'Actionは発行候補のEffect（gate前）。
type Response<'TimerId, 'Action> = {
    Actions: 'Action list
    TimerCommands: TimerCommand<'TimerId> list
}

// ===== 各ドメインのタイマーサブDU（§3.3） =====

type PlacementTimer =
    | PendingDeadline of RequestId
    | IntegrityReprobe of WindowId

type WindowsTimer =
    | ClassificationDeadline of WindowId

type TopologyTimer =
    | ScreenSettle
    | ScreenGrace
    | ReconfigurationCap

type FocusTimer =
    | WorkspaceSwitchSettle

type HealthTimer =
    | ResponsivenessReprobe of WindowId

/// 親のTimerId。TimerElapsedはケースで該当ドメインへ振り分ける（Elmの子Msgの持ち上げと同じ）。
/// ケース名（Placement/Windows/Topology/Focus/Health）がCoreのドメインモジュール名・`WmState`のフィールド名・
/// 将来の`Wm.Layouts`の`Placement`型・`Wm.Windows`プロジェクト名と衝突するため、`RequireQualifiedAccess`を
/// 付けて必ず`TimerId.Topology`のように書かせる（Opusレビュー5-1。付けない場合、F#の名前解決がopenの順序に
/// 依存して変わり、パターン`| Placement p ->`が意図しない型へ束縛される誤りが読みにくい形で起こり得る）。
[<RequireQualifiedAccess>]
type TimerId =
    | Placement of PlacementTimer
    | Windows of WindowsTimer
    | Topology of TopologyTimer
    | Focus of FocusTimer
    | Health of HealthTimer

// ===== combinator（ADR-019決定8。3つでドメインの多チック待ちを覆う） =====

/// 規約2（§3.3）：待ちをdebounceで書く。常に(再)始動のStartTimerを返し、
/// 「期限の正本」としてCore状態へ書き込むべき新しいdeadlineを返す。
let debounce (timerId: 'TimerId) (durationMs: int64) (now: int64) : TimerCommand<'TimerId> list * int64 =
    [ StartTimer(timerId, durationMs) ], now + durationMs

/// TimerElapsedが届いたとき、それが古い発火でないかをCore状態側のdeadlineと入力時刻で判定する
/// （§3.3「記録したDeadlineが入力時刻以下である場合だけ期限切れとして扱う」）。
/// 待ちが既に存在しない（None）場合も古い発火として扱う。
let awaitDeadline (recordedDeadline: int64 option) (inputTime: int64) : bool =
    match recordedDeadline with
    | Some deadline -> deadline <= inputTime
    | None -> false

/// 上限付き指数バックオフ（baseDelayMs * 2^AttemptCount、maxDelayMsで頭打ち）。上限回数に達したらNone（再probe打ち切り）。
let nextRetry (maxAttempts: int) (baseDelayMs: int64) (maxDelayMs: int64) (now: int64) (state: RetryState) : (RetryState * int64) option =
    if state.AttemptCount >= maxAttempts then
        None
    else
        let delay = min maxDelayMs (baseDelayMs * pown 2L state.AttemptCount)
        Some ({ AttemptCount = state.AttemptCount + 1; LastAttemptAt = now }, delay)

/// StartTimer/CancelTimerを処理する小さな表（同じIDの再始動・取消）。Windows版（Hostのタイマーサービス）と
/// Simulation版（仮想時計）が共有する純粋ロジック（§17「信念・タイマーの型の配置」）。
let applyTimerCommands (now: int64) (commands: TimerCommand<'TimerId> list) (table: Map<'TimerId, int64>) : Map<'TimerId, int64> =
    commands
    |> List.fold
        (fun t cmd ->
            match cmd with
            | StartTimer (id, durationMs) -> Map.add id (now + durationMs) t
            | CancelTimer id -> Map.remove id t)
        table
