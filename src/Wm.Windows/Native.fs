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

// Phase B ラウンド5：`AppIdentity.Exe`（実行ファイルのフルパス）と、`TitleMatches`の元になる
// 生タイトル文字列（R4-8：生の文字列自体はWindowSnapshotへ一切載せない、呼び出し側で即座に
// TitleMatchVectorへ変換してから捨てる）を取得するためのAPI。
[<DllImport("user32.dll")>]
extern uint32 GetWindowThreadProcessId(IntPtr hWnd, uint32& lpdwProcessId)

[<DllImport("kernel32.dll", SetLastError = true)>]
extern IntPtr OpenProcess(uint32 dwDesiredAccess, bool bInheritHandle, uint32 dwProcessId)

[<DllImport("kernel32.dll")>]
extern bool CloseHandle(IntPtr hObject)

/// `PROCESS_QUERY_INFORMATION`では同一ユーザーでも上位整合性プロセスの取得に失敗する
/// （architecture.md §21、F24で実機確認済み）。`PROCESS_QUERY_LIMITED_INFORMATION`は必須。
[<DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)>]
extern bool QueryFullProcessImageNameW(IntPtr hProcess, uint32 dwFlags, System.Text.StringBuilder lpExeName, uint32& lpdwSize)

[<DllImport("user32.dll", CharSet = CharSet.Unicode)>]
extern int GetWindowTextW(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount)

let PROCESS_QUERY_LIMITED_INFORMATION = 0x1000u

// Phase B ラウンド7：ホットキー（architecture.md §8.1、Phase 0のF11で`Alt+J`等がCI実機で
// 登録できることを確認済み）と、実際にそのキーを押させる・結果の実フォアグラウンド窓を確認するAPI。
// `keybd_event`での合成は`VK_MENU`(Alt)を絶対に押さない——spikes/Phase0CiSpikes/Program.fsのS4の
// コメントが記録する実際のCI実機ハング（Altのbare keydownがWM_ENTERMENULOOPへ入り、
// `Application.DoEvents()`がそこから戻れなくなった）を再発させないため、このラウンドのテスト用
// ホットキーはCtrl+Shift+<key>に限定する。
[<DllImport("user32.dll")>]
extern bool RegisterHotKey(IntPtr hWnd, int id, uint32 fsModifiers, uint32 vk)

[<DllImport("user32.dll")>]
extern bool UnregisterHotKey(IntPtr hWnd, int id)

[<DllImport("user32.dll")>]
extern void keybd_event(byte bVk, byte bScan, uint32 dwFlags, UIntPtr dwExtraInfo)

[<DllImport("user32.dll")>]
extern IntPtr GetForegroundWindow()

let WM_HOTKEY = 0x0312
let MOD_CONTROL = 0x0002u
let MOD_SHIFT = 0x0004u
let MOD_ALT = 0x0001u
let KEYEVENTF_KEYUP = 2u
let VK_LCONTROL = 0xA2uy
let VK_LSHIFT = 0xA0uy
let VK_J = 0x4Auy
let VK_K = 0x4Buy
let VK_Q = 0x51u
let VK_1 = 0x31u
let VK_2 = 0x32u

// 常駐ループ化（`Wm.Host.ResidentLoop`、opus-review-resident-daemon.md）向けに追加。
let WM_DISPLAYCHANGE = 0x007Eu
let WM_QUERYENDSESSION = 0x0011u
let WM_ENDSESSION = 0x0016u

type ConsoleCtrlDelegate = delegate of uint32 -> bool

[<DllImport("kernel32.dll", SetLastError = true)>]
extern bool SetConsoleCtrlHandler(ConsoleCtrlDelegate handler, bool add)

let CTRL_C_EVENT = 0u
let CTRL_BREAK_EVENT = 1u
let CTRL_CLOSE_EVENT = 2u

[<DllImport("kernel32.dll", SetLastError = true)>]
extern IntPtr GetStdHandle(int nStdHandle)

[<DllImport("kernel32.dll", SetLastError = true)>]
extern bool GetConsoleMode(IntPtr hConsoleHandle, uint32& lpMode)

[<DllImport("kernel32.dll", SetLastError = true)>]
extern bool SetConsoleMode(IntPtr hConsoleHandle, uint32 dwMode)

