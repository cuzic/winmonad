> **反映状況（v2.6）**：本ファイルは、architecture.md v2.6（ADR-016 観測・信念・決定の3層分離、ADR-017 待ちのデータ表現、ADR-018 Coreのドメイン分割、および前回レビュー[review-oss-survey.md]のR3-1〜R3-13の反映）に対する、独立したOpusレビュアー1名による5ラウンドの批判的レビューの全文である。第1ラウンド（R4-1〜R4-25）、第2ラウンド（R5-1〜R5-11）、第3ラウンド（R6-1〜R6-12。プロジェクトオーナーの指示による構造変更――信念のドメイン別化、Coreの6ドメイン分割、zipper、3.4の新設――の後の再確認）、第4ラウンド（R7-1〜R7-5）、第5ラウンド（収束確認）からなる。全ての「確定前に必須」の指摘はarchitecture.mdに反映済みで、第5ラウンドで収束と判定された。レビューで提示された「オーナー判断の論点」のうち、推奨案が明確なもの（稼働中のCheckpoint復元をしない、動かせない窓をWorkspaceに所属させない、`Reconfiguring`区間の抑止）は推奨どおり確定し、残り（hiddenなWorkspaceの窓の自己アクティブ化でWorkspaceを切り替えるか、分類待ち中のWorkspace切替時の所属、Unknown期限の既定、最小化した窓の枠、zipperを意図で回すか）は既定を置いてarchitecture.md 21章に記載し、その後プロジェクトオーナーが5件とも既定（(A)）を確認した。


---

# Opus批判的レビュー：architecture.md v2.6（ADR-016/ADR-017・R3反映）第1ラウンド

対象：`docs/architecture.md` v2.6 全文（1570行）＋ `git diff`（v2.5→v2.6）、`docs/review-oss-survey.md`（R3-1〜R3-13と反映表）。
行番号は現行 `docs/architecture.md` のもの。重大度順。各指摘は「場所／問題／失敗シナリオ／修正案」。

総評：ADR-016/017の方向性（推定を事実として扱わない、待ちをデータにする、タイマー発火の古さをCore側Deadlineで判定する）は正しく、特に「Deadline ≤ 入力時刻でのみ期限切れとみなす」規則はランタイム実装に依存せず成立する良い設計である。しかし、(1) 3.3の`Consumed`の固定順序が4.5/6.1/7.3の「観測は必ず信念に反映する」と正面衝突している、(2) 「期限の正本はCore状態」と言いながら`WmState`に待ち・管理状態を置くフィールドが存在しない、(3) 稼働中のCheckpoint復元がReplay決定性とタイマーの両方を壊す、(4) フォーカスの単一情報源が最頻ケース（新しい窓を開く）で破綻する、の4点は確定前に必須の修正である。

---

## R4-1【高】`Consumed`の固定順序「画面構成変化→Pending→分類→信念」が、信念層を最後に置いており、6.1・7.3・4.4・9.3と矛盾する。grace window中の観測は信念に届かない

**場所**：3.3 L167（`Consumed`の説明）、L180（固定順序の例）、7.3 L918（「観測自体は信念に反映する」）、4.4 L390（OS起因：「観測を信念には反映するが」）、6.1 L666（Observation→Belief Revision→Classify）、9.3 L1060（照合は`VisibleFrameRect`の信念と目標矩形を比べる）

**問題**：
1. 3.3は「前段が`Consumed = true`を返した入力は後段に渡さない」とし、例示順で信念を最後に置く。7.3は`ScreenGrace`中、画面構成の状態機械が窓の移動・最小化の観測に`Consumed = true`を返すとする。すると、grace中の観測はPending・分類・**信念**のいずれにも届かない。7.3/4.4の「観測自体は信念に反映する」は規約上実現不可能。
2. Pendingが信念より前にあるので、Pendingの照合（9.3：信念と目標矩形を比較）は**今回の観測を反映する前の**`VisibleFrameRect`で行われる。6.1の順序（信念→分類→Reconcile）とも逆。
3. `MinWidth`/`Movability(Confirmed)`は「Pendingの解決（Satisfied/Mismatch）」を証拠に信念を修正する（L464-465, L477, L479）ので、同じstep内で「観測→信念（矩形）→Pending照合→信念（MinWidth等）→分類→Reconcile」という**2段の信念修正**が必要だが、どこにも書かれていない。

**失敗シナリオ**：
- (i) スリープ復帰。`ScreenSettle`満了→`reconcile`→`ObserveAll`発行→`StartTimer ScreenGrace`。`ObserveAll`の結果の`WindowObserved`群はgrace中に届くので全て`Consumed`され、信念はスリープ前の矩形のまま。grace満了で「意図した配置を再適用」するReconcilerは、信念上は全窓が既に目標矩形にあるので`SetRect`を出さない。OSが寄せた窓は寄せられたまま放置される（E-3対策の核心が無効化）。
- (ii) `SetRect(R1, A, 500)`発行→`WindowObserved(A, 500)`到着。Pendingが先に処理され、古い信念（例：800）と比較して`Mismatch`→`Reevaluate`。直後に信念が500に更新されるが、Pendingは既に解決済み。次の観測が来なければ誤ったMismatchが`MinWidth`の証拠になり、`MinWidth`を誤って推定する。

**修正案**：
- 信念修正（観測由来の部分）は`Consumed`の対象外とし、**全入力で最初に無条件に実行する段**と明記する（6.1の順序に一致させる）。`Consumed`が抑止するのは「ユーザー操作としての解釈」（4.4のドラッグ規則）だけに限定する。より良くは`Consumed: bool`を廃止し、画面構成の状態機械が入力に原因ラベル（例：`MoveCause = OsInduced | UserDrag | Unattributed`）を付けて後段に渡す形にする（Boolで「後段に渡さない」は情報を捨てすぎる）。
- 1 step内の順序を規範として書く：①観測の`revise`（LatestWins等）→②Pending照合→③照合結果の`revise`（MinWidth/Movability/Responsiveness）→④分類→⑤Layout/Reconcile→⑥gate。15.1にこの順序の性質テスト（「同じ観測で`Satisfied`になるべきものが`Mismatch`にならない」）を追加。

---

## R4-2【高】「期限の正本はCore状態側」と言いながら、`WmState`に待ち状態・管理状態を置くフィールドが無い。#21とCheckpointが成立しない

**場所**：3.3 L178、4.4 L324-335（`WmState`）、4.3 L294-299、5.4 L598-602、6.2 L694/L759（`Management: Map<WindowId, Management>`の出所）、7.2 L907、7.3 L918、15.1 #21 L1334

**問題**：`TimerId`の5種のうち`WmState`内に待ちの実体（Deadline付き）を持てるのは`PendingRequest`だけで、しかもPendingの格納先フィールドすら`WmState`に無い。以下が全て「どこにも無い状態」になっている：
- 各窓の管理状態（Tiled/Floating/Excluded、Suspendedとその理由）。`GateInput.Management`/`ValidationInput.Management`の出所が無い。4.3のSuspendedは`Management`型にも無い。
- 分類待ち（`ClassificationDeadline w`の待ちとDeadline）、「期限超過でFloatingに解決済み」フラグ、「期限後の再分類を1回使ったか」（5.4 L602）、振動カウンタ（7.3）。
- 信念の再probe状態（`BeliefReprobe`の`RetryState`とDeadline。`WindowBeliefs`には無く、置くと「信念は観測の関数」と混ざる）。
- `ScreenSettle`中の未確定トポロジ（7.3 L918「満了時点の最新トポロジ」）、`ScreenGrace`の期限。
- `Movability`の「ユーザー操作後1回だけ試し直す」（L477）の残り回数。

これらが状態として定義されないと、(a) 「`Deadline = Some d`を持つ全ての待ち」（#21）を列挙できない、(b) Checkpointが分類結果・Suspendを保存しない、(c) 「期限超過でFloating」のような**時間依存の決定**を毎step信念から再計算することになり、期限超過の事実が失われる。

**失敗シナリオ**：窓WがUnknown→期限超過でFloatingに解決。次のstepで別窓の`WindowObserved`により全窓が再分類される実装（管理状態を持たず信念から毎回導出）では、Wは再びUnknown→保留→`ClassificationDeadline`を再始動、を永遠に繰り返す（5.4が退けたbspwmの「永久に未管理」と同型）。

**修正案**：`WmState`に最低限次を追加し、4.4の型定義を完全にする：`Windows: Map<WindowId, WindowCoreState>`（`Management`、`Suspension: Set<SuspendReason>`、`Classification: Classified | AwaitingEvidence of deadline | ResolvedByTimeout of at`、`ReclassifyBudget`、振動履歴）、`Pending: Map<RequestId, PendingRequest>`、`Reprobes: Map<WindowId * BeliefKey, RetryState * deadline>`、`Screen: ScreenPhase`（`Stable | Settling of latest: MonitorSnapshot list * deadline | Grace of deadline`）。#21の検査対象をこのフィールド群として列挙する。`SuspendReason`を集合にしておけば、R4-11/7.2の「Responsiveへ修正されたら解除」が振動由来のSuspendまで解除する事故も防げる。

---

## R4-3【高】稼働中の再起動によるCheckpoint復元が、(a) Replay決定性、(b) タイマー、(c) HWND↔WindowId対応の3点で破綻する

**場所**：4.5 L486、9.5 L1073、4.1 L204（`HWND→WindowId`はShellが持つ）、3.3 L178-179、16.2 L1350

**問題**：
- (a) 「復元時に全ての`Believed`を`Stale asOf 復元時刻`にする」処理は、入力（Journal上のイベント）として表現されていない。新セッションをReplayする者はCheckpoint Cから始めるが、4.5は「Replayのための復元ではそのまま使う」としているため、Replayでは非Staleの信念、元の実行ではStaleの信念、という食い違いが起きる。確度は分類の結論を変えないが、**再probeの契機を変える**（L431）ので`Reprobe` Effectの列が変わり、#10（Event/Effect/Traceハッシュの一致）が破れる。
- (b) ランタイムのタイマーはプロセスと共に消える。CheckpointにはPendingや分類待ちの`Deadline`が残るが、`StartTimer`を誰も再発行しないので、`TimerElapsed`が永遠に来ない。分類待ちの窓は永久にUnknown（5.4が禁じた状態）、Pendingは永久に残り7.3の重複抑止で新しい意図をブロックする。#21も「取り消されずに発行されている」が偽になる。
- (c) WindowIdはShellが割り当て、対応表はCore状態ではない。プロセス再起動でShellの対応表が失われれば、全窓が新しいWindowIdで再発見され、Checkpointの`Beliefs`/`Workspaces`/`FocusHistory`は誰とも対応しない孤児になる（`Beliefs`は`WindowRemoved`でしか消えないので残り続ける）。対応表を永続化するなら、HWND再利用の検査（Sticky `Process`）を復元時に必ず通す手順が要る。

**失敗シナリオ**：WMがクラッシュ→再起動してCheckpointから復元。WS3に属していた窓Xは新しいWindowId X'として発見され、Unknown→既定位置のまま。旧XはWS3の`Order`に残り、Layoutは存在しない窓の枠を空けて配置し、`RelayoutWithout`（6.2）が毎回発火して「導出不整合の疑い」をTraceに積み続ける。

**修正案**：二択をプロジェクトオーナーが選ぶ（下記「オーナー判断」参照）。
- 推奨（最小主義）：**稼働中の再起動ではCheckpointから復元しない**。16.2の「保存状態をOSの真実とみなさず全体再観測」を徹底し、`Workspaces`の所属だけをHWND＋`ProcessKey`をキーにした小さな「所属ヒント」として永続化し、再発見時に照合する。4.5 L486/9.5の「Staleにして使う」は削除。
- 復元を残すなら：`SessionResumed of fromCheckpoint`を新セッション最初の入力としてJournalに記録し、Stale化・全待ちの`StartTimer`再発行（残り時間または即時）・WindowIdの再照合をその`step`の応答として定義する。#19/#21に「復元直後も成り立つ」を加える。

---

## R4-4【高】`Focused`が`ForegroundObserved`でしか更新されないため、新しく開いた窓（最頻ケース）が分類確定後も`Elsewhere`のまま残る。Excluded化・消滅時の`Focused`の更新も無く(F2)が破れる

**場所**：4.4 L322, L331, L359, L383, L385 (F2)、5.4 L598（Unknownの間はOrderに入れない）

**問題**：新しい窓はまず「既定位置で表示→分類確定→タイルへ」の2段階（5.4）。表示された時点でOSはその窓をフォアグラウンドにし、`ForegroundObserved(w)`が来るが、wは未分類なので`Focused = Elsewhere`（L322）。数百ms後に分類が確定して`Order`に入っても、フォアグラウンドは変わらないので`ForegroundObserved`は二度と来ず、`Focused`は`Elsewhere`のまま。同様に、`Focused = ManagedWindow w`のwがExcluded化・消滅した場合、L359は`WindowWorkspace`と`FocusHistory`からの除去しか規定しておらず、`Focused`が死んだwを指したまま(F2)に違反する。

**失敗シナリオ**：ターミナルを新規起動→タイル化される→ユーザーが`Alt+Shift+C`（CloseFocused）。`Focused = Elsewhere`なので何もしない（L348）。`PromoteToMaster`も無反応、`FocusNext`は「そのWorkspaceで最後にフォーカスしていた窓」へ飛ぶ。新しい窓を開くたびに最初のキー操作が効かない。

**修正案**：生のフォアグラウンド観測を`ForegroundWindow: WindowId option`（LatestWinsの信念）として保持し、`Focused`は**導出値**にする：`Focused = if fg ∈ dom(WindowWorkspace) then ManagedWindow fg else Elsewhere`。管理状態が変わったstep（分類確定、Excluded化、消滅、ToggleFloating）でも再導出し、ManagedWindowへ遷移した瞬間に`FocusHistory`の先頭へ入れる。(F2)の性質テストの入力列に「フォアグラウンド中の窓の分類確定」「フォアグラウンド中の窓のExcluded化」を明示的に含める。

---

## R4-5【高】Workspace切替と「hiddenなWorkspaceの窓がフォアグラウンドになったら表示する」規則が競合し、WM自身の非表示操作で切替が巻き戻る

**場所**：4.4 L392、L380（SwitchWorkspace）、7.3 L918（`ScreenGrace`はあるがWS切替のgraceは無い）

