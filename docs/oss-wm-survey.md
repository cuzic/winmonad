# 既存OSSウィンドウマネージャー実装の設計判断調査

本調査は、`docs/feasibility.md`がkomorebi・GlazeWMを対象にWin32 API挙動レベル（ホットキー競合、SetForegroundWindow、ハング検知、DWM境界、DPI、Named Pipe、UWP識別）で行った検証を前提に、**より広い意味でのアーキテクチャ上の設計判断**を対象として、12個のOSSウィンドウマネージャーの実ソースコードを読んで調査したものである。Opusモデルによるエージェント調査で、全リポジトリをローカルにshallow cloneしHEADコミット時点のソースを直接読んだ上でまとめている。裏付けが弱い点は本文中に`[unverified]`と明記する。

対象リポジトリとHEAD（shallow clone、取得日時の前後関係は不明。機能がそのバージョンで存在することのみ確認できる）：

| リポジトリ | HEAD |
|---|---|
| xmonad | 7d51cb0 |
| xmonad-contrib | acb3cc6 |
| i3 | 8a97b0f |
| sway | 1652c54 |
| bspwm | e11eff4 |
| sxhkd | 2cc7aca |
| awesome | c463e19 |
| Hyprland | ecbcfe6 |
| yabai | dd84572 |
| komorebi | 72aa4c9 |
| glazewm | 5709ad0 |
| dwm | 1563572 |

---

## 1. xmonad（Haskell）

**状態モデル**（`src/XMonad/StackSet.hs:141-184`）
- `StackSet { current :: Screen; visible :: [Screen]; hidden :: [Workspace]; floating :: Map a RationalRect }`。
- `Screen { workspace; screen :: sid; screenDetail :: sd }`：ワークスペース↔モニターの対応はこの構造自体が持つ。
- `Stack a = Stack { focus; up; down }`はzipper。「データ構造自体がフォーカスを構築的に追跡する」ため、Core自体はフォーカス履歴を一切保持しない。
- xmonad-contribの`XMonad.Hooks.RefocusLast`が履歴を追加するが、ワークスペースごとに`Recent { previous, current }`の2件のみ（`RefocusLast.hs:109-114`）。
- `ScreenDetail`は`SD { screenRect :: Rectangle }`のみ（`Core.hs:157`）。DPI・スケールに相当する概念は一切ない。
- Floating窓はスクリーン相対の`RationalRect`として保存し、配置計算時にスクリーンごとにスケールする（`Operations.hs:198, 254`）。

**レイアウトとクラッシュ**
- `LayoutClass`は`runLayout`/`doLayout`/`pureLayout`/`handleMessage`/`pureMessage`を持つ（`Core.hs:290-339`）。
- `pureLayout :: layout a -> Rectangle -> Stack a -> [(a, Rectangle)]`が純粋経路。コメントに「エラーのリスクを抑え、テストを大幅に容易にする」とある。
- クラッシュ時は`Operations.hs:193`で`runLayout wsp ... `catchX` runLayout wsp { W.layout = Layout Full } viewrect`としてFullレイアウトへフォールバックする。
- `catchX`（`Core.hs:195-203`）はstderrへログし、`ExitCode`のみ再throwし、フォールバックは**アクション前の状態**に対して実行する。
- `userCode`/`userCodeDef`がmanageHook・logHook等のユーザーコード呼出し全てをラップする。これはwinmonad §5.6の`invokeLayout`/`LayoutCrashed`と同じ発想だが、型付きエラーを持たない点が異なる。

**OS側の失敗**
- `Main.hs:174-176`：「既定のハンドラを無効化し、全エラーを無視するハンドラに置き換える（醜いとわかっているが）」。失敗の分類体系は一切存在しない。

**モニター変化**
- `rescreen`（`Operations.hs:361-371`）は新しいスクリーン列へワークスペースを**位置で**zipして割り当てる。スクリーンの識別はインデックスに過ぎないため、ホットプラグや並び替えでワークスペースが別の物理モニターへ移ってしまい得る。

**診断性とテスト**
- `tests/Properties/*.hs`に`StackSet`に対するQuickCheckプロパティ（Focus, Screen, Shift, Swap等）がある。
- 再起動時の状態はファイルへ`show`される（`writeStateToFile`、`Operations.hs:647-665`）。Checkpointに近い仕組み。
- JournalやReplayに相当するものは無い。`windowBracket`（`Operations.hs:237`）は"replay"という語を使うが、意味はWindowSetの差分の再適用に過ぎない。

## 2. i3（C/X11）

