/// architecture.md §17/§8.1：P/Invokeはこのモジュールだけに閉じる。
///
/// Phase B ラウンド1（読み取りのみ）に必要な分だけを宣言していた。シグネチャは
/// spikes/Phase0CiSpikes/Program.fs（windows-latest CI実機で検証済み）から移した——再導出しない。
/// `GetMonitorInfo`だけはスパイクに無かったので新規に追加した（標準的なP/Invokeシグネチャ）。
/// v2.94（Phase B ラウンド3）で、実際にOSへEffectを発行するAPI（`SetWindowPos`/
/// `SetForegroundWindow`/`ShowWindowAsync`/`IsHungAppWindow`）と、architecture.md §8.1が指定する
/// `callBool`/`callBoolNoCode`/`callHr`の3つの変換点を追加した。
module Wm.Windows.Native

open System
open System.Diagnostics
open System.Runtime.InteropServices
open Wm.Contracts.ErrorTypes

[<StructLayout(LayoutKind.Sequential)>]
type RECT =
    struct
        val mutable Left: int
        val mutable Top: int
        val mutable Right: int
        val mutable Bottom: int
    end

[<StructLayout(LayoutKind.Sequential)>]
type MONITORINFO =
    struct
        val mutable cbSize: int
        val mutable rcMonitor: RECT
        val mutable rcWork: RECT
        val mutable dwFlags: uint32
    end

type EnumChildProc = delegate of IntPtr * IntPtr -> bool
type MonitorEnumProc = delegate of IntPtr * IntPtr * IntPtr * IntPtr -> bool

[<DllImport("user32.dll")>]
extern bool EnumWindows(EnumChildProc lpEnumFunc, IntPtr lParam)

[<DllImport("user32.dll")>]
extern bool IsWindowVisible(IntPtr hWnd)

[<DllImport("user32.dll")>]
extern bool IsIconic(IntPtr hWnd)

[<DllImport("user32.dll")>]
extern bool IsZoomed(IntPtr hWnd)

[<DllImport("user32.dll", SetLastError = true)>]
extern bool GetWindowRect(IntPtr hWnd, RECT& lpRect)

[<DllImport("dwmapi.dll")>]
extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, RECT& pvAttribute, int cbAttribute)

/// `DwmGetWindowAttribute`の別バインディング(同じ実体を4バイトDWORD出力で呼ぶ、`DWMWA_CLOAKED`用)。
/// spikes/Phase0CiSpikes/Program.fsの`DwmGetWindowAttributeInt`と同じ形。
[<DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")>]
extern int DwmGetWindowAttributeInt(IntPtr hwnd, int dwAttribute, int& pvAttribute, int cbAttribute)

[<DllImport("user32.dll")>]
extern IntPtr MonitorFromWindow(IntPtr hWnd, uint32 dwFlags)

[<DllImport("user32.dll")>]
extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData)

[<DllImport("user32.dll", CharSet = CharSet.Unicode)>]
extern bool GetMonitorInfo(IntPtr hMonitor, MONITORINFO& lpmi)

[<DllImport("shcore.dll")>]
extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, uint32& dpiX, uint32& dpiY)

// v2.94（Phase B ラウンド3）：実際にOSへ変更を加えるEffect系API。
[<DllImport("user32.dll", SetLastError = true)>]
extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint32 uFlags)

/// `SetForegroundWindow`/`ShowWindowAsync`は`SetLastError`を立てない（失敗時は単にBOOL `false`を
/// 返すだけで、`GetLastWin32Error()`は無関係・陳腐化した値を運ぶ——`callBoolNoCode`はこのために
/// 分けてある、v2.93で`IsWindowVisible`等へ`callBoolRead`相当のロジックを誤用して実際に踏んだ
/// バグと同じ種類の罠）。
[<DllImport("user32.dll")>]
extern bool SetForegroundWindow(IntPtr hWnd)

[<DllImport("user32.dll")>]
extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow)

/// ゴーストウィンドウ（OSが代理表示する応答不能窓の影武者）では常にtrueを返す既知の挙動がある
/// （architecture.md §4.5 Responsivenessの型直前のコメント参照）——ここでは§8.1の`probe`として、
/// ブロッキングしない最速の事前確認にだけ使う（`SendMessageTimeout`のような実際に待つ確認は
/// このラウンドでは発行しない、計画の「対象外にしたこと」参照）。
[<DllImport("user32.dll")>]
extern bool IsHungAppWindow(IntPtr hwnd)

let DWMWA_EXTENDED_FRAME_BOUNDS = 9
let DWMWA_CLOAKED = 14
let MDT_EFFECTIVE_DPI = 0
let MONITORINFOF_PRIMARY = 0x00000001u
let MONITOR_DEFAULTTONEAREST = 2u
let SWP_NOZORDER = 0x0004u
let SWP_ASYNCWINDOWPOS = 0x4000u
let SW_RESTORE = 9

let private elapsedUsOf (sw: Stopwatch) : int64 =
    max 1L (sw.ElapsedTicks * 1_000_000L / Stopwatch.Frequency) // R2-22：0を入れない

/// §8.1の`callBool`パターンの最小版：BOOLを返すだけの読み取り専用呼出を`Result`へ包む
/// （`SetLastError=true`の呼出だけが対象。副作用のあるEffect系APIの誤り分類（`classify`、
/// ADR-014）はこのラウンドでは一切発行しないため対象外）。
let callBoolRead (api: string) (f: unit -> bool) : Result<unit, NativeCallRecord> =
    let sw = Stopwatch.StartNew()
    let ok = f ()
    sw.Stop()
    if ok then
        Ok ()
    else
        let code = Marshal.GetLastWin32Error()
        Error { Api = api; Space = Win32LastError; Code = code; ElapsedUs = elapsedUsOf sw }

/// architecture.md §8.1「P/Invoke呼出しの変換点」（ADR-014/4-1）：`SetLastError=true`を立てる
/// BOOL成否シグナルAPI向け（`SetWindowPos`/`SendMessageTimeout`）。`callBoolRead`と形は同じだが、
/// 「読み取り専用（falseは正当な回答）」ではなく「失敗シグナルとしてのfalse」専用であることを
/// 名前で区別する（v2.93で踏んだ誤用を再発させないため）。
let callBool (api: string) (f: unit -> bool) : Result<unit, NativeCallRecord> = callBoolRead api f

/// `SetLastError`を立てないBOOL失敗シグナルAPI向け（`SetForegroundWindow`/`ShowWindowAsync`）。
/// 失敗時は`GetLastWin32Error()`を読まず、`BoolFalseNoCode`/`Code=0`で運ぶ（`classify`が
/// `SetForegroundWindow`だけをこの形で特別扱いする、ErrorTypes.fsの型直前のコメント参照）。
let callBoolNoCode (api: string) (f: unit -> bool) : Result<unit, NativeCallRecord> =
    let sw = Stopwatch.StartNew()
    let ok = f ()
    sw.Stop()
    if ok then Ok ()
    else Error { Api = api; Space = BoolFalseNoCode; Code = 0; ElapsedUs = elapsedUsOf sw }

/// HRESULTを返すAPI向け（`DwmGetWindowAttribute`等）。負値（失敗）をそのままHRESULTコードとして運ぶ。
let callHr (api: string) (f: unit -> int) : Result<unit, NativeCallRecord> =
    let sw = Stopwatch.StartNew()
    let hr = f ()
    sw.Stop()
    if hr >= 0 then Ok ()
    else Error { Api = api; Space = HResult; Code = hr; ElapsedUs = elapsedUsOf sw }