**問題**：`SwitchWorkspace`では、旧WSの窓の非表示Effectと新WSの窓への`RequestForeground`が非同期に適用される。非表示方式によって、OSはその途中でフォアグラウンドを別の窓へ自動的に移す（最小化方式ならZ順の次の窓、非表示方式なら`SW_HIDE`された窓の次）。それが**まだ非表示処理中の旧WSの窓**だと`ForegroundObserved(旧WSの窓)`が届き、L392の規則によりCoreはそのWSを再表示する（`SwitchWorkspace`と同じ遷移）。`RequestForeground`が`ForegroundDenied`で拒否された場合も、フォアグラウンドは旧WSの（隠した）窓に残り、キー入力が見えない窓へ流れる。

**失敗シナリオ**：WS1（窓A,B,C、Aがフォアグラウンド）→`SwitchWorkspace 2`。非表示Effectのバッチを順に適用中、Aが隠れた瞬間にOSがBをアクティブ化→`ForegroundObserved(B)`→「hiddenなWS1の窓がフォアグラウンド」→WS1を再表示→WS2は再びhidden。ユーザーには「切替キーが効かない／一瞬切り替わって戻る」に見える。komorebi/GlazeWMが「pending focus」や切替中フラグで抑止しているのはこの現象である。

**修正案**：ADR-017の規約どおり、WS切替を「待ち」として表す：`SwitchWorkspace`は`AwaitingSwitchFocus of target: WindowId option * deadline`を状態に置き、`StartTimer (WorkspaceSwitchSettle, ms)`を返す。この待ちの間に届いた「hiddenなWSの窓のフォアグラウンド化」はL392の規則を発火させず、`Focused`の更新だけ行う（必要なら`RequestForeground`を再発行）。`TimerId`に1ケース追加。L392の規則自体（アプリの自己アクティブ化でWSを切り替える）は「オーナー判断」の論点として残す。

---

## R4-6【中〜高】`Effect`に窓の非表示／再表示が無く、`Desired`も矩形しか持たないため、4.4の`SwitchWorkspace`・モニター切断時のhidden化・grace後の再適用が状態遷移として書けない

**場所**：7.1 L790-799（`Effect`）、4.4 L360, L378, L380, L390、21章 L1526、6.2 GateInput

**問題**：4.4はview意味論・hidden化・「grace終了後に意図した配置を再適用」をCoreの遷移として確定したが、それを外界へ反映する`Effect`（Hide/Show/Minimize/Cloak）が無い。非表示方式（21章）が未決なのは良いが、方式に依存しない抽象Effect（例：`SetPresence of RequestId * WindowId * Presence`、`Presence = Shown | HiddenByWm`）と、`Desired`の「表示されているべきか」の次元が無いと、(a) Pending/Reconcilerが「hidden WSの窓が見えている」をMismatchとして扱えない、(b) gateに「hidden WSの窓へのSetRect」を拒否する規則を置けない、(c) 8.4の安全停止で「直前に隠した窓」を列挙する根拠（Coreのどの状態か）が無い。

**失敗シナリオ**：モニターB切断（settle超過）→WS3はhiddenになる（L367 (1)）。OSはBにあったWS3の窓をAへ移しており、それらはAに表示中のWS1の上に重なって見える。Coreの状態上はhiddenだが、hiddenにするEffectが存在しないため何も起きない。さらに4.4 L391の「原因不明の移動→Tiled窓は意図した配置へ戻す」で、Coreは「意図した配置」を矩形で持たない（hidden WSの窓にLayoutは計算しない、L378）ので、戻し先が未定義。

**修正案**：方式非依存の`Presence`次元を`Desired`と`Effect`に入れる（実装方式は21章で選ぶ）。hidden WSの窓の`Desired`は`HiddenByWm`、可視WSの窓は`Shown + rect`。Reconcilerが`Presence`の不一致からEffectを出し、gateは「`HiddenByWm`の窓への`SetRect`/`RequestForeground`」を拒否する。8.4の復旧対象は「`Desired = HiddenByWm`の窓」と定義できる。Phase AでWmStateを固定する前に必要（ADR-011）。

---

## R4-7【中〜高】R3-4の「`MoveSizeEnded`の直後」が未定義で、ドラッグで別モニターへ移した窓が引き戻されるというR3-4の失敗シナリオがそのまま残る

**場所**：4.4 L389-391、7.1 L834-835、3.3（待ちのデータ表現）

**問題**：規則は「`MoveSizeEnded`の直後」に`WindowBeliefs.Monitor`を見る。しかし`MoveSizeEnded of WindowId`は位置を運ばず、`Monitor`の信念は後続の`WindowObserved`（LOCATIONCHANGE→Shellの問い合わせ）で更新される。到着順は`MoveSizeEnded`→`WindowObserved`が普通である。「直後」の窓（時間か入力数か）が無いので、(a) `MoveSizeEnded`処理時点では信念はまだ旧モニター→食い違い無し→何もしない、(b) 後で届いた`WindowObserved`は「`MoveSizeEnded`の直後」と認識されず「原因不明」に分類→Tiled窓を元のモニターへ戻す。

**失敗シナリオ**：WS1（モニターA）のTiled窓をBへドラッグ→ドロップ→窓はAへ引き戻される。ユーザーが繰り返す→7.3の振動検知→Floatingに劣化。R3-4が指摘した「ユーザーとの引っ張り合い」そのもの。

**修正案**：ADR-017で書く：`MoveSizeEnded w`で`AwaitingDropObservation of w * deadline`を状態に置き、`ObserveWindow w`を発行し、`StartTimer (DropSettle w, ms)`。この待ちの間に届いたwの観測を「ユーザーのドラッグの結果」として解釈する。または`MoveSizeEnded`のペイロードにShellが同時に取得した矩形とモニターを載せる（`MoveSizeEnded of WindowId * Observation<PixelRect> * Observation<MonitorId>`）。後者の方が待ちが要らず単純。

---

## R4-8【中〜高】`Title`の信念は、Journalに記録しない方針（5.3/9.6）と「信念はJournalから再計算する」（ADR-016）を同時に満たせない。さらにCheckpointへタイトルが漏れる

**場所**：4.5 L457、5.3 L588、9.6 L1077、9.5 L1073、13.1 `checkpoint.json` L1246、ADR-016 L32

**問題**：`TitleContains`を使う設定では「取得したタイトルをJournalへ記録しない（分類にのみ使う）」（5.3 L588）。すると、Journal上の`WindowObserved`は`Title`を欠き、Replayは`Title`の信念を再計算できず、`TitleContains`に依存した分類・Layout・Effectが元の実行と異なる（#10、#19違反）。逆に、`WmState.Beliefs`はCheckpointに入るので（9.5）、Journalに書かないはずのタイトルがCheckpoint・Incident Bundleの`checkpoint.json`にそのまま残る。

**失敗シナリオ**：ルール`TitleContains "Picture-in-Picture" → Floating`。Replayでは`Title`が`Unformed`→`Unknown`→期限→Floatingで、元の実行と同じ結論に見えるが、経路（Unknown保留＋タイマー）が異なり、Effect列と期限タイマーの発火時刻が変わる。別のルールでは結論自体が変わる。

**修正案**：Journalにはタイトルそのものではなく、**有効なPolicyの全`TitleContains`パターンに対する評価結果のビット集合**（＋Policyハッシュ）を記録し、`Title`の信念を「パターン一致ベクトル」として持つ。Replayはピン留めPolicyなのでこれで再現できる。Counterfactual（別Policy）では`Unformed`になることを明記する。Checkpointにも生タイトルを入れない。15.1に「Checkpoint/Bundleに`Title`の生文字列が含まれない」を追加。

---

## R4-9【中】信念はPendingの解決（＝決定）にも依存しており、「信念は観測履歴の関数」「決定は信念だけを読む」という3層の主張が成り立っていない。Counterfactualの説明が誤り

**場所**：ADR-016 L32、4.5 L399-400（表）、L464-465（`BeliefEvidence`にPending解決とユーザー操作）、L477、L479、L482-483、15.1 #19、22章 #18

**問題**：`MinWidth`と`Movability(Confirmed)`は`Satisfied`/`Mismatch`を証拠にするが、これはPending（決定層の状態：要求した目標矩形、IntentEpoch）に依存する。`Movability`はユーザーのCoreCommandにも依存する。したがって信念は「観測履歴の関数」ではなく「**入力列全体とCoreの過去の決定の関数**」である（フィードバックループ）。決定的ではあるのでFixed-log Replayは成立するが：
- L483「推定規則を改良したCoreでCounterfactualを行えば同じ観測履歴から改良後の信念」は、Policy/Layoutが変われば要求矩形が変わり`Mismatch`の証拠も変わるので不正確。
- Checkpointから始めるCounterfactualでは、Checkpoint内の信念は**旧`revise`**で畳み込まれた結果（Sticky/Accumulatingは過去に依存）なので、「改良後の信念」にはならない。
- 4.5の表で`WmState.Focused`を信念に分類しているが、`Confidence`を持たない。また決定（4.4 R3-4規則、7.4）は`MoveSizeEnded`・タイマー・コマンドといった信念でない入力も読む。

**失敗シナリオ**：改良版`revise`の効果をCheckpoint起点のCounterfactualで評価し「`MinWidth`の誤推定が直った」と判断するが、実際はCheckpoint以前に旧規則で固定された値を引き継いでいるだけで、改良は検証されていない。

**修正案**：ADR-016と15.1 #19の表現を「信念はCoreの決定的な状態の一部であり、入力列から（Pendingを含むCore状態と共に）再計算される。窓の**属性**について決定が読むのは信念だけである」に直す。Counterfactualで修正規則の改良を評価する場合は**セッション先頭から**畳み込み直す（Checkpoint起点では不可）と12.2に明記。`Focused`は表から外す（R4-4の導出値にする）。

---

## R4-10【中】イベント型の入力（`VisibilityChanged`/`MinimizedStateChanged`/`CloakChanged`/`ForegroundObserved`）が`BeliefEvidence`に含まれず、スナップショットとの新旧比較の規則も無い

**場所**：4.5 L464-465（`BeliefEvidence`の列挙）、L472（LatestWinsは`SampleSequence`で比較）、7.1 L836-838

**問題**：`IsVisible`/`IsMinimized`/`Cloak`はLatestWinsで、`WindowSnapshot`の`SampleSequence`の新旧で置換を決める。しかし同じ属性を運ぶ`VisibilityChanged`等のイベントは`BeliefEvidence`に入っておらず、`SampleSequence`も持たない（Envelopeの`Sequence`はReducer投入順で、`SampleSequence`とは別の番号空間）。イベントで信念を更新しないなら、イベントは無意味。更新するなら、「スナップショットはイベントより前に問い合わせたが、キューにはイベントの後に入った」場合に古い値で上書きされる（7.2 L901が防ぐと言っている現象そのもの）。

**失敗シナリオ**：Shellが窓Wの全体スナップショットを問い合わせ（Visible=true）→直後にWが非表示化され`VisibilityChanged(W,false)`が先にキューへ→スナップショットが後から到着→信念はVisible=true。WMは非表示の窓に`SetRect`を出し続ける／hidden WSの窓が見えているとMismatch判定する。

**修正案**：Shellが全ての観測（イベント由来・問い合わせ由来とも）に**単一の単調な`SampleSequence`**（Shell側の取得時点の採番）を付けることを契約にし、イベント型の入力も`Observation<'T>`を運ぶ形にして`BeliefEvidence`に含める。`revise`は全フィールドでこの番号だけを比較する。

---

## R4-11【中】`NotMovable`の既定の扱いが7.2と5.5で矛盾し、既定設定ではタイル配置に穴が残る。`NotMovable`は自動では二度と見直されない

**場所**：7.2 L893（「再分類（既定ルールでFloating）」）、5.5 L619（`MovableIs false`ルールが無ければTiledのままSuspended、Warning）、5.3 L565（ルールはユーザー設定のサンプル）、7.2 L907、4.5 L477、20章 L1511

**問題**：
1. 7.2は「既定ルールでFloating」と書くが、組み込みルールは存在せず、5.5は「ルールが無ければTiledのまま」と書く。どちらが仕様か不明。
2. 「Tiledのまま`SetRect`を出さずSuspended」とすると、窓は`Order`に残りLayoutは枠を割り当てるが、窓はそこへ動かない。昇格窓が他のタイルの上に重なり、割り当てた枠は空白になる（5.5が自認する「空き」）。安全ではあるが、既定設定での体験として欠陥。
3. `NotMovable`の修正契機はユーザーの明示操作だけ（L477）。`Denied OtherDenial`は将来`classify`の対応表に追加されるコードも含み（8.1：Phase 0で増える）、一過性の拒否で窓が恒久的に管理外になり得る。ADR-016の「推定は誤り得る」に照らして、自動の見直し経路（`ProcessKey`が変わるまで、または長いバックオフの再試行）が無いのは不整合。20章が自認する「誤った信念の固着」に対策が無い。
4. ユーザー操作で`Movable (Inferred [])`に戻したとき、`TargetIntegrity = Higher`（ProbeOnceで既に確定済み）からの導出規則が即座に`NotMovable`へ戻すのか（状態からの導出）、観測イベント時だけ適用されるのか（イベント駆動）が未定義。前者なら「試し直し」は永遠に起きない。

**修正案**：Coreの組み込み規則として「`NotMovable`の窓は`LayoutContext.TiledWindows`から除く」（Unknownの窓と同じ扱い、5.4 L598）を明記し、Policyの`MovableIs`は「Floatingとして所属・フォーカス巡回に入れるか、Excludedにするか」の選択だけにする。5.5のWarningと7.2の文言をこれに合わせる。`NotMovable`には「同じ`ProcessKey`の間は維持、変われば事前仮定から出直す」を追加。L477の導出規則を「`TargetIntegrity`の観測イベント時にのみ適用」と明記。

---

## R4-12【中】R3-5は解消していない：`MinWidth`の信念を物理ピクセルで蓄積するので、DPIの異なるモニターへ移ると信念が変わり、分類が反転する

**場所**：4.5 L452, L479、5.2 L509、8.3 L994、5.5 L626（読み取り集合に`Monitor`を含めない）

**問題**：アプリの最小幅は通常DIPで決まっている（400DIPなら100%で400px、150%で600px）。v2.6は閾値を物理pxに固定して「分類をDPIに依存させない」としたが、比較相手の`MinWidth`の信念自体が、窓がどのDPIのモニターで`Mismatch`を起こしたかに依存する。さらに規則は「より小さい幅が観測されたら下げる」「Mismatchで上げる」の非対称なので、複数DPIを往復すると最大値側に偏る。