**状態モデル**
- 単一のツリー：`Con`は`CT_ROOT/CT_OUTPUT/CT_CON/CT_FLOATING_CON/CT_WORKSPACE/CT_DOCKAREA`型を持つ（`include/data.h:659-746`）。
- 各コンテナは子リストを**2本**持つ：`nodes_head`（空間的な並び順）と`focus_head`（フォーカス順）。
- `con_focus`は親の`focus_head`に対し`TAILQ_REMOVE` + `TAILQ_INSERT_HEAD`を行う（`src/con.c:256-257`）。これはコンテナ単位のMRU（Most Recently Used）リストである。
- winmonadの`{Order; FocusHistory}`は、xmonadのzipperよりも**この構造に近い**。
- ワークスペース↔出力（モニター）の対応は、ツリー上の親子関係として表現される。

**出力の消失と復帰**（`i3/src/randr.c`）
- `init_ws_for_output`（456-490）：出力が復帰した際、`workspace X output Y`というユーザー設定（`struct Workspace_Assignment`、`include/data.h:235`）がその出力を名指ししているワークスペースを、該当出力へ引き戻す。
- `move_content`（860-920）：出力が消失すると、そのワークスペースを最初の出力へ移す。空かつ非フォーカスのワークスペースは削除し、Floating窓の座標を補正する。
- i3は「ユーザーの割当設定（Config）」と「現在どこに表示されているか」を分離し、モニター構成が変わるたびに配置を再計算する設計である。

**Xエラー**（`main.c:135-142`）
- 事前に`add_ignore_event`で登録済みのシーケンス番号なら「Expected X11 Error received」とログ。
- それ以外は「X11 Error received (probably harmless)!」とログ。
- いずれの場合もエラーは握り潰される。
- X11のシーケンス番号は、エラーや自己発生イベントを自分自身の要求へ結び付ける仕組みを提供する。`Con.ignore_unmap`（`data.h`）はi3自身が引き起こしたUnmapNotifyイベントのカウンタである。

**IPC**
- バイナリフレーミング："i3-ipc"というマジック文字列 + 32bit長 + 32bit型 + JSONペイロード（`docs/ipc:50-60`）。
- メッセージ型には`GET_TREE`、`SUBSCRIBE`、`GET_VERSION`、`SEND_TICK`、`SYNC`等がある（`include/i3/ipc.h:28-118`）。
- バージョニング：マジック文字列は「IPC APIの互換性を破壊する変更がある時だけ変更する」＋`GET_VERSION`。メッセージ自体にスキーマバージョンは乗らない。
- `SYNC`はテストスイートがi3と同期を取るために存在する（`docs/ipc:876-888`）。
- コマンドの応答は`[{success, error, parse_error}]`の形（`commands.c:20-37`、`commands_parser.c:332-344`）。エラーは自由文字列。

**設定の検証**
- `i3 -C`は`load_configuration(path, C_VALIDATE)`を呼んで終了する（`main.c:474-476`）。構文検査は行うが、パーサーがセマンティックな検査をどこまで行うかは本調査では未確認`[unverified]`。

## 3. sway（C/Wayland、i3互換）

**アトミックトランザクション**（`include/sway/desktop/transaction.h:7-21`）
- レイアウト変更は各コンテナの`pending`状態へまず入る。コミット時に新しいサイズをクライアントへ送り、`transaction_notify_view_ready_by_serial`（`transaction.c:934`）によって全ビューがシリアルで確認応答するまで待つ。その後すべての変更を一括適用する。
- 確認応答が来ない場合、`handle_timeout`（`transaction.c:796`）が`txn_timeout_ms`経過後に変更を強制適用する。既定値は200（`server.c:701`）。
- デバッグフラグ：`-Dtxn-timeout=`、`-Dtxn-timings`、`-Dnoatomic`（`main.c:169-176`）。
- これはwinmonadの Desired/Pending/Deadline に**最も近い先行事例**である。決定的な違いは、WaylandがリクエストとAckを結び付けるシリアルという一次プリミティブを提供する点であり、Win32には相当するものがない。

**スケール**
- コンテナのジオメトリは論理レイアウト座標系（倍精度浮動小数）で保持する（`include/sway/tree/container.h`の`state x/y/width/height`）。
- 新しいビューの出力が判明した時点で、スケールを`wlr_fractional_scale_v1_notify_scale`と`wlr_surface_set_preferred_buffer_scale(ceil(scale))`でクライアントへ通知する（`sway/tree/view.c:841-845`）。コードコメントは「起動時のジッターを減らすため」としている。

**識別子**：複数のソースを持つ複数のフィールドがあり、swayは**それらを統合しない**。
- IPCとcriteriaは`app_id`（xdg-shellでクライアントが宣言）、`class`/`instance`（XWayland）、`shell`、`sandbox_engine`/`sandbox_app_id`/`sandbox_instance_id`を別々に公開する（`ipc-json.c:586-623`、`criteria.c:551-618`）。
- `sandbox_*`系のフィールドは`wp-security-context-v1`経由で来る。これを設定するのはサンドボックスエンジン自身であり、アプリ自身ではない（`view.c:213-226`）。
- `launcher_ctx_find_pid`（`view.c:822`）が新しいビューを、起動元の場所へマッピングする。

