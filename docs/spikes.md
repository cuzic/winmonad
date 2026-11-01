# Phase 0 技術スパイク計画

対象: [architecture.md](./architecture.md) 18章「Phase 0 — 捨てる前提の素朴なWMによる失敗カタログ収集」の具体化。
関連: [feasibility.md](./feasibility.md)（各スパイクの前提となるWin32 APIの仕様・実務上の注意点のリサーチ結果）、[failure-catalog.md](./failure-catalog.md)（S1, S2, S3, S4, S5, S6, S9をGitHub Actions windows-latestランナー上で実際に実行した結果。各スパイクの見出しに反映済み）。

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

### S2. ハング検知とEffect適用のタイムアウト（ADR-012, E-1対応）【CI実機検証済み：failure-catalog.md F05-F08】
**結果**：`SetWindowPos`は`SWP_ASYNCWINDOWPOS`なしでハング窓に対し4000ms超ブロック、付きでは0msで返った。`IsHungAppWindow`は6秒後に正しく`true`。`SendMessageTimeout(SMTO_ABORTIFHUNG)`は即時(0ms)に中断。ADR-012の前提が実機で実証された。
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

### S4. フォーカス移動（SetForegroundWindow）の実現性（B-3対応）【CI実機検証済みだが結論保留：failure-catalog.md F04】
**結果**：検証ハーネス自身が起動した別プロセスからのフォーカス奪取は、自己プライミングの有無にかかわらず成功した。ただし対象が自分の子プロセスであるため「フォアグラウンドプロセスが呼び出し元によって起動された」という許可条件が別途働いた可能性があり、無関係な第三者プロセス相手の結論ではない。自己プライミングは既定実装として維持し、Phase Bのdogfoodingで再検証する。
- **検証する前提**: ホットキー受信 → キュー → Reducer相当の非同期処理を経た後でも、`SetForegroundWindow`の呼び出しが許可されるか。既存WM（komorebi/GlazeWM）が採用する「自分自身へ`SendInput`でダミー入力を送出後に`SetForegroundWindow`を呼ぶ」パターン（feasibility.md §2）で許可が確保できるか。
- **方法**: `ThrowawayWm`で、ホットキー受信イベントを一度キューに入れ、数msの遅延（実際のCore処理時間を模する）を挟んでから、(a)素の`SetForegroundWindow`、(b)自己`SendInput`（ダミーのマウスイベント）後に`SetForegroundWindow`、をそれぞれ呼び、成功したかを前面ウィンドウの変化で確認する。遅延量を段階的に増やし、(a)がどこで失敗し始めるか、(b)がどこまで有効かを計測する。`AttachThreadInput`はデッドロック報告があるため採用しない。(b)でも不十分な場合のみ、`SPI_SETFOREGROUNDLOCKTIMEOUT`でシステム設定を変更する案（副作用が大きいため最終手段）を試す。
- **記録する内容**: 遅延量ごとの(a)(b)それぞれの成功/失敗、(b)の副作用（意図しないメニュー表示・マウスカーソル移動等）の有無。
- **判定**: (b)で十分な成功率が得られれば「Bで対処」（Shell実装のデフォルトパターンとして確定、既にarchitecture.md 8.1に反映済み）。それでも不十分な場合は`CoreCommand.FocusNext`の実現方法そのものを「型に反映」として見直す。

### S5. 分類・正規化の難所（C-2対応）【CI実機検証を試みたが環境制約で不可：failure-catalog.md F02】
**結果**：GitHub Actions windows-latestランナーには モダンUWP版Calculatorが無く、`calc.exe`はクラシック`win32calc`として起動された。ApplicationFrameHost仮説自体をこの環境では検証できない。実際のWindows 10/11デスクトップでの再検証が必要（Phase 0〜B）。
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

### S7. 昇格（管理者）ウィンドウへの操作の失敗様式
- **検証する前提**: 管理者権限で実行されたウィンドウへ非特権プロセスから`SetWindowPos`等を呼ぶとどう失敗するか（サイレント失敗か、明示的なエラーか）。
- **方法**: 管理者権限で起動したアプリ（例：管理者権限のコマンドプロンプト）を対象に`SetWindowPos`を呼び、戻り値・`GetLastError`・実際にウィンドウが動いたかを確認する。
- **記録する内容**: 失敗の様式（戻り値、エラー、無反応）。
- **判定**: 「Bで対処」（検出してExcluded/Suspendedへ分類）。

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

### S10. Explorer再起動時のTaskbarCreated・トレイ復旧（E-2対応）
- **検証する前提**: `taskkill /im explorer.exe`等でExplorerを再起動した際、通知領域アイコンが消え、`TaskbarCreated`メッセージで復旧できるか。
- **方法**: `ThrowawayWm`に`TaskbarCreated`（`RegisterWindowMessage`で取得）を受けてトレイアイコンを再登録する最小実装を足し、Explorer再起動で実際に復旧するか確認する。
- **記録する内容**: 復旧までの時間、WorkArea再取得が必要か。
- **判定**: 「Bで対処」。

### S11. 異なるDPIモニター間移動時の自己リサイズ（B-6対応）
- **検証する前提**: 異なるDPIのモニター間でウィンドウを移動した際、`WM_DPICHANGED`を受けたPer-Monitor-V2対応アプリが自身でサイズを変える挙動と、非対応アプリ（OSがビットマップ拡大縮小するだけで2段階目のSetWindowPosは発生しない）との違い（feasibility.md §7）。
- **方法**: 100%と150%（または200%）のモニターを用意し（無ければ外部モニターかRDPで疑似的に作る）、複数アプリ（Per-Monitor-V2対応・非対応それぞれ1つ以上。`GetWindowDpiAwarenessContext`等でDPI Awarenessを事前に確認しておく）を移動させ、`SetWindowPos`後の実際のサイズ・Observedの時間的パターンを比較する。
- **記録する内容**: Per-Monitor-V2対応アプリと非対応アプリでの挙動差、対応アプリで2段階目のSetWindowPosが実際に観測されるタイミング。
- **判定**: 「型に反映」の可能性あり。ShellがDPI Awarenessを取得して対応/非対応で異なる完了判定をする設計（architecture.md 8.3）を、`SelfRepositionAfterMs`と区別する`DpiSelfResize`のような振る舞い種別として疑似OSに反映する必要があるか判断する。

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