**失敗シナリオ**：最小幅400DIPのアプリ、ルール`MinWidthAtLeast 500 → Floating`。100%モニターでは`MinWidth`は（観測されたとしても）400→No→Tiled。150%モニターへ移すとLayoutが450pxを要求→600pxで`Mismatch`→`MinWidth = 600`→Yes→Floating。100%へ戻すと、450px要求は400pxの最小幅を満たすので`Mismatch`は起きず、信念は600のまま→Floatingのまま。分類が移動経路に依存する（R3-5の失敗シナリオと同型）。

**修正案**：`MinWidth`の信念を「観測したモニターのDPIと組」で持ち、DIPに正規化して蓄積する（`MinWidthDip`）。比較・Layout時に対象モニターのDPIで物理pxへ戻す。DPI非対応アプリ（`DpiBehavior = OsBitmapScaled`）では物理pxのまま、という分岐が要るので、`DpiBehavior`との依存を規則表に書く。あるいは`MinWidthAtLeast`を分類述語から外し、`MinWidth`はLayoutのソフト制約にだけ使う（分類にDPIを持ち込まない最も単純な解）。

---

## R4-13【中】Sticky規則の`AppIdentity`は、最初の値が誤っていたときに直す経路が無い。また`Confirmed`の定義と矛盾する

**場所**：4.5 L410（`Confirmed`＝それ以降に矛盾する証拠が無い）、L473（Sticky）、4.2 L290、5.3 L587（フルスクリーン時は親子ともApplicationFrameHostのPID）、20章 L1511

**問題**：(1) `CoreWindowExe`の最初の`Ok`がフルスクリーン中に取れると`ApplicationFrameHost.exe`で固定され、そのアプリのルールは窓の寿命中二度と当たらない。20章の対策は「Explainで示す」「Movabilityはユーザー操作で試し直す」だけで、AppIdentityには何も無い。(2) Stickyは矛盾する証拠が来ても`Confirmed`のままにするが、`Confirmed`の定義は「矛盾する証拠が無い」。矛盾を「記録してExplainで示す」とあるが、`Belief<'T>`にそれを置くフィールドが無い。

**失敗シナリオ**：UWPのメディアプレーヤーをフルスクリーンで起動→`CoreWindowExe = ApplicationFrameHost.exe`で固定→`AppIs (CoreWindowExe, "Video.UI.exe") → Floating`が窓の寿命中Noのまま。

**修正案**：Stickyの対象値に**妥当性フィルタ**を入れる（`CoreWindowExe`/`Exe`の値が`ApplicationFrameHost.exe`なら`Ok`扱いしない＝既知の不定値は証拠にしない）。矛盾は`Confidence`に`Contested of EvidenceRef list`を追加するか、`Belief`に`Contradictions: int`を持たせる。確度だけを`Inferred`に落としても結論が変わらない（L431）ので安全。

---

## R4-14【中】Sticky `Process`の不一致で「新しい`WindowId`として扱う」とあるが、WindowIdを採番するのはShellであり、Core内での手順が無い

**場所**：4.5 L473、4.1 L204、7.1 L854（ID採番は入力として明示）

**問題**：CoreはWindowIdを作れない（作るなら採番を入力にする必要がある）。旧WindowIdの`Order`/`FocusHistory`/Pending/待ちをどう処理するか、Shellの対応表をどう更新させるか（Effect？）が無い。`Process`が`Unformed [MissingProcessIdentity]`のとき、ProbeOnce行の「同じ`ProcessKey`の間は再probeしない」も未定義。

**失敗シナリオ**：HWND再利用を見逃して、別アプリの窓が旧窓のWindowIdで届く。Coreは不一致を検知するが新IDを作れず、旧WindowIdのまま旧アプリ用の分類・WS所属で扱い続ける（4.1が防ぐと言った誤認そのもの）。

**修正案**：Coreは不一致を検知したら旧Idに対して`WindowRemoved`と同じ遷移を内部で行い、`Effect.ForgetWindow of WindowId`（Shellが対応表から外し、次の列挙で新Idとして再発見させる）を返す。`Process`が`Unformed`の間は「同一性未確認」としてTargetIntegrityの再probe抑止を適用しない、と書く。

---

## R4-15【中】画面構成の安定待ち（ScreenSettle）の間、Layout・gateは確定前の古い`Monitors`を使う。`ScreenReconfigurationInProgress true`には期限が無い

**場所**：7.3 L916, L918、4.4 L326（`Monitors`は確定済みトポロジ）、6.2 L760（`GateInput.Monitors`）、7.2 L897、5.4 L600（無期限に待たない原則）

**問題**：
1. `MonitorTopologyObserved`を受けてから`ScreenSettle`満了までの間（settleMs、数秒想定）、`WmState.Monitors`は古いまま。この間に入力されたユーザーコマンド（`SwitchWorkspace`、`FocusMonitor`）は、既に存在しないモニターのWorkAreaでLayoutを計算し`SetRect`を出す。`GateInput.Monitors`が確定済みトポロジなら`OutsideAnyMonitor`は通過してしまう。抑止は`ScreenReconfigurationInProgress`（Shellが送る場合のみ）に依存し、生の`MonitorTopologyObserved`だけでは抑止されない。7.2は全Pendingを`Superseded`にするが、新規Pendingの発生は止めない。
2. `ScreenReconfigurationInProgress true`の後に`false`が来なければ（復帰通知の取りこぼし）、Effectは無期限に抑止される。ADR-017とbspwm反面教師（5.4）の原則に反する。

**失敗シナリオ**：モニターBを抜いた直後（settle中）に`SwitchWorkspace 3`→WS3をBに表示する計算→Bの座標への`SetRect`→OSは窓を画面外へ置く。

**修正案**：`ScreenPhase = Settling`の間はLayout由来のEffectを抑止する（ユーザーコマンドの状態遷移は行うが、Effectはsettle後の`reconcile`でまとめて出す）と明記。`ScreenReconfigurationInProgress true`も`ScreenSettle`と同じ待ちに入れ、上限時間で強制解除（swayの`txn_timeout_ms`と同じ考え方）。7.2の`MonitorTopologyObserved → Superseded`は「`ScreenSettle`満了時」に改める。

---

## R4-16【中】モニター切断の`reconcile`で未定義の分岐が残る：`ActiveMonitor`の付け替え先、アプリ起因のWS表示が`ReturnMonitor`を消すか

**場所**：4.4 L367（更新規則(1)〜(3)）、L369、L392、15.1 #18 L1331

**問題**：
1. `ActiveMonitor`のモニターが消えたとき、どのモニターを新しい`ActiveMonitor`にするかが書かれていない（主モニター？最小MonitorId？）。`reconcile`は純粋関数でも、仕様が無ければ実装依存。
2. L392「hiddenなWSの窓がフォアグラウンドになった→`SwitchWorkspace`と同じ遷移」は、規則(3)「ユーザーが`SwitchWorkspace`で明示的に表示したら`ReturnMonitor := None`」を発火させるのか。発火させるなら、アプリの自己アクティブ化（会議リマインダ等）で`ReturnMonitor`が消え、#18「ユーザーのSwitchWorkspaceが挟まらない限り元のモニターへ戻る」が破れる。発火させないなら「同じ遷移」ではない。
3. `SwitchWorkspace w`で`w ∉ dom(Workspaces)`（初めて`Alt+5`を押す）の場合の扱いが無い（作成するのか、何もしないのか。(S4)に関わる）。

**失敗シナリオ**：WS3（モニターB）→B切断（settle超過）→WS3 hidden、`ReturnMonitor = B`→WS3のSlackが通知で自己アクティブ化→WS3がAに表示、規則(3)で`ReturnMonitor = None`→B再接続→WS3はBに戻らない。

**修正案**：(1)「残ったモニターのうち`IsPrimary`、無ければ最小`MonitorId`」等を明記。(2)規則(3)の発火条件を「`UserCommand`由来の遷移のみ」に限定し、L392の遷移は`ReturnMonitor`を保持すると明記（#18の前提と一致させる）。(3)「存在しなければ空のWSを作成」と明記し、#17の入力列に含める。

---

## R4-17【中】`MonitorId`のセッション間の永続性が未定義で、Policyの`WorkspaceAssignments`が意味を持たない。衝突検出でセッション中にIDが変わる

**場所**：4.1 L206-208、5.2 L554、4.4 L334, L366

**問題**：`MonitorId of int64`はShellが割り当てるが、安定キー→int64の対応表を永続化するとはどこにも書かれていない。永続化しなければ、ユーザーがPolicyに書く`WorkspaceAssignments: (WorkspaceId * MonitorId) list`の`MonitorId`は起動ごとに別のモニターを指す。また、同機種2台でシリアルが非0かつ同一の場合、1台だけ接続中はキー(1)で一意→2台目接続時に衝突→両方キー(2)へ切替、で1台目のMonitorIdがセッション中に変わり、`reconcile`は「モニターXが消えてY,Zが現れた」と解釈する（`ReturnMonitor`もXを指したまま失効）。

**失敗シナリオ**：ドッキングステーションで同機種2台。起動時は1台目だけ検出（ID=X、WS1を表示）→2台目が数秒遅れて検出（settle超過）→1台目がY、2台目がZに→WS1はXの切断としてhidden化、Y/Zには新しいWSが出る。毎朝WSが入れ替わる。

**修正案**：「安定キー→MonitorId」の対応表を永続化対象（Host管理、Journalに割当を記録）と明記。キーは「一度キー(2)にした機種は以後キー(2)を使う」（衝突の有無で切替えない）か、最初から`(製造者, 製品, シリアル, デバイスパス)`の部分一致による照合にする。Policy側の`WorkspaceAssignments`は`MonitorId`のint64ではなく、ユーザーが読める安定キー（または名前）で書かせ、Coreの写しを作る時に解決する。

---

## R4-18【中】フォーカス関連のCoreCommandの意味が、Focusedが非アクティブなWS／別モニターにあるときに未定義

**場所**：4.4 L343-351, L380, L386、7.4 L926

**問題**：
1. マウスで別モニターBの窓をクリック→`Focused`はBの窓になるが、`ActiveMonitor`はAのまま（`ActiveMonitor`は`FocusMonitor`/`SwitchWorkspace`でしか変わらない）。直後の`SwitchWorkspace 2`はユーザーが見ていない側（A）を切り替える。xmonad/i3はフォーカスに画面を追従させる。
2. `FocusMostRecent`の行き先（`FocusHistory`の2番目）がhiddenなWSの窓の場合、そのWSを表示するのか（view意味論でどのモニターに）が無い。`RequestForeground`だけ出すと隠れた窓をフォアグラウンドにする。
3. `FocusNext`は「アクティブなWSの`Order @ Floating`上の巡回」だが、`Focused`が別WS（別モニター）の窓のときの起点が無い（`Elsewhere`扱いか）。
4. `MoveFocusedToWorkspace`でフォーカス窓をhidden WSへ送った後、`Focused`/フォアグラウンドをどこへ移すかが無い（7.4 L926の「閉じた後」と同じ規則を適用すべき）。

**失敗シナリオ**：B上の窓をクリックして作業→`Alt+2`→Aの表示が変わり、Bは変わらない。ユーザーは「キーが効かない」と認識する。

**修正案**：`Focused`が`ManagedWindow w`へ変わったとき、wが可視WSに属するなら`ActiveMonitor`をそのモニターへ追従させる、を規則として追加。`FocusMostRecent`は「対象のWSがhiddenならview意味論でアクティブモニターに表示してから`RequestForeground`」。`FocusNext`の起点は「`Focused`がアクティブWSに属さなければ`Elsewhere`と同じ扱い」。`MoveFocusedToWorkspace`後は7.4の「閉じた後」の規則でフォーカスを移す。これらを#16/#17の入力列に含める。

---

## R4-19【中】Unknown期限の既定（Floating）が、不可視の候補窓にも適用され、Floatingは所属・フォーカス巡回の対象になる。所属先WSも未定義

**場所**：5.4 L600-602、4.4 L315（Floatingは所属・フォーカス巡回の対象）、4.3 L294（Discovered）、4.5 L459（Beliefsは消滅まで保持）

**問題**：`EnumWindows`は通常数百の不可視トップレベル窓を返す。Discoveredの窓が分類でUnknownになれば、それぞれに`ClassificationDeadline`タイマーが張られ、期限後にFloatingになり、`WorkspaceState.Floating`に入って`FocusNext`の巡回対象になる。また、期限で解決した窓が属するWSが「解決時のアクティブWS」なのか「出現時のWS」なのかが無く、期限の数秒間にユーザーがWSを切り替えると別WSに入る。

**失敗シナリオ**：起動直後、IsDialogが取得できない不可視窓多数が数秒後に一斉にFloatingになり、`FocusNext`が見えない窓を巡回する。

**修正案**：分類（とUnknown期限）の対象は「Normalizerの候補条件（可視・非Cloak・非ツール窓等）を満たす窓」に限ると明記し、それ以外は`Discovered`のまま期限を張らない。期限解決時の所属は「出現時（最初にUnknownと評価された時）のアクティブWS」を待ちのデータに保存して使う。

---

## R4-20【中】ユーザーの明示操作（`ToggleFloating`/`SetManagement`）が、信念の修正による再分類で上書きされる

**場所**：4.4 L353-354、4.5 L477（ユーザー操作は`Movability`のリセットにだけ使われる）、5.5 L626（変化したフィールドを読むルールだけ再分類）、5.4 L602

**問題**：v2.6では信念の修正が再分類の契機になる（`IsDialog`/`Title`/`Cloak`/`Movability`のLatestWins等）。ユーザーが`ToggleFloating`でTiledにした窓も、次に読み取り集合と交差する信念が変われば、Policyで再分類されて元に戻る。ユーザー指定がPolicyより優先されるかどうかが規定されていない。v2.5までは再分類の契機が少なかったので顕在化しにくかったが、v2.6で増えた。

**失敗シナリオ**：ブラウザのPiP窓をユーザーが`ToggleFloating`でFloatingに→タイトルが変わる→`TitleContains`を読むルールで再分類→Tiledに戻る。