## 4. bspwm と sxhkd（C）

**外部ルールフック**（`src/rule.c:353-384`）
- bspwmは`external_rules_command`を"wid class instance consequence"という引数で起動し、応答を"key=value"行としてパイプから読む。
- 外部コマンドが終わるまで、対象の窓は`pending_rule`扱い。`manage_window`は未決定の窓をスキップする（`window.c:60-64`）。
- 未決定の窓に対するXイベントはバッファリングされ、パイプが閉じた後に再生される（`bspwm.c:229-239`）。
- メインループの`select(..., NULL)`はタイムアウト無し（`bspwm.c:228`）。読んだ限り、ルールスクリプトがハングすると、その窓は**無期限に**未管理のまま残る。

**IPC**
- UNIXソケット越しのNUL区切り引数。
- 失敗応答はバイト`\x07`で始まる（`common.h:31`、`messages.c:1956-1962`）。エラー内容自体は自由文字列。

**フォーカス履歴**
- `coordinates_t {monitor, desktop, node}`（`types.h:316-328`）という**単一のグローバルリスト**。`--record-history`で有効/無効を切れる。

**sxhkd**
- 独立したホットキーデーモンで、コマンドの起動だけを行う。
- キー取得は`xcb_request_check`で確認する（`grab.c:76-82`）。

## 5. dwm（C）

`xerror`（`dwm.c:2109-2124`）は、`(request_code, error_code)`のペアをキーにしたハードコードの無視リストである。無視する組み合わせ：
- 任意の要求に対する`BadWindow`。
- `(X_SetInputFocus, BadMatch)`、`(X_ConfigureWindow, BadMatch)`。
- `(X_GrabKey, BadAccess)`、`(X_GrabButton, BadAccess)`。
- 一部の描画系エラー（`BadDrawable`）。

それ以外は"dwm: fatal error: request code=%d, error code=%d"を出力してXlibの既定ハンドラへ渡し、終了する場合がある。

winmonadのR2-1の判断（`classify`を`(Api, Space, Code)`の3つ組で切る）と同じ発想で、dwmもエラーコード単体ではなく**ペア**をキーにしている。

## 6. awesome（Lua）

**コンテキスト付き要求**
- `request::activate`、`request::geometry`、`request::tag`は、`mouse_enter`・`history`・`switch_tag`等のコンテキスト文字列を伴って発生する（`lib/awful/permissions/init.lua`）。
- `_common.lua`の`check(object, class, request, context)`が許可マトリクスを参照する。既定では`client.autoactivate.{mouse_enter, switch_tag, history} = false`。
- これらはWM自身の「誰が何を起動してよいか」というルールであり、OS側の拒否ではない。

**ルール**（`lib/gears/matcher.lua`）
- 各ルールは`rule`/`rule_any`/`except`/`except_any`/`rule_every`というテーブル。
- ルールは名前付きの"source"にグループ化され、`depends_on`/`precede`のトポロジカルソートで順序付けられる（579行目）。`add_matching_rules`は「依存関係の矛盾が見つかればfalseを返す」（355〜391行目）。
- その矛盾検査が唯一の静的検査である。

**DPI**
- 1つのscreenが複数の物理出力をカバーすることがある（クローンモード）ため、`minimum_dpi`/`maximum_dpi`/`preferred_dpi`を公開する（`lib/awful/screen.lua:783-810`）。
- `tests/test-dpi.lua`は異なるDPIの仮想ビューポートを偽装してスクリーン生成をテストする。

## 7. Hyprland（Wayland compositor、C++）

**2つの座標系、モニター単位で変換**（`src/managers/XWaylandManager.cpp:74-140`）
- Wayland側のレイアウトは論理座標（`m_position`）。XWayland側は物理座標（`m_xwaylandPosition`）。
- `waylandToXWaylandCoords`/`xwaylandToWaylandCoords`はまずモニターを選ぶ（`preferredMonitor`引数、無ければ距離で最も近いモニター）。
- そのモニターの原点を引き、`xwayland:force_zero_scaling`設定時は`m_scale`で乗除し、相手側空間の原点を足す。
- つまり変換結果は「どのモニターを基準にするか」に依存し、単一のグローバル関数では表現できない。

