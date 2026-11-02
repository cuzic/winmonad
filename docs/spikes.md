# Phase 0 技術スパイク計画

対象: [architecture.md](./architecture.md) 18章「Phase 0 — 捨てる前提の素朴なWMによる失敗カタログ収集」の具体化。
関連: [feasibility.md](./feasibility.md)（各スパイクの前提となるWin32 APIの仕様・実務上の注意点のリサーチ結果）、[failure-catalog.md](./failure-catalog.md)（GitHub Actions windows-latestランナー、および実デスクトップ`dragonflyg4`（clipwire経由）での実行結果。各スパイクの見出しに反映済み）。

## 進め方

Phase 0は「要素技術の確認」ではなく、**捨てる前提の素朴なWM（`ThrowawayWm`）を1本作り、それを実際に1〜2日使いながら、以下の個別スパイクに相当する観測を行う**という1本の縦切りで進める（architecture.md 18章・19章参照）。スパイクを11個の別プログラムとして作る必要はなく、`ThrowawayWm`に必要な計測コードを足していく。

**feasibility.mdによる前段リサーチ済みの範囲**：`docs/feasibility.md`は公式ドキュメントと既存のWindows用タイル型WM（komorebi、GlazeWM）の実コードを調査し、以下の論点について「仕様としてどう動くはずか」と「実際の実装パターン」を事前に確定済みにしている：フォーカス移動は自己`SendInput`→`SetForegroundWindow`（S4）、バッチ適用は`DeferWindowPos`系ではなく個別`SetWindowPos`+`SWP_ASYNCWINDOWPOS`が既定（S6）、可視フレーム矩形はkomorebiの`window_rect`/`shadow_rect`パターンを踏襲（S1）、`WM_DPICHANGED`の2段階移動はPer-Monitor-V2対応アプリに限定（S11）。これらのスパイクは「ゼロから発見する」のではなく、**既知のパターンが本プロジェクトの対象アプリ群でも実際に機能するかを確認する**作業になる。一方、`IsHungAppWindow`の予測精度（S2）、UWPアプリの実アプリ特定3手法の比較（S5）、実際のキーバインド一覧での`RegisterHotKey`成否（S3）は、feasibility.mdでも裏付けが弱く、実機確認そのものに価値がある。

各スパイクの結果は `docs/failure-catalog.md`（Phase 0完了時に作成）へ、次の3値のいずれかで分類して記録する。

- **無視してよい**：設計書の前提通りで、Phase A以降で特別な対応が不要。
- **型に反映**：Phase AのCore/Policy IR/WindowBehaviorの型設計に影響する（ドメインモデルの変更が必要）。
- **Bで対処**：Windows Shell実装（Phase B）側の対処で十分（Coreの型は変えない）。

## スパイク一覧

### S1. 可視フレーム矩形とウィンドウ矩形の差分計測（A-3対応）【CI実機検証済み：failure-catalog.md F01】
**結果**：自作WinForms窓・notepad.exeの両方で`diff(L,T,R,B)=(7,0,7,7)`px。`calc.exe`はCI環境ではクラシック`win32calc`として起動され計測できず（F02）。
- **検証する前提**: `GetWindowRect`と`DwmGetWindowAttribute(DWMWA_EXTENDED_FRAME_BOUNDS)`の差（不可視リサイズ枠、Windows 10で1辺あたり約7pxという報告あり。feasibility.md §6）がDPIごとにどの程度あり、補正すれば一致判定が安定するか。
- **方法**: `ThrowawayWm`に、komorebiの`window_rect`/`shadow_rect`パターン（`DWMWA_EXTENDED_FRAME_BOUNDS`優先取得＋`GetWindowRect`フォールバック、差分を「シャドウ分」として別管理）を実装し、対象ウィンドウを選んで両方の矩形を取得し差分（X/Y/Width/Height各辺）を表示するコマンドを足す。100%・125%・150%・200%の各DPIで、後述の対象アプリ一覧を一通り計測する。ウィンドウ生成直後・未表示状態では`DwmGetWindowAttribute`が`GetWindowRect`と同値を返す既知の注意点があるため、初回表示後に計測するタイミングも確認する。
- **対象アプリ**: Chrome/Edge、VS Code（Electron系）、メモ帳（クラシックWin32）、エクスプローラー、Windows Terminal、Office（Word等）。
- **記録する内容**: アプリ×DPIごとの差分値（px）。差分がDPIに比例するか、アプリ固有の値か。初回表示直後の未確定値が実際に観測されるか。
- **判定**: 差分がDPIから計算可能な定数なら「Bで対処」（Shellで補正、komorebiパターンを踏襲）。アプリ依存で予測できないなら「型に反映」（`WindowSnapshot`に補正済み/未補正の両方を持つ必要がある等）。