**修正案**：R4-2の`WindowCoreState`に`UserOverride: Management option`を持たせ、「ユーザー指定はPolicyより優先し、窓の寿命中維持する」と明記（23章の独立レビュー対象「ポリシーのUnknown処理」と並べて「ユーザー指定の優先順位」を追加）。

---

## R4-21【低】3.3/17.1のアーキテクチャテスト「Wm.Core/Wm.Policy/Wm.Layoutsが`FSharpFunc`型のフィールドを状態型に持たない」が、正当な関数フィールドを誤検出する

**場所**：3.3 L155、17.1 L1399、5.6 L645-650（`LayoutImplementation.Calculate`）

**問題**：`LayoutImplementation.Calculate: LayoutFunction`はWm.Layoutsにある関数フィールドで、`step`が受け取る`RuntimePolicy`もRegistry（関数）を含むはず。「状態型」の範囲が未定義だと、テストは誤検出で無効化されるか、範囲を狭めて意味を失う。

**修正案**：検査対象を「`WmState`から到達可能な全ての型」（＋Checkpoint DTO）と定義する。`RuntimePolicy`/`LayoutImplementation`は状態ではなく環境なので対象外と明記。

---

## R4-22【低】名前だけで中身の無い／重複する型

**場所**：3.3 L153（`CoroutineState`）、L164-168（`Response<'TimerId,'Action>`）、4.5 L460（`BeliefKey`はコメントのみ）、3.3 L189（`RetryState.NextAllowedAt`）

- `CoroutineState<'Tag,'Data>`は「強制しない」と本文が言っており、使う箇所が無い。規約は文章で足りる（ADR-008の最小主義）。削除するか「説明用の擬似型」と明記。
- `Response<'TimerId,'Action>`は`'Action`が何か（Effect？内部遷移？）、複数の部分状態機械の`Response`を`StepResult`へどう合成するかが無い。R4-1の修正（`Consumed`の廃止・原因ラベル）と合わせて具体化するか削除。
- `BeliefKey`はDTOタグ対象（9.2 L1048）かつ`TimerId`の一部なのに定義が無い。ケース一覧を書く（`WindowBeliefs`のフィールドと1対1と言うなら`App`の4サブフィールドをどう表すかも）。
- `RetryState.NextAllowedAt`とPendingの`Deadline`、`BeliefReprobe`のDeadlineが同じ「次の時刻」を二重に持つ。どれが正本か（3.3は「待ちのDeadline」）を明記し、`NextAllowedAt`は削除してよい。

---

## R4-23【低】gateの適用範囲が`Reprobe`/`ObserveWindow`について未定義。Hungの扱いがNotMovableと非対称

**場所**：6.2 L757-771、7.1 L795-797、4.5 L459（Excludedの窓も信念を持ち続ける）

**問題**：`TargetExcluded`/`TargetNotAlive`がどのEffect種別に適用されるか書かれていない。`Reprobe`/`ObserveWindow`まで拒否するとExcluded・Suspendedの窓の再probe（`TargetIntegrity`の再試行、Hungの再確認）が全て止まり、「属性が変われば再分類するために信念を持ち続ける」が死文化する。また`GateInput`は`NotMovable`を持つが`Hung`を持たず、Hung窓への`SetRect`の最終防衛線が無い（Reconcilerの規則だけ）。

**修正案**：Effect種別×拒否理由の適用表を6.2に置く（観測系Effectは`TargetNotAlive`のみ）。`Hung`も`GateInput`に入れるか、入れない理由（Shellの事前確認が同じ役割を果たす）を書く。

---

## R4-24【低】`Confidence`/`EvidenceRef`の細部が決定性に関わるのに未定義

**場所**：4.5 L404-420, L431

- 根拠リストを「新しい順に最大8件」に切り詰めるが、`Sample of sequence`・`Outcome of RequestId`・`ProbeEvidence of EvidenceId`は番号空間が別で、「新しい順」の全順序が無い。和集合（L431）の順序も同様。Replay一致（#10）のためには`EvidenceRef`に入力の`Sequence`を必ず持たせて並べる等の規定が要る。
- `All`/`AnyOf`の結果の`Belief<bool>`の`LastRevisedAt`の決め方が無い。
- `Responsiveness`/`DpiBehavior`が`Unformed`のときReconcilerがどう扱うか（Responsive扱い？2段目を待たない？）が無い。

---

## R4-25【低】その他の不整合・軽微な点

- 7.2 L886-900の状態遷移図に、v2.6で追加した`Movability`のユーザー操作によるリセット後の再試行、Hung→Responsiveによる解除（Suspend側の遷移）が無い。図はPendingの遷移なので管理状態の遷移図を別に置くか、注記で参照する。
- Hungの再probe（4.5 L478）は上限30sで**無期限**に続く。ゴーストウィンドウや恒久的にハングした窓に対して30秒ごとに`TimerElapsed`＋`WindowObserved`がJournalに積まれる。上限回数の後は「ユーザー操作か`ForegroundObserved`で再開」等の打ち切りを書く。
- 9.2 L1044-1049の形状変更規則はDUタグだけを扱い、Checkpoint DTOのレコードのフィールド追加・削除（v2.6で`WorkspaceState.FocusHistory`削除、`Floating`追加等）を扱っていない。`"home-monitor"`をタグとして予約しているのは、レコードのフィールド名もタグ扱いするという意味か不明確。
- 関連資料（L13）が`review-belief-layer.md`を参照しているが、ファイルが存在しない（本レビューの格納先として作る予定なら問題無い）。
- ADR-016（L32）は`WmState.Focused`を信念の例に挙げるが、R4-4/R4-9の修正後は導出値になるので表現を合わせる。

---

## 前回指摘（R3-1〜R3-13）の解消状況の検証

| 指摘 | 判定 | 根拠 |
|---|---|---|
| R3-1 | 概ね解消 | 2フィールド分離と更新規則は正しい。ただしアプリ起因のWS表示が`ReturnMonitor`を消すか未定義で#18と衝突し得る（R4-16）。`MonitorId`のセッション間永続性が無く`WorkspaceAssignments`が機能しない（R4-17） |
| R3-2 | 概ね解消 | (S1)〜(S5)、option化、tryFind、view意味論は妥当。`ActiveMonitor`付け替え先・存在しないWSへの切替が未定義（R4-16）、settle中の古い`Monitors`（R4-15） |
| R3-3 | **不十分** | 単一情報源にしたが、新規窓で`Focused`が`Elsewhere`に固着（R4-4）、Excluded化時の`Focused`更新漏れで(F2)違反、別モニター・hidden WSでのコマンドの意味が未定義（R4-18） |
| R3-4 | **不十分** | 「`MoveSizeEnded`の直後」が未定義で、R3-4の失敗シナリオ（ドラッグした窓が引き戻される）がそのまま残る（R4-7）。OS起因の場合の「観測は信念に反映」は`Consumed`規約で実現不可能（R4-1） |
| R3-5 | **不十分** | 閾値は物理pxに固定したが、比較相手の`MinWidth`の信念がDPI依存で蓄積されるので、分類が移動経路に依存する問題が形を変えて残る（R4-12） |
| R3-6 | 概ね解消 | 部分観測とフィールド規則は妥当。イベント型入力との新旧比較が欠落（R4-10）、Stickyの誤った初期値の固着（R4-13） |
| R3-7 | 解消（副作用あり） | 分類が`Movability`だけを読む形で未検証API依存は断たれた。副作用として`NotMovable`の既定挙動が7.2と5.5で矛盾し、既定設定でタイル配置に穴が残る（R4-11） |
| R3-8 | 解消 | 21章の比較軸として適切に追加。ただし非表示方式非依存のEffectが無い問題（R4-6）は別途 |
| R3-9 | 概ね解消 | タグ規則・予約は妥当。レコード形状の扱いが無い（R4-25） |
| R3-10 | 部分的 | 順序付きフォールバックは追加されたが、衝突の有無で切り替えるためセッション中にIDが変わる（R4-17） |
| R3-11 | 解消 | Phase Aゲートと19章に反映済み |
| R3-12 | 部分的 | タイマー化は正しいが、「1回だけ」の記録先・分類待ちの状態が`WmState`に無い（R4-2）。不可視窓への適用（R4-19） |
| R3-13 | 解消 | |

---

## 収束判定

**このままでは確定すべきでない。** ADR-016/017の骨格（3層の考え方、待ちのデータ表現、Deadlineによる古い発火の判定、`TimerCommand`の形）は妥当で維持してよいが、以下は「Phase Aで最初に固定する中核状態」（ADR-011）の型と1 stepの意味論そのものに関わり、実装すれば初日に表面化する。

### 確定前に必須の修正
1. **R4-1**：1 step内の処理順序を規範として書き、信念修正を`Consumed`の対象から外す（`Consumed: bool`は原因ラベルに置き換えるのが望ましい）。
2. **R4-2**：`WmState`に管理状態（Suspend理由の集合を含む）・Pending・分類待ち・再probe・画面フェーズを置くフィールドを定義し、#21の対象を列挙する。
3. **R4-3**：稼働中の再起動でCheckpointから復元するかを決める（推奨：しない）。残すなら`SessionResumed`入力・タイマー再発行・WindowIdの再照合を定義。
4. **R4-4**：`Focused`を生のフォアグラウンド信念からの導出値にし、管理状態の変化でも再導出する。
5. **R4-5／R4-6**：WS切替中の待ちを定義し、方式非依存の`Presence`次元を`Desired`/`Effect`に入れる。
6. **R4-7**：ドロップ後の観測の待ち（または`MoveSizeEnded`に矩形を載せる）を定義する。
7. **R4-8**：`Title`の信念とJournal/Checkpointの扱いを整合させる。
8. **R4-11**：`NotMovable`の既定挙動（7.2と5.5の矛盾）を1つに決める。

R4-9（表現の訂正）、R4-10、R4-12〜R4-20は、Phase AでBeliefs.fs/WmState.fsを書く前に1〜数行ずつ決めれば足りる。R4-21〜R4-25は記述修正。

## プロジェクトオーナーの判断が必要な論点（どちらも成り立つトレードオフ）

1. **稼働中の再起動でのCheckpoint復元**（R4-3）：(A) 復元しない（全体再観測＋所属ヒントだけ永続化。単純、最小主義）／(B) 復元する（WS配置・信念を引き継げるが、`SessionResumed`・タイマー再発行・WindowId再照合の仕組みが必要）。
2. **hiddenなWSの窓が自分でフォアグラウンドになったときWSを切り替えるか**（R4-5、R4-16）：(A) 切り替える（v2.6の既定。OSが見せている窓と一致するが、通知・リマインダで作業中の画面が勝手に変わる）／(B) 切り替えず、その窓のWSを示す通知だけ出す（GlazeWM寄り。画面は安定するが、見えない窓にキー入力が行き得る）。
3. **`NotMovable`の見直し**（R4-11）：(A) ユーザー操作のみ（v2.6）／(B) `ProcessKey`の変化または長いバックオフで自動再試行（推定の固着を避けるが、昇格窓に対して定期的に無駄な拒否を受ける）。
4. **ユーザーの明示的な管理指定とPolicyの優先順位**（R4-20）：ユーザー指定を窓の寿命中Policyより優先するか、Policyの再評価で上書きされてよいとするか。
5. **`MinWidthAtLeast`を分類述語に残すか**（R4-12）：残すなら`MinWidth`の信念をDIP正規化＋`DpiBehavior`依存で持つ（複雑）／外してLayoutのソフト制約専用にする（単純、ただし「最小幅の大きいアプリはFloating」という設定は書けなくなる）。
6. **AppIdentityのSticky**（R4-13）：最初の妥当な値で固定（妥当性フィルタ付き）／複数回一致で確定（揺れに強いが確定が遅れ、Unknown期間が延びる）。
7. **Unknown期限の既定分類**（R4-19）：Floating（v2.5〜）のままにするか、管理しない（Discoveredのまま）にするか。後者は誤ってタイル・巡回に入る事故が無いが、遅いprobeの窓はユーザーの操作待ちになる。

---

# Opus批判的レビュー：architecture.md v2.6（R4反映後）第2ラウンド

対象：`docs/architecture.md`（1677行）。行番号は現行版。前回の指摘は opus-review-belief-layer-round1.md（R4-1〜R4-25）。

総評：R4の大半は意図どおり、あるいはそれ以上に丁寧に解消された。特に、1 stepの処理順序の規範化（7.1 L941-951）、`WmState`への待ち・管理状態の配置（4.4 L330-378）、Checkpointの用途をReplay専用にしたこと（4.5 L549）、`focused`を導出値にしたこと（L385-388）、`TitleMatchVector`は筋が良い。ただし修正によって新しい問題が3つ生じた。いずれも中核状態の意味論に関わるので、確定前に直す必要がある。
- `ActiveMonitor`の追従規則を毎step適用するため、`FocusMonitor`と`SwitchWorkspace`（別モニター）が同じstepで打ち消される（R5-1）。
- WM自身の非表示が、候補条件（可視・非Cloak）と`SetPresence`の照合の両方で自分自身と衝突する（R5-2）。
- 動かせない窓を組込み規則でWSに入れたことで、hidden化できない窓に`SetPresence`が無限に出る（R5-3）。

---

## (1) R4指摘の解消判定