**モニターライフサイクルポリシー**（`src/state/workspace/LifecyclePolicy.hpp`）
- `CMonitorLifecyclePolicy`は`(SMonitorSnapshot, SWorkspaceSnapshot)`のスナップショットを入力に取り、`IPolicyContext`インターフェース（`createWorkspace`/`moveWorkspace`/`activateWorkspace`）を通じて作用する。実質的にFunctional Core / Imperative Shellである。
- `m_returnMonitors`（各ワークスペースが戻るべきモニター）と`m_rememberedActiveWorkspaces`を保持し、両方ともモニターの**コネクタ名**（`m_name`、`LifecyclePolicyAdapter.cpp:40`で設定）をキーにする。
- `monitorDisconnected`（`LifecyclePolicy.cpp:95-120`）：切断されたモニターがフォールバックでない場合のみreturn monitorを記録し、ワークスペースをバックアップモニターへ移す。
- ユニットテストが`tests/state/WorkspaceLifecyclePolicy.cpp`にある。例：`cascadedDisconnectsReturnEachWorkspaceToItsFirstOwner`、`fallbackRecoveryPreservesRealMonitorProvenance`、`reconnectToleratesWorkspaceDestructionDuringMove`。

**モニターゼロ台**（`src/state/FallbackState.cpp`）
- 最後の実モニターが取り除かれると、headlessな"FALLBACK"出力を作る。
- 起動から2000ms（`READY_TIMEOUT_TO_UNSAFE_MS`）経っても準備できない場合もfallbackへ入る。
- 実モニターが追加されるとfallbackを抜ける。

**ルール**（`src/desktop/rule/Rule.hpp`）
- 各ルールは、それが読むプロパティのビットマスクを持つ。例：`RULE_PROP_CLASS`、`TITLE`、`INITIAL_CLASS`、`INITIAL_TITLE`、`XWAYLAND`、`EXEC_TOKEN`、`EXEC_PID`。
- `CWindowRuleApplicator::propertiesChanged(props)`は、変化したプロパティとマスクが重なるルールだけを再評価する（`WindowRuleApplicator.cpp:604-628`）。
- `CWindowMetadata`は`initialAppID`/`initialTitle`を現在値とは別に保持する（`WindowMetadata.hpp:16-31`）。
- 「Execルール」は、トークン・PID・有効期限によって、ルールと起動済みプロセスを結び付ける（`Rule.hpp`の`m_execData`）。

**エラー**
- `SDispatchResult`は`{passEvent, success, requestRelease, error: string}`（`SharedDefs.hpp:40-45`）。エラーは自由文字列。

**緊急設定**
- 設定にエラーがありキーバインドが1つも登録されない場合、Hyprlandは組込みのLuaキーバインド（端末を開く、実行する、終了する）をロードする（`ConfigManager.cpp:744-747`、`Emergency.hpp`）。

**IPC**
- socket1はテキストコマンドを受ける。未知のコマンドには"unknown request"を返す（`ipc/s1/S1.cpp:107`）。
- socket2イベントは型無しの`{event, data}`文字列（`ipc/s2/S2.hpp`）。
- 一部のイベントは窓をヒープ上のポインタで識別する。例：「pin」イベントは`rc<uintptr_t>(window.get())`を使う（`ConfigActions.cpp:332`）。
- 別に`stableID`も存在する（`WindowMetadata.hpp:20`）。両方が存在する理由は未確認`[unverified]`。

## 8. yabai（macOS）

**失敗後の分類ではなく、事前のcapability probe**
- `window_create`は`kAXPosition`/`kAXSize`に対し`AXUIElementIsAttributeSettable`を呼び、結果を`WINDOW_MOVABLE`/`WINDOW_RESIZABLE`としてキャッシュする（`window.c:773-798, 1116-1121`）。
- `WINDOW_RESIZED`/`WINDOW_MINIMIZED`/`WINDOW_DEMINIMIZED`イベント時にも再probeする（`event_loop.c:760, 841, 885`）。
- これらのフラグがmanage/float判断を駆動する（`window_manager.c:280`、`1505-1510`）：移動できない窓は`WINDOW_FLOAT`になる。
- つまり「OSがこの窓を動かすことを許さない」という事実は、**事後の失敗分類ではなく、観測された属性として記録され、分類に使われる**。

**エラー報告の実態**
- `window_op_error`（`window_manager.h:8-24`）、`space_op_error`（`space_manager.h:32-45`）は主にWM側の前提条件違反（`INVALID_SRC_NODE`、`SAME_WINDOW`、`IN_MISSION_CONTROL`等）である。
- OS側の失敗は、操作ごとの個別コード（例：`MINIMIZE_FAILED`）か、単一のバケツ`SPACE_OP_ERROR_SCRIPTING_ADDITION`に押し込められる。
- ユーザー向けメッセージは「...スクリプティングアドオンのエラーのため」であり、原因は示さない（`message.c:1740-1902`）。