### S2. ハング検知とEffect適用のタイムアウト（ADR-012, E-1対応）【CI実機検証済み：failure-catalog.md F05-F08, F12, F13】
**結果**：`SetWindowPos`は`SWP_ASYNCWINDOWPOS`なしでハング窓に対し4000ms超ブロック、付きでは0msで返った。`IsHungAppWindow`は6秒後に正しく`true`。`SendMessageTimeout(SMTO_ABORTIFHUNG)`は即時(0ms)に中断。ADR-012の前提が実機で実証された。**追加検証（S2b/S2c）**：ハング判定前（~1s）の窓への`SendMessageTimeout`は要求タイムアウト（10/50/100/500ms）をほぼそのまま待ち、判定後（~6s）は全て0msで返った→事前確認のタイムアウト値を**10ms**に確定（F12）。`RestoreBeforeMove`用の`ShowWindowAsync(SW_RESTORE)`も、最大化状態のハング窓で同期版が4000ms超ブロックし非同期版が0msで返ることを実証（F13）。
- **検証する前提**: 応答しないウィンドウへ`SetWindowPos`相当の呼出を行うと呼び出し側がブロックされるか、`SWP_ASYNCWINDOWPOS`で回避できるか、`SendMessageTimeout`/`IsHungAppWindow`で事前・事後に検知できるか。既存WM（komorebi/GlazeWM）は明示的なハング検知コードを持たず`SWP_ASYNCWINDOWPOS`のみで対処しているため、それだけで十分か確認する（feasibility.md §5・§10）。
- **方法**: `ThrowawayWm`からテスト用プロセスを起動し、メッセージループを意図的に止めてハング状態を作る（例：`Sleep`や同期待ちをUIスレッドで実行するテストハーネス）。その状態へ(a)`SWP_ASYNCWINDOWPOS`無しの`SetWindowPos`、(b)`SWP_ASYNCWINDOWPOS`付きの`SetWindowPos`をそれぞれ直接呼び、ブロック時間を比較する。次に`SendMessageTimeout(SMTO_ABORTIFHUNG, ...)`や`IsHungAppWindow`を事前に使った場合の予測精度（誤判定の有無）を確認する。`IsHungAppWindow`は公式に「一般利用を意図していない」と警告されているため、主たる検知手段にできるかも併せて判断する。
- **対象**: 自作のテストハーネス（本物のアプリのハングを安全に再現するのは難しいため、まずは自作で確認し、可能なら「応答なし」になりやすい実アプリ（大きいファイルを開いた直後のOffice等）でも軽く試す）。
- **記録する内容**: (a)(b)それぞれのブロック時間、`SendMessageTimeout`で指定タイムアウト内に制御が戻るか、`IsHungAppWindow`の誤判定率、戻り値・エラーの詳細。
- **判定**: `SWP_ASYNCWINDOWPOS`単独で十分なら「Bで対処」（6.2の既定方針どおり）。それでも専用スレッド＋`SendMessageTimeout`が必要と分かれば、タイムアウト値をPolicy/既定設定に出す必要があるかを「型に反映」として判断する。

### S3. ホットキー登録の実現性（B-2対応）【CI実機検証済み：failure-catalog.md F11】
**結果**：`Alt+J`/`Alt+Shift+Return`/`Alt+Shift+1`は確保でき、`Win+Return`/`Win+1`/`Win+L`は全て`GetLastError=1409`で失敗。既定のキー体系はAlt系に確定。
- **検証する前提**: 使いたいキーバインド（例：`Alt+j/k/Enter`、`Alt+Shift+1..9`、`Win+Enter`等、具体的な一覧を事前に決めてから検証する）が`RegisterHotKey`で確保できるか。`MOD_WIN`で`Win`キーを使う場合、GlazeWMの報告のように`lwin`/`rwin`個別指定が必要か（feasibility.md §1）。
- **方法**: `ThrowawayWm`で候補のキーバインドを一つずつ`RegisterHotKey`し、戻り値と`GetLastError`（`ERROR_HOTKEY_ALREADY_REGISTERED`=1409を特に確認）を記録する。`Win`修飾を使う候補は`MOD_WIN`（左右共通）と`lwin`/`rwin`個別指定の両方を試す。確保できないものは、既存のWindows用タイル型WM（GlazeWM/komorebi等）がどの組み合わせを使っているかをfeasibility.mdの調査結果と照合し、採用するキー体系を決める。確保できないキーについては`WH_KEYBOARD_LL`での代替を小さく試作し、フックが無言で外れた場合の自己検知（登録したはずのキーイベントが一定時間観測できない場合に再登録）も試作する。
- **記録する内容**: キーごとの登録成否、`lwin`/`rwin`個別指定が必要だったキー、代替が必要なキーの一覧、`WH_KEYBOARD_LL`使用時のレスポンス（遅延が無いか）、フック自己検知の動作確認。
- **判定**: 最終的なキーバインド一覧と、`RegisterHotKey`/`WH_KEYBOARD_LL`どちらで実装するかを確定する（「型に反映」：CoreCommandに割り当てるキーの確定）。

### S4. フォーカス移動（SetForegroundWindow）の実現性（B-3対応）【実デスクトップ実機検証で確定：failure-catalog.md F04, F16】
**結果**：CI実機検証では、検証ハーネス自身が起動した子プロセスからのフォーカス奪取が自己プライミングの有無にかかわらず成功してしまい（F04）、親子関係の影響を排除できなかった。**実デスクトップ（`dragonflyg4`）での再検証（S4r）で、自分が起動した子プロセス・無関係な第三者プロセスの両方に対し、plain `SetForegroundWindow`は明確に失敗し`mouse_event`自己プライミングは明確に成功することを確認し、限界は解消した（F16）**。自己プライミングは実機実証済みの既定実装として確定。
- **検証する前提**: ホットキー受信 → キュー → Reducer相当の非同期処理を経た後でも、`SetForegroundWindow`の呼び出しが許可されるか。既存WM（komorebi/GlazeWM）が採用する「自分自身へ`SendInput`でダミー入力を送出後に`SetForegroundWindow`を呼ぶ」パターン（feasibility.md §2）で許可が確保できるか。
- **方法**: `ThrowawayWm`で、ホットキー受信イベントを一度キューに入れ、数msの遅延（実際のCore処理時間を模する）を挟んでから、(a)素の`SetForegroundWindow`、(b)自己`SendInput`（ダミーのマウスイベント）後に`SetForegroundWindow`、をそれぞれ呼び、成功したかを前面ウィンドウの変化で確認する。遅延量を段階的に増やし、(a)がどこで失敗し始めるか、(b)がどこまで有効かを計測する。`AttachThreadInput`はデッドロック報告があるため採用しない。(b)でも不十分な場合のみ、`SPI_SETFOREGROUNDLOCKTIMEOUT`でシステム設定を変更する案（副作用が大きいため最終手段）を試す。
- **記録する内容**: 遅延量ごとの(a)(b)それぞれの成功/失敗、(b)の副作用（意図しないメニュー表示・マウスカーソル移動等）の有無。
- **判定**: (b)で十分な成功率が得られれば「Bで対処」（Shell実装のデフォルトパターンとして確定、既にarchitecture.md 8.1に反映済み）。それでも不十分な場合は`CoreCommand.FocusNext`の実現方法そのものを「型に反映」として見直す。

