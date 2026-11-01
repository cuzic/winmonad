> **反映状況（v2.6）**：本レビュー（v2.5に対する第1ラウンド）の指摘R3-1〜R3-13は全てarchitecture.md v2.6に反映済み。R3-3（フォーカスの二重の情報源）・R3-6（スナップショットの統合規則）・R3-7（ADR-015の未検証API依存）は個別の修正ではなく、ADR-016（観測・信念・決定の3層分離、4.5）という同じ仕組みで解消した。対応表：
> - R3-1 → 4.4：`HomeMonitor`を`PolicyIR.WorkspaceAssignments`と`WorkspaceState.ReturnMonitor`に分離、更新規則を確定、15.1 #18
> - R3-2 → 4.4：(S1)〜(S5)、`ActiveMonitor: MonitorId option`、`activeWorkspace`をtryFindで導出、`SwitchWorkspace`はview意味論、15.1 #17
> - R3-3 → 4.4：`WmState.Focused`を唯一の情報源、履歴はグローバル単一MRUに一本化（Workspace単位の履歴は導出）、`FocusNext`は巡回、15.1 #16
> - R3-4 → 4.4：食い違いの原因（ユーザー／OS／不明）ごとの規則
> - R3-5 → 5.2・8.3：`MinWidthAtLeast`は物理px、`MonitorWidthAtLeast`はDIP
> - R3-6 → 4.2・4.5：`WindowSnapshot`は部分観測、フィールドごとの修正規則（LatestWins/Sticky/ProbeOnce/Accumulating）、15.1 #20
> - R3-7 → ADR-015・4.5・5.4：分類は`Movability`の信念を読み、整合性レベルのprobeは事前証拠に格下げ。`MissingTargetIntegrity`追加、classifyの不変条件の表現を訂正
> - R3-8 → 4.2・8.4・21章：`CloakState`、Cloak採用条件の比較軸を追加
> - R3-9 → 9.2・23章：既知タグの形状変更禁止、予約済みタグ、`DenialKind`を独立レビュー対象に明記
> - R3-10 → 4.1：安定識別キーの順序付きフォールバック、`MonitorSnapshot`の定義
> - R3-11 → 18章・19章：Phase Aゲートに#16〜#21、`MonitorLifecycle.fs`等
> - R3-12 → 5.4：期限は`ClassificationDeadline`タイマー、期限後の再分類は1回まで
> - R3-13 → 8.4：`wm-watcher`の引用をクラッシュ検知の先行例に限定

# Opus批判的レビュー：OSS調査反映（architecture.md v2.4.5 → v2.5）第1ラウンド

対象：`git diff docs/architecture.md`（v2.5）＋全文、根拠資料`docs/oss-wm-survey.md`。
行番号は現行`docs/architecture.md`のもの。

指摘は重大度順。各指摘に「場所／問題／具体的な失敗シナリオ／修正案」を付ける。

---

## R3-1【高】`HomeMonitor`の「最後に表示されていたモニター」という更新規則が、切断時の復帰先を上書きして消す

**場所**：4.4 L237（`HomeMonitor: MonitorId option // 設定上の割当、または最後に表示されていたモニター`）、L270、7.3 L681 (2)

**問題**：1つのフィールドに2つの異なる意味（i3の`Workspace_Assignment`＝設定、Hyprlandの`m_returnMonitors`＝切断時に記録する復帰先）を混ぜたうえ、更新規則を「最後に表示されていたモニター」にしている。調査資料（oss-wm-survey.md L157）が明記する通り、Hyprlandは**切断されたモニターがフォールバックでない場合のみ**return monitorを記録し、フォールバック先へ移動したことで上書きはしない（テスト名`cascadedDisconnectsReturnEachWorkspaceToItsFirstOwner`がまさにこの性質）。v2.5の規則はこれと逆の動作をする。

**失敗シナリオ**：
1. WS3がモニターBに表示中（`HomeMonitor = Some B`）。
2. スリープ復帰でDisplayPortのモニターBが一時的に切断（実機で頻出）。7.3の反応でWS3をモニターAへ退避。
3. WS3は今モニターAに「表示されている」ので、「最後に表示されていたモニター」規則により`HomeMonitor = Some A`に更新される。
4. 数秒後にBが再接続 → WS3の復帰先はAなので戻らない。HomeMonitorを導入した目的（切断・再接続をまたいだ配置の保持）がまさに最頻ケースで失われる。カスケード切断（B→A→Cの順で消える）でも同様に最初の所有者が失われる。