**一時的 vs 永続的**
- AXオブザーバー登録は、唯一1種類のtransientケースを区別する：`kAXErrorCannotComplete`は`ax_retry`をセットし、`APPLICATION_LAUNCHED`イベントが100ms後に再投函される（`application.c:51`、`event_loop.c:149-160`）。
- それ以外のAXエラーはアプリケーションを即座に切り離す。

**権限はWM自身の能力として、起動時に検査される**
- Accessibility信頼が無ければ、yabaiは「could not access accessibility features! abort..」と中断する（`yabai.c:272`）。
- SIP設定ビット（`CSR_ALLOW_UNRESTRICTED_FS`、`CSR_ALLOW_TASK_FOR_PID`）がscripting additionを使えるかどうかを決める（`sa.m:301-316`）。

**スケールとディスプレイ**
- スケール関連のコードは一切ない（`backingScale`/`scale_factor`でgrepしても0件）。macOSは単一のグローバルポイント空間を提供するため。
- ディスプレイはUUIDで識別される（`display.c:101-103`）。
- ディスプレイの追加/削除時、yabaiは影響を受けたviewを無効マークし、遅延的に再構築する（`window_manager.c:2679-2707`）。

## 9. komorebi（Windowsタイル型WM、Rust）——feasibility.mdが扱わなかった点のみ

**ルールは宣言的だが優先順位付けではない**
- ルールはserde/JSON、JSON schema付き（`schema.json`、`core/config_generation.rs:57-106`）。
- 1ルールは`MatchingRule::Simple`か`MatchingRule::Composite`で、`IdWithIdentifier { kind, id, matching_strategy }`を軸に判定する。
  - `kind`は`Exe`/`Class`/`Title`/`Path`。
  - `matching_strategy`は`Equals`/`StartsWith`/`EndsWith`/`Contains`/`Regex`。
- ルールは別々のリスト（ignore, manage, floating, layered 等）に分かれており、1本の優先順位付きリストではない。

**アプリ単位の個別対応フラグ**
- `ApplicationOptions`は`ObjectNameChange`/`Layered`/`TrayAndMultiWindow`/`Force`（`config_generation.rs:14-20`）。
- これらは外部の「アプリ別設定」ファイル（`static_config.rs:560, 1256`）から来る。つまり、個々のアプリの挙動をまとめた、キュレーション済みの共有カタログである。

**winmonadのExplain/whyNotに最も近いもの**
- `SocketMessage::DebugWindow(hwnd)`が`RuleDebug`を返す（`window.rs:890-912`、`process_command.rs:2216`）。
- `RuleDebug`は各適格性検査（`matches_ignore_identifier`、`matches_managed_override`、`matches_floating_applications`等）の真偽値とマッチしたルールを持つ。
- その場で計算される。履歴からの読み出しは一切ない。

**UWP**
- `ApplicationFrameHost.exe`は`TRAY_AND_MULTI_WINDOW_IDENTIFIERS`内の`Exe`エントリとしてしか現れない（`lib.rs:118`）。
- 実アプリの解決・AUMID対応は存在しない。`ApplicationIdentifier`にそのような種別は無い。

**昇格とアクセス拒否**
- 整合性レベルやelevationを検査するコードは見当たらなかった。
- `E_ACCESSDENIED`への唯一の言及は`animation/ghost_dispatcher.rs:200-203`のコメント：「`DwmSetWindowAttribute(DWMWA_CLOAK)`は外部プロセスのHWNDに対しては`E_ACCESSDENIED`で拒否される。別経路（未文書化の`IApplicationView::SetCloak`）にはこの制約がない」。
- つまりアクセス拒否は、昇格していない窓でも発生する。

**エラーと窓単位の独立性**
- すべてのWin32エラーは型無しの`eyre::Report`になる（`windows_api.rs:217-250`、`ProcessWindowsCrateResult`）。
- `Workspace::update`では、`window.set_position(layout, false)?`が窓ループの中にある（`workspace.rs:840`）。静的に読む限り、1窓の失敗がそのワークスペースの残りの窓の配置を止めてしまうように見える。呼び出し元でリトライされるかまでは追跡していない`[unverified]`。

**モニター変化とサスペンド**
- `MonitorNotification`には`ResolutionScalingChanged`/`WorkAreaChanged`/`DisplayConnectionChange`/`EnteringSuspendedState`/`ResumingFromSuspendedState`/`SessionLocked`/`SessionUnlocked`がある（`monitor_reconciliator/mod.rs:36-44`）。
- サスペンドやロック時に`ACTIVE=false`にし、レジュームやアンロック時に戻してアタッチされたディスプレイを再検査する（255〜285行目、365〜400行目）。
- `display_change_in_progress(grace_period)`（87〜100行目）は、ディスプレイ変化の直後に起きる最小化をユーザー操作ではなくOS起因として扱う。
- `MONITOR_CACHE`は、切断されたモニターの状態を**シリアル番号やデバイスIDで**キー化して（`HMONITOR`ではなく）保持する（55行目、104〜118行目）。
- `attached_display_devices`は`display_provider`を注入可能にしている（121行目）。このモジュールには14個のユニットテストがある。

