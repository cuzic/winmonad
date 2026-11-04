/// architecture.md §17：WinEvent購読（Phase B ラウンド2）。`SetWinEventHook`はspikes/Phase0CiSpikes/
/// Program.fs（S20/S20b/S22/S23/S24/S25、windows-latest実機で検証済み）の各スパイクが使う方式と
/// 同じ——ただしスパイクはイベント種別ごとに定数をローカル再定義していた（散らばっていた）のに対し、
/// このファイルはプロダクションコードとして1箇所に集約する。値自体はwinuser.hの公開済み定数（実行時
/// の挙動だけがCI実機検証の対象——計画の「単一スレッドで新しい同期が要らない」参照）。
module Wm.Windows.EventHooks

open System
open System.Runtime.InteropServices

type WinEventProc = delegate of IntPtr * uint32 * IntPtr * int * int * uint32 * uint32 -> unit

[<DllImport("user32.dll", SetLastError = true)>]
extern IntPtr SetWinEventHook(uint32 eventMin, uint32 eventMax, IntPtr hmodWinEventProc, WinEventProc lpfnWinEventProc, uint32 idProcess, uint32 idThread, uint32 dwFlags)

[<DllImport("user32.dll")>]
extern bool UnhookWinEvent(IntPtr hWinEventHook)

let WINEVENT_OUTOFCONTEXT = 0x0000u
let OBJID_WINDOW = 0

let EVENT_SYSTEM_FOREGROUND = 0x0003u
let EVENT_SYSTEM_MOVESIZESTART = 0x000Au
let EVENT_SYSTEM_MOVESIZEEND = 0x000Bu
let EVENT_SYSTEM_MINIMIZESTART = 0x0016u
let EVENT_SYSTEM_MINIMIZEEND = 0x0017u
let EVENT_OBJECT_CREATE = 0x8000u
let EVENT_OBJECT_DESTROY = 0x8001u
let EVENT_OBJECT_SHOW = 0x8002u
let EVENT_OBJECT_HIDE = 0x8003u
let EVENT_OBJECT_CLOAKED = 0x8017u
let EVENT_OBJECT_UNCLOAKED = 0x8018u
let EVENT_OBJECT_LOCATIONCHANGE = 0x800Bu

/// 登録した全フックをまとめて外すためのハンドル。`KeepAlive`は、ネイティブ側へ渡した
/// `WinEventProc`デリゲート自身をGCされないよう保持するためだけに存在する——呼び出し元が
/// `HookSet`を`Unhook`を呼ぶまで生き続けさせれば、このフィールド経由でデリゲートも生き続ける
/// （実機CI実機で実際に踏んだ不具合：`hookEvents`のローカル変数だけに積んでいた当初の実装は
/// 関数の戻り後にGCされ、"A callback was made on a garbage collected delegate"で異常終了した）。
type HookSet = { Unhook: unit -> unit; KeepAlive: obj }

/// `onEvent eventType hwnd`を、トップレベル窓自身への通知(`idObject = OBJID_WINDOW`)だけに絞って呼ぶ。
/// 子要素(ボタン等)やキャレット等の通知は無視する——spikes/Phase0CiSpikes/Program.fsの全スパイクも
/// 同じ絞り込みをしている（`idChild`は見ない、同ファイルのどのスパイクも使っていない）。
/// `SetWinEventHook`は1呼出につき連続した[eventMin, eventMax]区間しか登録できないため、
/// 対象イベントの集合を連続区間へグルーピングして複数回呼ぶ。
let hookEvents (onEvent: uint32 -> IntPtr -> unit) : HookSet =
    let delegates = ResizeArray<WinEventProc>()
    let handles = ResizeArray<IntPtr>()

    let hook (eventMin: uint32) (eventMax: uint32) =
        let proc = WinEventProc(fun _hWinEventHook eventType hwnd idObject _idChild _idEventThread _dwmsEventTime ->
            if idObject = OBJID_WINDOW then onEvent eventType hwnd)
        delegates.Add(proc)
        let h = SetWinEventHook(eventMin, eventMax, IntPtr.Zero, proc, 0u, 0u, WINEVENT_OUTOFCONTEXT)
        handles.Add(h)

    hook EVENT_OBJECT_CREATE EVENT_OBJECT_HIDE // CREATE(0x8000)..HIDE(0x8003)の連続区間
    hook EVENT_SYSTEM_MINIMIZESTART EVENT_SYSTEM_MINIMIZEEND
    hook EVENT_OBJECT_CLOAKED EVENT_OBJECT_UNCLOAKED
    hook EVENT_SYSTEM_FOREGROUND EVENT_SYSTEM_FOREGROUND
    hook EVENT_SYSTEM_MOVESIZESTART EVENT_SYSTEM_MOVESIZEEND

    { Unhook = fun () -> for h in handles do UnhookWinEvent(h) |> ignore
      KeepAlive = box delegates }

/// Phase B ラウンド4：`EVENT_OBJECT_LOCATIONCHANGE`専用の購読。`hookEvents`には含めない——
/// 全プロセス相手に購読すると系内の全窓の移動・リサイズ毎に発火し流量が大きすぎる（ラウンド1/2の
/// アンビエント全窓観測はこの頻度のイベントを診断用途で受ける理由が無い）。`idProcess`に自プロセスの
/// PIDを渡すことで、自分で作った使い捨てテスト窓（ラウンド3/4が対象とする、architecture.md §7.1の
/// 「隔離された自己所有窓だけ」の方針）だけに絞り込む。
let hookLocationChangeForProcess (idProcess: uint32) (onEvent: uint32 -> IntPtr -> unit) : HookSet =
    let proc = WinEventProc(fun _hWinEventHook eventType hwnd idObject _idChild _idEventThread _dwmsEventTime ->
        if idObject = OBJID_WINDOW then onEvent eventType hwnd)
    let h = SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE, IntPtr.Zero, proc, idProcess, 0u, WINEVENT_OUTOFCONTEXT)
    { Unhook = fun () -> UnhookWinEvent(h) |> ignore
      KeepAlive = box proc }

/// Phase B ラウンド8：単一のイベント種別だけをシステム全体で購読する（`hookEvents`の中の`hook`と
/// 同じ形だが、呼び出し側が1件だけ欲しい場合向けの公開版——`EVENT_SYSTEM_FOREGROUND`を特定の
/// 自作テスト窓だけに絞ってCoreへ中継する用途で使う、呼び出し側がhwndで絞り込む）。
let hookSingleEvent (event: uint32) (onEvent: uint32 -> IntPtr -> unit) : HookSet =
    let proc = WinEventProc(fun _hWinEventHook eventType hwnd idObject _idChild _idEventThread _dwmsEventTime ->
        if idObject = OBJID_WINDOW then onEvent eventType hwnd)
    let h = SetWinEventHook(event, event, IntPtr.Zero, proc, 0u, 0u, WINEVENT_OUTOFCONTEXT)
    { Unhook = fun () -> UnhookWinEvent(h) |> ignore
      KeepAlive = box proc }