加えて「設定上の割当」を置く場所が`PolicyIR`（5.2）に存在しない。Coreの`WmState`にしか置けないため、Policy Deploy/Rollbackで割当を変えても`WmState`の値は更新されない（設定とランタイム状態の混同）。

**修正案**：
- 2フィールドに分ける：設定割当は`PolicyIR`側（例：`WorkspaceAssignments: (WorkspaceId * MonitorId) list`、DTOタグ対象）、ランタイムの復帰先は`WorkspaceState.ReturnMonitor: MonitorId option`。
- `ReturnMonitor`の更新規則を明記する：「実モニターの切断により退避した時にのみ、退避元を記録する。退避先（フォールバック）への表示では上書きしない。復帰したら`None`へ戻す」。
- 15.1に性質を追加：「切断列→再接続列の任意の順序で、各WSは最初に退避した時点のモニターへ戻る」（Hyprlandのcascadedテストと同型。7.3でPhase Aのシミュレーション対象とした以上、ここが最初のテスト対象になる）。

---

## R3-2【高】`Screens`/`ActiveMonitor`に不変条件が無く、モニター切断時に`Screens.[ActiveMonitor]`が例外を投げて安全停止に落ちる

**場所**：4.4 L242-244、L270、15.1（追加なし）、7.1 `MonitorTopologyObserved`

**問題**：xmonadの`StackSet`は構造（current/visible/hiddenの分割）で「1つのWSは高々1画面」「currentは必ず存在する」を保証する。v2.5は`Map<MonitorId, WorkspaceId>` + 独立した`ActiveMonitor`に分解したため、それらの保証を全て失ったが、FocusHistoryには追加した「型で保証できない分を性質テストで守る」（L272、#16）という扱いをScreens側には適用していない。欠けている不変条件：
- (a) `ActiveMonitor ∈ dom(Screens)`
- (b) `Screens`は単射（同じWSが2画面に出ない）
- (c) `range(Screens) ⊆ dom(Workspaces)`
- (d) `dom(Screens)` = 直近の`MonitorTopologyObserved`のモニター集合
- (e) モニター0台の状態（RDP切断、全ディスプレイ電源断で列挙が空になる場合。HyprlandはこのためにFALLBACK出力を持つ：survey L160-163）の表現。`ActiveMonitor: MonitorId`は非optionなので表現できない。

**失敗シナリオ1（安全停止）**：ActiveMonitorのモニターAが抜かれる → `MonitorTopologyObserved [B]`の処理で`Screens`からAを除いた後、同じstep内の導出処理（「アクティブなWSは`Screens.[ActiveMonitor]`から導出する」L244）が`Map`のインデクサで`KeyNotFoundException`を投げる。これはADR-014の第3層（想定外の例外）なので`InternalFault`→8.4安全停止になる。**モニターを1本抜くたびにWMが安全停止する**。決定的なのでReplayでも再現するが、それは救いにならない。

**失敗シナリオ2（矛盾した二重配置）**：`SwitchWorkspace 2`をモニターAで実行、WS2が既にモニターBに表示中。xmonadの`view`（Bへフォーカス移動）か`greedyView`（AとBのWSを交換）かが未定義のため、素朴な実装が`Screens.Add(A, 2)`すると`{A→2; B→2}`になる。stepは2画面それぞれでLayoutを計算し、WS2の各窓に対し異なる`WorkArea`の`SetRect`を出す。PlanValidatorの重複検査（6.2 #2）は**Plan単位**なので、画面ごとの2つのPlanはそれぞれ通過し、GatedEffectにも窓単位の重複検査は無い。同じ窓が2つの目標矩形の間で振動し、7.3の振動検知がSuspendするまで続く。

