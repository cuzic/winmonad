/// architecture.md §17/§8.1：P/Invokeはこのモジュールだけに閉じる。
///
/// Phase B ラウンド1（読み取りのみ）に必要な分だけを宣言する。シグネチャは
/// spikes/Phase0CiSpikes/Program.fs（windows-latest CI実機で検証済み）から移した——再導出しない。
/// `GetMonitorInfo`だけはスパイクに無かったので新規に追加した（標準的なP/Invokeシグネチャ）。
/// `SetRect`/`RequestForeground`等を実際にOSへ発行するEffect系API（`SetWindowPos`/
/// `SetForegroundWindow`等）はこのラウンドでは一切呼ばない——意図的に未使用のまま持ち越す
/// （計画の「このラウンドで対象外にしたこと」参照）。
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

let DWMWA_EXTENDED_FRAME_BOUNDS = 9
let DWMWA_CLOAKED = 14
let MDT_EFFECTIVE_DPI = 0
let MONITORINFOF_PRIMARY = 0x00000001u
let MONITOR_DEFAULTTONEAREST = 2u

/// §8.1の`callBool`パターンの最小版：BOOLを返すだけの読み取り専用呼出を`Result`へ包む
/// （`SetLastError=true`の呼出だけが対象。副作用のあるEffect系APIの誤り分類（`classify`、
/// ADR-014）はこのラウンドでは一切発行しないため対象外）。
let callBoolRead (api: string) (f: unit -> bool) : Result<unit, NativeCallRecord> =
    let sw = Stopwatch.StartNew()
    let ok = f ()
    sw.Stop()
    let elapsedUs = max 1L (sw.ElapsedTicks * 1_000_000L / Stopwatch.Frequency) // R2-22：0を入れない
    if ok then
        Ok ()
    else
        let code = Marshal.GetLastWin32Error()
        Error { Api = api; Space = Win32LastError; Code = code; ElapsedUs = elapsedUs }
