# Failure Catalog（Phase 0 実機検証）

対象: [spikes.md](./spikes.md) のS1〜S11のうち、GitHub Actions `windows-latest` ランナー上でCI実機検証できたS1, S2, S2b, S2c, S3, S4, S5, S6, S9。S2b/S2cはADR-014（Result型によるエラー耐性設計）のレビュー収束後に、テキストでの推測（事前確認タイムアウト値「50ms」、`RestoreBeforeMove`の`ShowWindowAsync`化）を実機データに置き換えるために追加した。
検証に使ったコード: [spikes/Phase0CiSpikes](../spikes/Phase0CiSpikes)（捨てる前提の検証ハーネス。Phase A以降のCoreへは持ち込まない）。
実行結果: [run #41275322319](https://github.com/cuzic/winmonad/actions/runs/41275322319)（S1〜S9、1分24秒）、[run #41285111988](https://github.com/cuzic/winmonad/actions/runs/41285111988)（S2b/S2c含む全9スパイク、1分43秒、2026-11-01T07:32 UTC）。

環境: `Microsoft Windows NT 10.0.26100.0`（64bit）、画面 1024x768（CI仮想ディスプレイ）。

試行の経緯: 最初の2回の実行は、それぞれ別の原因でジョブタイムアウト（15分）まで無反応になった。1回目は`startHangChild`の`StandardOutput.ReadLine()`が無期限に待ち続ける経路、2回目はS4で`keybd_event(VK_MENU, ...)`（Altキー）を送出したことでWindowsのメニューモード（`WM_SYSKEYDOWN`/`WM_ENTERMENULOOP`）に入り`Application.DoEvents()`が戻らなくなる経路だった。両方とも実機でしか踏めなかった地雷であり、修正後の3回目の実行で初めて全スパイクが完走した。この経緯自体も「実機検証でしか分からないことがある」という本プロジェクトの前提を裏付けている。

| ID | スパイク | 観測内容 | 分類 | architecture.mdへの反映先 |
|---|---|---|---|---|
| F01 | S1 | 自作WinForms窓・notepad.exeとも `GetWindowRect` と `DWMWA_EXTENDED_FRAME_BOUNDS` の差は `diff(L,T,R,B)=(7,0,7,7)` px で完全一致。feasibility.md §6が引用した「Windows 10で約7px」という報告値と実測が一致した | Bで対処（確定） | 4.2 座標系の契約 |
| F02 | S1/S5 | `calc.exe`を起動すると、CIランナー上では実プロセス名が`win32calc`（クラシック版Win32電卓）になり、`MainWindowHandle`もトップレベル窓も一定時間内に取得できなかった。GitHub windows-latestランナーのイメージはモダンUWP版Calculatorを提供していないと判断する | 無視してよい（CI環境固有の制約。実機デスクトップで別途検証） | 5.3 `AppId`導出注記にCI環境の限界として追記 |
| F03 | S9 | ボーダレスかつ画面サイズ一致の窓に対し`SHQueryUserNotificationState`は`state=6`（`QUNS_QUIET_TIME`）を返し、全画面として検出しなかった。一方「ボーダレス and 矩形==モニター矩形」のジオメトリヒューリスティックは正しく`true`を返した | 型に反映（確定） | 8.1 フルスクリーンアプリの除外 |
| F04 | S4 | 別プロセス（自分がPhase0 harnessから`Process.Start`した子プロセス）が直前にフォアグラウンドを得ていた状態から、plain `SetForegroundWindow`・`mouse_event`自己プライミング版の**両方**が成功した（返り値true、実際にフォアグラウンドもtrue）。トリックの有無で差が出なかった | 型に反映（要注意フラグ付き） | 8.1/4.4 フォーカス移動の実現性 — 下記「限界」参照 |
| F05 | S2 | `IsHungAppWindow`は、メッセージポンプを6秒止めた窓に対し正しく`true`を返した | Bで対処（確定） | 7.1 実行スレッドモデルと耐ハング性 |
| F06 | S2 | `SendMessageTimeout(WM_NULL, SMTO_ABORTIFHUNG, 2000ms)`はハング窓に対し**即時（0ms）**に失敗を返した。2000msの期限を使い切らず、OSが既にハング判定済みの窓へは即座に中断される | Bで対処（確定） | 7.1 実行スレッドモデルと耐ハング性 |
| F07 | S2 | `SetWindowPos`を`SWP_ASYNCWINDOWPOS`なしでハング窓へ発行すると、4000msの計測上限を超えてもリターンしなかった（実際にブロックされることを実測で確認） | 型に反映（確定・ADR-012の前提を実証） | 7.1 ADR-012 |
| F08 | S2 | 同じハング窓へ`SWP_ASYNCWINDOWPOS`付きで`SetWindowPos`を呼ぶと**即時（0ms）**にリターンした | Bで対処（確定・ADR-012/6.2の既定方針を実証） | 6.2 / 7.1 |
| F09 | S6 | `BeginDeferWindowPos`/`DeferWindowPos`/`EndDeferWindowPos`でハング窓を含む3窓を一括適用すると、4000msの計測上限を超えてもリターンしなかった | 型に反映（確定・DeferWindowPos不採用の判断を実証） | 6.2 レイアウト切替の一括適用 |
| F10 | S6 | 同条件（ハング窓1つ＋正常窓2つ）を個別`SetWindowPos`+`SWP_ASYNCWINDOWPOS`で適用すると、3窓すべて1ms以内にリターンした（ハング窓自身への呼出も含めて） | Bで対処（確定・6.2の既定方針を実証） | 6.2 レイアウト切替の一括適用 |
| F11 | S3 | `Alt+J`/`Alt+Shift+Return`/`Alt+Shift+1`は`RegisterHotKey`で確保できた。`Win+Return`/`Win+1`/`Win+L`は全て`GetLastError=1409`（`ERROR_HOTKEY_ALREADY_REGISTERED`）で失敗した | 型に反映（確定） | 8.1 ホットキー登録の実現性 |
| F12 | S2b | `SendMessageTimeout(WM_NULL, SMTO_ABORTIFHUNG, timeoutMs)`を、OSがまだハング判定していない窓（停止後~1秒）に対し10/50/100/500msの各タイムアウトで呼ぶと、実測`elapsedMs`は要求値とほぼ一致した（9/57/99/499ms）。同じ窓がOSにハング判定された後（停止後~6秒）は、同じ4つのタイムアウト値すべてで`elapsedMs=0`だった。「ハング判定前は要求タイムアウトをほぼそのまま待ち、判定後は即時に返る」という挙動を実測で確定した | 型に反映（確定。タイムアウト値を10msに決定） | 7.1 ハング事前確認のタイムアウト値 |
| F13 | S2c | 最大化状態のまま応答停止した窓に対し、`ShowWindow(SW_RESTORE)`（同期）は4000msの計測上限を超えてもリターンせず、`ShowWindowAsync(SW_RESTORE)`は0msで即時リターンした。`SetWindowPos`+`SWP_ASYNCWINDOWPOS`（F07/F08）と同じ非同期/同期の差がここでも実証された。なお最初の試行では子窓を最大化しておらず両方0msという無意味な結果になった（`SW_RESTORE`が実質no-opだったため）ため、子窓を最大化してから再実行して確認した | 型に反映（確定。`RestoreBeforeMove`=`ShowWindowAsync`を実証） | 7.1/8.1 RestoreBeforeMove |

## F04（S4）の限界：実証したことと、まだ実証していないこと

S4で「他プロセスが直前にフォアグラウンドだった状態からの奪取」として使った他プロセスは、**この検証ハーネス自身が`Process.Start`で起動した子プロセス**である。`SetForegroundWindow`の公式ドキュメントが列挙する許可条件には「フォアグラウンドプロセスが呼び出し元プロセスによって起動された」という項目が含まれており、今回の成功がこの条件（親子関係）によるものである可能性を排除できていない。したがって、

- 実証したこと：ハング窓を含む別プロセスへ`SetForegroundWindow`を呼んでも、呼出自体はブロックされない（S2/S6の知見と整合）。
- まだ実証していないこと：**自分が起動していない、無関係な第三者プロセス**（ユーザーが操作しているブラウザやIDE等）からフォーカスを奪う場合に、`mouse_event`自己プライミングが本当に必要か。

8.1節のkomorebi/GlazeWM方式（自己`SendInput`/`mouse_event`プライミング）は、この限界が解消されるまで既定の実装として維持する（外す判断はしない）。無関係な第三者プロセスでの再検証は、実際のWindows環境でのdogfooding（Phase B）まで持ち越す。

## CI実機検証で踏んだ地雷（それ自体が成果）

- `Process.StandardOutput.ReadLine()`を無期限に待つと、子プロセス側で何らかの理由（ウィンドウステーション制約、クラッシュダイアログ等）が起きた場合に親プロセス側が無期限に止まる。タイムアウト付きの読み取りに変更した。
- `keybd_event`でAltキー（`VK_MENU`）を送出すると、Windowsのメニューモード処理に入り`Application.DoEvents()`が戻らなくなることがある。komorebi/GlazeWMが`SendInput`で**マウス**イベントを使っている理由はまさにこれを避けるためだと、実機で初めて実感を伴って理解できた。
- どちらの地雷も、6.2/7.1に書いた「ハング耐性」の設計方針そのものと同じ種類の問題（同期呼び出しが想定外に長時間ブロックする）であり、本プロジェクトが目指す「不確実性を観測・診断可能にする」という設計思想がテストハーネス自身にも適用できることを示している。
- S2cの初回実行では、子窓を最大化せずに`SW_RESTORE`を送ったため、同期/非同期の両方が0msで返り「差が無い」という誤った結論になりかけた。実際には`SW_RESTORE`が状態を変えない操作（既に通常状態の窓への復元）であり、ターゲットスレッドと通信する必要が最初から無かっただけだった。**「ブロックしないこと」を実証するには、本当にブロックしうる状態変化を伴う操作で試す必要がある**という、スパイク設計そのものの教訓。