**修正案**：
- `ActiveMonitor: MonitorId option`にする（0台を表現）か、HyprlandのFALLBACKに相当する仮想MonitorIdを定義する。
- `activeWorkspace : WmState -> WorkspaceId option`を`Map.tryFind`で定義し、インデクサ使用を禁止する旨を明記。
- 15.1に#17として(a)〜(d)を性質テストで追加（#16と同じ理由づけ）。
- `SwitchWorkspace`の「対象WSが他画面に表示中」の意味（view/greedyView）を4.4で決める。
- `MonitorTopologyObserved`の処理（モニター消失時のWS退避、ActiveMonitorの付け替え）を、7.3で言う「Phase Aでシミュレーションする純粋関数」として同一stepで原子的に行うことを明記する。

---

## R3-3【中〜高】「現在フォーカスされている窓」の情報源が2つ（`RecentFocus`先頭と`FocusHistory`先頭）あり、食い違う。`RecentFocus`には不変条件が一切無い

**場所**：4.4 L236、L246、L252、L272、15.1 #16、5.6 `LayoutContext.Focused`

**問題**：
1. `WmState`に「現在のフォーカス窓」フィールドが無く、`LayoutContext.Focused`（L406）の導出元が未定義。候補が`head RecentFocus`と`head (FocusHistory of active WS)`の2つになった。
2. #16により`FocusHistory ⊆ Order`。5.4によりUnknown窓はOrderに入らず、`Order`がTiledのみなら（L264「配置アルゴリズムが参照する唯一の並び順」）Floating窓もFocusHistoryに入れない。ADR-015のサンプル（L348）は昇格窓をFloatingにするので、昇格窓にフォーカスしてもFocusHistoryは更新されない。一方`RecentFocus`には何の制約も無いので入る。
3. `RecentFocus`について：破棄窓の除去規則なし（L266は`WindowWorkspace`の除去しか書いていない）、長さ上限なし、Excluded窓（スタートメニュー、通知、IME候補窓等の`ForegroundObserved`）を入れるか未定義。
4. `FocusHistory`を「Workspace内のFocusNext/FocusPrevious用」（L272）と書いているが、MRUでNext/Previousを実装すると交互往復になる。

**失敗シナリオ**：
- (i) WS1でTiled窓T（FocusHistory先頭）→ Floatingの昇格ターミナルFへフォーカス。`RecentFocus=[F;T;…]`、`FocusHistory=[T;…]`。ここで`FocusNext`：Fの次かTの次か未定義。`PromoteToMaster`/`CloseFocused`も同様で、**CloseFocusedが、ユーザーが見ている窓Fではなく、FocusHistory先頭のTを閉じる**実装が規格上許される。
- (ii) 直前にフォーカスした窓を閉じた後に`FocusMostRecent`を押すと`RequestForeground`が破棄済みWindowIdへ出てGatedEffectの`TargetNotAlive`で弾かれ、何も起きない（次の生存窓へスキップしない）。
- (iii) WindowIdは再利用しない（4.1）ので、除去規則が無いと`RecentFocus`はセッション中にフォーカスされた全窓の数だけ単調増加し、Checkpoint（9.5）・Incident Bundleの`checkpoint.json`も比例して肥大する。
- (iv) `FocusHistory=[B;A;C]`で`FocusNext`→A（`[A;B;C]`）→`FocusNext`→B…とCに永遠に到達しない。

**修正案**：
- `WmState.Focused: WindowId option`を唯一の情報源として明示し、`RecentFocus`/`FocusHistory`は「Focused移動時に更新する派生履歴」とする。
- 不変条件を追加：`RecentFocus`は重複なし、`RecentFocus ⊆ dom(WindowWorkspace)`（＝生存かつ非Excluded）、長さ上限N。`Focused = Some w`かつ`w ∈ Order(ws)`なら`head FocusHistory(ws) = w`。
- `FocusHistory ⊆ Order`のOrderがFloating窓を含むかを決める（含まないなら、WS切替時にFloating窓へフォーカスを戻せない点を許容と明記）。
- `FocusNext/FocusPrevious`は`Order`上の巡回であり、`FocusHistory`はWS切替・窓クローズ後のフォーカス復元用と書き直す。
- 引用の訂正：L272「i3のRefocusLast」は誤り。`RefocusLast`はxmonad-contrib（survey L30）。i3の`focus_head`は全階層（root/output含む）にあるので、i3は「Workspace単位のみ」の例として不適切。