let STD_INPUT_HANDLE = -10
let ENABLE_QUICK_EDIT_MODE = 0x0040u
let ENABLE_EXTENDED_FLAGS = 0x0080u

// Phase B ラウンド10：`RequestClose`（§8.4「窓を破壊・終了するEffectを提供しない」、WM_CLOSEによる
// 丁寧な要求——アプリは`WM_CLOSE`を無視・キャンセルできる）。`PostMessage`はキューへ投函するだけで
// 応答を待たない（`SendMessage`と違いハングした対象でも呼び出し側を止めない、7.1のADR-012と同じ
// 考え方）。`SetLastError=true`のBOOL成否シグナルなので`callBool`で包める。
[<DllImport("user32.dll", SetLastError = true)>]
extern bool PostMessage(IntPtr hWnd, uint32 Msg, UIntPtr wParam, IntPtr lParam)

[<DllImport("user32.dll")>]
extern bool IsWindow(IntPtr hWnd)

let WM_CLOSE = 0x0010u

// Phase B ラウンド14：E-4全画面検出の前提（architecture.md §21、opus-review-fullscreen-detection.md）
// 向けに追加。境界スタイル（WS_CAPTION/WS_THICKFRAME）の有無を読むためのAPI——
// spikes/Phase0CiSpikes/Program.fsのS28/S30（F36/F37）で検証済みのシグネチャをそのまま移す。
[<DllImport("user32.dll", SetLastError = true)>]
extern int GetWindowLong(IntPtr hWnd, int nIndex)

let GWL_STYLE = -16
let WS_CAPTION = 0x00C00000
let WS_THICKFRAME = 0x00040000
let WS_CHILD = 0x40000000

// 常駐ループ化（Opus敵対的相談`opus-review-resident-daemon.md`のP0-1/P0-2）向けに追加。
// P0-2「管理対象候補になるトップレベル窓かどうか」をShell側で判定するための属性
// （`WS_EX_TOOLWINDOW`、子窓判定の`GetAncestor`）と、P0-1「`IsDialog`/`Owner`が実装されずに
// `None`固定だった」を埋めるための属性（`GetWindow(GW_OWNER)`、ダイアログクラス名判定用の
// `GetClassName`、`WS_EX_DLGMODALFRAME`）。
let GWL_EXSTYLE = -20
let WS_EX_TOOLWINDOW = 0x00000080
let WS_EX_DLGMODALFRAME = 0x00000001

[<DllImport("user32.dll", SetLastError = true)>]
extern IntPtr GetAncestor(IntPtr hWnd, uint32 gaFlags)

let GA_ROOT = 2u

[<DllImport("user32.dll", SetLastError = true)>]
extern IntPtr GetWindow(IntPtr hWnd, uint32 uCmd)

let GW_OWNER = 4u

[<DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)>]
extern int GetClassNameW(IntPtr hWnd, Text.StringBuilder lpClassName, int nMaxCount)

/// spikes/Phase0CiSpikes/Program.fsの`suppressCrashDialogs`と同じ理由・同じシグネチャ：子プロセスの
/// 未処理例外がWindows Error Reporting UIを出すと、親の`StandardOutput.ReadLine()`を無期限に
/// 止める（ヘッドレスCIランナーには閉じる者がいない）。ラウンド14で子プロセスを初めて使うため移す。
[<DllImport("kernel32.dll")>]
extern uint32 SetErrorMode(uint32 uMode)

let SEM_FAILCRITICALERRORS = 0x0001u
let SEM_NOGPFAULTERRORBOX = 0x0002u

let DWMWA_EXTENDED_FRAME_BOUNDS = 9
let DWMWA_CLOAKED = 14
let MDT_EFFECTIVE_DPI = 0
let MONITORINFOF_PRIMARY = 0x00000001u
let MONITOR_DEFAULTTONEAREST = 2u
let SWP_NOZORDER = 0x0004u
let SWP_ASYNCWINDOWPOS = 0x4000u
let SW_RESTORE = 9
/// §21「ワークスペースの実装方式」v2.102でプロジェクトオーナーが最小化を採用。
let SW_MINIMIZE = 6

let elapsedUsOf (sw: Stopwatch) : int64 =
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