### S5. 分類・正規化の難所（C-2対応）【CI・実デスクトップ双方で未確定：failure-catalog.md F02, F21】
**結果**：GitHub Actions windows-latestランナーには モダンUWP版Calculatorが無く、`calc.exe`はクラシック`win32calc`として起動された（F02）。実デスクトップ（`dragonflyg4`）でも`calc.exe`は同様にトップレベル窓を取得できず、`ms-settings:`は`SystemSettings.exe`（WinUI）の起動は確認できたが2.5秒以内にウィンドウハンドルが得られなかった（F21、待機時間を延ばした再検証が必要）。ApplicationFrameHost仮説の直接確認はまだ取れていない。
- **検証する前提**: UWP/WinUIアプリの最上位HWND所有関係（`ApplicationFrameHost.exe`）、Cloakされた別デスクトップの窓、表示直後にスタイル・所有関係が変わる窓が、どう観測されるか。UWPアプリの実アプリ特定について、(a)実行ファイル名、(b)`Windows.UI.Core.CoreWindow`クラス名を持つ子ウィンドウ探索、(c)AUMID/パッケージファミリ名、の3手法を比較する（feasibility.md §9）。
- **方法**: `ThrowawayWm`に`WinEvent`のRaw観測をそのまま標準出力へ出すモードを足し、対象アプリ起動時のイベント列（`EVENT_OBJECT_SHOW`、`EVENT_OBJECT_CREATE`、スタイル変更等）と、その時点で取得できる属性（プロセス名、オーナー、Cloak状態）を記録する。UWPアプリについては(a)(b)(c)それぞれでAppId相当を導出し、通常表示時とフルスクリーン表示時（(b)が親子両方`ApplicationFrameHost.exe`を返す既知の例外がある）で結果を比較する。
- **対象アプリ**: UWP設定アプリ、WinUI3アプリ（あれば）、IME候補窓が出るIME、トースト通知、ツールチップ、Teams/Slackの通話ポップアップ。
- **記録する内容**: アプリごとのイベント列、属性が確定するまでの遅延、`ApplicationFrameHost.exe`経由かどうか、(a)(b)(c)各手法の安定性比較。
- **判定**: ほぼ確実に「型に反映」。Unknown暫定方針（5.4）の猶予時間や、`AppId`導出ロジック（5.3の`AppIs`の注記）に反映する。

### S6. バッチ適用と最大化/Snapからの復元（A-4対応）【CI実機検証済み：failure-catalog.md F09-F10】
**結果**：ハング窓を含む3窓を`DeferWindowPos`系で一括適用すると4000ms超ブロック。個別`SetWindowPos`+`SWP_ASYNCWINDOWPOS`では3窓全てが1ms以内にリターン。6.2の既定方針（個別+ASYNC）が実証された。最大化/Snap状態の窓での`RestoreBeforeMove`検証は未実施（実機デスクトップが必要）。
- **検証する前提**: 既定方針（個別`SetWindowPos` + `SWP_ASYNCWINDOWPOS`、feasibility.md §4でkomorebi/GlazeWM双方が採用していると確認済み）でちらつきが実用上問題になるか、`DeferWindowPos`系の方が明確に優れるか。最大化・Aero Snap状態の窓は`ShowWindow(SW_RESTORE)`を先に呼ばないと`SetWindowPos`が効かないか。
- **方法**: `ThrowawayWm`に4〜6枚のウィンドウを並べ替えるコマンドを足し、(a)個別`SetWindowPos`+`SWP_ASYNCWINDOWPOS`版、(b)`DeferWindowPos`系版を見比べる（目視でちらつきを確認、可能なら画面キャプチャで比較）。(b)では1窓をハング状態にした場合に残りの窓の適用が完了するかも確認する（`DeferWindowPos`はドキュメント上失敗時は全体中止が推奨されており、ハング耐性に不利な可能性がある）。最大化・Snap状態の窓を含めて試す。
- **記録する内容**: (a)(b)それぞれのちらつきの有無、`SW_RESTORE`が必要なケースの一覧、(b)でハング窓を含めた場合の挙動。
- **判定**: (a)で十分なら「Bで対処」で確定（既にarchitecture.md 6.2の既定方針）。(b)が明確に優れ、かつハング耐性を損なわないと確認できた場合のみ方針変更を検討する。「`RestoreBeforeMove`が必須なケースの判定条件」はCoreのPlanValidatorが持つ必要があれば「型に反映」。

### S7. 昇格（管理者）ウィンドウへの操作の失敗様式【実デスクトップで確定：failure-catalog.md F14, F17, F18, F23, F24】
**結果（確定）**：GitHub Actions windows-latestランナーのプロセス自身がAdministratorsグループ所属・整合性レベル**High**で動作しており、CIでは非対称性を観測できなかった（F14）。実デスクトップ（`dragonflyg4`）の通常セッションはAdministrators非所属・整合性レベル**Medium**であり（F17）、非昇格↔昇格の非対称性は実在する。ユーザーが実際にUAC承認して開いた昇格コマンドプロンプト（整合性High）に対し`SetWindowPos`（no-op移動）を呼んだ結果、**`returned=false`、`GetLastError=5`（`ERROR_ACCESS_DENIED`）**と明確に失敗することを実機で確認した（F23）。開発過程で`OpenProcess`は`PROCESS_QUERY_LIMITED_INFORMATION`が必要（通常の`PROCESS_QUERY_INFORMATION`では候補を見逃す）、`SetWindowPos`のP/Invoke宣言には`SetLastError = true`が必須、という2つの実装上の落とし穴も判明した（F24）。
- **検証する前提**: 管理者権限で実行されたウィンドウへ非特権プロセスから`SetWindowPos`等を呼ぶとどう失敗するか（サイレント失敗か、明示的なエラーか）。
- **方法**: 管理者権限で起動したアプリ（例：管理者権限のコマンドプロンプト）を対象に`SetWindowPos`を呼び、戻り値・`GetLastError`・実際にウィンドウが動いたかを確認する。
- **記録する内容**: 失敗の様式（戻り値、エラー、無反応）。
- **判定**: 「Bで対処」（`ERROR_ACCESS_DENIED=5`を`classify`で`Denied OtherDenial`として検出し、Excluded/Suspendedへ分類）で確定。