---

## R3-4【中】窓→モニターの真実が2つ（観測`WindowSnapshot.Monitor`と、`Screens`∘`WindowWorkspace`による意図）あり、ユーザーの画面間ドラッグ・OSの移動に対する反応が未定義

**場所**：4.2 L198（`Monitor: MonitorId option`）、4.4 L242/L245、7.4、7.3 L681 (4)

**問題**：v2.4.5までは単一のActiveWorkspaceしかなく「窓がどの画面に属すべきか」という概念自体が無かった。v2.5で`WindowWorkspace`＋`Screens`から各窓の「あるべきモニター」が導出可能になったが、観測側の`WindowSnapshot.Monitor`と食い違った時の扱いが無い。

**失敗シナリオ**：ユーザーがWS1（モニターA）のTiled窓をモニターBへドラッグする。`MoveSizeEnded`後、7.4は「再整列、現配置の採用、Floating化」のいずれかとしか言っていない。「再整列」を選べば窓はAへ引き戻され、ユーザーは同じ操作を繰り返す（ユーザーとの引っ張り合い。20章E-5の亜種）。「現配置の採用」を選んでも、`WindowWorkspace`はWS1のままなので次のLayoutでAへ戻される。7.3(4)のgrace window外でOSが窓を別モニターへ移した場合（タスクバー位置変更、解像度変更）も同じ。

**修正案**：4.4または7.4に規則を1つ書く：「Tiled窓の観測MonitorがScreensの導出結果と異なる状態でMoveSizeEndedが来たら、`MoveFocusedToWorkspace (Screens.[observedMonitor])`と同等の状態遷移をCoreが行う（i3/komorebiと同じ）」、またはその逆を明示的に選ぶ。grace window内の不一致は7.3(4)に従いOS起因として無視する。

---

## R3-5【中】DIP変換の位置が誤っている：`MinWidthAtLeast`は分類述語でありLayoutContext構築より前に評価される。結果として分類がDPI依存になり、モニター移動で反転する

**場所**：5.2 L293、8.3 L755、5.5 L401、5.2 `LayoutPredicate.MonitorWidthAtLeast` L311

**問題**：
1. 6.1のパイプラインは`Window Snapshot → Classify → Select LayoutRef → … → LayoutFunction`。`MinWidthAtLeast`は`WindowPredicate`なのでClassify段で評価される。「`LayoutContext`構築時に変換する」（L293/L755）は時系列上不可能。
2. Classify段で変換に使うDPIは`WindowSnapshot.Monitor`（option）のモニターのものしか無い。`None`時の扱いが無い。
3. 変換後のしきい値がDPIで変わるため、**同じ窓・同じ観測MinWidthHintでも、どのモニターにいるかで分類結果が変わる**。
4. 同じ「ユーザーが指定する長さ」である`MonitorWidthAtLeast of int`の単位が未定義のまま。
5. 5.5の静的読み取り集合：`MinWidthAtLeast`の読み取り集合に`MinWidthHint`だけでなく`Monitor`（とそのDPI）を含めないと、モニター移動時に再分類が発火しない。

**失敗シナリオ**：`MinWidthHint`=700物理px（観測値）、ルール`MinWidthAtLeast 500 → Floating`。100%モニターではしきい値500px→Yes→Floating。150%モニターでは750px→No→Tiled。Tiled窓をBへ移す（R3-4、または`MoveFocusedToWorkspace`）たびにTiled⇔Floatingが反転し、5.3が言う「7.3の振動防止対象」に毎回入る。読み取り集合に`Monitor`が無い実装では逆に再分類が起きず、移動前の分類が残るので、どちらの分類になるかが移動経路に依存する（決定的ではあるが説明不能）。

**修正案**：
- 変換の位置を「Policy評価時、`WindowSnapshot.Monitor`のDPIで変換。`Monitor = None`ならUnknown（EvidenceGap）」と書き直す。
- 分類がDPI依存になることを許容するか（許容するなら再分類トリガーに`Monitor`変化を明記）、`MinWidthAtLeast`は物理px（観測値と同単位）のままにしてギャップ等のレイアウト値だけDIPにするか、を決める。後者の方が分類の安定性を壊さず、GlazeWMの先例（ギャップのみスケール、survey L279）にも忠実。
- `MonitorWidthAtLeast`の単位（DIPか物理か）を明記する。