| 指摘 | 判定 | 根拠／残る点 |
|---|---|---|
| R4-1 Consumedと順序 | 解消 | 7.1の段①〜⑧、`MoveCause`、#22。7.3 L1021に「`ScreenGrace`タイマーと`Consumed`として具体化」という古い記述が残る（R5-10） |
| R4-2 待ち・管理状態の置き場 | 解消 | `WindowCoreState`/`Pending`/`Reprobes`/`Screen`/`SwitchSettle`。#21の対象として列挙済み |
| R4-3 稼働中の復元 | 解消 | Replay専用＋所属ヒント。ヒントの到着順に小さな穴がある（R5-8） |
| R4-4 Focusedの固着 | 解消。ただし副作用あり | 導出値にしたのは正しい。ただし`focused`がhidden WSの窓を指し得る（R5-4）。追従規則が回帰を起こした（R5-1） |
| R4-5 WS切替の揺れ | 解消 | `SwitchSettle`。期限後に`RequestForeground`を1回だけ再発行するのも妥当 |
| R4-6 Presence次元 | **不十分** | Coreの側の型（`DesiredPresence`/`SetPresence`/(S6)/gate）は入ったが、観測側で「WMが隠した状態」を判定できず、候補条件とも衝突する（R5-2） |
| R4-7 ドロップ観測 | 解消 | `MoveSizeEnded`に矩形とモニターを載せる案。Grace中のドラッグの原因ラベルの優先順位だけ未定（R5-9） |
| R4-8 Title | 概ね解消 | Policy切替前後でパターン集合のハッシュが食い違う観測の扱いが`revise`/評価器に無い（R5-6） |
| R4-9 3層の表現 | 解消 | L453-455、L546の訂正は正確 |
| R4-10 イベント入力 | 解消 | `SampleSequence`の契約、`AttributeChanged`、`ForegroundObserved of Observation` |
| R4-11 NotMovable | 解消。ただし副作用あり | 5.5/7.2/5.3の矛盾は解消、組込みFloatingも妥当。ただしFloatingとしてWSに入れたことでR5-3が生じた |
| R4-12 MinWidth DIP | 解消 | 両辺DIP。PMv2アプリの2段リサイズの途中で観測した値を誤って正規化し得る点はPhase Aで十分（R5-11） |
| R4-13 Sticky | 解消 | 妥当性フィルタ、矛盾時は`Inferred`に下げる |
| R4-14 ForgetWindow | 解消 | `ForgetWindow`が処理される前に旧WindowIdで届く観測を捨てる規則は1行あるとよい（R5-11） |
| R4-15 Settling | **不十分** | 段⑦のEffectは抑止されたが、既存Pendingの再送はsettle中も出る。また`ScreenReconfigurationInProgress true`の意味が7.3の中で矛盾している（R5-5） |
| R4-16 reconcile分岐 | 解消 | |
| R4-17 MonitorId永続 | 解消 | 機種単位で固定、`MonitorSelector`、`StableKey` |
| R4-18 フォーカスコマンド | 回帰あり | コマンドの意味の補完は良いが、追従規則がR5-1を生んだ |
| R4-19 不可視窓・所属 | 解消。ただし副作用あり | 候補条件で限定したのは正しい。ただし所属を「最初のUnknown時点のWS」にしたため、ユーザーが使っている窓が隠れる経路ができた（R5-4） |
| R4-20 UserOverride | 解消 | ユーザー指定を解除する手段が無い（R5-11） |
| R4-21〜R4-25 | 解消 | gateの適用表、`BeliefKey`、`EvidenceRef`の順序、`Unformed`時の扱い、Hungの再probe上限、いずれも妥当 |

---

## (2) 修正で新たに生じた問題

### 確定前に必須

#### R5-1【高】`ActiveMonitor`の追従を毎stepの段⑥で適用するため、`FocusMonitor`と`SwitchWorkspace`（他モニター）が同じstepで打ち消される。空のモニターには永久に移れない

**場所**：4.4 L437（更新規則）、L430（view意味論）、7.1 L947（段⑥）、CoreCommand `FocusMonitor`

**問題**：追従規則は「各stepの最後に、`focused s = ManagedWindow w`で、wのWSが別モニター`n`に表示されていれば`ActiveMonitor := n`」で、level-triggered（状態を見て毎回適用）になっている。`FocusMonitor n`は段⑤で`ActiveMonitor := n`にして`RequestForeground`を出す。しかしその`ForegroundObserved`が届くまで、`Foreground`の信念は元のモニターAの窓のままなので、同じstepの段⑥で`ActiveMonitor`はAへ戻される。

**失敗シナリオ**：
- (i) モニターBのWSが空のとき、`FocusMonitor B`を押しても、フォーカスを渡す窓が無いので`RequestForeground`は出ず、段⑥で即座にAへ戻る。Bを`ActiveMonitor`にする方法が無いので、キーボードだけでは空のBに`SwitchWorkspace`できない。
- (ii) Bに窓がある場合でも、`FocusMonitor B`の直後（`ForegroundObserved`の前）に押した`SwitchWorkspace 3`はAを切り替える。
- (iii) `SwitchWorkspace w`でwが既にBに表示中の場合も、view意味論の「`ActiveMonitor`を`n`へ移すだけ」が同じstepで打ち消される。
- (iv) `RequestForeground`が`ForegroundDenied`になれば、恒久的に戻る。

**修正案**：追従をedge-triggeredにする：「段①で`Foreground`の信念の**値が変わった**step（`ForegroundObserved`由来）に限り、新しい値の窓のWSが別モニターに表示されていれば`ActiveMonitor`をそこへ移す」。#16の入力列に「`FocusMonitor`で空のモニターへ移り、続けて`SwitchWorkspace`」を加える。

#### R5-2【高】WM自身の非表示が自分の観測と衝突する：(a) 非表示にした窓が候補条件（可視・非Cloak）を満たさなくなる、(b) `SetPresence`の`Satisfied`を判定する観測が無い

**場所**：4.4 L334（`NotCandidate`の候補条件）、5.4 L670、4.4 L347-349（`DesiredPresence`）、7.1 L881（`SetPresence`）、7.2 L984（`PendingRequest`は`TargetRect`で照合）、4.2 `WindowSnapshot`、21章 Cloak行の比較軸(a)

**問題**：
- (a) 候補条件は「可視、非ツール窓、非Cloak等」である。どの非表示方式を選んでも、WMが隠した窓は条件を外れる：`SW_HIDE`なら`IsVisible = false`、Cloakなら`CloakState.Shell = true`、最小化方式でも候補条件に最小化を含めれば外れる。`Classification`が候補条件をstepごとに評価し直すのか、発見時に一度だけ評価するのかが書かれていない。評価し直すなら、hidden WSの窓は全て`NotCandidate`へ落ちる。その結果、`Classified`が失われ、(S5)/(F1)から外れ、Excluded化と同様に`WindowWorkspace`から除去され、WSに戻っても窓が戻らない。R3-8で指摘した問題が、Cloakに限らず全方式に一般化した形である。
- (b) `SetPresence`は`RequestId`を持つのでPendingになる。しかし`PendingRequest`の照合は`TargetRect`対`VisibleFrameRect`で、「隠れた／現れた」を判定する観測が無い。Coreは方式を知らない（L409）ので、`IsVisible`/`IsMinimized`/`Cloak`/画面外矩形のどれを見ればよいかも分からない。結果として`SetPresence`のPendingは必ず`Timeout`→Retryになる。

**失敗シナリオ**：`SwitchWorkspace 2`→WS1の窓をSW_HIDE→`VisibilityChanged(false)`→段⑤で候補条件を外れて`NotCandidate`→`WindowWorkspace`から除去→`SwitchWorkspace 1`で何も表示されない。窓は隠れたまま行方不明になる（4.4 L409が要件として防ぐと言っているリスクそのもの）。

**修正案**：
- 候補条件は**発見時（と`NotCandidate`の間）だけ**評価し、一度`Classified`になった窓は候補条件では`NotCandidate`へ戻らない（戻すのは消滅とExcludedへの再分類だけ）、と4.4/5.4に明記する。分類の述語・正規化も、`Desired = HiddenByWm`の窓の`IsVisible`/`IsMinimized`/`Cloak`の変化を外部要因と解釈しない。
- Shellに方式非依存の観測`Presence: Observation<ObservedPresence>`（`VisibleToUser | HiddenByWmMethod | HiddenOther`。Shellは自分の方式を知っているので判定できる）を`WindowSnapshot`に追加させ、`SetPresence`のPendingはこれと照合する。21章のCloak行の比較軸(a)は、この`HiddenByWmMethod`を判定できるかどうか、という具体的な条件になる。

#### R5-3【中〜高】動かせない窓を組込み規則でFloatingとしてWSに入れたため、hidden化できない窓に`SetPresence`が無限に出る

**場所**：4.4 L406-407、6.2 L858（`TargetNotMovable`は`SetPresence`に適用しない）、(S6) L426、5.3 L633-634

**問題**：`NotMovable`の典型は昇格窓で、UIPIにより`SetWindowPos`が拒否される（F23）。画面外移動方式では隠せない。`ShowWindowAsync(SW_HIDE/SW_MINIMIZE)`も、上位整合性の窓へのメッセージ送信系なので拒否または無視される可能性が高い（未検証。S7r系スパイクで確認が必要）。v2.6はこの窓をFloatingとしてWSに入れるので、そのWSがhiddenになると`Desired = HiddenByWm`（(S6)）になる。しかし実際には隠せない。`SetPresence`は`Denied`→`Cancelled`になり、次のstepのReconcilerは再び差分を見て`SetPresence`を出す。`SetPresence`の再試行上限・Suspendの経路が定義されていないので、#7（要求の無制限増殖）に違反する。

**失敗シナリオ**：管理者のPowerShell（WS1、組込み規則でFloating）→`SwitchWorkspace 2`→PowerShellはWS2の上に残り、WMは入力が来るたびにhideを出し続け、Journalに拒否が積み上がる。

**修正案**：二択をオーナーが選ぶ（下記）。推奨は「`NotMovable`の窓はWSに所属させない（全WSに共通の“sticky”窓として扱い、`Desired`も`SetPresence`も持たない）」。5.3のサンプルが`Excluded`にしている理由も、それで説明できる。そうしない場合でも、`SetPresence`の失敗は`Movability`ではなく窓ごとの`Suspensions`（例：`CannotHide`）に入れて再試行を止め、(S6)の例外として明記する。

#### R5-4【中〜高】`focused`がhidden WSの窓を指し得るので、`CloseFocused`/`PromoteToMaster`が見えない窓に作用する。分類時の所属規則が、使用中の窓を隠す

**場所**：4.4 L385-388（`focused`）、L432（SwitchSettle中は`Foreground`だけ更新）、L439（コマンドの意味）、5.4 L670（所属は最初のUnknown時点のWS）、21章（hidden WSの自己アクティブ化、オーナー判断）

**問題**：`focused`は「`Foreground`が`WindowWorkspace`に含まれる窓なら`ManagedWindow`」とするだけで、そのWSが表示中かどうかを見ない。次の3つの経路で、フォアグラウンドがhidden WSの窓に残る：
- `SwitchSettle`中（OSが旧WSの窓へ一時的にフォアグラウンドを移したとき）。
- `RequestForeground`が`ForegroundDenied`になったとき。
- 21章で(B)「自己アクティブ化でWSを切り替えない」を選んだとき。

この状態で`CloseFocused`を押すと見えない窓を閉じ、`PromoteToMaster`/`SwapWithMaster`は見えないWSの`Order`を並べ替える。

R4-19で入れた「所属は最初のUnknown時点のアクティブWS」の副作用もある。新しい窓が表示され（フォアグラウンドになり、ユーザーが入力し始め）、その間にユーザーがWSを切り替えていると、分類確定時にその窓はhidden WSへ入り、`SetPresence(hide)`で消える。フォアグラウンドのまま隠れるので、キー入力が見えない窓へ流れる。

**失敗シナリオ**：WS1でエディタEがフォアグラウンド→`SwitchWorkspace 2`→OSが一時的にWS1の別窓をアクティブ化（SwitchSettle中）→ユーザーが反射的に`CloseFocused`→見えないWS1の窓が閉じる。

**修正案**：`focused`を「`WindowWorkspace[w]`が可視WS（`range(Screens)`）に属する場合だけ`ManagedWindow w`」にする。hidden WSの窓がフォアグラウンドの間は`Elsewhere`扱いになり、コマンドは無害になる。分類確定時、その窓が現在フォアグラウンドなら、所属はアクティブWSにする（R4-19の「出現時のWS」規則より優先）。#16に両方を入れる。

#### R5-5【中】Settling中の抑止に穴がある：既存Pendingの再送は抑止されない。また`ScreenReconfigurationInProgress true`の意味が7.3の中で矛盾している

**場所**：7.1 L948（段⑦だけを抑止）、L944（段③で期限切れを処理）、7.2 L996（Supersededは確定時）、7.3 L1017とL1019、20章 L1610

**問題**：
1. 抑止対象は段⑦のEffectだけである。settle開始前に発行済みのPendingが段③で`Timeout`/`Transient`になって再送される場合、その`SetRect`は段⑦を通らない。7.2の注記「settle中は新しいPendingも生まれない」は、再送には成り立たない。`GateInput.Monitors`は確定済み（古い）トポロジなので、`OutsideAnyMonitor`も通過する。R4-15の失敗シナリオ（消えたモニターの座標へ窓を送る）が再送経路に残っている。
2. L1017（v2.1）は「`ScreenReconfigurationInProgress`がtrueの**間は**新規Effectを抑止」と言う。L1019（v2.6）は「trueはdebounceを再始動するだけ、falseは特別扱いしない、hardDeadline 30秒で強制確定」と言う。セッションロックやRDP切断が数十分続く場合、v2.6ではsettleMs後（遅くともhardDeadline後）にEffectが再開し、RDP切断中の仮想ディスプレイ（解像度の低い1画面）に全窓を詰め直してしまう。再接続時にもう一度詰め直しになる。意味の変更が意図的かどうかが分からない。

**修正案**：(1) 抑止を段⑦ではなくgate（段⑧）に置く。`Screen = Settling`の間は`SetRect`/`RestoreBeforeMove`/`SetPresence`をgateが保留する（Pendingは`Superseded`にする）。こうすれば、どの段で生まれたEffectでも漏れない。(2) `InProgress true`→`false`の区間は「明示的な抑止区間」として`Settling`とは別の状態（例：`Suspended of since`。期限は長め、またはfalseかトポロジ変化で解除）にするか、v2.6の簡略化を意図的に採るならL1017と20章の記述を書き換える。どちらを採るかはオーナー判断（下記）。

#### R5-6【中】`TitleMatchVector`のパターン集合のハッシュの照合が、`revise`にも評価器にも定義されていない

**場所**：4.2 L289-292、5.3 L656（「Policyを切り替えたら`Unformed`に戻る」）、4.5 L523-528（`BeliefEvidence`に`PolicyActivated`が無い）、L535（LatestWins）

**問題**：「Policyを切り替えたら`TitleMatches`の信念を`Unformed`に戻す」とあるが、`BeliefEvidence`にその契機が無い（`revise`はPolicyを知らない）。また、Policy切替の前にShellが旧パターン集合で評価した観測が切替の後に届くと、`SampleSequence`が新しいのでLatestWinsで採用される。その結果、旧集合の添字を新集合の添字として読み、別のルールが当たる。

