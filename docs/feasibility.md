# Phase 0 技術的前提の実現性調査

本調査は `docs/architecture.md` の各ADR・節で前提とされているWindows固有のAPI挙動について、Microsoft公式ドキュメント（learn.microsoft.com）、既存のWindows用タイル型WM（[komorebi](https://github.com/LGUG2Z/komorebi)、[GlazeWM](https://github.com/glzr-io/glazewm)）の実装・issue、その他信頼できる技術情報を調査し、設計書の前提の妥当性を評価するものである。コードは書いていない。

komorebi・GlazeWMについては、GitHub Code Search（`gh api search/code`）で実際のソースコードを特定し、該当箇所を直接引用した（以下、特記のない限り「komorebiの実装」「GlazeWMの実装」は実際に読んだソースコードに基づく）。

---

## 1. RegisterHotKeyの競合

### 仕様
- [`RegisterHotKey`](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerhotkey) の公式ドキュメントは、`MOD_WIN` フラグの説明として明記している：「Keyboard shortcuts that involve the WINDOWS key are reserved for use by the operating system.」（Winキーを使うキーボードショートカットはOSのために予約されている）。
- 戻り値は失敗時0、成功時非0。失敗時は `GetLastError` で詳細情報を取得する。ドキュメント本文：「Typically, RegisterHotKey also fails if the keystrokes specified for the hot key have already been registered for another hot key.」
- 典型的な失敗コードは `ERROR_HOTKEY_ALREADY_REGISTERED`（1409）。これはMicrosoft公式リファレンスページには明記がないが、Win32バインディング各種（例：Dartのwin32パッケージ）や複数のMicrosoft Q&Aスレッドで一貫して報告されている値である。
- [`WM_HOTKEY`](https://learn.microsoft.com/en-us/windows/win32/inputdev/wm-hotkey) のドキュメントにも同様の文言「Hotkeys that involve the Windows key are reserved for use by the operating system.」が繰り返される。

### 実務上の注意
- Microsoft Q&A（[エラー報告例](https://learn.microsoft.com/en-us/answers/questions/1020405/error-while-registering-the-hotkey-in-c)）では `Win+D` の登録で1409エラーが発生したと報告されている。
- `Win+L`（ロック）は特殊で、低レベルフックでもブロックできない。AutoItフォーラムの報告（[該当スレッド](https://autoitscript.com/forum/topic/90492-hotkey-udf/page/5)）では「WinKeyを無効化してもWin+Lのブロックは不可能」としている。`HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System` の `DisableLockWorkstation` レジストリ値はロック画面の表示を止めるだけで、キー自体の内部処理を止めるものではない可能性があるとMicrosoft Q&A（[Blocking windows hotkeys in an application](https://learn.microsoft.com/en-us/answers/questions/1286619/blocking-windows-hotkeys-in-an-application)）でも議論されている。
- GlazeWMのGitHub Discussion [#1206](https://github.com/glzr-io/glazewm/discussions/1206)（"solve hot key conflict when using win as 'meta'"）では、`Win+E`・`Win+V`・`Win+Shift+S` 等のExplorer標準ショートカットとの競合が実際に報告されており、ユーザーはPowerShellスクリプトでOS標準のWinキーショートカットを無効化する回避策を取っている。また、modキーに `win` ではなく `lwin`（左Winキー限定）を指定する必要があったという報告もある。
- GlazeWMのissue #589（"Start menu opens when hitting Windows + R or any other Windows key combination"）が存在する（本調査では issue 本文の現在のステータスまでは確認できていない）。
- AutoHotkeyは `RegisterHotKey` が失敗した場合に低レベルキーボードフックへ自動的にフォールバックする実装になっているとの報告がある（PureBasicフォーラム）。

### 設計書への影響
B-2（8.1節）の記述「`Win+1..9`等の多くの組み合わせはExplorerが既に登録しており、`RegisterHotKey`は失敗する」は、公式ドキュメントの「WindowsキーのショートカットはOSのために予約されている」という文言および実例（GlazeWM Discussion #1206）と整合しており、**前提は正しい**。「`Win+L`等は原理的に奪えない」という記述も、Win+Lが低レベルフックでも介入困難という実務報告と整合する。

Phase 0のスパイクで確認すべき具体項目：
1. 実際に使いたいキーバインド（xmonad風の `Mod+j/k/Return/Shift+数字`）を一つずつ `RegisterHotKey` で試し、失敗するものを列挙する。
2. Modキーとして `Win`（`MOD_WIN`）を使う場合、GlazeWMの報告のように `lwin`/`rwin` 個別指定が必要か確認する。
3. `RegisterHotKey` で確保できないキーについて、`WH_KEYBOARD_LL` へのフォールバック（本書2章参照）を前提に設計する。

---

## 2. SetForegroundWindowの許可条件

### 仕様
[`SetForegroundWindow`](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setforegroundwindow) の公式ドキュメントは許可条件を明確にリストしている。

以下の条件が**すべて**真であること：
- 呼び出し元プロセスがデスクトップアプリケーションである（UWP/Windows 8/8.1ストアアプリではない）。
- フォアグラウンドプロセスが `LockSetForegroundWindow` で `SetForegroundWindow` 呼び出しを無効化していない。
- メニューがアクティブでない。

加えて、以下の**いずれか**が真であること：
- フォアグラウンドロックのタイムアウトが経過している（`SPI_GETFOREGROUNDLOCKTIMEOUT`）。
- 呼び出し元プロセスがフォアグラウンドプロセスである。
- 呼び出し元プロセスがフォアグラウンドプロセスによって起動された。
- 現在フォアグラウンドウィンドウが存在しない。
- 呼び出し元プロセスが直前の入力イベントを受け取った。
- フォアグラウンドプロセスまたは呼び出し元プロセスがデバッグされている。

ドキュメントはさらに「これらの条件を満たしてもシステムがフォアグラウンド設定の権利を拒否することがある（It is possible for a process to be denied the right to set the foreground window even if it meets these conditions）」と明記しており、**条件を満たすことは成功の必要条件だが十分条件ではない**。

### 実務上の注意（既存WMの回避策）
設計書が想定する「ホットキー受信→キュー→Reducer→Journal→Effectという非同期経路を経た後でも許可が残っているか」という問いについて、komorebi・GlazeWMの実装は、非同期経路後にそのままでは許可が落ちることを前提に、能動的な回避策を講じている。

**komorebi**（`komorebi/src/windows_api.rs`、`raise_and_focus_window` 関数、[該当ファイル](https://github.com/LGUG2Z/komorebi/blob/72aa4c97ade72b802a008bdba4bf59cca04df665/komorebi/src/windows_api.rs)）：
```rust
pub fn raise_and_focus_window(hwnd: isize) -> eyre::Result<()> {
    let event = [INPUT { r#type: INPUT_MOUSE, ..Default::default() }];
    unsafe {
        // Send an input event to our own process first so that we pass the
        // foreground lock check
        SendInput(&event, size_of::<INPUT>() as i32);
        let _ = SetWindowPos(HWND(as_ptr!(hwnd)), Option::from(HWND_TOP), 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW | SWP_ASYNCWINDOWPOS).process();
        SetForegroundWindow(HWND(as_ptr!(hwnd)))
    }.ok().process()
}
```
自分自身のプロセスへ `SendInput` でダミーの入力イベント（マウスイベント）を送出し、「直前に入力イベントを受け取ったプロセス」の条件を満たしてから `SetForegroundWindow` を呼ぶ、という回避策である。

さらにkomorebiは起動時に `foreground_lock_timeout` 関数で `SPI_GETFOREGROUNDLOCKTIMEOUT`／`SPI_SETFOREGROUNDLOCKTIMEOUT` を用いてシステム全体の `ForegroundLockTimeout` を0に変更し、「キーボード駆動のフォーカス更新を有効にするため」というコメントを付けている。これはユーザーのシステム設定をWM自身が変更する、副作用の大きい回避策である。

**GlazeWM**（`packages/wm-platform/src/platform_impl/windows/native_window.rs`、[該当ファイル](https://github.com/glzr-io/glazewm/blob/5709ad0a3c7c386bbc3e38166a865ffc12937515/packages/wm-platform/src/platform_impl/windows/native_window.rs)）も同様に、`SendInput` でダミー入力を送出した直後に `SetForegroundWindow` を呼ぶパターンを採用している（299〜304行目）。

一般的なWin32開発者コミュニティ（DevXのTips、CodeProjectの議論）では、`AttachThreadInput` で呼び出し元スレッドの入力状態を対象スレッドに一時的に関連付けてから `SetForegroundWindow`/`SetFocus` を呼ぶパターンも紹介されているが、`AttachThreadInput` はデッドロックを引き起こす報告がある（PureBasicフォーラム）ため、komorebi/GlazeWMはこちらではなく `SendInput` 方式を採用していると見られる。

### 設計書への影響
B-3（8.1節）の記述「ホットキー受信から『キュー→Reducer→Journal→Effect』という非同期経路を経た後でも許可が残っているかは未検証」という問題設定は正しく、かつ**実際に許可が落ちるケースが存在する**ことがkomorebi/GlazeWMの実装（自己SendInputでの回避策の必要性）から裏付けられる。設計書が言う「既存のWindows用タイル型WMで一般的な回避策（キー送出等）」は、具体的には「自分自身へのダミー入力送出」であり、`AttachThreadInput` ではない。

Phase 0のスパイクで確認すべき具体項目：
1. 非同期経路を経た `RequestForeground` Effectで素の `SetForegroundWindow` がどの程度失敗するか計測する。
2. komorebi/GlazeWM方式（自己 `SendInput` → `SetForegroundWindow`）を実装し、成功率が改善するか確認する。
3. `ForegroundLockTimeout` をWM側で変更する副作用（ユーザー環境のシステム設定変更）を許容するかどうかを設計判断として明記する。4.4節の「フォーカス移動が実際にOSへ反映できるかはB-3の検証に依存する」という記述に、この回避策の採用可否も含めて反映する。

---

## 3. WH_KEYBOARD_LLとLowLevelHooksTimeout

### 仕様
[`LowLevelKeyboardProc`](https://learn.microsoft.com/en-us/windows/win32/winmsg/lowlevelkeyboardproc) の公式ドキュメントに明記されている：

- タイムアウト値は `HKEY_CURRENT_USER\Control Panel\Desktop` の `LowLevelHooksTimeout` レジストリ値（ミリ秒単位）。
- 「If the hook procedure times out, the system passes the message to the next hook. However, on Windows 7 and later, the hook is silently removed without being called. There is no way for the application to know whether the hook is removed.」（Windows 7以降ではタイムアウトしたフックは**黙って外され**、アプリケーションはそれを検知する方法がない）。
- Windows 10 version 1709以降では最大許容タイムアウト値は1000ミリ秒（1秒）。`LowLevelHooksTimeout` にこれより大きい値が設定されていても、システムは1000ミリ秒にフォールバックする。
- 公式ドキュメントは既定値そのものを明記していない（本調査で確認できたのは「最大許容値1000ms」という上限のみ）。
- 公式の推奨：「In most cases where the application needs to use low level hooks, it should monitor raw input instead.」（低レベルフックではなくRaw Inputの利用を推奨）。ただしRaw Inputは他スレッド宛のキー入力を"横取り"してブロックする用途（本WMが必要とする、特定キー組み合わせをOSや他アプリに渡さず消費する用途）には使えないため、WMの文脈では採用できない。

### 実務上の注意
- フックは「登録したスレッドのメッセージポンプ中に同期的に呼ばれる」ため、フック処理内でブロッキングI/Oや重い処理を行うとタイムアウトしフックが外れる。ドキュメントも「アプリケーションは専用スレッドでフックを実行し、作業をワーカースレッドに渡してすぐ戻るべき」と明記している。
- 外れたことを検知する公式な手段が無いため、タイムアウトによる機能停止はサイレント障害になりやすい。WM側で定期的にフックの有効性を自己診断する仕組み（例：自分がフックしたはずのキーイベントが一定時間観測できない場合に再登録する）が実務上必要になる。

### 設計書への影響
B-2（8.1節）の記述「`WH_KEYBOARD_LL`を使う場合、フックスレッドがGCやI/Oで停止すると`LowLevelHooksTimeout`を超過してOSがフックを黙って外すため、フック処理はEffect適用スレッドやJournal I/Oと分離する」は公式ドキュメントと完全に整合し、**前提は正しい**。

Phase 0のスパイクで確認すべき具体項目：
1. 実行環境（Windows 11）での既定の `LowLevelHooksTimeout` 値を実機レジストリで確認する（公式ドキュメントに既定値の明記がないため）。
2. .NET/F#のGCポーズがフックコールバックの実行時間に影響する度合いを計測し、1000ms上限に対してどの程度マージンがあるか確認する。
3. フックが無言で外れた場合の自己検知・再登録ロジックをPhase 0で最低限のレベルで試作する。

---

## 4. DeferWindowPos系とSWP_ASYNCWINDOWPOS

### 仕様
- [`BeginDeferWindowPos`/`DeferWindowPos`/`EndDeferWindowPos`](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-deferwindowpos) は、複数ウィンドウの位置・サイズ・Z順序を**一括で**変更するための機構。`EndDeferWindowPos` が実際にすべてのウィンドウを位置決めする。ドキュメントには「移動対象の全ウィンドウは同じ親を持たなければならない」という制約がある（トップレベルウィンドウであれば通常は満たされる）。
- `DeferWindowPos` が失敗した場合（戻り値`NULL`）、ドキュメントは「アプリケーションはウィンドウ位置決め操作全体を放棄し、`EndDeferWindowPos`を呼ぶべきではない」と明記している。つまり**バッチの部分失敗は「全体中止」が公式に推奨される挙動**である。
- ちらつき抑制効果については公式ドキュメント自体には明記がないが、CodeProjectの技術記事では「個別にウィンドウを移動するのに比べ、一括で移動することでちらつきを減らせる」と説明されている（コミュニティ解説であり公式保証ではない）。
- [`SetWindowPos`](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowpos) の `SWP_ASYNCWINDOWPOS` フラグの説明：「If the calling thread and the thread that owns the window are attached to different input queues, the system posts the request to the thread that owns the window. This prevents the calling thread from blocking its execution while other threads process the request.」（呼び出し元スレッドと対象ウィンドウ所有スレッドが異なる入力キューに属する場合、リクエストは対象スレッドへポストされ、呼び出し元スレッドは他スレッドの処理待ちでブロックされない）。

### 実務上の注意
- komorebi・GlazeWMの**両方**とも、ソースコード上で `DeferWindowPos`/`BeginDeferWindowPos` を使用していない（GitHub Code Searchで `DeferWindowPos` の検索結果は両リポジトリとも0件）。両者とも各ウィンドウに対して個別に `SetWindowPos` を呼び、フラグに `SWP_ASYNCWINDOWPOS` を使っている（komorebiの `raise_and_focus_window`、GlazeWMの `native_window.rs` のインポートリストに `SWP_ASYNCWINDOWPOS` が含まれる）。
- これは実務上重要な情報である：**既存のWindows用タイル型WM2つは、ちらつき抑制よりもハング耐性（`SWP_ASYNCWINDOWPOS` による非ブロッキング）を優先する設計を選んでいる**。`DeferWindowPos`はちらつき抑制に有利だが、ハング耐性に関する公式な言及は見つからず、`EndDeferWindowPos` 自体が内部で対象スレッドへの同期処理を要する可能性がある（根拠は未確認。下記「調査できなかった論点」参照）。

### 設計書への影響
6.2節（A-4対応）の記述「Shell側で`BeginDeferWindowPos`/`DeferWindowPos`/`EndDeferWindowPos`（または`SWP_ASYNCWINDOWPOS`）を用いて一括適用することを前提に設計する」は、両方を選択肢として並記している点で**修正の必要はない**が、実在するWMの選択（`DeferWindowPos`系を使わず`SWP_ASYNCWINDOWPOS`を個別適用で使う）を踏まえると、**設計書が想定する「どちらが有利か」という問いには、ハング耐性を最優先するなら`SWP_ASYNCWINDOWPOS`個別適用、という実務上の先行事例がある**ことを追記すべきである。「バッチの一部だけが失敗した場合の扱い」についても、`DeferWindowPos`を使う場合は公式ドキュメントが「全体中止」を推奨している点をPending追跡の設計に反映できる。

Phase 0のスパイクで確認すべき具体項目：
1. `DeferWindowPos`系と`SWP_ASYNCWINDOWPOS`個別適用のそれぞれで、1窓がハングしている状態で残りの窓の再配置が完了するかを実機で比較する。
2. ちらつきの主観評価を両方式で比較する（komorebi/GlazeWMが採用していない`DeferWindowPos`を敢えて採用する価値があるか判断する）。

---

## 5. ハング検知

### 仕様
- [`SendMessageTimeout`](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendmessagetimeouta) のドキュメント：「The function calls the window procedure for the specified window and, if the specified window belongs to a different thread, does not return until the window procedure has processed the message or the specified time-out period has elapsed.」（対象ウィンドウが別スレッドに属する場合、ウィンドウプロシージャがメッセージを処理するか、指定したタイムアウトが経過するまで戻らない）。
  - `fuFlags` に `SMTO_ABORTIFHUNG` を指定すると「受信スレッドが応答していない（ハングしている）ように見える場合、タイムアウト期間の経過を待たずに戻る」。
  - 「この関数は、スレッドが5秒以内に`GetMessage`または類似の関数を呼んでいない場合、そのスレッドを『応答していない』と判定する」（ハング判定の内部基準が明記されている）。
  - 失敗時に必ず `SetLastError` が呼ばれるとは限らないため、呼び出し前に `SetLastError(ERROR_SUCCESS)` しておくことが推奨されている。
- [`IsHungAppWindow`](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-ishungappwindow) のドキュメント：「An application is considered to be not responding if it is not waiting for input, is not in startup processing, and has not called PeekMessage within the internal timeout period of 5 seconds.」。ただし**冒頭に警告がある**：「This function is not intended for general use. It may be altered or unavailable in subsequent versions of Windows.」（一般利用を意図していない、将来変更・廃止される可能性がある）。「Ghost windows always return TRUE」（ゴーストウィンドウは常にTRUEを返す）という注意点もある。
- `SetWindowPos` 自体の公式ドキュメントには「内部でメッセージを送信するため対象スレッドがハングしているとブロックされる」という直接の記述は見当たらなかったが、`SWP_ASYNCWINDOWPOS`フラグの説明（4章参照）が間接的にこれを裏付けている：異なる入力キューの場合リクエストを「ポスト」することでブロックを防ぐ、という記述は、**デフォルト動作（`SWP_ASYNCWINDOWPOS`なし）ではブロックが発生する**ことを前提にしている。

### 実務上の注意
- Raymond Chenのブログ（The Old New Thing）に関する言及として、Larry Ostermanのブログ記事で「新しいウィンドウが古いウィンドウへ`WM_SETFOCUS`を送ろうとし、古いウィンドウのスレッドがメッセージポンプを回していなかったためハングした」という実例が紹介されている。これはクロススレッドの同期送信が、対象スレッドがメッセージループを止めていると無条件にブロックすることを示す実例である。
- `IsHungAppWindow` は「一般利用を意図していない」という公式の留保があるため、ハング検知の主手段とするのはリスクがある。実務上は `SendMessageTimeout` に `SMTO_ABORTIFHUNG` を付けて実際に呼び出し、応答有無で判定する方が確実である（`IsHungAppWindow`は補助的な事前チェックとして使う）。
- komorebi・GlazeWMのソースコードを調査した限り、両者とも `IsHungAppWindow` や `SendMessageTimeout` を明示的に呼び出すコードは見つからなかった（Code Searchで0件）。これは、両WMがハング対策を明示的に実装していない、または別の手段（タイムアウト付きスレッド分離等）で対処している可能性を示唆するが、本調査では確証を得られていない。

### 設計書への影響
7.1節（ADR-012対応）の記述「`SetWindowPos`等は対象窓を所有するスレッドへメッセージを送るため、対象アプリがハングしていると呼び出し側がブロックされる」は、`SWP_ASYNCWINDOWPOS`の公式説明から逆算して**妥当な前提**と判断できるが、公式ドキュメントに「`SetWindowPos`が同期メッセージを送る」という直接的な一文は見つからなかった点は留保する。「`SendMessageTimeout`／`SWP_ASYNCWINDOWPOS`を用い、必要に応じ`IsHungAppWindow`で事前検知する」という設計方針自体は妥当だが、`IsHungAppWindow`の「一般利用を意図していない」という公式の警告をPhase 0の完了条件に反映すべきである（将来のWindowsバージョンで動作が変わるリスクを記録する）。

Phase 0のスパイクで確認すべき具体項目：
1. 実際にハングさせた窓（例：メッセージループを意図的に止めたテストアプリ）に対して `SetWindowPos` を呼び、ブロックが発生することを実測で確認する。
2. `SWP_ASYNCWINDOWPOS` を付けた場合にブロックが解消されることを確認する。
3. `IsHungAppWindow` の事前チェックが実際に有用な予測精度を持つか（ハングしていないのに`TRUE`を返す、あるいはハングしていても`FALSE`を返す誤判定の頻度）を実機で評価する。

---

## 6. 不可視リサイズ枠とDWM拡張フレーム境界

### 仕様
- [`DWMWINDOWATTRIBUTE`](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/ne-dwmapi-dwmwindowattribute) 列挙型の `DWMWA_EXTENDED_FRAME_BOUNDS`：「Retrieves the extended frame bounds rectangle in screen space.」（画面座標系での拡張フレーム境界矩形を取得する）。`DwmGetWindowAttribute`と組み合わせて使用する。
- `GetWindowRect` との差異について、公式ドキュメント上に数値の明記はないが、本書4.2節の記述（「`GetWindowRect`とDWM拡張フレーム境界は同一ではなく、DPI仮想化の扱いも異なる」）と整合する一般的な理解として、Windows 10以降で導入された**不可視のリサイズ用枠**（視覚的には見えないが`GetWindowRect`の範囲には含まれる）が差分の原因である。

### 実務上の注意
- Stack Overflowの複数の回答（本調査ではkiwixミラー経由で参照）によれば、典型的な差分は**Windows 10で1辺あたり約7ピクセル**（例：`{left:7, top:0, right:7, bottom:7}`）という報告がある。これはDPI・ウィンドウの状態（最大化中は差分が出ない等）に依存し、スケーリングが100%でない場合やDPI非対応アプリでは単純な固定値にならない点も報告されている。
- 「DWM呼び出しは物理座標を返すが`GetWindowRect`は論理座標を返す」という指摘があり、DPIスケーリングが100%でない環境ではこの差分自体がDPI倍率の影響を受ける。
- タイミングの注意点として、「ウィンドウが表示される前は`DwmGetWindowAttribute`が`GetWindowRect`と同じ値を返す」という報告がある（ウィンドウ生成直後、まだ一度も表示されていない状態での呼び出しに注意が必要）。
- **komorebiの実装**（`komorebi/src/windows_api.rs`、`window_rect`関数）は、まさにこの前提に基づいて実装されている：
```rust
pub fn window_rect(hwnd: isize) -> eyre::Result<Rect> {
    let mut rect = unsafe { std::mem::zeroed() };
    if Self::dwm_get_window_attribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, &mut rect).is_ok() {
        // TODO(raggi): once we declare DPI awareness, we will need to scale the rect.
        Ok(Rect::from(rect))
    } else {
        unsafe { GetWindowRect(HWND(as_ptr!(hwnd)), &mut rect) }.process()?;
        Ok(Rect::from(rect))
    }
}
```
`DWMWA_EXTENDED_FRAME_BOUNDS`を優先的に使い、失敗時のみ`GetWindowRect`にフォールバックする設計である。さらに `shadow_rect` という別関数で、`window_rect`（DWM拡張フレーム境界）と`GetWindowRect`の差分を明示的に計算し、「シャドウを無視して対象領域を埋めるサイズを`SetWindowPos`用に計算するために加算する値」としてコメントしている。これは**設計書4.2節が要求する「可視フレームとウィンドウ矩形の差分を計測し補正する」という方針そのもの**が、実在するWMで実装されている直接的な裏付けとなる。
重要な点として、komorebiのコード中に「DPIスケーリングを宣言した時点でスケーリングが必要になる」というTODOコメントが残っており、**プロダクション実装でもDPI対応は未完了のまま運用されている**ことを示している。

### 設計書への影響
4.2節（A-3対応）の座標系契約「`SetRect` Effectが渡す値は可視フレーム矩形であり、Shellは`SetWindowPos`に渡す実際のウィンドウ矩形との差分を自分で補正する」という方針は、komorebiの`window_rect`/`shadow_rect`の実装とまさに一致しており、**前提は正しく、実装パターンも確立されている**。「この補正無しでは、ほぼ全ての窓でDesiredとObservedが恒常的に数px食い違う」という懸念も、komorebiが実際にこの補正を行っている事実から妥当性が裏付けられる。

Phase 0のスパイクで確認すべき具体項目：
1. komorebiの`window_rect`/`shadow_rect`パターン（DWM拡張フレーム境界を基準に、`GetWindowRect`との差分をシャドウ分として別管理する）を踏襲し、対象アプリ群で実測する。
2. DPIスケーリングが100%でない環境での差分の挙動を実測する（komorebiのTODOが示すように、ここは未解決領域である可能性が高い）。
3. ウィンドウ生成直後・未表示状態での`DwmGetWindowAttribute`呼び出しが`GetWindowRect`と同値を返す現象が実際に発生するか確認し、発生する場合は初回配置のタイミングを調整する。

---

## 7. WM_DPICHANGEDと異なるDPIモニター間移動

### 仕様
- [`WM_DPICHANGED`](https://learn.microsoft.com/en-us/windows/win32/hidpi/wm-dpichanged) の公式ドキュメント：
  - 送信条件：「ウィンドウが異なるDPIのモニターへ移動した」「ウィンドウをホストするモニターのDPIが変化した」。
  - `wParam`：新しいDPI値（X軸・Y軸、HIWORD/LOWORD）。
  - `lParam`：新DPIにスケーリングされた、推奨されるウィンドウの矩形への`RECT`構造体へのポインタ。「アプリは`lParam`が示す推奨値に基づいてウィンドウを再配置・リサイズすることが期待される」。
  - 「This message is only relevant for PROCESS_PER_MONITOR_DPI_AWARE applications or DPI_AWARENESS_PER_MONITOR_AWARE threads. It may be received on certain DPI changes if your top-level window or process is running as DPI unaware or system DPI aware, but in those situations it can be safely ignored.」（Per-Monitor DPI Aware でないアプリ・スレッドにはこのメッセージは本質的に関係せず、受信しても安全に無視できる）。
  - 処理例として、`WM_DPICHANGED`ハンドラ内で`lParam`の矩形を使い`SetWindowPos`を呼ぶサンプルコードが提供されている。
- 関連して[`WM_GETDPISCALEDSIZE`](https://learn.microsoft.com/windows/win32/hidpi/wm-getdpiscaledsize)は、Per Monitor v2トップレベルウィンドウに対して`WM_DPICHANGED`より**先に**送られ、アプリが新DPIでの自分のサイズを計算する機会を与える（既定は線形スケーリング）。

### 実務上の注意
- Per-Monitor-V2非対応アプリ（System DPI AwareやDPI Unaware）は`WM_DPICHANGED`を実質的に処理しない（無視してよい）。この場合、OSがそのウィンドウをビットマップとして拡大/縮小して描画するため、見た目がぼやける・サイズが見かけ上変わらない等の差異が生じる。これはWMからは「アプリが`WM_DPICHANGED`を無視している」ことと「アプリがPer-Monitor-V2非対応である」ことを区別しにくい、という診断上の課題につながる。
- SDLプロジェクトのコミット議論では、「Windowsはモニター間ドラッグ時にアプリが線形スケーリングでウィンドウ矩形を自分でスケールすることを期待している」とされ、また「PMv2ウィンドウ向けと文書化されたメッセージが他のウィンドウでも観測された」という実装上のドキュメントと実挙動のズレの報告がある。

### 設計書への影響
8.3節（B-6対応）の記述「異なるDPIのモニターへ対象アプリを移動すると、OSから`WM_DPICHANGED`相当の通知を受けたアプリ自身がサイズを変える場合がある」という前提は、公式ドキュメントの「アプリは`lParam`の推奨値に基づき自分で`SetWindowPos`を呼ぶことが期待される」という記述と整合し、**前提は正しい**。「この自己リサイズは`SelfRepositionAfterMs`による自発的な挙動と見分けがつかない」という懸念も、`WM_DPICHANGED`がアプリ側の自発的な`SetWindowPos`呼び出しとしてObservedに現れる以上、妥当な懸念である。

ただし「DPIをまたぐ移動は2段階のSetWindowPosが必要になることが多い」という記述について、公式ドキュメントのサンプルコードは**WM側ではなくアプリ側**が`WM_DPICHANGED`受信時に1回`SetWindowPos`を呼ぶ想定になっている。WM側が「モニター間移動のための1段階目のSetWindowPos」と「DPI変化に対するアプリの自発的な2段階目のSetWindowPos（Observed側）」を区別する、という設計書の意図自体は筋が通っているが、Per-Monitor-V2非対応アプリの場合は2段階目が発生しない（OSのビットマップ拡大縮小で済ませる）ため、**「2段階のSetWindowPosが必要」という一般化はPer-Monitor-V2対応アプリに限定した記述に修正すべき**である。

Phase 0のスパイクで確認すべき具体項目：
1. Per-Monitor-V2対応アプリ（例：最近のWindows Terminal、Chrome系）と非対応アプリ（レガシーアプリ）の両方で、異なるDPIモニター間を移動させた際のObserved矩形の変化パターンを記録する。
2. `WM_GETDPISCALEDSIZE`を実装しているアプリとしていないアプリで、Observed側の挙動差を確認する。
3. 「アプリの自発的なDPI起因SetWindowPos」と「ユーザーの手動操作」を見分けるため、DPI変化イベント（`MonitorTopologyObserved`等、7.1節のInputEvent）とObservedの時間的近接性を疑似OSの振る舞いモデルに組み込めるか検証する。

---

## 8. Named Pipeのセキュリティ

### 仕様
- `CreateNamedPipe`の`FILE_FLAG_FIRST_PIPE_INSTANCE`について、Microsoft公式リファレンス：「If you attempt to create multiple instances of a pipe with this flag, creation of the first instance succeeds, but creation of the next instance fails with ERROR_ACCESS_DENIED.」（このフラグを付けて複数インスタンスを作成しようとすると、最初のインスタンス作成は成功するが、次のインスタンス作成は`ERROR_ACCESS_DENIED`で失敗する）。
- `GetNamedPipeServerProcessId`：パイプハンドルからサーバー側のプロセスIDを取得する。公式ドキュメントでは「ハンドルは`CreateNamedPipe`で作成されたものである必要がある」とされているが、この条件がクライアント側の検証用途（クライアントが接続先サーバーを確認する）とどう整合するかはドキュメント上明確ではなかった（下記「調査できなかった論点」参照）。
- Windows 10 version 1709以降では、アプリコンテナ内のパイプ作成に制限があり、パイプ名に`\\.\pipe\LOCAL\`形式の使用が必要になる場合があるという言及がある（コミュニティ情報源）。

### 実務上の注意
- Klocworkの静的解析ドキュメント（セキュリティベンダーの資料であり、Microsoft公式ではない）は、`FILE_FLAG_FIRST_PIPE_INSTANCE`を使わないと「既存ファイル上にパイプを作成できてしまい、悪意あるユーザーが機密データを取得したり任意コマンドを実行できる可能性がある」とし、このフラグを「パイプの乗っ取り攻撃への対策」として明確に位置付けている。
- mpv（メディアプレイヤー）のWindows IPC実装（[ipc-win.c](https://git.tjdev.de/mirror/mpv/src/commit/5a6ba2a4fd35ba27c288d8d845f262a0ceee7170/input/ipc-win.c)）は、「自分と同じユーザーかつ自分以上のインテグリティレベルのプロセスのみアクセスを許可するカスタムセキュリティ記述子」をパイプに設定する修正を行っており（issue #556への対応）、「Windowsの既定のパイプセキュリティ記述子はEveryone（匿名リモートユーザーを含む）にアクセスを許可する」という一般的な注意点が複数の情報源で指摘されている。
- komorebi・GlazeWMのソースコードを本調査では直接確認できなかった（GitHub API レート制限により調査中断。下記「調査できなかった論点」参照）。両WMともIPC機構を持つことは知られているが、具体的なセキュリティ記述子の実装は未確認。

### 設計書への影響
9章該当節（F-3対応）の記述「`FILE_FLAG_FIRST_PIPE_INSTANCE`を付けてパイプを作成し、既に同名のインスタンスが存在する場合は起動を失敗させる」「クライアントは接続後に`GetNamedPipeServerProcessId`で接続先プロセスを検証する」という方針は、公式ドキュメントの`FILE_FLAG_FIRST_PIPE_INSTANCE`の挙動（二重作成が`ERROR_ACCESS_DENIED`で失敗する）と整合し、**前提は概ね正しい**。ただし以下の点を追記すべきである：
- `GetNamedPipeServerProcessId`はPIDのみを返すため、PIDの再利用（プロセス終了後の同一PID再割当）への対策（プロセスの開始時刻やイメージパスの併用確認）が必要であり、設計書にはこの点の明記がない。
- セキュリティ記述子（アクセス許可）自体の設定も、パイプ名の乗っ取り対策と同じくらい重要である。mpv の実装例のように「同一ユーザー・同等以上のインテグリティレベルのみ許可」という記述子を明示的に設定すべきであり、設計書9章の「セキュリティ記述子で利用ユーザーを制限し、可能ならリモート接続を拒否する」という既存の記述（571行目）と整合させて、F-3対応節に「セキュリティ記述子の設定」と「`FILE_FLAG_FIRST_PIPE_INSTANCE`によるパイプ名乗っ取り対策」を並記すべきである。

Phase 0のスパイクで確認すべき具体項目：
1. `FILE_FLAG_FIRST_PIPE_INSTANCE`を付けたパイプ作成と、既存パイプがある場合の失敗挙動を実機で確認する。
2. `GetNamedPipeServerProcessId`で取得したPIDをプロセスのイメージパス確認（`QueryFullProcessImageName`等）と組み合わせる実装パターンを検証する。
3. セキュリティ記述子の設定（同一ユーザー・同等インテグリティレベルのみ許可）を実装し、別ユーザーアカウントからの接続が実際に拒否されることを確認する。

---

## 9. UWP/WinUIアプリの最上位ウィンドウ

### 仕様
UWPアプリの最上位ウィンドウに関する公式ドキュメントでの直接的な解説は本調査では見つからなかったが、Chromiumプロジェクトのソースコード（[desktop_capture_devices_util_win.cc](https://chromium.googlesource.com/chromium/src/+/a2fa993e901b58e36748540c21f3bad51187f87d/chrome/browser/media/webrtc/desktop_capture_devices_util_win.cc)）に実装レベルの解説がある：
- 「ApplicationFrameHost.exeはUWPアプリのホストプロセスであり、システムディレクトリ（通常`C:\Windows\System32`）に存在する」。
- 「UWPアプリのUIは、トップレベルウィンドウが`ApplicationFrameHost.exe`によって作成され、実際のアプリウィンドウはそのトップレベルウィンドウの子ウィンドウであるという階層構造を持つ」。
- 実際のアプリウィンドウを特定する手段として、クラス名`Windows.UI.Core.CoreWindow`を持つ子ウィンドウを探し、そのプロセスIDを読む、という実装パターンがChromiumで使われている。

### 実務上の注意
- Stack Overflowの複数の回答によれば、この子ウィンドウ探索アプローチには例外がある：Windows 10のフルスクリーンUWPアプリでは、親・子の両方が`ApplicationFrameHost.exe`のPIDを返すケースが報告されている。
- 実行ファイル名が`ApplicationFrameHost.exe`ではなく、実際のアプリパッケージの実行ファイル（例：`Microsoft.Msn.Weather.exe`、`C:\Program Files\WindowsApps`配下）になる例も報告されている。つまり**どのプロセスが「実行ファイル名」として観測されるかはアプリ・取得タイミングによって揺れる**。
- 子ウィンドウからの取得は「アプリがアクティブな間だけ機能する」という制約も報告されている。

### 設計書への影響
5.3節（A-6対応）の記述「`AppIs`が参照する`AppId`の導出方法…をPhase 0で確定する。UWP/WinUIアプリの最上位HWNDは`ApplicationFrameHost.exe`が所有するため、実行ファイル名だけで判定するとサンプル設定（`AppIs "WindowsTerminal"`等）がアプリの種類によって機能しない場合がある」という記述は、Chromiumの実装解説およびStack Overflowの実例と整合し、**前提は正しい**。

Phase 0のスパイクで確認すべき具体項目：
1. 対象とする実アプリ群（18章のリストにあるUWP/WinUI＝`ApplicationFrameHost`経由のアプリ）について、トップレベルHWNDの実行ファイル名を実測し、`ApplicationFrameHost.exe`になるケースと実アプリ名になるケースを分類する。
2. `Windows.UI.Core.CoreWindow`クラス名を持つ子ウィンドウの探索による実アプリ特定が、フルスクリーン時にも機能するか確認する。
3. AUMID（Application User Model ID）やパッケージファミリ名による識別が、実行ファイル名ベースの識別よりも安定するかを比較評価する（5.3節が列挙する3つの導出方法のどれを採用するかの判断材料とする）。

---

## 10. 既存のWindows用タイル型WMにおける既知の問題・回避策のまとめ

本調査でGitHub Code Search・Discussion等から直接確認できた、komorebi・GlazeWMの実装上の工夫・既知の課題を論点別に整理する。

| 論点 | komorebi | GlazeWM |
|---|---|---|
| フォーカス移動（2章） | `SendInput`で自己にダミー入力を送出後`SetForegroundWindow`。加えて起動時に`SPI_SETFOREGROUNDLOCKTIMEOUT`でシステム設定を0に変更 | `SendInput`で自己にダミー入力を送出後`SetForegroundWindow`（komorebiと同じパターン） |
| ウィンドウ再配置（4章） | `DeferWindowPos`系は未使用。個別`SetWindowPos`＋`SWP_ASYNCWINDOWPOS` | 同様に`DeferWindowPos`系は未使用。`SWP_ASYNCWINDOWPOS`を使用 |
| ハング検知（5章） | `IsHungAppWindow`/`SendMessageTimeout`の明示的呼び出しは確認できず | 同様に確認できず |
| DWM拡張フレーム境界（6章） | `window_rect`関数で`DWMWA_EXTENDED_FRAME_BOUNDS`を優先し`GetWindowRect`にフォールバック。`shadow_rect`関数で両者の差分を明示的に管理。DPIスケーリング対応は**TODOコメントとして未完了** | 本調査では未確認 |
| ホットキー競合（1章） | 本調査では未確認 | Discussion #1206で`Win+E`/`Win+V`/`Win+Shift+S`等の競合が実際に報告され、PowerShellでOS標準ショートカットを無効化する回避策、`lwin`/`rwin`個別指定の必要性が報告されている |

これらは「捨てる前提の素朴なWM」（Phase 0）が実際に直面するであろう課題の多くが、既存の実動WMでも未解決・回避策レベルで対処されていることを示している。特に、DPIスケーリング対応のTODOが本番運用中のkomorebiに残っている事実は、設計書が8.3節で「2段階のSetWindowPosが必要になることが多い点をPhase 0で確認する」としている慎重な姿勢を裏付ける。

---

## 調査できなかった／裏付けが弱い論点

以下は本調査で十分な裏付けを得られなかった、または推測で埋めることを避けた論点である。

1. **`LowLevelHooksTimeout`の既定値**：公式ドキュメントは「Windows 10 1709以降の最大許容値1000ms」のみを明記しており、レジストリ値が未設定の場合の既定動作・既定値そのものの記載は見つからなかった。実機レジストリの確認が必要。
2. **`SetWindowPos`が内部で対象スレッドに同期メッセージを送信することの直接的な公式記述**：`SWP_ASYNCWINDOWPOS`フラグの説明から論理的に推測できるが、「`SetWindowPos`は内部的に`SendMessage`を使う」という明示的な一文は公式ドキュメントに見つけられなかった。Raymond Chenのブログ（The Old New Thing）に関連記事がある可能性が高いが、本調査では該当する具体的な記事を特定できなかった。
3. **`GetNamedPipeServerProcessId`をクライアント側から呼んでサーバーを検証する具体的な実装パターン**：ドキュメント上「ハンドルは`CreateNamedPipe`で作成されたものである必要がある」という記述があり、クライアント側（`CreateFile`で接続したハンドル）に対してこの関数がどう振る舞うかが文面から明確に確認できなかった。Phase 0での実機確認が必要。
4. **komorebi・GlazeWMのNamed Pipe実装の詳細**：GitHub APIのレート制限により、両リポジトリのIPC実装（セキュリティ記述子の設定有無、`FILE_FLAG_FIRST_PIPE_INSTANCE`の使用有無）を本調査では直接確認できなかった。
5. **komorebi・GlazeWMのホットキー競合の実装詳細**：同じ理由により、両リポジトリの`RegisterHotKey`呼び出しコードと、フォールバックとしての`WH_KEYBOARD_LL`実装の有無を直接確認できなかった（1章の実務上の注意はDiscussion等の間接情報に基づく）。
6. **GlazeWMの`DWMWA_EXTENDED_FRAME_BOUNDS`/`GetWindowRect`差分対応の実装**：komorebiについては直接確認できたが、GlazeWM側の同等実装は本調査では未確認。
7. **`IsHungAppWindow`の実運用での予測精度**（偽陽性・偽陰性の頻度）：公式ドキュメントの警告（「一般利用を意図していない」）以上の定量的な情報は見つからなかった。