---

## R3-6【中】`AppIdentity`の「初期値と現在値を別に記録できる」という主張を型が支えていない。`WindowSnapshot`のフィールド寿命（再観測時のマージ規則）が未定義で、`TargetIntegrity`の「一度だけprobe」とも衝突する

**場所**：4.2 L176-200、L211、5.4 L372、7.1 `WindowObserved of WindowSnapshot`、ADR-015（L31）

**問題**：
1. L211は「フィールドごとに初期値と現在値を別のサンプルとして記録できるため、A-6にもそのまま答えられる」と主張するが、各フィールドは`Observation<string> option`が**1つ**しかない。Hyprlandが`initialClass`を**別フィールド**として持つ（survey L168）のと構造が違う。初期値を保持する場所が無い。
2. `CoreWindowExe`は5.3自身が書く通り「アプリがアクティブな間だけ取得可能」「フルスクリーン時は親子ともApplicationFrameHostのPIDを返す」。すなわち値が状態依存で揺れる。
3. `WindowObserved of WindowSnapshot`はスナップショット全体を運ぶ。次の`WindowObserved`で、Shellが今回問い合わせなかったフィールド（再probeしない`TargetIntegrity`、非アクティブ時の`CoreWindowExe`）を`None`で送るのか前回値を運ぶのか、Core側でマージするのかが定義されていない。

**失敗シナリオ**：`AppIs (CoreWindowExe, "Calculator.exe") → Floating`。アクティブ時の観測でYes→Floating。非アクティブ化後の再観測で`CoreWindowExe = Some {Value = Error …}`または`ApplicationFrameHost.exe`（フルスクリーン時）に変わる→No/Unknown→`DefaultManagement = Tiled`へ再分類→再びアクティブ化→Floating。フォーカスするたびに分類が反転する。同様に、`TargetIntegrity`を発見時にしかprobeしないShellが後続スナップショットで`None`を送ると、`TargetIntegrityIs Higher`（Priority 90）が毎回Unknownになり、5.4の「高優先のUnknownがあれば低優先のTiledを保留」により**全ての窓の分類が毎回の再観測で保留へ戻る**。

**修正案**：
- 7.1/4.2に「`WindowSnapshot`は部分観測であり、Coreは`None`フィールドを前回値で上書きしない（フィールド単位のlast-known-goodマージ）」と明記し、マージ関数を純粋関数として性質テスト対象にする。
- 初期値保持を本当に主張するなら`AppIdentity`に`Initial`/`Current`の2スロットを持たせるか、L211の主張を削除する。
- A-6の既定を「`AppIdentity`の各フィールドは最初に`Ok`になった値で固定（sticky）」にすることを検討（CoreWindowExeの揺れに対しては事実上これが必要）。

---

## R3-7【中】ADR-015は未検証の1点に依存したまま「採用」になっており、その1点が失敗した場合の代替が、ADR-015自身が退けた推論と同じになる。また根拠の一部が既存の対応表と矛盾する

**場所**：§0 ADR-015（L31）、8.1 L740-742、21章 L1265、5.4、11.1 `EvidenceGap`

**問題**：
1. ADR-015の成立は「Medium整合性から`OpenProcessToken`+`GetTokenInformation(TokenIntegrityLevel)`がHigh整合性プロセスに対して成功する」ことに依存する（survey L299で`[unverified]`、21章でも未検証）。これが失敗（`ERROR_ACCESS_DENIED`）した場合、残る手掛かりは「トークンを開けなかった＝おそらくHigh」という**エラーコードからの原因推定**だけになる。これはADR-015が`TargetIntegrityHigher`を退けた理由（「エラーコード単体では整合性起因か判別できない」）と同型の推論である。つまりADR-015は検証結果次第で自分の根拠を失う。21章が「残る論点は実装手段のみ」と書くのは過小評価。
2. ADR-015は「`classify`は`(Api, Space, Code)`という純粋な3つ組のまま保つ」を根拠に挙げるが、8.1の既存の対応表（L740-741）は既に`ElapsedUs`で`Transient`/`TargetHung`を分けており、`classify`は3つ組の関数ではない。正しい不変条件は「`classify`は`NativeCallRecord`だけの純粋関数で、窓状態を読まない」。ADR本文がこれを誤って言い換えているため、後の変更者が「ElapsedUsを使う既存エントリはADR-015違反」と誤読しうる。
3. probe結果が`Some { Value = Error … }`の時に`TargetIntegrityIs`がどの`Truth`になるかが未定義。5.4は「未観測→Unknown」としか言わない。また`Unknown of EvidenceGap list`に載せる`EvidenceGap`のケースが11.1に無い（`MissingAppIdentity`等はあるが整合性レベルの欠落は無い）。