**修正案**：`revise`ではなく**述語の評価器**で照合する：`TitleContains`の評価時に、信念の`PatternSetHash`が採用中Policyの集合ハッシュと異なれば`Unknown [MissingTitleMatch]`を返す。これなら`revise`はPolicyを知らないまま保て、Counterfactualの扱い（L291）とも一致する。「Unformedに戻す」という記述は削除する。

---

### Phase Aで決めれば足りる

#### R5-7【低〜中】`SwitchSettle`中・Settling中に出る`RequestForeground`の扱い

L948で段⑦の`SetPresence`は抑止されるが、`SwitchWorkspace`由来の`RequestForeground`は抑止されない。そのため、まだ表示されていない（`SetPresence(show)`が保留中の）窓をフォアグラウンドにし得る。同じバッチ内の順序（`SetPresence`→`RequestForeground`）と、Settling中は`RequestForeground`も保留することを明記する。`GateInput`（特に`HiddenByWm`）は段⑦の後の`Next`状態から作る、と書いておくとFocusMostRecentの同一step表示が安全に通る。

#### R5-8【低〜中】`MembershipHintsLoaded`の到着順

L549は、Shellが最初の全体列挙の後にヒントを送るとしている。しかし同じ窓の`WindowObserved`がヒントより先に届いて分類まで進むと、ヒントは適用されない（「分類されたときにヒントのWSへ入れる」）。Shellの契約として「最初の列挙の`WindowObserved`より前に`MembershipHintsLoaded`を投入する」か、Coreが分類済みの窓にも後からヒントを適用する（所属の移動）かを決める。

#### R5-9【低】Grace中のユーザードラッグの原因ラベル

`OsInduced`は「Settling/Grace中の移動・最小化」、`UserDrag`は「`MoveSizeEnded`による観測」で、両方に当たる場合の優先順位が無い。`MoveSize`系はユーザー操作でしか出ないので、`UserDrag`を優先すると明記する。

#### R5-10【低】記述の残骸

- 7.3 L1021「v2.6で`ScreenGrace`タイマーと`Consumed`として具体化」→`MoveCause`に直す。
- (S6)のモニター0台の例外（L428）を、#17の性質の文言に入れる（「`Monitors`が空でない間は(S6)」）。
- 4.3の状態一覧（Discovered/Managed/…/Suspended）と、`ClassificationState`/`Suspensions`の対応表が無い。読者が2つの語彙を突き合わせる必要がある。

#### R5-11【低】小さな未定義点

- `UserOverride`を解除して「Policyに従う」に戻すコマンドが無い（`SetManagement`は上書きしかできない）。ヒントで永続化されるので、一度付けると窓の寿命中外せない。
- `ForgetWindow`が処理されるまでの間、Shellが旧WindowIdで送ってくる観測は、Coreが`Windows`に無いIdとして捨てる、と1行書く。
- `MinWidth`のDIP正規化で、PMv2アプリの1段目（OSの拡大）と2段目（自己リサイズ）の間に観測した`Mismatch`は誤った値を生む。`DpiBehavior = RescalesItself`の窓では、DPI変化後の2段目を待つ間の`Mismatch`を証拠にしない、とする。
- Floating窓の`Shown None`：画面外移動方式で隠したFloating窓を再表示するときの位置はShell側の記憶に依存する。Shellが落ちると失われるので、8.4の「画面内への移動」で救えることを確認する。

---

## (3) 収束判定

**まだ確定すべきでない。ただし収束は近い。** R4の構造的な指摘（順序、状態の置き場、Checkpoint、Focused、Title）はいずれも正しく解消されており、ADR-016/017の骨格はこのまま確定してよい水準にある。残る必須修正は、どれも1〜数段落の追記で済む：

1. **R5-1**：`ActiveMonitor`の追従をedge-triggered（`Foreground`の値が変わったstepだけ）にする。
2. **R5-2**：一度分類した窓は候補条件で`NotCandidate`へ戻らないと明記する。方式非依存の`ObservedPresence`観測を追加し、`SetPresence`のPendingの照合先にする。
3. **R5-3**：`NotMovable`の窓のWS所属（非所属＝全WS共通にするか、hide失敗用のSuspend理由を足すか）を決める。
4. **R5-4**：`focused`を可視WSの窓に限る。分類時、フォアグラウンドの窓はアクティブWSへ入れる。
5. **R5-5**：Settling中の抑止をgateへ移す。`ScreenReconfigurationInProgress`の意味を1つに決める。
6. **R5-6**：`TitleMatchVector`のハッシュ照合を評価器に置く。

これらが入れば、第3ラウンドは確認だけで終わる見込みである。

## プロジェクトオーナーの判断が必要な論点（今回新たに生じたもの）

1. **動かせない（昇格）窓をWorkspaceに所属させるか**（R5-3）：(A) 所属させない（全WS共通の窓。隠せない現実と一致し、hideの失敗ループが原理的に無い。ただし「WS1の管理者ターミナル」という概念は持てない）／(B) 所属させる（v2.6。WSごとに整理できるが、hidden WSでも見え続け、hide失敗を個別のSuspendで止める必要がある）。
2. **ロック・RDP切断などの長い再構成区間の扱い**（R5-5）：(A) `InProgress true`〜`false`の間は抑止し続ける（v2.1の意味。RDP切断中の詰め直しが無い。ただしfalseを取りこぼすと長い上限まで止まる）／(B) v2.6の簡略化（debounce＋30秒の強制確定。取りこぼしに強いが、長い区間の途中で一度詰め直す）。
3. **分類待ち中にユーザーがWSを切り替えた場合の所属**（R5-4）：「出現時のWS」（R4-19）と「分類時にフォアグラウンドならアクティブWS」のどちらを優先するか。本レビューは後者の優先を推奨するが、「窓が出現したWSに整理したい」というワークフローもあり得る。

---

# Opus批判的レビュー：architecture.md v2.6（R5反映＋ドメイン分割）第3ラウンド

対象：`docs/architecture.md`（1782行）。行番号は現行版。オーナー指示による構造変更（信念のドメイン別化、ADR-018の6ドメイン分割、TimerIdの入れ子化、3.4、`combineAll`/`combineAnyOf`、`Responding`への改名）は前提として受け入れ、是非は問わない。変更後の設計が正しく一貫しているかだけを見た。

総評：ドメイン分割そのものは筋が良い。`Displays`（xmonadの`StackSet`と同形）・`WorkspaceSet`（private）・`Zipper`によって、R3-2/R3-3系の不変条件の多くが型に移った。信念をドメインごとに分けた表（4.5 L574-584）も明快で、3.4の用語の整理も正確である。

しかし、7.1の段⑤⑥の中でのドメインの処理順序と、ドメイン間のデータの流れ（DomainEvent/Ctx）に、後ろ向きの依存（先に処理したドメインの状態を、後から別のドメインが書き換える）と循環が残っている。これはADR-018の「各ドメインが自分の状態を持ち、固定順に合成する」という前提そのものを崩す。またR5-1の修正（edge-triggered）が、R4-4で直した「新しい窓のフォーカス」を別の形で壊している。旧`WmState`前提の記述も十数か所残っている。

---

## (1) R5の解消判定

| 指摘 | 判定 | 根拠／残る点 |
|---|---|---|
| R5-1 追従のlevel-trigger | 解消。ただし回帰あり | edge-triggeredにしたこと自体は正しい（4.4 L532、7.1段⑥）。ただし「新しい窓が分類された時点で既にフォアグラウンドだった」場合に、zipper/履歴が二度と更新されなくなった（R6-3） |
| R5-2 自分の非表示との衝突 | 解消 | 候補条件は`NotCandidate`の間だけ評価（L511）、`ObservedPresence`、`SetPresence`の照合先（7.2 L1096）。ただし`HiddenOther`の扱いが無い（R6-4） |
| R5-3 動かせない窓 | 解消 | `Unplaced`（L407, L508）。5.4 L763と7.1/7.2の注釈に「Floating」が残る（R6-5） |
| R5-4 hidden WSの`focused` | 解消 | `focused`は表示中のWSに限定（L468-474）、分類時にフォアグラウンドならアクティブWS（L761） |
| R5-5 Settling中の抑止 | 解消 | gateで保留（段⑧）、`Reconfiguring`と3つの解除条件。7.2 L1092に旧記述が残る（R6-5）。保留の副作用はR6-8 |
| R5-6 Titleのハッシュ | 解消 | 評価器で照合、`MissingTitleMatch`（L747、L1356） |
| R5-7 SetPresenceとRequestForegroundの順序 | 解消 | 段⑦の並び順、段⑧で`GateInput`を次の状態から作る |
| R5-8 ヒントの到着順 | 解消 | Shellの契約（L640） |
| R5-9 Grace中のドラッグ | 解消 | `UserDrag`を優先（L536） |
| R5-10 記述の残骸 | **未解消** | 7.3 L1122「`ScreenGrace`タイマーと`Consumed`として具体化」がそのまま残る（R6-5） |
| R5-11 小さな未定義点 | 解消 | `ClearUserOverride`、旧IDの観測の破棄、`RescalesItself`の窓の2段目待ち中の`Mismatch`を証拠にしない |

---

## (2) 構造変更で新たに生じた不整合・穴

### 確定前に必須

#### R6-1【高】段⑤⑥の中のドメインの処理順序に、後ろ向きの書き込みと循環がある。ADR-018の「各ドメインは自分の状態だけを更新し、固定順に合成する」が成り立っていない

**場所**：7.1 段⑤⑥（L1040-1041）、4.4 L499（合成の契約）、L518（`ReturnMonitor`はWorkspacesが持ち、TopologyはDomainEventで依頼）、L502（X1）、L524（未知のWSは「Workspacesが作ってからTopologyが表示」）、L532（edge-triggerの更新規則(a)(c)）、L534（`FocusMostRecent`→view）、L540（hidden WSのフォアグラウンド化→view）

段⑤の順序は「Windows→Workspaces→Topology」、段⑥がFocus、段⑦がPlacementである。ところが次の4つのデータの流れは、この順序と逆向きである。
1. **Topology→Workspaces（ReturnMonitor）**：`MonitorLifecycle.reconcile`（Topology、段⑤の後ろ）は、(2)の規則で「`ReturnMonitor = Some m`のhiddenなWS」を探すためにWorkspacesの状態を読む。さらに`ReturnMonitor`の記録・解除をDomainEventでWorkspacesへ依頼する。しかしWorkspacesは同じ段⑤で既に処理済みなので、依頼の受け手がいない。次のstepまで持ち越すと、その間の状態は(X1)/#18が前提とする「最初の所有者を保つ」を満たさない。カスケード切断が1 stepに2回の`reconcile`を含む場合に、記録が落ちる。
2. **Topology→Workspaces（Workspaceの作成）**：L524/(X1)は「Workspacesが空のWSを作ってからTopologyが表示」と言う。`reconcile`の(2)の最後の分岐「未使用の最小IDで空のWSを作って表示」は、Topologyの中で決まる。これを先に処理済みのWorkspacesにどう作らせるのかが無い。
3. **Focus→Workspaces/Topology**：段⑥のFocusが、(a) Workspacesのzipperのフォーカスと、(c) Topologyの`current`を書き換える（L532）。これらは他ドメインが所有する状態であり、しかも両ドメインとも段⑤で処理済みである。L499の`update`の型（各ドメインは自分の`'State`だけを返す）では書けない。
4. **Focus→Topology（view）**：`FocusMostRecent`の行き先がhidden WSならview（L534）、hidden WSの窓のフォアグラウンド化でview（L540）。どちらもFocusの状態（`History`、`SwitchSettle`）を読んでから、Topologyを変える必要がある。しかしTopologyはFocusより前に処理済みである。

**失敗シナリオ**：WS3をBに表示中→B切断（settle超過）→Topologyが`reconcile`でWS3をhiddenにし、`ReturnMonitor := B`をDomainEventで依頼。Workspacesは処理済みなので反映されない（実装者が「次のstepで処理」とすれば、その次の入力が`MonitorTopologyObserved`（B再接続）の場合、`reconcile`の(2)は`ReturnMonitor`がまだ`None`のWS3を見つけられず、別のWSをBに出す）。#18が落ちる。

**修正案**：依存が一方向になるように所有を組み替え、段⑤⑥の内側の順序を表で明記する。
- `ReturnMonitor`はTopologyの状態（`Map<WorkspaceId, MonitorId>`）に移す。復帰先は「どのモニターにどのWSを戻すか」というTopologyの関心事で、Workspacesが読む必要は無い。これで1.が消える。
- `WorkspaceSet`を全域にする（未知の`WorkspaceId`は空のWSとして振る舞い、窓が入った時点で実体を作る）。そうすれば(X1)は作成の依頼なしに常に成り立ち、2.が消える。
- フォアグラウンドの変化は、段②でFocusが`ForegroundChanged w`（と、hidden WSの窓かどうか、`SwitchSettle`中かどうか）をDomainEventとして出す。段⑤でTopology（`current`の追従、hidden WSのview）とWorkspaces（zipperのフォーカス）がそれを受ける。Focusの段⑥は自分の`History`だけを更新する。`FocusMostRecent`は段⑤の前にFocusが「行き先`w`」を解決して`FocusTargetRequested w`を出し、Topology/Workspacesが受ける。これで3.と4.が消える。
- 段⑤の内側を「Windows→Topology→Workspaces」に固定し、各ドメインが読むCtx（前段の**今回の**状態）と受けるDomainEventを、7.1に1表で示す。15.1 #22に「どのドメインも他ドメインの状態を書かない」をアーキテクチャテスト（`update`の戻り値の型）として加える。

#### R6-2【中〜高】CoreCommandの振り分け先と、`RequestForeground`を発行するドメインが定義されていない。ユーザー操作による`Movability`の試し直しを行う段も無い

**場所**：7.1 段⑤（「TopologyがCoreCommandと…」とだけある）、4.4 L476-489、L534、4.5 L631（`SetManagement`/`ToggleFloating`で`Movability`を`Movable`へ戻す）、段④（照合結果由来の修正のみ）