**混在DPI**
- 窓が異なるDPIのモニター間を移動すると、komorebiは単純にレイアウトをもう1回実行し直す（`window_manager.rs:2573-2575`、`monitors_have_same_dpi`）。
- `window_rect`のDPIスケーリングはfeasibility.mdが既に指摘した通り、依然TODO（`windows_api.rs:792-795`）。

**IPC**
- 型付き`SocketMessage` enumをJSONとして、Unixドメインソケット（`uds_windows`）経由でWindows上に実装する（`static_config.rs:114`、`main.rs:382`）。
- 購読者への全ての通知は、状態の全体（full `State`）を乗せる（`lib.rs:354-357`）。

## 10. GlazeWM（Windowsタイル型WM、Rust）——feasibility.mdが扱わなかった点のみ

**昇格窓**
- `ui_access`というcargo featureが、マニフェストに`uiAccess="true"`をセットする（`packages/wm/build.rs:7-24`）。
- ビルドスクリプトは「フォアグラウンドを設定する権限と、昇格窓の位置を設定する権限を与える」と説明する。既定ではオフで、理由は「署名されかつセキュアな場所にインストールされていることが必要」だから。
- Elevation検知も失敗分類も一切ない。
- `reposition_window`が失敗すると、`tracing::warn!("Failed to set window position")`をログして次の窓へ進む（`commands/general/platform_sync.rs:288-292`）。これはkomorebiの`?`（即座に伝播）とは正反対の方針。

**クラッシュ監視watchdog**（`packages/wm-watcher/src/main.rs`）
- `wm-watcher`は独立したプロセス。
- IPCで`window_managed`/`window_unmanaged`/`application_exiting`を購読し、自分自身のHWNDリストを保持する。
- 購読が`application_exiting`イベントを受け取らずに終了した場合、全窓を表示し、タスクバーの可視性を復元し、ボーダーの色と透明度をリセットする。
- これはwinmonad §8.4（安全停止）に対する**直接の先行実装モデル**である。

**ワークスペースの非表示方法**
- `HideMethod`は`Hide`、`Cloak`（既定）、`PlaceInCorner`（`wm-common/src/parsed_config.rs:145-150`）。
- `Cloak`は未文書化のCOMインターフェース`IApplicationViewCollection`を使う（`wm-platform/src/platform_impl/windows/com.rs:30-105`）。

**ルール**
- `WindowRuleConfig`は`{ match: [WindowMatchConfig { window_process, window_class, window_title }], commands, on, run_once }`という構造。
- `on`は`Focus`/`Manage`/`TitleChange`等の名前付きイベントのリストであり、ルールはそれらのイベント発生時に再評価される（`parsed_config.rs:310-373`）。
- UWPやAUMIDの扱いは存在しない（grepで0件）。

**DPI**
- レイアウトは物理ピクセル。ギャップのみがモニターのDPIに応じてスケールし、かつそれは`scale_with_dpi`が有効な時のみ（`models/workspace.rs:108-127`、`traits/tiling_size_getters.rs:28-40`）。

**IPC**
- `127.0.0.1:6123`のWebSocket。
- 応答は`{ client_message, data, error: Option<String>, success }`（`wm-common/src/ipc.rs:17-22`）。
- `ipc_server.rs:40-80`に認証やOriginチェックは見当たらなかった。Webページから実際に到達できるかどうかは未確認`[unverified]`。

---

## 11. バックログ項目への対応づけ（synthesis）

### Phase A — DenialKind判断

**推奨：`TargetIntegrityHigher`は追加しない。整合性レベルは窓の観測属性として記録する。**

根拠：
1. 調査した12個のWMのどれも「対象の権限が自分より高い」という専用の失敗分類を持たない。X11/Waylandの実装群はこの問題自体に遭遇しない（`BadWindow`/`BadMatch`/`BadDrawable`/grab時の`BadAccess`しか扱わない：dwm、i3）。komorebiはこの種のエラーを型無しのまま素通りさせ、GlazeWMはログして次へ進む。権限モデルが最も敵対的なyabaiも、事後の原因分類をしていない。
2. 存在する2つのパターンは、いずれも**呼出し前に**作用する：yabaiは窓単位でcapabilityをprobeし、フラグとしてキャッシュし、manage/float判断に使い、既知のイベントで再probeする。GlazeWMは自分自身の権限を上げる（UIAccess）——winmonadのADR-009・1.3はこれを明確に非対象としている。
3. エラーコード単体は原因を語らない。komorebiのコードが明記する通り、`DWMWA_CLOAK`の`E_ACCESSDENIED`は整合性レベルに関係なく任意の外部HWNDに対して発生する。したがって`TargetIntegrityHigher`を追加すると、`(Api, Space, Code)`という純粋な3つ組（15.1 #14）だけでは判定できず、`classify`に窓の状態（または新しい`NativeCallRecord`フィールド）を読ませる必要が生じる。その割に、反応（Suspend/Floating）は`OtherDenial`と同じになる。