**失敗シナリオ**：Phase 0スパイクで`OpenProcessToken`がMedium→Highで拒否されたとする。全ての昇格窓で`TargetIntegrity = Some (Error …)`→`TargetIntegrityIs Higher`がUnknown→Priority 90のUnknownにより保留→5.4の期限超過→Floating。結果は偶然正しく見えるが、同じ経路を通る非昇格窓（`OpenProcess`自体が失敗するプロセス、終了直前のプロセス）もすべてFloatingになり、Explainは「整合性レベル起因の可能性が高い」（L742）と誤った説明を返す。

**修正案**：
- ADR-015の状態を「採用（条件付き：S7r系スパイクで`TokenIntegrityLevel`取得のMedium→High成否を確認するまで）」にし、失敗時の代替（例：`Error`時はUnknownのまま扱い、Explainでは原因を断定しない。`OtherDenial`の既存の反応で十分なので`TargetIntegrity`自体を削除できる）を前もって書いておく。
- ADR-015本文の「`(Api, Space, Code)`の3つ組」を「`NativeCallRecord`のみの純粋関数（窓状態を読まない）」に訂正。15.1 #14の表現も合わせる。
- 11.1に`MissingTargetIntegrity of WindowId`を追加し、`Error`観測→`Unknown [MissingTargetIntegrity]`と5.4に明記。

---

## R3-8【中】ワークスペース非表示方式にCloakを加えると、WM自身が隠した窓が「別仮想デスクトップの窓」と区別できず、分類でExcludedになって所属を失う

**場所**：21章 L1262、4.4 L267、4.2 `IsCloaked: bool option`、7.1 `CloakChanged`、4.3 L221、12.1 L964、8.4 L763

**問題**：`IApplicationView::SetCloak`による非表示は、OSの仮想デスクトップ切替と同じシェル起因のCloakになる（`DWMWA_CLOAKED`のビットで言えば`DWM_CLOAKED_SHELL`）。本書はCloakを「別デスクトップの窓・UWPのゴーストフレーム」の判別手掛かりとして扱っており（4.3、12.1）、`IsCloaked`は`bool`なのでCloakの理由ビットも失われる。21章の比較軸には「未文書化APIの依存リスク」と「クラッシュ時の復元容易性」しか無く、この自己観測の干渉が挙がっていない。

**失敗シナリオ**：WS2を非表示にするためWM自身が窓をCloak→`CloakChanged(w, true)`が届く→Normalizer/分類がこれを「他の仮想デスクトップの窓」と判定してExcluded→4.4 L266により`WindowWorkspace`から除去→WS2へ戻っても窓は戻らない。またクラッシュ時、Cloakされた窓は未文書化COMでしか戻せないが、8.4の復旧経路は`Native`モジュールの「表示の復元・画面内への移動」に限定されており、COM呼出しはその範囲に入っていない（復旧不能）。

**修正案**：21章のCloak行の比較軸に「WMが自分で付けたCloakを観測側で区別する手段（自分が発行したCloak Effectの記録との照合、`DWMWA_CLOAKED`のビット値を`IsCloaked`の代わりに保持）」と「8.4の限定APIにuncloakを含められるか」を追加する。決められないならCloakを選択肢から外す条件として明記する。

---

## R3-9【低〜中】DTO/スキーマ規約の適用漏れ：既存タグのペイロード形状変更、新DUの登録、廃止タグの扱い

**場所**：5.2 L290、5.3 L342（`SchemaVersion = 1`のまま）、9.2 L803-807、§23 L1289、7.1 L577