**問題**：
- 13個のCoreCommandのうち、段⑤でTopologyが処理すると書かれているのはview系だけである。`FocusNext`/`PromoteToMaster`/`SwapWithMaster`（Workspacesのzipper）、`CloseFocused`（Effectは？）、`MoveFocusedToWorkspace`（Workspaces、のちに隣の窓への`RequestForeground`）、`ToggleFloating`/`SetManagement`/`ClearUserOverride`（Windows）、`ChangeLayout`（Placement？　Layoutの選択状態はどのドメインにも無い）について、どのドメインがどの段で受けるかが無い。
- `RequestForeground`は、zipperの回転・view・閉じた後の移動・`SwitchSettle`の再発行と多くの経路から生まれる。どのドメインがEffectとして出すのかが無い。段⑦（Placement）が`SetPresence`との順序を決めると書いているので、Placementが出すなら、各経路からPlacementへのDomainEventが要る。
- `ToggleFloating`による`Movability`のリセット（Placementの信念）は段①（観測）にも段④（照合結果）にも入らない。しかもWindowsの分類（段⑤）より前に行わないと、同じstepでは`NotMovable`→`Unplaced`のまま分類され、試し直しが1 step遅れる。
- `ChangeLayout of LayoutRef`の結果（WSごとの採用中Layout）を持つ状態が、どのドメインの型にも無い。

**修正案**：CoreCommandごとに「受けるドメイン／段／出すDomainEvent／最終的にEffectを出すドメイン」の表を7.1に置く（R6-1の表と同じ場所）。`RequestForeground`はPlacementが段⑦で一元的に出し、他のドメインは`FocusTargetRequested w`を出すだけ、とするのが単純である。`Movability`のリセットは段④に「`UserCommand`由来の証拠」として加える。WSごとのLayoutの選択（`ChangeLayout`の結果）をWorkspacesState（またはPlacement）のフィールドとして追加する。

#### R6-3【高】edge-triggeredにした更新規則は、R4-4（新しい窓の最初のキー操作が効かない）を形を変えて再発させる

**場所**：4.4 L532（更新規則）、L533 (F2)、7.1 段⑥、15.1 #16

**問題**：更新は「`Foreground`の信念の**値が変わった**stepに限り、新しい値`w`が管理下の窓なら」行う。しかし新しい窓は、フォアグラウンドになったstepではまだ未分類（管理下でない）なので、更新されない。数百ms後に分類されて管理下に入るstepでは、`Foreground`の値は変わらないので、やはり更新されない。結果として：
- `focused`は導出値なので`ManagedWindow w`になる（`CloseFocused`は効く）。
- しかし、zipperの`Focus`は前の窓のまま、`History`の先頭にもwは入らない。

同じことは、hidden WSが表示されて、もともとフォアグラウンドだったその窓が可視になった場合（`focused`が`Elsewhere`→`ManagedWindow`に変わるが`Foreground`は不変）にも起きる。(F2)は「値が変わったstepの後」という条件付きなので、この経路を検査しない。

**失敗シナリオ**：WS1（A, B、zipperの`Focus = A`）でターミナルCを新規起動→Cがフォアグラウンド（未分類）→分類されてWS1のStackの末尾へ→ユーザーがCに入力中に`FocusNext`→zipperはAから回ってBへ（Cの次ではない）。`FocusMostRecent`→`History`の2番目は、Cより前に使っていた窓ではなく、さらにその前の窓になる。`SwitchWorkspace 2`→`SwitchWorkspace 1`で戻ると、フォーカスはCではなくAに渡る。

**修正案**：更新の契機を2つに分ける。
- zipperのフォーカスと`History`は、**導出値`focused`の値が変わったstep**（step前後の`focused`を比較する。`Foreground`の変化、分類の確定、WSの表示、のどれが原因でもよい）で更新する。
- `current`の追従だけを`Foreground`の値の変化に限る（R5-1の回帰を防ぐのはこちらだけで十分。`FocusMonitor`や他モニターへの`SwitchWorkspace`は`focused`の値を変えないので、前者の規則でも打ち消されない）。

(F2)を「step終了時に`focused = ManagedWindow w`なら、wのWSのzipperの`Focus = w`かつ`head History = w`」という無条件の形に戻す。#16の入力列に「フォアグラウンドの未分類窓の分類確定」を再び明記する。

#### R6-4【中〜高】`ObservedPresence = HiddenOther`（ユーザー自身の最小化等）を、Reconcilerが`Desired = Shown`との差分として`SetPresence(shown)`で戻してしまう

**場所**：4.5 L591（`ObservedPresence`）、4.4 L432-434（`DesiredPresence`）、7.1 段⑦、7.2 L1096

**問題**：`Presence`の照合とReconcileが導入されたので、Tiled窓をユーザーがタイトルバーやタスクバーで最小化すると、観測は`HiddenOther`、`Desired`は`Shown rect`になる。差分EffectとしてWMが`SetPresence(shown=true)`を出し、ユーザーの最小化を即座に取り消す。最小化はWindowsの日常操作であり、Phase Bの常用初日に出る。v2.6の第1版までは矩形しか照合しなかったので、この経路は無かった（R4-6の修正で生じた）。

**修正案**：`HiddenOther`への遷移は、`MoveCause`と同様に「ユーザーまたはアプリによる外部の非表示」として扱い、`Desired`を上書きしない代わりにReconcileの対象から外す、と決める。i3/komorebiと同じく、最小化された窓はLayoutからも外して空きを詰める（`HiddenOther`の間は`TiledWindows`に入れない）か、枠を残すかは1行で選ぶ。WMの`SetPresence(shown)`を出すのは、`Desired`が`HiddenByWm`→`Shown`に**変わった**ときだけにする。

#### R6-5【中】旧`WmState`・旧規則を前提とした記述の取り残し（AIエージェントが本書を仕様として読むので、確定前に掃除が必要）

| 行 | 残っている記述 | 正しくは |
|---|---|---|
| 5.4 L757 | 「4.4の`WorkspaceState.Order`には挿入しない」「Orderへ挿入」 | WorkspaceのStack（zipper） |
| 5.4 L763 | 「`NotMovable`の窓の既定の扱い（Floating）はCoreの組込み規則」 | `Unplaced` |
| 6.2 L910 | 「`WindowRemoved`入力による`WmState.Order`からの除去」 | Workspacesのzipperからの除去 |
| 6.2 GateInput | `Management: Map<WindowId, Management>`（`Management`型に`Unplaced`が無い） | `EffectiveManagement`、または`Unplaced`の窓を`Alive`から外すと明記 |
| 7.1 L991-992 | `TargetHung`の注釈「→Suspend」、`Denied`の注釈「→Coreの組込み規則でFloating」 | Healthの信念で配置対象外／`Unplaced` |
| 7.1 L1007 | `ForegroundObserved`の注釈「`WmState.Foreground`の信念を更新」 | `Focus.Foreground` |
| 7.2 L1084, L1087 | 「→ Suspend」 | Suspendという状態はもう無い（Healthの信念／`Oscillation`） |
| 7.2 L1088 | 「Floatingへ（Orderから外す）」 | `Unplaced`へ（どのWSにも属さない） |
| 7.2 L1092 | 「settle中は段7のEffectを出さないので新しいPendingも生まれない」 | 段⑧のgateで保留し`Superseded` |
| 7.3 L1122 | 「`ScreenGrace`タイマーと`Consumed`として具体化」（R5-10の未修正） | `MoveCause` |
| 11.1 L1365 | 「Probeの結果は観測として`revise`に入り」 | 該当ドメインの修正関数 |
| 15章表 L1509 | 「Hung→Respondingの修正によるSuspend解除」 | 「配置対象への復帰」 |
| 15.1 #22 L1539 | 「`Screen`が`Settling`/`Grace`」「`WmState.Beliefs`に反映」 | `ScreenPhase`、各ドメインの信念 |
| 3.3 L153, L156 | 「4.4の`WmState`に定義」「`WmState`のどこか名前のあるフィールド」 | 各ドメインの状態（同じ段落の後半とも食い違う） |
| 19章 | `WmState.fs`と並んで`CoreState.fs`/`Reconciler.fs`が残る | ドメインファイルとの重複を整理（Reconcilerは`Placement.fs`に含まれるはず） |

機械的な置換で済むが、「Suspend」「Order」「Floatingへ読み替え」は意味が変わっているので、grepだけで見逃さないこと。

---

### Phase Aで決めれば足りる

#### R6-6【中】zipperの`Focus`に、意図（`FocusNext`による回転）と観測（`ForegroundObserved`）の2つの書き手がいる

L529は`Focus`を「このWorkspaceで最後にフォーカスされた窓という1つの事実」と言うが、`FocusNext`はコマンドの時点でzipperを回す（意図。ADR-003で言えば受付）。観測はedgeでzipperを上書きする。`RequestForeground`が`ForegroundDenied`になると、zipperの`Focus`は実際のフォアグラウンドと食い違ったまま残る。次の`FocusNext`は意図の位置から回るので、使い勝手としては悪くない。しかし「事実」という説明は誤りなので、「最後にフォーカスした、またはフォーカスを要求した窓」と定義し直すか、zipperはコマンドでは回さず`FocusTargetRequested`だけを出すかを選ぶ。

また、`focused = Elsewhere`のときの`FocusNext`は「zipperの`Focus`の次」へ移る（L534）。v2.6第2版の「最後にフォーカスしていた窓へ移る」から挙動が変わっているので、意図的かどうかを明記する。

#### R6-7【中】「Hungの窓を配置対象から外す」のLayout上の意味

L632は「Placementは`Hung`の窓を配置対象から外すだけ」と言うが、`LayoutContext.TiledWindows`から外す（他の窓が詰めて再配置され、回復時にもう一度再配置される）のか、枠を残して`SetRect`だけ出さないのかが無い。前者は、ハングと回復のたびに全窓が2回動く。振動（`Oscillation`）による停止についても同様。ハングは数秒で回復することが多いので、後者（枠を残す）を既定にするのが自然である。

#### R6-8【低〜中】gateでの保留の副作用

`Reconfiguring`/`Settling`の間、Reconcilerは毎stepで同じ差分Effectを作り、gateがそれを保留して新しい`RequestId`のPendingを`Superseded`にする。これを入力ごとに繰り返し、`RequestId`とTraceを消費する。Placementは`ScreenPhase`をCtxで読めるので、段⑦では差分を作らず、gateの保留は最終防衛線に留める方がよい。

また、保留した`RequestForeground`はReconcileの対象（`Desired`）ではないので、確定後に再発行されない。settle中に`SwitchWorkspace`したユーザーにはフォーカスが渡らない。`SwitchSettle`の期限による再発行と、どちらが先に満了するかに依存する。確定時に「最後に保留したフォーカス要求」を1件だけ再発行する、と決める。

#### R6-9【低〜中】`EvidenceRef`の順序と`combineAll`の結合則に、型が足りない

- L570は根拠を「その根拠を運んだ入力の`Sequence`」で並べるとするが、`Outcome of RequestId`と`ProbeEvidence of EvidenceId`はその`Sequence`を持たない。各ケースに`sequence: int64`を持たせる必要がある。
- 同じ入力が複数の根拠を生む場合（同じ`Sequence`）のタイブレーク（ケース順、次に値）も必要。そうしないと上位8件の切り詰めが全順序にならず、#25の結合則が成り立たない（並べ方が演算の順序に依存する）。
- `Unknown`の`EvidenceGap`の和集合も、リストではなく集合（重複排除・正準順序）として定義する。

#### R6-10【低〜中】ID採番の状態が無い

3.4/7.1は「ID採番は状態や入力として明示し、`Guid.NewGuid`等を読まない」とする。しかし`RequestId`/`BatchId`/窓ごとの`IntentEpoch`の次の値を持つフィールドが、どのドメインの型（`PlacementState`）にも無い。Replay一致（#10）に直結するので、`PlacementState`に`NextRequestId`/`NextBatchId`/`Epochs: Map<WindowId, IntentEpoch>`を加える。

#### R6-11【低】モニター0台の間に分類された窓の所属

`AwaitingEvidence`の`homeWorkspace`は`WorkspaceId option`で、`NoMonitors`の間は`None`になり得る。この間に分類が確定した窓をどのWSへ入れるか（(X2)は所属を要求する）が無い。「最小IDのWS」等を1行で決める。

#### R6-12【低】名前の細部

- `TimerId`の`Classification of ClassificationTimer`はWindowsドメインのタイマーなのに、ドメイン名で揃っていない（他は`Placement`/`Topology`/`Focus`/`Health`）。`Windows of WindowsTimer`に揃えるか、理由を書く。
- `HealthState.Responsiveness`は`Map<WindowId, Belief<Responsiveness>>`（`BeliefSlot`ではない）。「無ければResponding」で`Unformed`を表している。他の信念は`BeliefSlot`を使うので、意図的な違いである旨を一言書くと読みやすい（`MinWidth`/`DpiBehavior`/`Movability`も同様）。

---

## (3) 収束判定

**まだ確定すべきでない。** ただし問題は、ドメイン分割を7.1の手続きへ落とす部分に集中しており、設計の方向（ADR-016/017/018、3.4）は確定してよい。

確定前に必須の修正：
1. **R6-1**：`ReturnMonitor`をTopologyへ移す。`WorkspaceSet`を全域にする。フォアグラウンドの変化を段②のDomainEventにする。そのうえで、段⑤⑥の内側のドメイン順と、各ドメインが読むCtx・受けるDomainEventを表で固定し、「他ドメインの状態を書かない」を型で保証する。
2. **R6-2**：CoreCommandの振り分け表、`RequestForeground`を出すドメインの一元化、`Movability`のリセットの段、`ChangeLayout`の結果の置き場。
3. **R6-3**：zipper/履歴の更新を`focused`の変化で、`current`の追従を`Foreground`の変化で、と契機を分ける。(F2)を無条件の形に戻す。
4. **R6-4**：`HiddenOther`（ユーザーの最小化）をReconcileで戻さない規則。
5. **R6-5**：取り残された旧記述の掃除（表のとおり）。

R6-1/R6-2は、7.1に「ドメイン×段×Ctx×DomainEvent」の1枚の表を置けばまとめて解消できる。これが入れば、次のラウンドは確認だけで収束する見込みである。

## プロジェクトオーナーの判断が必要な論点（今回新たに生じたもの）

1. **ユーザーが最小化したTiled窓のLayout上の扱い**（R6-4）：(A) 枠を詰める（i3/komorebi寄り。最小化＝一時的に管理から外す）／(B) 枠を残す（復元したときに元の位置に戻るが、画面に空白が残る）。
2. **zipperを意図で回すか観測で回すか**（R6-6）：(A) コマンドで回す（キーの連打に強く、`ForegroundDenied`でも次のキーが期待どおりに進む）／(B) 観測でだけ回す（ADR-003に忠実で、zipperが常に実際のフォーカスと一致する。ただし連打すると、観測が届く前の2回目のキーが同じ窓を起点に回る）。