### S8. 画面構成変化（スリープ/モニタ抜き差し/RDP）の観測（E-3対応）
- **検証する前提**: スリープ復帰、モニター抜き差し、RDP接続/切断時にOS自身が窓を動かす挙動と、それを検知できるイベント（`WM_DISPLAYCHANGE`、`WM_DEVICECHANGE`等）。
- **方法**: `ThrowawayWm`を動かしたまま実際にスリープ→復帰、モニター抜き差し、RDP接続/切断を行い、WinEvent/ウィンドウメッセージのログを記録する。
- **記録する内容**: 変化中に発生するイベント量・順序、変化が収まったと判定できるタイミングの目安。
- **判定**: 「型に反映」（`ScreenReconfigurationInProgress`イベントの発火条件・収束判定ロジックの確定）。

### S9. 全画面アプリの検出（E-4対応）【CI実機検証済み：failure-catalog.md F03】
**結果**：ボーダレス全画面相当の窓に対し`SHQueryUserNotificationState`は`QUNS_QUIET_TIME`を返し検出に失敗。「ボーダレス and 矩形==モニター矩形」のジオメトリヒューリスティックは正しく検出。検出はジオメトリヒューリスティックを主手段とする方針に確定。
- **検証する前提**: `SHQueryUserNotificationState`や「モニター全体と同じ矩形かつ枠なし」判定で、全画面ゲーム・動画プレイヤーを検出できるか。
- **方法**: 動画プレイヤーを全画面にした状態、ブラウザのF11全画面、実際のゲーム（あれば）で、上記APIの戻り値とウィンドウ属性を記録する。
- **記録する内容**: 検出できたケース・できなかったケース。
- **判定**: 「Bで対処」または「型に反映」（Excluded判定条件に属性を追加する必要があるか）。

### S10. Explorer再起動時のTaskbarCreated・トレイ復旧（E-2対応）【CI・実デスクトップともにエンドツーエンドで確定：failure-catalog.md F15, F19】
**結果**：GitHub Actions windows-latestランナー上で`explorer.exe`強制終了→504ms後の自動再起動を確認（F15）。**実デスクトップ（`dragonflyg4`）での拡張検証で、`RegisterWindowMessage("TaskbarCreated")`→`Shell_NotifyIcon(NIM_ADD)`→Explorer強制終了→381ms後の自動再起動→`TaskbarCreated`ブロードキャストの実際の受信→トレイアイコン再追加成功、という全ステップがエンドツーエンドで成立することを確認した（F19）**。8.4の復旧ロジックは実証済みの確定事項。
- **検証する前提**: `taskkill /im explorer.exe`等でExplorerを再起動した際、通知領域アイコンが消え、`TaskbarCreated`メッセージで復旧できるか。
- **方法**: `ThrowawayWm`に`TaskbarCreated`（`RegisterWindowMessage`で取得）を受けてトレイアイコンを再登録する最小実装を足し、Explorer再起動で実際に復旧するか確認する。
- **記録する内容**: 復旧までの時間、WorkArea再取得が必要か。
- **判定**: 「Bで対処」。

### S11. 異なるDPIモニター間移動時の自己リサイズ（B-6対応）【CI・実デスクトップ双方で未実施：failure-catalog.md F22】
**結果**：GitHub Actions windows-latestランナーも実デスクトップ（`dragonflyg4`）も単一モニター（CIは1024x768、dragonflyg4は1920x1280、両方96 DPI相当）のため、複数DPIモニター間の移動は検証できなかった（F22）。複数DPI環境が用意できれば再検証する。
- **検証する前提**: 異なるDPIのモニター間でウィンドウを移動した際、`WM_DPICHANGED`を受けたPer-Monitor-V2対応アプリが自身でサイズを変える挙動と、非対応アプリ（OSがビットマップ拡大縮小するだけで2段階目のSetWindowPosは発生しない）との違い（feasibility.md §7）。
- **方法**: 100%と150%（または200%）のモニターを用意し（無ければ外部モニターかRDPで疑似的に作る）、複数アプリ（Per-Monitor-V2対応・非対応それぞれ1つ以上。`GetWindowDpiAwarenessContext`等でDPI Awarenessを事前に確認しておく）を移動させ、`SetWindowPos`後の実際のサイズ・Observedの時間的パターンを比較する。
- **記録する内容**: Per-Monitor-V2対応アプリと非対応アプリでの挙動差、対応アプリで2段階目のSetWindowPosが実際に観測されるタイミング。
- **判定**: 「型に反映」の可能性あり。ShellがDPI Awarenessを取得して対応/非対応で異なる完了判定をする設計（architecture.md 8.3）を、`SelfRepositionAfterMs`と区別する`DpiSelfResize`のような振る舞い種別として疑似OSに反映する必要があるか判断する。