[unverified]：この提案はF24がまだ検証していない1点に依存する——`OpenProcessToken` + `GetTokenInformation(TokenIntegrityLevel)`をMedium整合性プロセスからHigh整合性プロセスに対して呼んだ場合に成功するか。F24が確認したのは`OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)`の成功のみで、トークン・整合性レベルの読み取り自体は未検証。

### S5 — UWPのAppId

- 調査した2つのWindows用WM（komorebi、GlazeWM）とも、`ApplicationFrameHost.exe`の裏にある実アプリを解決していない。
- swayの設計（`app_id`（クライアントが宣言）、`class`（XWayland）、`sandbox_app_id`（サンドボックスエンジンが設定）という複数の出自を持つ識別子を**統合せずに別フィールドのまま持つ**）が最も参考になる。
- Hyprlandも`initialClass`/`initialTitle`を現在値と別に保持する。

提案：`AppId`を単一の文字列ではなく、派生元ごとの観測レコード（実行ファイル名、ApplicationFrameHostを所有する実行ファイル、AUMID、CoreWindow子窓から導出した実行ファイル等）にする。これにより、S5は「3つの手法から1つを選ぶ」問題ではなく「各フィールドの信頼度を測る」問題になり、Phase Aをブロックしなくなる。

### S11 — 混在DPI

- sway・Hyprland・yabai（macOSを通じて）はいずれも論理座標でレイアウトする。komorebi・GlazeWM（Windowsの2例）はいずれも物理ピクセルを使う——これはPer-Monitor-V2の実際の挙動と一致する。
- Hyprlandの変換関数が示す通り、論理↔物理の変換は「どのモニターを基準にするか」に依存し、単純な定数変換ではない。

提案：Coreは物理ピクセル継続。ユーザーが設定する長さの値（ギャップ、`MinWidthAtLeast`等の視覚的な一貫性を意図する値）はDIPとして指定し、`LayoutContext`構築時に対象モニターのDPIで物理ピクセルへ変換する（GlazeWMの`scale_with_dpi`が先例）。フレームオフセット（7,0,7,7px）は単一定数ではなくDPI別に記録すべき——komorebi自身のTODOが、本番運用中もこの差分が未解決のまま残っていることを示す。

実機要件は変わらない：対象アプリ自身の`WM_DPICHANGED`への反応はシミュレーションできない。

### S8 — 画面再構成（スリープ/ホットプラグ/RDP）

- Hyprlandの`CMonitorLifecyclePolicy`は、モニタートポロジのスナップショットに対する純粋関数（`IPolicyContext`というポートの向こう側に実装を置く——winmonadのADR-002と同じFunctional Core/Imperative Shellの構造）であり、cascaded disconnectを含むユニットテストがある。

採用すべき具体項目：
1. モニターを安定したID（シリアル/デバイスID、komorebiの`MONITOR_CACHE`）で識別する。`HMONITOR`等の一時ハンドルは避ける。xmonadの`rescreen`（位置による再割当）は避けるべき反面教師。
2. ワークスペースごとの「home/return monitor」概念（Hyprlandの`m_returnMonitors`、i3の`Workspace_Assignment`）。
3. サスペンド/ロックを画面再構成中の明示的な状態として扱う（komorebiは`EnteringSuspendedState`/`SessionLocked`で同期を止め、逆イベントで再開する）。
4. 画面変化イベント後にgrace windowを設け、その間の最小化/移動をユーザー操作ではなくOS起因として扱う（komorebiの`display_change_in_progress(grace_period)`）。

OSが実際に窓を動かす部分自体は実機依存のまま残り、自動化されたハードウェアテストの代わりに日常利用時のJournal採取が主な証拠源になる。

## 12. バックログ外の驚き（surprising findings）