---

# Opus批判的レビュー：architecture.md v2.6（R6反映後）第4ラウンド

対象：`docs/architecture.md`（1809行）。行番号は現行版。

総評：7.1の表（L1053-1070）で、ドメイン間のデータの流れは一方向になった。3つの規則（自分の状態だけを返す／読めるのは「前段の今回の状態・任意ドメインのstep開始時の状態・前段までのDomainEvent」）は明快で、後ろ向きの書き込みは解消している。R6はすべて意図どおりに反映された。

残る問題は、この表の上で2つのDomainEventの「出す条件」が原因を区別していないことの2点だけである。どちらも構造の変更を要さない局所的な修正で済む。

---

## (1) R6の解消判定

| 指摘 | 判定 | 根拠 |
|---|---|---|
| R6-1 後ろ向きの依存 | 解消 | `Returns`をTopologyへ移した（L400, L532）、`WorkspaceSet`の全域化（L379）、`ForegroundChanged`を①で、`FocusTargetRequested`を②で出す形、7.1の表と3つの規則、#22のアーキテクチャテスト |
| R6-2 コマンドの振り分け | 解消 | 表の⑤にコマンドを列挙、`RequestForeground`は⑦で一元的に作る、`Movability`のリセットは④、`LayoutOverride`、`RequestClose` |
| R6-3 edgeの回帰 | 解消 | zipper/Historyは⑥で`focused`の変化、`current`の追従は⑤で`ForegroundChanged`。L1067-1068の補足も正確 |
| R6-4 `HiddenOther` | 解消。ただし副作用あり | 規則は入った（L524）。ただし「HiddenByWm→Shownに**変わったとき**だけ」という変化ベースの条件が、抑止区間と組み合わさると脆い（R7-2） |
| R6-5 残骸 | ほぼ解消 | 7.2 L1111の`Timeout → Retry/Backoff/Suspend`が1つ残る（R7-3） |
| R6-6 zipperの書き手 | 解消 | 定義を修正し、21章に記載 |
| R6-7 Hungの枠 | 解消 | L525 |
| R6-8 保留 | 解消（細部はR7-3） | ⑦で差分を作らない、`HeldFocusTarget` |
| R6-9 `EvidenceRef` | 解消 | |
| R6-10 ID採番 | 解消 | `NextRequestId`等（L446） |
| R6-11 0台中の所属 | 解消 | |
| R6-12 名前 | 解消 | `Windows of WindowsTimer`（L176, L188） |

---

## (2) 新たな問題

### 確定前に必須（いずれも局所的な修正）

#### R7-1【中〜高】`FocusTargetRequested`の出し方が原因を区別していない。アプリの自己アクティブ化やモニターの再接続でフォーカスを奪い、逆に`FocusMonitor`ではフォーカスが動かない

**場所**：7.1 表⑤のWorkspacesの出力（L1061「表示されたWSのzipperの`Focus`」）、⑤のTopologyの操作（`FocusMonitor`、表示中のWSへの`SwitchWorkspace`は`current`を変えるだけ）、⑦（最後の1件から`RequestForeground`）、4.4 L540（hidden WSの窓のフォアグラウンド化→view）

**問題**：Workspacesは`WorkspaceShown ws`を受けるたびに、そのWSのzipperの`Focus`へ`FocusTargetRequested`を出す。`WorkspaceShown`の原因は3つあるが、適切な振る舞いはそれぞれ異なる：
- (a) ユーザーの`SwitchWorkspace`：zipperの`Focus`へ移すのが正しい。
- (b) hidden WSの窓Bの自己アクティブ化（タスクバーのクリックを含む）：既にBがフォアグラウンドなので、zipperの`Focus`（別の窓A）へ`RequestForeground`を出すと、ユーザーがタスクバーで選んだBからAへフォーカスを奪う。
- (c) `MonitorLifecycle.reconcile`（モニターの再接続）：再接続した各モニターに表示されたWSのzipperの`Focus`が全て要求され、「最後の1件」が勝つ。ユーザーが作業していた窓から、たまたま最後に処理されたWSの窓へフォーカスが移る。

逆に、`current`だけを変える操作（`FocusMonitor n`、既に`n`に表示中のWSへの`SwitchWorkspace`）は`WorkspaceShown`を出さないので、`FocusTargetRequested`が出ず、フォーカスが移らない。`current = n`のまま、実際のフォーカスは元のモニターに残る。直後に`ForegroundObserved`が来なければ追従もしないので、表示上のアクティブなモニターと入力先が食い違う（v2.6第2版までは`FocusMonitor`は`RequestForeground`を出すと書いていた、R5-1）。

**失敗シナリオ**：
- WS1（hidden、zipper`Focus = A`）のSlack（B）をタスクバーでクリック→`ForegroundChanged B`→Topologyが`view WS1`→`WorkspaceShown WS1`→Workspacesが`FocusTargetRequested A`→PlacementがAへ`RequestForeground`。ユーザーがクリックしたSlackではなく、エディタAが前面に出る。
- 2台目のモニターを挿し直す→settle確定→`reconcile`が`WorkspaceShown WS3`を出す→作業中のWS1の窓からWS3の窓へフォーカスが飛ぶ。

**修正案**：`WorkspaceShown`に原因を持たせる（`WorkspaceShown of ws * ShownBy`、`ShownBy = UserView | ForegroundFollow | Reconcile`）。Workspacesは`UserView`のときだけzipperの`Focus`へ`FocusTargetRequested`を出す。`ForegroundFollow`のときは、フォアグラウンドの窓を`FocusObserved`経由でzipperに反映するだけにする（⑥で既にそうなる）。`Reconcile`のときは何も出さない。

`FocusMonitor`と表示中WSへの`SwitchWorkspace`については、Topologyが`CurrentChanged ws`（`UserView`相当）を出し、Workspacesがそのzipperの`Focus`へ`FocusTargetRequested`を出す。#16の入力列に、(b)(c)と`FocusMonitor`を含める。

#### R7-2【中】`SetPresence`を`Desired`の「変化」だけで出す規則は、抑止区間・失敗・再起動で変化を取りこぼす

**場所**：4.4 L524、7.1 表⑦（「抑止区間の間は差分を作らない」）、7.2 `SetPresence`のPending

**問題**：R6-4の修正で、`SetPresence(shown=true)`は「`Desired`がHiddenByWm→Shownに**変わったとき**だけ」になった。しかし変化は1 stepでしか観測できないので、次の場合に取りこぼす：
- (a) `Reconfiguring`/`Settling`の間にユーザーが`SwitchWorkspace`すると、⑦は差分を作らない。`Desired`をその間に更新するなら変化は抑止区間内で消費され、確定後には変化が無い。結果としてWSを切り替えたのに窓が隠れたままになる。`Desired`を更新しないなら、確定時の`Desired`の再計算で変化が生じるので動くが、どちらなのかが書かれていない。
- (b) `SetPresence(shown)`のPendingが`Timeout`→Retryを上限まで使い切った場合や、gateで弾かれた場合、二度と出し直されない。
- (c) 隠す側（`shown=false`）が状態ベースか変化ベースかも書かれていない。

**修正案**：変化ではなく**状態と観測の組**で決める。`Desired = Shown`かつ観測が`HiddenByWmMethod`（WM自身が隠した状態）なら`SetPresence(shown=true)`。`Desired = HiddenByWm`かつ観測が`VisibleToUser`なら`SetPresence(shown=false)`。観測が`HiddenOther`なら、どちらの場合も何もしない。こうすればユーザーの最小化（`HiddenOther`）は戻さず、抑止区間や失敗の後も、確定後の最初のReconcileで自然に収束する。R6-4の意図（ユーザーの最小化を取り消さない）は、`HiddenOther`を照合対象外にする部分だけで達成されている。

---

### Phase Aで決めれば足りる

#### R7-3【低〜中】7.1の表の小さな抜け

- **Focusのタイマー**（`FocusTimer.WorkspaceSwitchSettle`の満了、L540の「期限時にフォアグラウンドがまだhidden WSなら1回だけ再発行」）を受ける段が、表のどこにも無い。②のFocusに加え、`FocusTargetRequested`を出すようにする。
- **`Reconfiguring`の解除条件(b)「ユーザーの`UserCommand`」**：②のTopologyの入力に、任意の`UserCommand`を加える（現状は`FocusMostRecent`しか列挙されていない）。
- **settle確定時の全Pendingの`Superseded`**：どの段で行うか表に無い。`TopologySettled`を③のPlacementの入力に加える。
- **`HeldFocusTarget`の再発行**：L1069は「`TopologySettled`の後の**step**で」とするが、後続の入力が来なければ再発行されない。同じstepの⑦（`TopologySettled`はDomainEventとして⑦から見える）で出す、とする。
- **Stickyの`Process`不一致**：①でWindowsが検知するが、①のWindowsには出すDomainEventが無い。`WindowUnmanaged`と`ForgetWindow`を、①のWindowsの出力（または⑤で①の検知結果を読む形）として表に入れる。
- **⑥のFocusの`History`からの除去**：`WindowUnmanaged`による除去（(X3)、4.4 L521）を、⑥の「自分の状態に行うこと」に明記する。
- **7.2 L1111**：`Timeout → Retry/Backoff/Suspend`の「Suspend」が残っている（R6-5の取り残し）。

#### R7-4【低】`HiddenOther`の窓のWSが一度hiddenになってから再表示された場合

ユーザーが最小化した窓のWSを`SwitchWorkspace`で離れて戻ると、R7-2の状態ベースの規則なら、観測は`HiddenOther`のままなので何もしない（最小化を保つ）。現行の変化ベースの規則では、HiddenByWm→Shownの変化で`SetPresence(shown)`が出て、最小化が解除される。どちらが望ましいかを1行で決める。R7-2を採れば自然に前者になる。

#### R7-5【低】zipperが`HiddenOther`の窓を含む場合の`FocusNext`

最小化された窓はLayoutからは除かれるが、zipperには残る。そのため`FocusNext`の巡回で`FocusTargetRequested`が最小化窓に当たる。`SetForegroundWindow`は最小化された窓を復元しないことがある。巡回で`HiddenOther`の窓を飛ばすか、`RestoreBeforeMove`と同様に復元してからフォーカスするかを決める。

---

## (3) 収束判定

**R7-1とR7-2を直せば、収束と判定してよい。** どちらも7.1の表と4.4の数行で閉じる局所的な修正であり、ドメインの境界、段の順序、型の構造には影響しない。修正後は、新しい全体レビューのラウンドは不要で、この2点が反映されていることの確認だけで確定してよい。R7-3〜R7-5はPhase Aの実装時に決めれば足りる（R7-3の各項目は表に1行足すだけである）。

ADR-016/017/018、3.3/3.4、4.4/4.5、7.1の骨格は、現行の内容で確定してよい水準にある。

## プロジェクトオーナーの判断が必要な論点（今回新たに生じたもの）

なし（R7-4/R7-5は、R7-2の採否と実装時の細部で決まる）。

---

# Opus批判的レビュー：architecture.md v2.6 第5ラウンド（最終確認）

対象：`docs/architecture.md`（1814行）。R7-1〜R7-5の反映の確認。

## 判定：**収束。確定してよい。**

## R7の解消確認

| 指摘 | 判定 | 根拠 |
|---|---|---|
| R7-1 フォーカス要求の原因 | 解消 | `ShownBy = UserView \| ForegroundFollow \| Reconcile`（L390-394）。Workspacesは`UserView`と`CurrentChanged`のときだけzipperの`Focus`を要求する（7.1表⑤）。自己アクティブ化は`ForegroundFollow`（L561）、`FocusMostRecent`は`UserView`（L555）、`FocusMonitor`と表示中のWSへの`SwitchWorkspace`は`CurrentChanged`（L543）。タスクバーでの選択とモニターの再接続でフォーカスを奪う経路、`FocusMonitor`でフォーカスが動かない経路は、どちらも無くなった |
| R7-2 表示状態のReconcile | 解消 | 「`Desired`×`ObservedPresence`」の状態ベースの規則にした（L529、表⑦）。`HiddenOther`は対象外。抑止区間やRetryの使い切りの後も、確定後の最初のReconcileで収束する |
| R7-3 表の抜け | 解消 | ①の`Process`不一致、②の任意の`UserCommand`と`SwitchSettle`の満了、③の`TopologySettled`→`Superseded`、⑥の`History`からの除去、⑦で`HeldFocusTarget`を同じstepに再発行、7.2の`Suspend`の残り（L1116） |
| R7-4 | 解消 | R7-2により、最小化した窓はWSを離れて戻っても最小化されたまま |
| R7-5 | 解消 | `FocusNext`は`HiddenOther`の窓を飛ばす（L555） |

## Phase Aの実装時に決めれば足りる細部（確定を妨げない）

1. **`FocusMostRecent`の行き先がhiddenなWSにある場合の「最後の1件」**：②でFocusが行き先`w`を`FocusTargetRequested`として出し、⑤でWorkspacesが`UserView`の表示に応じてzipperの`Focus`を要求する。⑦は最後の1件を採るので、zipperの`Focus`が勝つ。通常は`w`とzipperの`Focus`は一致する（そのWSで最後にフォーカスした窓）。しかし、そのWSでコマンドによってzipperを回した後に別のWSへ移った場合は、一致しないことがある。次のいずれかを1行で決める：
   - 明示的な行き先（②由来）がzipper由来より優先される、と⑦の規則に書く。
   - `FocusMostRecent`由来のviewでは、Workspacesがzipperの`Focus`を要求しない。
2. **`SwitchSettle`を始める条件**：⑥は`SwitchSettle`の開始条件を「`SwitchWorkspace`由来の`WorkspaceShown`」としている。`FocusMostRecent`由来のview（`UserView`）も旧WSを隠すので、同じ揺れが起きる。条件を「`ShownBy = UserView`の`WorkspaceShown`」に揃える。

どちらも7.1の表の1語程度の修正であり、ドメインの境界・段の順序・型の構造には影響しない。

## まとめ

ADR-016（観測・信念・決定の3層）、ADR-017（待ちのデータ表現）、ADR-018（ドメイン分割）、3.3/3.4、4.4/4.5、7.1の処理順序の表は、現行の内容で確定してよい。R3〜R7の指摘は、上記の2つの細部を除いてすべて解消された。