### S12. Cloakの自己/他者判別と未文書化COMによる制御（21章/R3-8対応）【S12aはCI検証済み、S12bは調査のみで見送り：failure-catalog.md F26/F34】
**結果**：§21のワークスペース非表示方式の判断は2つの軸を持つ——(a) WM自身が付けたCloakを他要因（他の仮想デスクトップ、UWPゴーストフレーム）によるCloakと`CloakState`の理由ビットだけで区別できるか、(b) 未文書化の`IApplicationView::SetCloak`で非表示/復帰できるか。
- **S13b（オーナー最小化）**：仮説通りの挙動が得られず、データが得られなかった（F26）。
- **S12a（同一プロセス＋公開API）**：`IVirtualDesktopManager`（公開・文書化API）で同一プロセスの窓を別の仮想デスクトップへ移すと、`DWMWA_CLOAKED`が正しく`DWM_CLOAKED_SHELL=2`を返した（F34）。Cloak理由ビットAPI自体が機能することは確認できたが、(a)の本題（自己要因と他要因の判別）はまだ検証していない——このテストはCloakを引き起こす役が1つしかいない状況だったため、判別する必要が無かった。21章に、理由ビットに依存しない代替案（WM専用の隠しデスクトップのGUIDと`GetWindowDesktopId`を比較する、未検証）を記録した。
- **S12b（他プロセスへの適用）**：`MoveWindowToDesktop`（公開API）は自分のプロセスが所有する窓にしか使えず、他プロセスの窓には`E_ACCESSDENIED`で失敗することが判明（Microsoft公式のアーカイブされたSDKブログ、および複数の実例で確認済み）。既存の保守されたラッパー（[Grabacr07/VirtualDesktop](https://github.com/Grabacr07/VirtualDesktop)、MIT）のソースを調査したが、他プロセス対応にはWindowsビルドごとのGUID/インターフェース形状表と動的プロキシアセンブリ生成が必要で、今回の作業量では見送った。(b)（未文書化COMでのCloak制御そのもの）も依然未着手。
- **検証する前提**: `DWMWA_CLOAKED`の理由ビットが、Cloakの原因ごとに実際に異なる値を返すか。WM自身が`IApplicationView::SetCloak`相当の操作でCloak/解除を行えるか。
- **方法**: `Win+Ctrl+D`（公開のシェルショートカット）で仮想デスクトップを作成し、`IVirtualDesktopManager`（公開API）で同一プロセスの窓を移して`DwmGetWindowAttribute(DWMWA_CLOAKED)`を観測する。他プロセスへの適用、および未文書化COMでの自己Cloak制御は別の手段が必要。
- **記録する内容**: 誘発方法ごとの理由ビットの値、COM呼出の成否。
- **判定**: 「型に反映」（部分確定。Cloak理由ビットAPIは機能する。(a)の判別そのもの、(b)、他プロセス対応は未決のまま。(a)(b)のどちらかが満たせない場合はCloakを21章の選択肢から外す、という既存方針は変わらず）。

### S13. ObservedPresenceの方式非依存性（4.5対応）【CI実機検証済み：failure-catalog.md F25】
**結果**：テスト窓を`(IsVisible, IsMinimized, DWMWA_CLOAKED)`で観測すると、「通常表示」=(true,false,0)、`SW_HIDE`=(false,false,0)、`SW_MINIMIZE`=(true,true,0)、「画面外へ移動」=(true,false,0)。**「画面外へ移動」と「通常表示」が同一タプルになり区別できない**。21章の「画面外へ移動」方式を使う場合、`ObservedPresence`の判定には矩形位置も必要（4.5は元々「Shellが自分の非表示方式を知って判定する」前提なので設計の誤りではないが、具体的に明記すべき）。
- **検証する前提**: 4.5の`ObservedPresence`（`VisibleToUser`/`HiddenByWmMethod`/`HiddenOther`）が、窓の非表示方式（`SW_HIDE`/最小化/画面外移動/Cloak）それぞれについて、Shell側で観測可能な属性から一意に導出できるか。
- **方法**: テスト窓を4つの状態（通常表示、`SW_HIDE`、`SW_MINIMIZE`、画面外へ移動）それぞれにし、`IsVisible`/`IsMinimized`/`DWMWA_CLOAKED`を記録して状態ごとに比較する。
- **記録する内容**: 状態ごとの属性タプル、タプルが一意かどうか。
- **判定**: 「型に反映」（確定。4.5に、非表示方式ごとにShellが追加で見るべき属性を明記する）。

### S14. WH_KEYBOARD_LLの無言脱落と自己回復（8.1対応）【CI実機検証済み：failure-catalog.md F27→F33で説明付きの結果に確定】
**結果**：フックのコールバックを約1150ms停止（文書上の上限1000msを超過）させ、完全に戻ってくるのを待ってから新しいキー入力を送って確認したが、脱落は観測されなかった（F27）。再検証（F33）で`LowLevelHooksTimeout`レジストリ値がこの環境では未設定であることを確認し、「文書の1000msは設定可能な上限であって保証された既定値ではない」という説明が得られた。再登録による自己回復ロジックは、脱落自体が起きなかったため一度も試せていない。
- **検証する前提**: `LowLevelHooksTimeout`を超えてブロックしたフックコールバックが実際に無言で外れるか、再登録で回復できるか（feasibility.md §3）。
- **方法**: `WH_KEYBOARD_LL`フックを別スレッドで登録し、コールバックを意図的に1秒超停止させ、完全に戻ってきた後の新しいキー入力への反応（呼び出し回数）で脱落を判定する。`LowLevelHooksTimeout`レジストリ値も読み取る。
- **記録する内容**: 停止前後の呼び出し回数、レジストリ値、再登録の成否。
- **判定**: 「型に反映」（確定ではないが説明付き。このランナーでは脱落しないという結果自体は再現性があるが、再登録ロジックは別途、実際に脱落する環境で確認する必要がある）。

### S15. ProcessKeyによるHWND再利用検知（4.5 Sticky規則対応）【CI実機検証済み：failure-catalog.md F28】
**結果**：2つの子プロセスを連続起動し`(Pid, CreationTime)`を記録すると、PID（9492→6712）・作成時刻とも異なり、キーの衝突は無かった。メカニズム自体は想定通り動作することを確認（実際のPID再利用は偶然に依存するため再現できていない）。
- **検証する前提**: `GetWindowThreadProcessId`+`GetProcessTimes`による`(Pid, CreationTime)`の組が、HWND再利用（プロセス再起動後の同一HWND）を正しく検知できるか。
- **方法**: 子プロセスを起動→終了→再起動し、2回分の`(Pid, CreationTime)`を比較する。
- **記録する内容**: 各回のPID・作成時刻、キーの衝突有無。
- **判定**: 「無視してよい」（メカニズム確認。4.5のSticky規則の前提は妥当）。

### S16. Hung→Responding復帰（4.5 Responsiveness対応）【CI実機検証済み：failure-catalog.md F29】
**結果**：ハング中（最後のポンプから約6秒）は`SendMessageTimeout`が`returned=0, elapsedMs=0`（ハング）。その後子窓がメッセージポンプを再開すると、同じ呼出が`returned=1, elapsedMs=17`（応答）に変わった。**復帰方向を実機で初めて確認**（既存のS2/F05〜F08は進入方向のみ）。
- **検証する前提**: ハングと判定された窓が応答を取り戻した後、`SendMessageTimeout`が正しく「応答あり」に戻るか。
- **方法**: S2のハング窓ハーネスを拡張し、一定時間停止後にメッセージポンプを再開させ、前後で`SendMessageTimeout(WM_NULL, SMTO_ABORTIFHUNG)`を呼ぶ。
- **記録する内容**: 停止中・復帰後それぞれの戻り値・経過時間。
- **判定**: 「型に反映」（確定。4.5のResponsiveness信念の復帰規則を実証）。

### S17. RequestClose/WM_CLOSEの丁寧な拒否（7.1 R6-2対応）【CI実機検証済み：failure-catalog.md F30】
**結果**：`WM_CLOSE`を無視する窓へ`PostMessage(WM_CLOSE)`を送ると、呼出は即時（0ms、非ブロッキング）に返り、窓は生存したまま（WndProcは`WM_CLOSE`を受信）。通常の窓（`DestroyWindow`を呼ぶ）は実際に閉じた。
- **検証する前提**: `RequestClose`Effect（`WM_CLOSE`の送信）が非ブロッキングで、アプリ側が拒否できる「丁寧な要求」として機能するか。
- **方法**: `WM_CLOSE`を無視する窓・通常に閉じる窓の両方へ`PostMessage(WM_CLOSE)`を送り、呼出時間と窓の生死を確認する。
- **記録する内容**: 呼出のブロック時間、窓の生死、WndProcでの受信回数。
- **判定**: 「Bで対処」（確定）。

### S18. MoveSizeEnded矩形/モニタ捕捉（4.4 R4-7対応、単一モニタ部分のみ）【CI実機検証済み：failure-catalog.md F31】
**結果**：投稿メッセージによる`WM_ENTERSIZEMOVE`/`WM_EXITSIZEMOVE`のシミュレーションで、ドロップ時点の矩形`(400,250,700,450)`（意図した移動先と一致）とモニタハンドル`65537`を正しく捕捉した。単一モニタでの捕捉機構のみの確認であり、クロスモニタでの実際の転送（R3-4の本来の動機）はS11と同じ理由で未検証。
- **検証する前提**: `WM_EXITSIZEMOVE`のハンドリングで、ドロップ時点の矩形・モニタを正確に捕捉できるか（v2.6の`MoveSizeEnded`ペイロード変更、R4-7）。
- **方法**: テスト窓へ投稿メッセージで`WM_ENTERSIZEMOVE`→移動→`WM_EXITSIZEMOVE`を発生させ、ハンドラ内で`GetWindowRect`/`MonitorFromWindow`を呼ぶ。実際のドラッグ（`SendInput`）は使わず、過去のVK_MENUハング教訓を踏まえてより安全な投稿メッセージ方式を採った。
- **記録する内容**: 捕捉した矩形・モニタハンドル、意図した移動先との一致。
- **判定**: 「Bで対処」（単一モニタの捕捉機構は確定。クロスモニタはS11同様実機待ち）。

### S19. DpiAwarenessContext問い合わせの健全性確認（8.3対応、低優先）【CI実機検証済み：failure-catalog.md F32、実装の穴あり】
**結果**：`GetWindowDpiAwarenessContext`は自作/子WinFormsウィンドウで`0x6010`、`notepad.exe`で`0x22`という異なる値を返し、API呼出経路自体は機能した。ただしスパイクのラベル関数が既知sentinel値（-1〜-5）しか判定しておらず両方`unknown`と表示された（実装の手抜き、`GetAwarenessFromDpiAwarenessContext`呼出で直る見込み）。実際のマルチDPI問題（S11）は未解決のまま。
- **検証する前提**: `GetWindowDpiAwarenessContext`の呼出経路がこの環境で機能するか（マルチDPI問題自体の解決ではない）。
- **方法**: 複数の既存テスト窓・外部プロセス（notepad.exe）に対し呼び出し、返り値を記録する。
- **記録する内容**: プロセスごとの返り値。
- **判定**: 「無視してよい」（低優先。本質的な問題はS11のまま実機待ち）。

### S20. SwitchSettle用、前面切替のフック遅延と揺れの実測（4.4/R4-5対応）【CI実機検証済み：単純なフォーカス交換では揺れ無し、実際のhide/show順序はS20bへ】
**結果**：自プロセスの2窓（A/B）間を`mouse_event`自己プライミング付き`SetForegroundWindow`で5往復（計10回の切替）させたところ、全ての切替で`EVENT_SYSTEM_FOREGROUND`は2〜3msで目的の窓へ届き、その後800ms観測しても揺れ（別のhwndへの中間イベント）は一度も発生しなかった。**ただしこれは「どちらも既にvisibleな2窓間の単純なフォーカス交換」であり、`SwitchWorkspace`が実際に行う「旧Workspaceの窓を隠してから新Workspaceの窓を見せてフォーカスを要求する」という順序を再現していない**——揺れの原因になり得るのは隠す操作自体がOS側で引き起こす一時的なフォーカス再割り当てであり、本spikeはそれを検証対象にしていなかった。実際のhide/show順序での揺れの有無はS20bで追加検証する。
- **検証する前提**: `FocusState.SwitchSettle`（Workspace切替直後の揺れの吸収、§4.4/R4-5）の`SettleMs`は、実際のOS側のタイミング挙動の実測が無いまま未決定だった（architecture.md v2.29）。実運用の`ForegroundObserved`は`SetWinEventHook(WINEVENT_OUTOFCONTEXT)`で`EVENT_SYSTEM_FOREGROUND`を購読して届く（§8.1/21章）ので、ポーリングではなくこのフック自体で一次データを取る必要がある。
- **方法**: `Phase0CiSpikes`の`spikeS20_SwitchSettleTiming`で、自プロセスの2窓（A/B）間を`mouse_event`自己プライミング付き`SetForegroundWindow`で5往復させ、各切替について(a)呼び出しから`EVENT_SYSTEM_FOREGROUND`が最初に届くまでの遅延、(b)その後800ms観測窓の中で別のhwndへのイベントが続く「揺れ」の有無、を記録する。
- **記録する内容**: 往復ごとの最初のイベント遅延（最小/最大/平均の見立て）、揺れの発生回数、`SetForegroundWindow`のAPI戻り値。
- **判定**: 「フックの配送自体は速くCI実機では揺れ無し」という前提条件が確認できた（型への反映はまだ早い——下記S20bの結果を合わせて判断する）。CI実機（仮想化・ヘッドレス寄り）での一次データであり、S4がS4rで実デスクトップ再検証を要したのと同様、実デスクトップでの再検証（S20r）が必要かはS20bの結果を見て判断する。

### S20b. SwitchWorkspace実際の順序（hide→show→RequestForeground）での揺れの実測（4.4/R4-5対応）【CI実機検証済み：揺れ無し、SwitchSettleMs=150Lの採用根拠（v2.40）】
**結果**：`ShowWindow(SW_HIDE)`で前面窓を隠した直後にself-priming付き`SetForegroundWindow`で別窓へ要求する、実際の`SwitchWorkspace`の順序を5往復×2方向（計10回）再現したところ、全ての切替で目的窓への到達は3〜4ms、目的窓到達前に届いた別hwndへのイベントは一度も無かった（S20と合わせて計20回の切替で揺れ0件）。
- **検証する前提**: S20は「どちらも既にvisibleな2窓間の単純なフォーカス交換」であり、`SwitchSettle`が本来吸収したい経路——`SwitchWorkspace`が旧Workspaceの窓を`SetPresence`で隠し、新Workspaceの窓を見せてからフォーカスを要求する——を再現していなかった。隠す操作自体がOS側の一時的なフォーカス再割り当てを引き起こすかどうかを検証する。
- **方法**: `Phase0CiSpikes`の`spikeS20b_WorkspaceHideShowSettle`で、`ShowWindow(SW_HIDE)`で前面窓を隠した直後に（self-priming付き）`SetForegroundWindow`で別窓へ要求する、という実際の`SwitchWorkspace`の順序を5往復×2方向（計10回）再現し、目的の窓以外への`EVENT_SYSTEM_FOREGROUND`が先行して届くか（＝本当の意味での「揺れ」）を記録する。
- **記録する内容**: 往復ごとの目的窓への到達遅延、目的窓到達前に届いた別hwndへのイベントの有無・回数。
- **判定**: 「CI実機（自プロセス内の2窓、仮想化・ヘッドレス寄り）では`SwitchSettle`の揺れ吸収機構自体は不要」という一次データが得られた。S4がS4rで実デスクトップ再検証を要した前例に鑑み、測定値（2〜4ms）そのままではなく十分な安全マージンを取った`SwitchSettleMs = 150L`（`RuntimePolicy`）を暫定採用し、`WmState.step`へ配線した（v2.40）。実デスクトップでの再検証（S20r）は持ち越し。

### S21. SetRectの往復精度（M3、Placement `Mismatch → Reevaluate`設計の前提）【CI実機検証済み：自作WinForms窓では位置・サイズに関わらず完全一致】
**結果**：現在位置で測り直したシャドウは`(L=7,T=0,R=7,B=7)`——S1が一度だけ測った値と完全に一致し、**位置に依存しない定数**であることを新たに確認した。このシャドウを使って変換した3つの異なる位置・サイズ（workArea相当のフルサイズ、別の四分割位置、3つ目の任意位置）全てで、要求した矩形と`DWMWA_EXTENDED_FRAME_BOUNDS`の読み戻し値が1px単位で完全一致した（`exact-match=true`×4）。自作WinForms窓（S1と同じ窓種）に限ればMismatch→Reevaluate段階1導入の前提（M3）は満たされる。他の窓種（Electron/UWP/カスタム非クライアント領域描画）のシャドウがこの値と異なる・位置依存である可能性はS1同様未確認のまま。
- **検証する前提**: `Mismatch → Reevaluate`（§7.2、v2.24以来安全上の理由で対象外のままだった項目）の設計相談（Opus敵対的レビュー）で、「段階1（`PendingDeadline`時点でのMismatch分類）はコードの論理だけで安全」としつつ、その前提として「可視枠（`DWMWA_EXTENDED_FRAME_BOUNDS`）と要求した矩形（`SetWindowPos`に渡す`GetWindowRect`空間の値）が正確に一致するか」の実測を先に求めた——換算がシステム的にずれていれば、段階1導入後は全ての配置が常にMismatchと判定され「止まる」が永続化する逆効果になる。S1が測った「シャドウ」（自作WinForms窓で`diff(L,T,R,B)=(7,0,7,7)`px）が位置・サイズを変えても同じ値のまま使えるかを確認する。
- **方法**: `Phase0CiSpikes`の`spikeS21_SetRectRoundTrip`で、窓の現在位置でシャドウを測り直し、それを使って3つの異なる位置・サイズへ`SetWindowPos`を発行、`DwmGetWindowAttribute(DWMWA_EXTENDED_FRAME_BOUNDS)`で読み戻した可視枠が要求した矩形と1px単位で一致するかを記録する。
- **記録する内容**: 測り直したシャドウ値、各ターゲットでの要求矩形・観測されたDWM可視枠・一致したかどうか。
- **判定**: 全ターゲットで一致すれば、`Mismatch → Reevaluate`段階1導入の前提（M3）が満たされる。一致しない（固定オフセットがあるか、位置依存でシャドウが変わる）場合は、Shell側の換算ロジックに追加の補正が必要であり、段階1の導入前にその補正方法を設計し直す必要がある。

### S22. SetWindowPos(ASYNC)発行からLOCATIONCHANGE観測までの遅延分布（M1、Placement `Mismatch → Reevaluate`設計の前提）【未検証：CIで実行予定】
- **検証する前提**: `Mismatch → Reevaluate`の設計相談（Opus敵対的レビュー）のM1：`pendingDeadlineMs = 2000`が、段階1（`PendingDeadline`満了時点でのMismatch分類）の判定時刻として十分な余裕を持つか。`SetWindowPos`は`SWP_ASYNCWINDOWPOS`で非同期に発行されるため、OSが要求を受け付けてから実際に矩形が変わり`EVENT_OBJECT_LOCATIONCHANGE`が届くまでの遅延を実測する必要がある。
- **方法**: `Phase0CiSpikes`の`spikeS22_SetRectDispatchDelay`で、自作WinForms窓へ5つの異なる位置・サイズを`SetWindowPos(..., SWP_ASYNCWINDOWPOS)`で順に発行し、各発行から自分自身の`EVENT_OBJECT_LOCATIONCHANGE`（`idObject = OBJID_WINDOW`）が最初に届くまでの遅延を`SetWinEventHook(WINEVENT_OUTOFCONTEXT)`で記録する。
- **記録する内容**: 発行ごとの最初のLOCATIONCHANGEまでの遅延（ms）、LOCATIONCHANGEが一度も届かなかったケースの有無。
- **判定**: 遅延が`pendingDeadlineMs`（2000ms）に対して十分小さければ、段階1の判定時刻として問題ない根拠になる。極端に遅い・届かないケースがあれば、`pendingDeadlineMs`自体の見直しが必要。

### S23. 最小幅での丸めに未丸め矩形の中間観測が出るか（M2、Placement `Mismatch → Reevaluate`設計の前提）【未検証：CIで実行予定】
- **検証する前提**: M2：`WM_GETMINMAXINFO`の`MinTrackSize`で丸められる窓へ、それより狭い幅を`SetWindowPos`で要求したとき、Windowsは要求どおりの（未丸めの）矩形を一度`EVENT_OBJECT_LOCATIONCHANGE`で見せてから丸め後の矩形へ訂正するのか、最初から丸め後の矩形だけを見せるのか。前者なら、既存の`checkPendingSatisfied`（および将来のMismatch段階2早期判定）が、この中間状態を実際には起きていない「一致」や「不一致」と誤認する危険がある。
- **方法**: `Phase0CiSpikes`の`spikeS23_MinWidthRoundingObservation`で、`WM_GETMINMAXINFO`をオーバーライドして`MinTrackSize.X = 400px`を強制する`MinWidthForm`を作り、幅200pxへ`SetWindowPos`を発行、以降に届く全ての`EVENT_OBJECT_LOCATIONCHANGE`それぞれで`GetWindowRect`の幅を記録する。
- **記録する内容**: LOCATIONCHANGEのシーケンス（各発火時点の幅）、最終的な幅、400px以外の（未丸めの）幅が一度でも観測されたか。
- **判定**: 未丸めの中間幅が観測されなければ、`checkPendingSatisfied`等の既存実装はこの点について追加の対策が要らない。観測される場合は、どのタイミングで最終値に収束するかを見て、早期判定（段階2）やMinWidth証拠収集に「最後の安定した観測を使う」等の対策を検討する。

### S24. MOVESIZESTART/LOCATIONCHANGE/MOVESIZEENDの到着順（M6、Placement `Mismatch → Reevaluate`設計の前提）【未検証：CIで実行予定】
- **検証する前提**: M6：Mismatch判定の条件6（ユーザーのドラッグ中の観測を不一致と誤認しない）を実装する際、`EVENT_SYSTEM_MOVESIZEEND`（ドラッグ終了、段②の`MoveAttributed`の契機）が、ドラッグ中の`EVENT_OBJECT_LOCATIONCHANGE`群より後に届くか。また、S18と同じ理由（実際のOSモーダルドラッグループに入るとハングの危険がある、S4のAltキー合成入力での既往症参照）で、`WM_ENTERSIZEMOVE`/`WM_EXITSIZEMOVE`を直接postする安全な技法のままで、そもそも`EVENT_SYSTEM_MOVESIZESTART`/`MOVESIZEEND`自体が発火するのかも未確認。
- **方法**: `Phase0CiSpikes`の`spikeS24_MoveSizeEventOrder`で、S18と同じ投稿メッセージ技法（`WM_ENTERSIZEMOVE`→`SetWindowPos`×2→`WM_EXITSIZEMOVE`）を使い、`EVENT_SYSTEM_MOVESIZESTART`/`EVENT_SYSTEM_MOVESIZEEND`/`EVENT_OBJECT_LOCATIONCHANGE`の到着順を記録する。
- **記録する内容**: 3種のイベントの到着順・タイムスタンプ、`MOVESIZESTART`/`MOVESIZEEND`が一度も発火しなかったかどうか。
- **判定**: `MOVESIZESTART`/`MOVESIZEEND`が発火しないなら、この投稿メッセージ技法ではM6に答えられないと判明したことになり、実際のマウス入力を使う実デスクトップでの再検証（S11r等と同様の限定）へ持ち越す。発火する場合は、到着順が条件6の実装（`MoveAttributed`をMismatch判定より先に読めるか）の根拠になる。

### S25. 最小化時のMINIMIZESTART/LOCATIONCHANGEの到着順（M7、Placement `Mismatch → Reevaluate`設計の前提）【未検証：CIで実行予定】
- **検証する前提**: M7：Mismatch判定の条件5（最小化中の窓の観測を不一致と誤認しない）の実装は、`IsMinimized`信念が最小化後の`-32000`付近の矩形観測より先に（または同じstepで）確定していることを前提にする。`EVENT_SYSTEM_MINIMIZESTART`と、最小化による`EVENT_OBJECT_LOCATIONCHANGE`のどちらが先に届くかを確認する。
- **方法**: `Phase0CiSpikes`の`spikeS25_MinimizeEventOrder`で、自作WinForms窓を`ShowWindow(SW_MINIMIZE)`し、`EVENT_SYSTEM_MINIMIZESTART`と`EVENT_OBJECT_LOCATIONCHANGE`の到着順・各時点の`GetWindowRect`を記録する。
- **記録する内容**: 2種のイベントの到着順・タイムスタンプ・各時点の矩形。
- **判定**: `MINIMIZESTART`が`LOCATIONCHANGE`より先に届けば、条件5の実装（最小化の信念を先に確定させてから矩形観測を評価する順序）はこのままで安全。逆順なら、矩形観測だけでは最小化中かどうかを即座に判定できない期間があることになり、条件5の実装に追加の猶予（デバウンス等）が必要になる。

## 失敗カタログのテンプレート（`docs/failure-catalog.md`、Phase 0完了時に作成）

```markdown
# Failure Catalog（Phase 0）

| ID | スパイク | 観測内容 | 分類（無視/型に反映/Bで対処） | architecture.mdへの反映先 |
|---|---|---|---|---|
| F01 | S1 | ... | ... | 4.2 座標系の契約 |
| ... | ... | ... | ... | ... |
```

Phase AのPolicy/Core型設計（architecture.md 18章 Phase Aゲート）は、この表の「型に反映」の行を元に決める。「無視してよい」「Bで対処」はPhase A着手の前提にしない。

## 所要期間の見積り

S1〜S11は`ThrowawayWm`1本への機能追加として並行して進められるものが多い。目安として3〜5日（S4・S5・S11は実機条件の確保に時間がかかる可能性がある）。architecture.md 18章のPhase 0完了条件（失敗カタログの作成と分類）を満たした時点で終了とし、全項目を完璧に検証し終えることを目的にしない。