**問題と具体的影響**：
- `AppIs`はタグを保ったままペイロードが`string`→`AppIdSource * string`に変わった。9.2の規約は「未知のタグ」しか扱わず、「既知タグのペイロード形状変更」の扱いが無い。`PinnedPolicy.ofStored`（14章）は「当時のまま」読む契約なので、旧形状のPolicyを読むと`ofStored`が失敗するか、寛容なデコーダが黙って`(Exe, s)`へ移行する（＝`PinnedPolicy`の「当時のまま」に反する暗黙のマイグレーション）。canonical hashも変わる。サンプルの`SchemaVersion = 1`は据え置き。
- 新DU`AppIdSource`（PolicyArtifact）、`IntegrityRelation`（PolicyArtifactと、`WindowObserved`経由でJournal）は9.2の「全DUに安定タグ」の対象だが、対象一覧を置くはずの`docs/contracts.md`はリポジトリに存在しない（17章で参照のみ）。
- `DenialKind.TargetIntegrityHigher`の削除：`EffectFailure`はJournalに永続化されずReplay時に`classify`で再計算される（9.2 L799）ので、Journal互換上の実害は無い。ただしIPCのExplain出力とIncident Bundleには表示用DTOとして出る（9.2 L803が`EffectFailure`を対象に含める）。廃止タグ（`"target-integrity-higher"`等）を将来別の意味で再利用しないという規則が無い。§23は「`EffectFailure`のDUケースの追加・削除」を独立レビュー対象にするが、ペイロードの`DenialKind`は文言上含まれない（今回の削除はまさにこの穴を通った）。

現時点で実データが無いので実害はゼロだが、Phase AでofDtoを書く時点（21章）で必ず踏む。

**修正案**：9.2に「既知タグのペイロード形状を変える場合はタグを新設し旧タグは読み取り専用で残す、またはPolicyのSchemaVersionを上げて明示的な変換Artifactを作る（9.5の方針）」「廃止したタグは予約済みとして再利用しない」を追加。§23の対象を「`EffectFailure`とそのペイロードDU（`DenialKind`）」に広げる。サンプルの`SchemaVersion`を2にするか、v2.5時点で互換対象のデータが無いことを明記。

---

## R3-10【低〜中】`MonitorId`の安定識別がEDIDシリアルの衝突を考慮していない。`MonitorSnapshot`型が定義されていない

**場所**：4.1 L158、7.1 L608、6.2 L532

**問題**：同一機種モニター2台はEDIDのシリアルが同一または0のことが珍しくない。シリアルだけをキーに`MonitorId`を割り当てると2台が同じ`MonitorId`になり、`Screens: Map<MonitorId, _>`で片方が消える（そのモニターにはWSが割り当たらない）。一方、接続ポート（デバイスパス）を混ぜると「同じモニターを別ポートへ挿し直した」場合に別IDになる。どの組合せでキーにするかが未定義。また安定IDの契約を運ぶはずの`MonitorSnapshot`は本書のどこにも定義されていない（v2.5以前からの欠落だが、今回それに依存する契約を追加した）。

**修正案**：キーを「(EDIDの製造者・製品・シリアル, 衝突時はデバイスパス)」のような順序付きフォールバックとして定義し、衝突検出時の振る舞いを書く。`MonitorSnapshot`をContractsの型として4章に定義する。

---

## R3-11【低】Phase Aのゲートと実装単位が今回の追加を拾っていない

**場所**：18章 L1170、L1158、19章 L1202

- Phase Aのゲートは「15.1 #1〜#8、#11〜#13」で、#16（FocusHistory）が含まれない。Phase Aは「WmStateを最初に固定する」Phaseなので、#16と、R3-2/R3-3で追加すべき性質はPhase Aゲートに入れないと、不変条件が未実装のままPhase Aを通過できる。
- 7.3 L681で「S8の決定ロジックはPhase Aのシミュレーション対象にできる」と書いたが、18章Phase Aの項目にも、ゲートにも、19章の実装ファイル一覧にも対応物が無い（「できる」であって「する」ではない）。R3-1のHomeMonitorのバグはまさにこのテストで見つかるはずのもの。

