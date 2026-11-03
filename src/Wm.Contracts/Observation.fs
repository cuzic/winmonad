/// 決定6層3（ADR-019）：Observation/ObservedPresence等。architecture.md §4.2。
module Wm.Contracts.Observation

open Wm.Contracts.Identifiers
open Wm.Contracts.ErrorTypes

/// 1回のOS呼出の生の結果。「まだ観測していない」（option = None）と
/// 「観測を試みたが失敗した」（Some { Value = Error ... }）を区別する（v2.4、ADR-014/1-9）。
type Observation<'T> = {
    Value: Result<'T, NativeCallRecord>
    /// Shellが全ての観測に取得時点で付ける単一の単調増加番号。信念の新旧比較（LatestWins）はこれだけで行う（v2.6、R4-10）。
    SampleSequence: int64
    MonotonicTime: int64
    Source: string
}

/// UWP/WinUIのように「実アプリの実行ファイル」が1つに定まらない対象のため、導出元ごとに別の観測として持つ（v2.5）。
type AppIdSource =
    | Exe
    | HostExe
    | Aumid
    | CoreWindowExe

type AppIdentity = {
    Exe: Observation<string> option
    HostExe: Observation<string> option
    Aumid: Observation<string> option
    CoreWindowExe: Observation<string> option
}

/// v2.6で位置づけを変更：分類は直接読まず、Movabilityの信念の事前証拠とExplainの材料にする（ADR-015）。
type IntegrityRelation =
    | NotHigher
    | Higher

/// PIDは再利用されるので作成時刻と組にする（4.5の再probeの発火条件）。
type ProcessKey = { Pid: int; CreationTime: int64 }

/// DWMWA_CLOAKEDの理由ビットを捨てない（v2.6、R3-8）。全てfalse＝Cloakされていない。
type CloakState = { App: bool; Shell: bool; Inherited: bool }

/// IsHungAppWindowは「ゴーストウィンドウは常にTRUE」なので、生の結果は観測として残し、
/// ハングかどうかの判断は信念（Beliefs.fs）で行う。
type ResponsivenessSample =
    | PumpResponded
    | PumpTimedOut of elapsedUs: int64
    | IsHungAppWindowTrue

type DpiAwarenessDeclared =
    | Unaware
    | SystemAware
    | PerMonitor
    | PerMonitorV2

/// Shellが自分の非表示方式で判定した、方式に依存しない表示状態（v2.6、R5-2）。
type ObservedPresence =
    | VisibleToUser
    | HiddenByWmMethod
    | HiddenOther

/// 生のタイトル文字列はWindowSnapshotに載せない（v2.6、R4-8）。採用中Policyの全TitleContainsパターンに対する一致ビット列だけを運ぶ。
type TitleMatchVector = { PatternSetHash: string; Matches: Set<int> }

/// 「今回Shellが問い合わせた属性だけ」を運ぶ部分観測。問い合わせなかったフィールドはNone（v2.6、ADR-016）。
type WindowSnapshot = {
    Id: WindowId
    Process: Observation<ProcessKey> option
    AppIdentity: AppIdentity
    Owner: Observation<WindowId option> option
    IsDialog: Observation<bool> option
    NativeRect: Observation<PixelRect> option
    VisibleFrameRect: Observation<PixelRect> option
    IsVisible: Observation<bool> option
    IsMinimized: Observation<bool> option
    /// 最大化状態（`IsZoomed`相当、v2.74。§6.2/§7.1の`NeedsRestore`/`RestoreBeforeMove`の生産元）。
    IsMaximized: Observation<bool> option
    Cloak: Observation<CloakState> option
    Monitor: Observation<MonitorId> option
    MinWidthHint: Observation<int> option
    TargetIntegrity: Observation<IntegrityRelation> option
    Responsiveness: Observation<ResponsivenessSample> option
    Presence: Observation<ObservedPresence> option
    DpiAwareness: Observation<DpiAwarenessDeclared> option
    TitleMatches: Observation<TitleMatchVector> option
}
