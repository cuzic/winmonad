# Failure Catalog（Phase 0 実機検証）

対象: [spikes.md](./spikes.md) のS1〜S11のうち、GitHub Actions `windows-latest` ランナー上でCI実機検証できたS1, S2, S2b, S2c, S3, S4, S5, S6, S7, S9, S10。S2b/S2cはADR-014（Result型によるエラー耐性設計）のレビュー収束後に、テキストでの推測（事前確認タイムアウト値「50ms」、`RestoreBeforeMove`の`ShowWindowAsync`化）を実機データに置き換えるために追加した。S7/S10はPhase 0計画のうち未着手だった項目（昇格窓・Explorer再起動）を、UACプロンプト等の危険を避けつつ安全に検証できる形に落として追加した。
検証に使ったコード: [spikes/Phase0CiSpikes](../spikes/Phase0CiSpikes)（捨てる前提の検証ハーネス。Phase A以降のCoreへは持ち込まない）。
実行結果: [run #41275322319](https://github.com/cuzic/winmonad/actions/runs/41275322319)（S1〜S9、1分24秒）、[run #41285111988](https://github.com/cuzic/winmonad/actions/runs/41285111988)（S2b/S2c含む、1分43秒）、[run #41285769219](https://github.com/cuzic/winmonad/actions/runs/41285769219)（S7/S10含む全11スパイク、1分47秒、2026-11-01T07:42 UTC）。

環境: `Microsoft Windows NT 10.0.26100.0`（64bit）、画面 1024x768（CI仮想ディスプレイ）。

**実デスクトップ（`dragonflyg4`）での追加検証**：CI環境には構造的に作れない条件（無関係な第三者プロセスが実際にフォーカスを持っている状態、非昇格のユーザーセッション、実際のExplorerシェル）を検証するため、ユーザーの実Windows PC（`dragonflyg4`、Windows 11 build 22631、tailscale経由のclipwireでコマンド実行）で同じハーネスに実デスクトップ専用スパイク（S4r/S5r/S7r/S11r、既存S10の拡張）を追加して実行した（2026-11-01T08:42 UTC）。結果はF16以降。

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
| F14 | S7 | GitHub Actions windows-latestランナー上のプロセスは、Administratorsグループに属し、トークンの整合性レベルは**High**（RID=0x3000）だった。つまりCI実機検証ハーネス自身が既に昇格済みの状態で動いている | 無視してよい（CI環境固有の制約。非昇格↔昇格の非対称性を観測する前提がこの環境には無い） | 21章 `DenialKind.TargetIntegrityHigher`の生成手段：CIでは検証不可、実デスクトップでの検証が必要と明記 |
| F15 | S10 | `explorer.exe`はCIランナー上にも実際に1プロセス存在し、`Process.Kill()`で終了させると**504ms後に自動的に再起動**した（10秒以内の再起動を確認） | 型に反映（確定。Explorer再起動の発生自体と再起動の速さを実証） | 8.4 `TaskbarCreated`によるトレイアイコン再登録：再起動は高速（1秒未満）なので、トレイアイコンの空白期間は短いことを前提にできる |
| F16 | S4(実デスクトップ)/S4r | `dragonflyg4`の実デスクトップでは、CIと違って**plain `SetForegroundWindow`が明確に失敗する**（返り値false、実際にフォアグラウンドもfalse）。これは自分が起動した子プロセスに対して（S4）も、**自分が起動していない無関係な第三者プロセス**（pid=24980、ハーネス自身はpid=12208）に対して（S4r）も同様だった。両方のケースで`mouse_event`自己プライミング版は成功した（返り値true、実際にフォアグラウンドもtrue） | 型に反映（確定。F04の限界を解消：自己プライミングは無関係な第三者プロセスに対しても実際に必要であることを実証） | 8.1/4.4 フォーカス移動の実現性 |
| F17 | S7(実デスクトップ) | `dragonflyg4`の通常のユーザーセッションは、CIランナー（High、Administrators所属）と異なり、**Administratorsグループに属さず、整合性レベルはMedium**（RID=0x2000）だった | 型に反映（確定。実デスクトップには非昇格↔昇格の非対称性が実在することを確認） | 21章 `DenialKind.TargetIntegrityHigher`：CI限界(F14)の裏返しとして、実デスクトップでは原理的に検証可能と明記 |
| F18 | S7r | （初回実行時点の記録）テスト実行時点で昇格済みのウィンドウが開いていなかったため、非対称性そのものはまだ観測できていなかった。ユーザーに昇格コマンドプロンプトを開いて承認してもらい再実行した結果はF23参照 | 解消済み（F23で確定） | 21章 `DenialKind.TargetIntegrityHigher` |
| F23 | S7r（再実行） | ユーザーが実際にUAC承認して開いた昇格コマンドプロンプト（pid=27788、整合性High）に対し、Medium整合性の本ハーネスから`SetWindowPos`（同じ矩形へのno-op移動）を呼ぶと、**`returned=false`、`GetLastError=5`（`ERROR_ACCESS_DENIED`）**で明確に失敗した。`classify`対応表（8.1）の`(SetWindowPos, Win32LastError, 5) → Denied OtherDenial`がそのまま実機で成立することを確認した | 型に反映（確定。非昇格→昇格の拒否モードを実機で確定） | 8.1 classifyの対応表／21章 `DenialKind.TargetIntegrityHigher` |
| F24 | S7r開発中に発見 | (a) `OpenProcess`を`PROCESS_QUERY_INFORMATION`で呼ぶと、同じユーザーの昇格プロセスに対しても取得失敗し候補を見逃す。`PROCESS_QUERY_LIMITED_INFORMATION`（Task Managerが昇格プロセスの情報を非昇格のまま読める際に使う権限）に変更して解決した。(b) `SetWindowPos`のP/Invoke宣言に`SetLastError = true`が付いておらず、失敗時に`Marshal.GetLastWin32Error()`が`0`という偽の値を返していた。属性を追加して解決した | 型に反映（Shellの`Native`モジュール実装規約に反映。8.1） | 8.1 `Native`モジュール：全DllImportに`SetLastError = true`が必要なものを明記、クロス整合性レベルの照会は`PROCESS_QUERY_LIMITED_INFORMATION`を使う |
| F19 | S10(拡張) | 実デスクトップで`RegisterWindowMessage("TaskbarCreated")`→`Shell_NotifyIcon(NIM_ADD)`→Explorer強制終了→**381ms後に自動再起動**→**`TaskbarCreated`ブロードキャストを実際に受信**→**トレイアイコンの再追加に成功**、という一連の流れが全て成立した | 型に反映（確定。8.4のTaskbarCreated復旧ロジックをエンドツーエンドで実証） | 8.4 `TaskbarCreated`によるトレイアイコン再登録 |
| F20 | S1/S9(実デスクトップ) | 可視フレーム差分`diff(L,T,R,B)=(7,0,7,7)`はCI（Windows 11 build 26100）と実デスクトップ（build 22631）の両方で一致し、Windowsビルドに依存しない値であることが分かった。`SHQueryUserNotificationState`は実デスクトップでは`state=5`（`QUNS_ACCEPTS_NOTIFICATIONS`）を返した（CIでは`state=6`=`QUNS_QUIET_TIME`。CIが新規プロビジョニングされた直後で「最初の時間帯の静かな時間」扱いだったためと推測）。どちらの場合もボーダレス全画面相当の窓を「フルスクリーン」と認識せず、ジオメトリヒューリスティックは両環境で一致して検出できた | 型に反映（確定。ジオメトリヒューリスティックの環境非依存性を実証） | 4.2 座標系の契約／8.1 フルスクリーンアプリの除外 |
| F21 | S5r | `dragonflyg4`でも`calc.exe`はトップレベル窓を一定時間内に取得できなかった（CI同様、クラシック版かStore版が無効化されている可能性）。`ms-settings:`は`SystemSettings.exe`（WinUIパッケージアプリ）の起動自体は確認できたが、2.5秒以内には`MainWindowHandle`が得られなかった | 未確定（待機時間を延ばして再検証が必要） | 5.3 `AppId`導出注記 |
| F22 | S11r | `dragonflyg4`は単一モニター・96 DPIのみで、複数DPIモニタ間のウィンドウ移動は検証できなかった | 無視してよい（この環境の制約。複数DPI環境を用意できれば再検証） | 8.3 DPI自己リサイズ：実機でも未検証のまま |

## F04/F16（S4/S4r）：フォーカス移動の実現性は実デスクトップで解消済み

CI実機検証の時点では、S4で使った「他プロセス」が**この検証ハーネス自身が`Process.Start`で起動した子プロセス**であり、`SetForegroundWindow`の許可条件にある「フォアグラウンドプロセスが呼び出し元プロセスによって起動された」という項目の影響を排除できていなかった。さらにCI環境自体では、plain `SetForegroundWindow`がなぜか成功してしまい（F04）、トリックの必要性を判定できなかった。

**実デスクトップ（`dragonflyg4`）での再検証（F16）で、この限界は解消した**：
- 自分が起動した子プロセス（S4）・**自分が起動していない無関係な第三者プロセス**（S4r）の両方で、plain `SetForegroundWindow`は明確に失敗し、`mouse_event`自己プライミング版は明確に成功した。
- komorebi/GlazeWM方式（自己`SendInput`/`mouse_event`プライミング）が実際に必要であることが、無関係な第三者プロセスに対しても実証された。8.1節の既定実装方針は、推測ではなく実機実証済みの確定事項として扱える。

## CI実機検証で踏んだ地雷（それ自体が成果）

- `Process.StandardOutput.ReadLine()`を無期限に待つと、子プロセス側で何らかの理由（ウィンドウステーション制約、クラッシュダイアログ等）が起きた場合に親プロセス側が無期限に止まる。タイムアウト付きの読み取りに変更した。
- `keybd_event`でAltキー（`VK_MENU`）を送出すると、Windowsのメニューモード処理に入り`Application.DoEvents()`が戻らなくなることがある。komorebi/GlazeWMが`SendInput`で**マウス**イベントを使っている理由はまさにこれを避けるためだと、実機で初めて実感を伴って理解できた。
- どちらの地雷も、6.2/7.1に書いた「ハング耐性」の設計方針そのものと同じ種類の問題（同期呼び出しが想定外に長時間ブロックする）であり、本プロジェクトが目指す「不確実性を観測・診断可能にする」という設計思想がテストハーネス自身にも適用できることを示している。
- S2cの初回実行では、子窓を最大化せずに`SW_RESTORE`を送ったため、同期/非同期の両方が0msで返り「差が無い」という誤った結論になりかけた。実際には`SW_RESTORE`が状態を変えない操作（既に通常状態の窓への復元）であり、ターゲットスレッドと通信する必要が最初から無かっただけだった。**「ブロックしないこと」を実証するには、本当にブロックしうる状態変化を伴う操作で試す必要がある**という、スパイク設計そのものの教訓。