**修正案**：Phase Aゲートに#16（と新設する#17等）を追加。Phase Aの項目と19章にモニターライフサイクルの純粋関数（例：`MonitorLifecycle.fs`）とcascaded disconnectテストを入れる。

---

## R3-12【低】Unknownの期限超過後に遅れて届いた証拠の扱いと、期限の実装位置が未定義

**場所**：5.4 L381

- 期限超過でFloatingに解決した後、遅れて証拠が届いて分類がTiledになったら再分類するのかが無い。する場合はユーザーから見て「Floatingで表示→数秒後に突然タイルへ移動」になり、7.3の再分類振動の対象に入れる必要がある。しない場合は遅いprobeの窓が恒久的にFloatingになる（bspwmより軽いが同種の問題）。
- 期限は決定性（2.2）のためCoreの`ArmTimer`/`TimerElapsed`で表現すべきだが、明記が無い。Shell側で時間切れにすると期限判定がReplayに残らない。

**修正案**：「期限はCoreの`ArmTimer`で実装し、期限後の証拠による再分類は1回まで許可（7.3の振動カウンタに算入）」等、どちらかを選んで書く。

---

## R3-13【低】GlazeWM `wm-watcher`の引用が、本書の要件（ハング耐性）より弱いモデルを「同型」と呼んでいる

**場所**：8.4 L765

`wm-watcher`の発動条件は「`application_exiting`を受けずにIPC購読が終わったこと」（survey L266）であり、WMがクラッシュした場合は機能するが、**ハングした場合（プロセスもパイプも生きている）は何もしない**。8.4の主要件は「ハング時でも停止経路が生きていること」（E-1/E-2）なので、「同型の先行実装」「本節の方針を裏付ける」は言い過ぎ。実装者がwm-watcherの発動条件をそのまま写すと、ハング時の復旧が抜ける。

**修正案**：「クラッシュ検知の経路についての先行例。ハング検知（ハートビート等）はwm-watcherには無く、本WM独自に必要」と限定する。

---

## ADR整理についての所見

- ADR-015を独立ADRにしたこと自体は妥当：`WindowSnapshot`への観測属性追加・新しい述語・`DenialKind`の縮小と、ADR-014（エラー表現）の範囲を越えて4.2/5.2/7.1/8.1にまたがるため。ただし実質的にADR-014の一部を改訂しているので、ADR-015の「状態」欄に「ADR-014の`DenialKind`初期セットを改訂」と相互参照を書くべき（ADR-014の行は「`EffectFailure`の`Denied`」に触れたまま）。状態は上記R3-7の通り条件付きにすべき。
- ADR-011のv2.5追記は、v2.5で`Screens`を「修正した」と書いているが、R3-1/R3-2/R3-3のため、まだ「Policy IRより先に固定」（ADR-011本文・22章#13）できる状態ではない。
- Version行・Status行・関連資料の追記は互いに整合している。軽微：Version行「`WmState`に`Screens`/`HomeMonitor`を追加」とあるが、`HomeMonitor`は`WorkspaceState`のフィールド。

---

## 判定

**v2.5の変更はこのままでは確定扱いにすべきではない。** 以下の3点は、ADR-011（「Phase Aで最初に固定する中核状態」）そのものに関わり、実装すると最初の実機利用で表に出る：

- **R3-1**（HomeMonitorの更新規則が、スリープ復帰の一時切断という最頻ケースで復帰先を消す）
- **R3-2**（Screens/ActiveMonitorに不変条件が無い。アクティブなモニターを抜くと`KeyNotFoundException`→安全停止。SwitchWorkspaceが同じWSを2画面に出しうる）
- **R3-3**（フォーカスの情報源が二重で、RecentFocusに生存・上限の不変条件が無い）

R3-4〜R3-7は、Phase A着手前（PolicyEvaluator/WindowSnapshotのマージを書く前）に方針を1行ずつ決めれば足りる。R3-8〜R3-13は現行の未決事項・ゲートの記述修正で対応できる。OSS調査の結論（DenialKindを増やさない、AppIdを派生元別にする、Coreは物理px）自体の方向性は妥当で、問題は主にそれを状態モデルへ落とす際の不変条件と更新規則の欠落にある。