1. **winmonadの`WmState`（§4.4）にはワークスペース↔モニターの対応が無い**。`ActiveWorkspace`が単一のフィールドであるのに対し、§4.4が模範として引用するxmonadの`StackSet`には`current`/`visible`/`hidden`という**複数のscreen**概念がある。マルチモニターはFR-13（Phase B、P0〜P1）であるため、ADR-011を確定する前にこの構造を直す必要がある。architecture.md内で`MonitorId`は`WindowSnapshot.Monitor`、`GateInput`、`MonitorTopologyObserved`にしか現れない。
2. **FocusHistoryには明示的な不変条件とスコープの決定が必要**。winmonadの`{Order; FocusHistory}`はi3のコンテナ単位`nodes_head`/`focus_head`（MRUリスト、`con.c:256-257`）と構造的に一致し、xmonadのzipper（構造自体から不変条件を得る）とは異なる。「`FocusHistory ⊆ Order`かつ重複なし」という性質テストを追加すべきである。またフォーカス履歴がワークスペース単位（i3、RefocusLast）かグローバル（bspwm）かを決める必要があり、「別モニター/ワークスペースの直前のフォーカス先へ戻る」という要求を考えるとグローバルの形が必要になる。
3. **12個のリポジトリ全てにJournal/Replay/Forkに相当するものは無い**（"replay"、"journal"、"record"でgrepしたが、ヒットはX pointer replay、swaybarのレンダリング記録、xmonadの`windowBracket`という名前だけの偽陽性のみ）。最も近いものは、komorebiの`DebugWindow`が返す`RuleDebug`（その場で計算する「なぜでないか」）、swayの`-Dtxn-timings`、i3の決定的テスト用`SYNC`、純粋ポリシーに対するユニットテスト（xmonadのQuickCheck、Hyprlandのlifecycle）に限られる。「キューに入った」と「適用された」を区別するIPCも調査対象のどのWMにも無く、winmonadの`InterveneResult = Queued`も新規性がある。
4. **Hyprlandのルールごとの「読み取るプロパティのビットマスク」**は、winmonadのPolicy IR Analyzerに直接応用できる。各`WindowRule`の`WindowPredicate`が読むフィールド集合を静的に計算できれば、incrementalな再分類（A-6）のトリガーになり、§5.3「`TitleContains`は常にタイトル取得を強制する」と§9.6「機密フィールドは必要時のみ取得する」の緊張を解消できる（実際に有効なルールがタイトルを読む時だけタイトルを取得すればよい）。
5. **§5.4のUnknown状態に期限が必要**。bspwmの外部ルールフックは、ハングしたスクリプトが窓を無期限に未管理のまま残す反面教師である。
6. **§21のワークスペース非表示方式の選択肢からCloakが欠けている**。GlazeWMは既定でCloakを使うが、文書化された`DWMWA_CLOAK`が外部プロセスのHWNDに対し拒否されるため（komorebiも同じ理由でこのエラーに遭遇している）、未文書化の`IApplicationView` COM経由に依存するリスクがある。
7. **GlazeWMの`wm-watcher`**は§8.4の安全停止の直接の実装モデルである：独立プロセス、IPCで構築する自前のHWNDリスト、クリーンな終了を`application_exiting`イベントの有無で判別する。
8. **komorebiの窓ループ内`?`（1窓の失敗が残りを止める）と、GlazeWMのログして継続という対比**は、§15.1 #12（窓単位の結果の独立性）を支持する実例である。
9. **swayは200msのタイムアウト後に強制適用する**。これはwinmonadのPending Deadlineに最も近い先例だが、X11（シーケンス番号）とWayland（configureシリアル/トランザクションAck）がリクエスト↔応答を一次的に対応付けられるのに対し、Win32にはそれが無い。§9.3のヒューリスティックな照合は、これらのWMから借用できる、よりきれいな仕組みに置き換えられるものではないという裏付けになる。
10. **awesomeの(request, context)許可マトリクス**は、`RequestForeground`をどこからの要求かで許可/拒否するモデルの参考になる。既定では`mouse_enter`/`switch_tag`/`history`からの自動活性化を拒否する。
11. **GlazeWMのIPC（認証なしのlocalhost WebSocket）**は、winmonadの§10.1（Named Pipe + セキュリティ記述子 + First Pipe Instance）を支持する負の対照例になる。ただし「Webページから実際に到達できるか」自体は本調査では確認できていない`[unverified]`。

## 調査できなかった／裏付けが弱い論点

- komorebiの`Workspace::update`内`?`演算子について、呼び出し元が窓単位でリトライするかどうかは追跡していない。
- GlazeWMのIPC（認証なしWebSocket）が実際にブラウザから悪用可能かどうかは未検証（コード上にOrigin/認証チェックが見当たらないことのみ確認）。
- Hyprlandの各ウィンドウが`uintptr_t`ベースの識別子と`stableID`の両方を持つ理由は未確認。
- i3の`-C`（設定検証）がパーサーレベルの構文チェックを超えてセマンティックな検査（矛盾検出等）まで行うかどうかは未確認。
- 全リポジトリはshallow cloneのHEAD時点のみを読んでおり、各機能がいつ導入されたかの履歴は確認していない。また実際にどのWMも動作させておらず、Issueトラッカーも読んでいない。
