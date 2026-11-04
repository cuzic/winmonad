/// 決定6層2（ADR-019）：ErrorSpace/NativeCallRecord/EffectFailure/classify（ADR-014）、
/// ProbeOutcome/executeEffectProtocol（§12.1）。Observation<'T>より前に必要。architecture.md §7.1/§8.1。
module Wm.Contracts.ErrorTypes

open Wm.Contracts.Identifiers

type ErrorSpace =
    | Win32LastError
    | HResult
    | BoolFalseNoCode

/// Shellが実測する生の呼出結果。ElapsedUsは常に0を入れない（v2.4、R2-22）。
type NativeCallRecord = {
    Api: string
    Space: ErrorSpace
    Code: int
    ElapsedUs: int64
}

/// v2.5でTargetIntegrityHigherを削除（ADR-015）。タグ"target-integrity-higher"は予約済み・再利用禁止（§9.2）。
type DenialKind =
    | ForegroundDenied
    | OtherDenial

type EffectFailure =
    | TargetGone
    | TargetHung
    | Denied of DenialKind
    | Transient
    | Unclassified

/// NativeCallRecordだけを読み、窓の状態・信念を読まない純粋関数（v2.6、R3-7で誤った言い換えを訂正）。
/// 対応表は(Api, Space, Code)の3つ組をキーにし、一部のエントリはElapsedUsも読む。
/// classifyはCoreのstep内で呼ぶ（Journalには結果だけを永続化し、Replay時に再計算する。ADR-004）。
let classify (record: NativeCallRecord) : EffectFailure =
    match record.Api, record.Space, record.Code with
    | "SendMessageTimeout", Win32LastError, 1460 (* ERROR_TIMEOUT *) ->
        // 判定前（ハング判定前にタイムアウト満了）はTransient、判定済みはTargetHung。
        // §7.1：ElapsedUsがtimeoutMsに近ければ判定前、0に近ければ判定済み。閾値は実測（F12）に基づき半分で分ける。
        if record.ElapsedUs <= 0L then TargetHung else Transient
    | _, Win32LastError, 1400 (* ERROR_INVALID_WINDOW_HANDLE *) -> TargetGone
    | _, Win32LastError, 5 (* ERROR_ACCESS_DENIED *) -> Denied OtherDenial
    // v2.94で追加（Phase B ラウンド3）：architecture.md §8.1の対応表。`SetForegroundWindow`は
    // `SetLastError`を立てないため失敗時は`BoolFalseNoCode`/`Code=0`で運ばれる（`callBoolNoCode`、
    // Native.fsの型直前のコメント参照）——Apiをワイルドカードにすると`ShowWindowAsync`（同じ
    // `BoolFalseNoCode`/`Code=0`の形だが「前は可視だったか」を返すだけで失敗シグナルではない、
    // `RestoreBeforeMove`の型直前のコメント参照）の戻り値を誤って`Denied`に分類してしまうため、
    // `SetForegroundWindow`だけに絞る。
    | "SetForegroundWindow", BoolFalseNoCode, 0 -> Denied ForegroundDenied
    | _ -> Unclassified

/// Shell/Simulationが共有する純粋な手順（§7.1「事前確認→ASYNC呼出」）。
type ProbeOutcome =
    | Responsive
    | HungConfirmed
    | HungPending of waitedMs: int

/// WindowIdの型は7.1のexecuteEffectProtocolが扱う対象窓を表すが、ここではIdentifiers.WindowIdを使う。
let executeEffectProtocol
    (probe: WindowId -> ProbeOutcome)
    (dispatch: unit -> Result<unit, NativeCallRecord>)
    (target: WindowId)
    : Result<unit, NativeCallRecord> =
    match probe target with
    | HungConfirmed -> Error { Api = "SendMessageTimeout"; Space = Win32LastError; Code = 1460; ElapsedUs = 0L }
    | Responsive
    | HungPending _ -> dispatch ()
