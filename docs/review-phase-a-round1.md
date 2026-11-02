# Phase A 実装（04404dd..e59540a）批判的レビュー

対象：`git diff 00d7ed5..e59540a`（12コミット）。照合した設計書：architecture.md §3.3 / §4.2 / §4.4 / §4.5 / §6.2 / §7.1 / §7.3 / §15.1 / §19。
行番号はe59540a時点のファイル内の行。

重要度の凡例：
- **blocking**：今の実装・契約のままWindows/Placementを結線すると、確実に誤動作する（多くは「今はTopologyがObserveAllしか出さないので顕在化していない」だけ）。
- **should-fix**：次のドメインの実装に入る前に直すべきもの。
- **nice-to-have**：直す価値はあるが、急がない。

---

## サマリ（重要度順）

| # | 重要度 | 場所 | 一行要約 |
|---|---|---|---|
| 2-1 | blocking | GatedEffect.fs:98-103 | `SetPresence`に`TargetHiddenByWm`を適用しているので、WMが隠したい窓へのSetPresence(shown=false)が**全て**弾かれる |
| 2-2 | blocking | GatedEffect.fs:98-103 | `SetPresence`に`TargetBelievedHung`を適用している（表では「適用しない」）。ハングした窓を隠せない |
| 2-3 | blocking | GatedEffect.fs:82-89 | `SetRect`/`RestoreBeforeMove`に`TargetBelievedHung`/`TargetHiddenByWm`を**適用していない**（表では適用） |
| 2-4 | blocking | GatedEffect.fs:47, 79 | `ForgetWindow`に`TargetNotAlive`を適用している（表では除外）。HWND再利用時のForgetWindowが必ず弾かれる |
| 2-5 | blocking | GatedEffect.fs:104-105 | `ObserveWindow`/`Reprobe`に`TargetExcluded`を適用している（表では適用しない）。Excludedの窓を再probeできない |
| 3-1 | blocking | Topology.fs:215, 224, 260, 231 | `Reconfiguring`の解除時に`Settling ([], …)`で入り、Reconfiguring中の`MonitorTopologyObserved`は捨てる → 安定待ち満了で`reconcile state []`が走り**NoMonitors**になる |
| 1-1 | should-fix（Windowsドメイン着手前に必須） | Focus.fs:26, 32, 36 / Health.fs:20, 31-42 | `LastRevisedAt`の単位がドメインごとに違う（FocusはSampleSequence、Healthは単調時刻ms）。`combineAll`の`max LastRevisedAt`や`Stale asOf`が意味を失う |
| 1-2 | should-fix | Focus.fs:26, 36 | 1つのフィールドに「値の出所のSequence」と「処理済みの水位」を兼ねさせたため、順序が入れ替わって届いたOk観測を失う |
| 3-2 | should-fix（Shell契約次第でblocking） | Topology.fs:232-237, 193-200 | トポロジが変わっていない`MonitorTopologyObserved`でもSettlingへ入る。settleが`ObserveAll`を出すので、Shellの全体再観測がトポロジも報告すると Grace→Settling→Grace… の無限周回になる |
| 2-6 | should-fix | GatedEffect.fs:68-106, 173-181 | `MissingRestoreBeforeMove`を生成する経路が無い。GateInputに「最大化/Snap状態」の入力が無く、`restoreTargetsSeen`引数は未使用 |
| 2-7 | should-fix | GatedEffect.fs:112-134 | 入れ子の`ApplyBatch`は検査されずに素通りする（ゲートを迂回できる） |
| 2-8 | should-fix | GatedEffect.fs:29, 111 | `gate`がpublicなので、`InternalsVisibleTo("Wm.Core")`が何も守っていない。偽の`GateInput`で誰でも`GatedEffect`を作れる |
| 4-1 | should-fix | WmState.fs:75-83, Focus.fs:72-76 | `WindowRemoved`で`SwitchSettle`のfocusTargetを更新しないので、消滅した窓を指したまま残る |
| 4-2 | should-fix | WmState.fs:75-83 | 「消滅」の除去と「管理外化」の除去が1つのmatchに融合している。Windowsが入るとHealthの信念を誤って消す／消し忘れる形になる |
| 5-1 | should-fix | Timers.fs:43-48 | `TimerId`のケース名（Placement/Windows/Topology/Focus/Health）が、Coreのモジュール名・`WmState`のフィールド名・予定されている`Placement`型・`Wm.Windows`名前空間と衝突する |
| 5-2 | should-fix | WmState.fs:64-153 | `step`が§7.1の段の順に組まれていない（Topologyの②がFocusの①より前、`ForegroundChanged`を出さない、DomainEventを捨てる） |
| 5-3 | should-fix | JournalWriter.fs:96-123 | `step`が状態遷移に使っている入力（ForegroundObserved/WindowRemoved/UserCommand）をJournalに`unsupported-input-event`として書く → Replay不能 |
| 5-4 | should-fix | Health.fs:31-32, 41-42 | Mapに無い窓へ根拠の無い`Confirmed Responding`を書き込む。`IsHungAppWindowTrue`を`Inferred []`（「事前の仮定」の意味）にする |
| 3-3 | should-fix | Topology.fs:147-155 | 新しいモニターへの割当で、別のモニターへの`Returns`を持つhiddenなWSを取ってしまう（#18が未テスト） |
| その他 | nice-to-have | 下記 | debounceをhardDeadlineで頭打ちにしない、空ApplyBatch、Eff接頭辞、DecisionTraceが文字列だけ、RuntimePolicyが薄い、テストの欠落 |

---

## 1. `Focus.reviseForeground`：`LastRevisedAt`をSampleSequenceとして読み替えている件

### 結論
「Coreは実時間を読まないので、入力由来の単調な番号として使う」という**動機は正しい**。LatestWinsにはSampleSequenceの比較が必須で、§4.5の`Belief<'T>`にはそのための置き場所が無い。この穴は設計書側にある。
ただし、`LastRevisedAt`を流用するのは**妥当ではない**。`ClassInputBeliefs`（5フィールド）と`GeometryBeliefs`（5フィールド）が同じパターンを真似ると、壊れ方が11か所に増える。踏襲させないこと。理由は次の3点。

### 1-1 [should-fix／Windowsドメイン着手前に必須] 単位がドメインごとに食い違う
- **場所**：`src/Wm.Core/Focus.fs:26, 32, 36`（`LastRevisedAt = observation.SampleSequence`）と、`src/Wm.Core/Health.fs:20, 31, 35, 42`（`LastRevisedAt = now`、つまり`EventEnvelope.MonotonicTime`のms）。さらに`Stale asOf`は、Focusでは`SampleSequence`（Focus.fs:36）、`EvidenceRef.Sample`ではJournalの`Sequence`（Beliefs.fs:23「観測を運んだ入力のSequence」）。§4.2のとおりSampleSequenceは「EventEnvelopeのSequenceとは別の番号空間」なので、**int64が3つの別々の番号空間を運んでいる**。
- **問題**：`Beliefs.combineAll`/`combineAnyOf`（Beliefs.fs:84, 96, 103, 115）は`max ba.LastRevisedAt bb.LastRevisedAt`を取る。§4.5の`ClassificationView`は、Windowsの`ClassInputs`（LatestWins。踏襲すればSampleSequence）とPlacementの`Movability`/`MinWidth`（Accumulating。Healthを踏襲すればms）を、同じ`All [...]`の中で合成する。SampleSequenceとmsの`max`は、どちらの意味も持たない。
- **失敗シナリオ**：
  1. Policy `All [ IsDialog; MovableIs Movable ]` を評価する。`IsDialog`の信念は`LastRevisedAt = 120`（SampleSequence）、`Movability`は`LastRevisedAt = 5_400_000`（起動から90分後のms）。合成結果の`LastRevisedAt`は5_400_000になる。Explainが「この分類は Sequence 5_400_000 の入力で改訂された」と表示し、Journalのその番号を引くと、存在しないか無関係な入力に当たる。
  2. 「確度が使われるのは、Explainと再probeの契機の2つ」（§4.5）。将来、再probeの契機を`now - b.LastRevisedAt > N`のような経過時間で書けば、Focus型の信念（SampleSequenceは高々数万）は常に「数十分前に改訂された」と判定され、毎step再probeが走る。逆向きの誤りも起こる。
  3. Checkpoint（Replayの開始点）から再開するケース：Checkpointに保存された`LastRevisedAt`の値は、どのドメインのものかによって単位が違う。「CheckpointのLastRevisedAtを、再開後の`now`と比べて古さを判断する」コードが1か所でも入ると、Focus/ClassInputs/Geometryの信念だけが誤判定される。型が同じ`int64`なので、コンパイラは気付けない。
- **提案**：
  - §4.5に1行足して、`LastRevisedAt`は全ドメインで「改訂を引き起こした入力の`EventEnvelope.MonotonicTime`」と固定する（Healthの現状に合わせる）。`Stale asOf`も同じ単位にそろえる。
  - LatestWinsの水位は別のフィールドで持つ。例：Contractsに
    `type LatestWins<'T> = { Slot: BeliefSlot<'T>; Watermark: int64 option }  // Watermark = 処理済みの最大SampleSequence`
    を置き、`ClassInputBeliefs`/`GeometryBeliefs`/`FocusState.Foreground`のフィールドをこの型にする。
  - 修正関数`LatestWins.revise : now -> LatestWins<'T> -> Observation<'T> option -> LatestWins<'T>`は、**Contractsに1つだけ**置く。11フィールドで`reviseForeground`をコピーさせないため。§4.5は「規則は閉じた4種類」と言っているので、規則の実装を1つにしても「信念を1つのレコードに集めない」原則には反しない（共有するのは規則であって信念ではない）。`None`（今回問い合わせなかった）は何もしない、もこの関数で一元化できる。

### 1-2 [should-fix] 1つのフィールドに「値の出所」と「水位」を兼ねさせたため、順序が入れ替わったOk観測を失う
- **場所**：`Focus.fs:26`（比較）、`Focus.fs:36`（Errorのとき`LastRevisedAt`をErrorのSequenceまで進める）。
- **問題**：Errorの観測で`LastRevisedAt`を進めると、それより前のSequenceを持つが、今保持している値よりは新しいOk観測が、後から届いても捨てられる。§7.2/R4-10がSampleSequenceを導入した理由は、まさにスナップショット由来とイベント由来の観測の到着順が入れ替わることにある。
- **失敗シナリオ**：`ForegroundObserved Ok(Some A)` が seq=5 → Believed A。`ForegroundObserved Error` が seq=7（問い合わせ失敗）→ A, Stale, LastRevisedAt=7。その後、イベント経路で遅れた`ForegroundObserved Ok(Some B)` seq=6 が届く → `6 > 7`が偽なので捨てる。実際の最新のフォアグラウンドはBなのに、信念は`A (Stale)`のまま残る。`ForegroundObserved`は「次の変化」が起きるまで来ないので、`focused`はA（またはElsewhere）に固定される。FocusNextがAを起点に回り、F2（`head History = w`）もAの側で成り立ってしまう。
- **提案**：1-1の`Watermark`と、値の出所のSequenceを分ける。Okは「値の出所より新しければ採用」、Errorは「水位より新しければStaleにする」。上のケースではB（seq 6）を採用し、7のErrorを受けているので確度は`Stale asOf`のまま、とするのが§4.5の文言（「より新しいSampleSequenceのOk観測で置き換える」「古い観測は新しい信念を上書きしない」）に一番近い。どちらに倒すかは設計書に1行書いておくこと。今は実装者が暗黙に選んでいる。

### 1-3 [nice-to-have] `Unformed`の間のErrorは水位を残さない
- **場所**：`Focus.fs:37-38`。
- **失敗シナリオ**：起動直後に`Error` seq=9 → `Unformed []`のまま（水位の記録が無い）。その後、古い`Ok(Some C)` seq=3 が届く → `Confirmed C`になる。より新しい失敗を知っているのに`Confirmed`を名乗る。1-1の`Watermark`を入れれば同時に解消する。

### 1-4 [nice-to-have／設計書] SampleSequenceの単調性の範囲が契約に無い
- §4.2は「単一の単調増加番号」としか書いていない。Shell（またはその観測スレッド）がCoreを再起動せずに再初期化され、SampleSequenceが0から振り直されると、全てのLatestWinsの信念が凍る（以後の観測は全部「古い」扱い）。§4.2に「同じSessionIdの間は単調」と書き、Coreの`state`が生きている間にShellのカウンタを戻さない契約を明記すること。

### ClassInputBeliefsが踏襲してよいか
**踏襲させないこと。** 1-1の提案（`LatestWins<'T>`型とContracts内の単一の修正関数）を先に入れてから、Windowsの`ClassInputs`とPlacementの`Geometry`を書く。Focusも同じ型へ移す。

---

## 2. `GatedEffect.gate`：§6.2の適用範囲表との1セルずつの照合

行番号は`src/Wm.Contracts/GatedEffect.fs`。`checkOne`は68-106行。

### 照合表（✓ = 一致、✗ = 不一致）

| 拒否理由 \ Effect | SetRect | RestoreBeforeMove | RequestForeground | SetPresence | ObserveWindow / Reprobe | ForgetWindow | RequestClose（§6.2本文：RequestForegroundの列と同じ、ただしHungは除く） |
|---|---|---|---|---|---|---|---|
| TargetNotAlive | 適用 ✓（79行） | 適用 ✓ | 適用 ✓ | 適用 ✓ | 適用 ✓ | **表は除外／実装は適用 ✗**（47, 79行） | 適用 ✓ |
| TargetExcluded | 適用 ✓（83行） | 適用 ✓ | 適用 ✓（91行） | 適用 ✓（99行） | **表は適用しない／実装は適用 ✗**（105行） | 適用しない ✓ | 適用 ✓（97行） |
| NonPositiveRect / OutsideAnyMonitor | 適用 ✓（87-88行） | —（矩形が無い）✓ | — ✓ | — ✓ | — ✓ | — ✓ | — ✓ |
| MissingRestoreBeforeMove | **表は適用／生成経路が無い ✗** | — | — | — | — | — | — |
| TargetNotMovable | 適用 ✓（84行） | 適用 ✓ | **表は適用しない／実装は適用 ✗**（92行） | **表は適用しない／実装は適用 ✗**（100行） | 適用しない ✓ | 適用しない ✓ | 適用しない ✓ |
| TargetBelievedHung | **表は適用／実装は無し ✗** | **表は適用／実装は無し ✗** | 適用 ✓（93行） | **表は適用しない／実装は適用 ✗**（101行） | 適用しない ✓ | 適用しない ✓ | 適用しない ✓ |
| TargetHiddenByWm | **表は適用／実装は無し ✗** | **表は適用／実装は無し ✗** | 適用 ✓（94行） | **表は適用しない／実装は適用 ✗**（102行） | 適用しない ✓ | 適用しない ✓ | **表は適用／実装は無し ✗** |

不一致は11セル。重要度別に分ける。

### 2-1 [blocking] SetPresence × TargetHiddenByWm を適用している（GatedEffect.fs:102）
- **問題**：`GateInput.HiddenByWm`は「`Desired = HiddenByWm`の窓」で、§7.1段⑧により**次の状態から**作る。`SetPresence(shown=false)`は、まさに`Desired`が`HiddenByWm`になった窓に対して出すEffectである。
- **失敗シナリオ**：`SwitchWorkspace 2`で、WS1の窓A, Bの`Desired`が`HiddenByWm`になり、段⑦が`SetPresence(rA, A, false)`と`SetPresence(rB, B, false)`を出す。段⑧の`HiddenByWm = {A, B}`で両方とも`TargetHiddenByWm`として弾かれる。Workspace切替で旧WSの窓が1つも隠れない。さらに§6.2により、Pendingは同じstepで`Cancelled(GateRejected)`になる。§4.4の状態ベースのReconcile（`Desired = HiddenByWm`かつ観測が`VisibleToUser`ならSetPresence(false)）が毎回同じEffectを作って毎回弾かれ、1回のReconcileごとに拒否が積み上がる。

### 2-2 [blocking] SetPresence × TargetBelievedHung を適用している（GatedEffect.fs:101）
- 表の注記「ハング窓も隠す必要があり、ShowWindowAsync系で非同期に出す」と逆の動作。**失敗シナリオ**：ハングしたブラウザのあるWS1からWS2へ切り替えると、ハング窓だけがWS2の上に残る。ユーザーからは「Workspaceが切り替わらない」ように見える。

### 2-3 [blocking] SetRect/RestoreBeforeMove に Hung/HiddenByWm を適用していない（GatedEffect.fs:82-89）
- **失敗シナリオ（Hung）**：Reconcilerの再送（§7.2 Transient→Retry）が、Hungと信じている窓へ`SetRect`を出す。§7.1/§7.2の「Hungの窓にはSetRectを出さない」の最終防衛線が無いので、ゲートを通過してしまう。SWP_ASYNCWINDOWPOSでブロックは避けられても、Pendingは観測されないまま`Timeout`→Retry上限まで回る（§15.1 #7の「要求の無制限増殖」側の圧力になる）。
- **失敗シナリオ（HiddenByWm）**：`SetRect`がhiddenなWSの窓に通る。最小化・画面外方式では、隠した窓が画面内の目標矩形へ戻され、**再表示されてしまう**。
- **付記**：`GateRejection`の型コメント（設計書の969行「WMが隠している窓へのSetRect/RequestForeground」）と表（「SetRect/RestoreBeforeMove」の列で適用）が微妙に食い違う。表に従い、`RestoreBeforeMove`にも適用するのが安全（隠した窓を`SW_RESTORE`すると、それ自体が再表示になる）。設計書の969行のコメントも直すこと。

### 2-4 [blocking] ForgetWindow × TargetNotAlive を適用している（GatedEffect.fs:47, 79）
- `targetOf`が`ForgetWindow w -> Some w`を返し、79行の`notAlive`検査が全Effectに先にかかる。§4.5 Sticky規則：Process不一致を検知したら「旧WindowIdを消滅と同じに扱い」、`ForgetWindow`を返す。この時点で旧IDは`Records`から外れているので`Alive`に無い。**失敗シナリオ**：HWND再利用を検知するたびに`ForgetWindow`が`TargetNotAlive`で弾かれ、ShellのHWND→WindowId対応表に旧IDが残り続ける。次の列挙でも同じHWNDが旧IDで報告され、Windowsは`Records`に無いIDとして捨てる（R5-11）。**その窓は永久に再発見されない**（§15.1 #5の破れ）。
- **修正**：`targetOf`から`ForgetWindow`を外すか、`notAlive`検査を`ForgetWindow`の場合だけ飛ばす。

### 2-5 [blocking] ObserveWindow/Reprobe × TargetExcluded を適用している（GatedEffect.fs:104-105）
- 表は「適用しない（Excludedの窓も信念を持ち続け、再probeする、4.4）」。**失敗シナリオ**：未分類の間にタイトル一致でExcludedになった窓が、後でタイトルが変わってTiledになるべき状態になる。`ObserveWindow`が弾かれるので、属性の変化を拾う問い合わせが出ず、§4.4「属性が変われば再分類する」が死文化する。設計書の989行が「死文化」と名指しで警告している、まさにその状態。

### 2-6 [should-fix] MissingRestoreBeforeMove の生成経路が無い（GatedEffect.fs:68, 173-181）
- `checkOne`の引数`restoreTargetsSeen`は一度も読まれていない。そもそも「最大化/Snap状態の窓」かどうかの情報が`GateInput`に無いので、正しく判定することもできない（設計書のGateInputにも無い。**設計書の穴**）。
- **失敗シナリオ**：最大化された窓へ`RestoreBeforeMove`無しの`SetRect`を出すと、ゲートを通過する。OSは最大化状態のまま矩形を無視するか、復元時の矩形として記録する。Pendingは`Mismatch`になり、§4.5の`MinWidth`の誤った証拠になる（観測された幅は最大化の幅で、要求より大きい）。
- **提案**：`GateInput`に`NeedsRestore: Set<WindowId>`（Geometryの信念がMaximized/Snapの窓）を追加する。§6.2の表も「バッチ内で先行する`RestoreBeforeMove`がある、または`NeedsRestore`に無い」と具体化する。

### 2-7 [should-fix] restoreSeenの範囲が「同一ApplyBatch内」になっていない（GatedEffect.fs:126-143）
- `seen0 = input.RestoreIssuedInBatch`を起点に、`goTop`がトップレベルの`RestoreBeforeMove`と各バッチの結果を**次のバッチへ持ち越す**（134行の`seen'`）。§6.2のGateInputのコメントは「同一ApplyBatch内で先行するRestoreBeforeMoveの対象集合」。
- **失敗シナリオ**（2-6を実装した後）：バッチ1に`RestoreBeforeMove w`、バッチ2に`SetRect w`。Shellは各バッチを別のスレッド・別の時刻に適用し得るので、バッチ2の時点でRestoreの効果は保証されない。それでも`MissingRestoreBeforeMove`を出さずに通過する。
- さらに、`RestoreIssuedInBatch`を**呼び出し側が与える入力**にしているのは筋が悪い。バッチごとにゲート内部で計算する値であり、外から与えると全バッチに同じ集合が効く。`GateInput`から外し、`goFlat`の内部で空集合から始めるべき。設計書の該当フィールドも削除を推奨する。

### 2-8 [should-fix] 入れ子のApplyBatchが素通りする（GatedEffect.fs:112-124, 106）
- `goFlat`は子の`ApplyBatch`を`checkOne`へ渡すだけで、`checkOne`は`ApplyBatch _ -> None`（106行）を返す。**失敗シナリオ**：`ApplyBatch(b1, [ApplyBatch(b2, [SetRect(dead窓)])])`が無検査で通過する。§22受け入れ条件4（任意の出力がゲートを通過しなければEffectを発行できない）の迂回路になる。
- **修正**：`goFlat`で再帰的に展開するか、入れ子を拒否する（`Effect`型を`BatchableEffect`と分ける方が、型で封じられてよい）。

### 2-9 [should-fix] `gate`がpublicなので、internalのコンストラクタが何も守っていない（GatedEffect.fs:29, 111 / AssemblyInfo.fs）
- `GatedEffect`の構築子はinternalだが、それを作る唯一の関数`gate`は、Wm.Contractsのpublicなモジュール関数である。Wm.Runtime・将来のREPL・Wm.Windowsは、`{ Alive = 全部; Management = Map.empty; … }`という偽の`GateInput`で`gate`を呼べば、任意のEffectを`GatedEffect`にできる。`InternalsVisibleTo("Wm.Core")`は、Wm.Coreがinternalを一度も使っていないので空振りしている。
- §6.2の約束は「Coreの現在状態（同じstep内のWmState）との整合」。これは「GateInputを作るのがCoreである」ことに依存している。
- **修正**：`gate`をinternalにする（Wm.CoreはInternalsVisibleToで呼べる）。外部に公開するのは`GatedEffect.effect`だけにする。設計書§6.2のコード例にも`internal`を書き足すこと。

### 2-10 [should-fix] RequestForeground/SetPresence × TargetNotMovable を過剰適用（GatedEffect.fs:92, 100）／RequestClose × TargetHiddenByWm の欠落（96-97）
- **RequestForeground**：昇格窓（Unplaced）は`focused`が`Elsewhere`になるので、Core由来の`RequestForeground`は通常出ない。しかしREPLのIntervene由来の要求は、§6.2の表どおり通るべきである。
- **SetPresence**：Placementがもともと出さないので、実害は小さい。表とのずれだけを直す。
- **RequestClose**：CloseFocusedは`focused = ManagedWindow`の窓にしか出ないので、hiddenな窓は来ないはず。ただし最終防衛線として表どおり加える。

### 2-11 [nice-to-have]
- 子が全て弾かれると、`ApplyBatch(bid, [])`をそのまま出す（GatedEffect.fs:134）。ShellとPendingに空のバッチを渡す意味は無いので、捨てること。
- 「全ての違反を蓄積し」（108行のコメント）と書いてあるが、1つのEffectにつき最初の1件しか返していない。コメントを直すか、実装を合わせる。
- `intersectsAnyMonitor`（61-65行）の`r.X + r.Width`は、int32の範囲外の値で桁あふれする。§6.2 #3の検査はPlanValidatorの範囲だが、ゲートは再送経路（PlanValidatorを通らない）も見るので、int64で計算すること。
- **GatedEffectのテストが1本も無い**。表は「拒否理由 × Effect種別 → 適用/非適用」という有限の表なので、`applies : RejectionKind -> EffectKind -> bool`という表駆動の実装と、全セルを列挙する単体テストにすれば、2-1〜2-5のような不一致は機械的に防げる。

---

## 3. `Topology.update`：§7.3との照合

行番号は`src/Wm.Core/Topology.fs`。

### 3-1 [blocking] Reconfiguringの解除で`Settling ([], …)`に入り、Reconfiguring中のトポロジ観測は捨てる → 空のトポロジで確定する
- **場所**：215行（`ScreenReconfigurationInProgress false`）、224行（`UserCommand`）、260行（`ReconfigurationCap`）がいずれも`Settling ([], deadline, …)`を作る。231行は`Reconfiguring`中の`MonitorTopologyObserved`を捨てる（コメントは「観測を信念へ反映するのみ（段①）」だが、Topologyにはトポロジの信念が無く、どこにも残らない）。249行の`settle latest`は`latest = []`のまま`MonitorLifecycle.reconcile state []`を呼ぶ。
- **失敗シナリオA（ロック/アンロック、トポロジは変化なし）**：`ScreenReconfigurationInProgress true`（ロック）→ `false`（アンロック）。モニター構成は変わっていないので、ShellはMonitorTopologyObservedを出さない。500ms後にScreenSettleが発火し、`reconcile state []`が全モニターを`removedIds`として扱う。全WSが`WorkspaceHidden`になり、`Returns`に全WSの退避元が記録され、`Displays = NoMonitors`。Placementが結線されていれば、`WorkspaceHidden`で全窓の`Desired`が変わる（モニター0台の例外規則が入っていても、`TopologySettled []`で全Pendingが`Superseded`になる）。その後、settleが出した`ObserveAll`の応答でトポロジが届けば、再びSettling→reconcileで復帰する。ただし`current`は「primary、無ければ最小ID」に付け替わり、ユーザーがいたモニターから外れる。応答にトポロジが含まれなければ、**次にモニターを抜き差しするまでNoMonitorsのまま**。
- **失敗シナリオB（RDP再接続）**：`true` →（新しい仮想ディスプレイ構成の`MonitorTopologyObserved`。231行で捨てる）→ `false` → `Settling []` → 空で確定する。設計書の「RDP切断中の仮想ディスプレイに全窓を詰め直し、再接続時にもう一度詰め直す往復を避ける」の逆で、構成を一度空にしてから戻す。
- **3-1b**：Settling中に`ScreenReconfigurationInProgress true`が来ると、206行で`Reconfiguring now`へ移り、Settlingが持っていた`latest`を捨てる。同じ機構で失われる。
- **設計書の穴**：§4.4の`ScreenPhase.Reconfiguring of since: int64`には最新の観測の置き場所が無く、§7.3は「Reconfiguringの解除でSettlingへ入る」とだけ書いている。このとき`latest`を何にするのかが未定義。
- **提案**：`TopologyState`に`LastObserved: MonitorSnapshot list option`を持つ（どのPhaseでも`MonitorTopologyObserved`で更新する。§7.1段①「全入力について無条件に最初に行う」の具体化）。Settlingへ入るときは`LastObserved`を使い、`None`なら現在の`Displays`のモニターを使う（その場合のreconcileは何もしない）。ScreenPhaseのケースに持たせる手もあるが、Phaseをまたいで保持する必要があるので、独立したフィールドの方が素直。設計書の§4.4の型と§7.3に反映すること。
- **テスト**：#21の性質テストはStable→Settlingしか生成していない（TopologyScreenSettleTests.fs）。Reconfiguring経由の遷移と、「確定したトポロジは、最後に観測したトポロジ（無ければ確定前の構成）に等しい」という性質を加えること。

### 3-2 [should-fix／Shellの契約次第でblocking] 変化していないトポロジでもSettlingへ入る → ObserveAllと噛み合うと無限に周回する
- **場所**：232-237行（Stable/Graceで無条件にSettlingへ）、193-200行（settleは常に`ObserveAll`を出し、Graceへ）。
- **問題**：§7.3は「トポロジ変化（`MonitorTopologyObserved`）で入る」としている。実装は、観測の内容が現在の`Displays`と同じでも入る。
- **失敗シナリオ**：settleが`ObserveAll`を出す。Shellの全体再観測が（自然な実装として）モニター列挙を含み、同じ構成の`MonitorTopologyObserved`を出す。Graceの間にそれが届くと、Settlingへ戻り、SettleMs後に再びsettle→`ObserveAll`→…となる。**Settling（配置Effectを抑止）とGrace（移動を`OsInduced`扱い）を永久に往復**し、ドラッグもReconcileも効かなくなる。§7.3の「定期的な全体Snapshot」がトポロジを含む場合も、そのたびに配置が抑止される。
- **提案**：Stable/Graceでは、`snapshots`が現在の`Displays`のモニター集合（Id・Bounds・WorkArea・Dpi・IsPrimaryの比較）と同じなら何もしない。Settling中は、同じ内容でもdebounceを延長しない、を検討する。合わせて、「`ObserveAll`への応答がトポロジを含むか」をShellの契約として§7.1に書くこと。

### 3-3 [should-fix] 新しいモニターへのWS割当が、別のモニターへの`Returns`を持つWSを奪う（Topology.fs:147-155）
- `bySmallestUnshown`は「表示されていない最小のWorkspaceId」を選び、`Returns`を持つかどうかを見ない。
- **失敗シナリオ**：構成 C(primary, ws0), A(ws1), B(ws2)。AとBを切断 → `Returns {ws1→A, ws2→B}`。新しいモニターDを接続 → `bySmallestUnshown = ws1`がDに表示される（`Returns[ws1]`は残る）。Aを再接続 → `byReturn`はws1を拾えない（表示中なので）→ `bySmallestUnshown = ws2`がAに出る。Bを再接続 → ws2は表示中 → ws3。結果、ws2は最初の所有者Bへ戻れず、`Returns {ws1→A, ws2→B}`は永久に残る。
- **提案**：hiddenなWSの候補は「`Returns`を持たないもの」を先にする。§4.4の規則(2)「最小IDのhiddenなWorkspace」も同様に明確化すること。§15.1 #18は「Phase Aで検査する」とされているのに、性質テストがまだ無い。実装済みの`reconcile`に対して今すぐ書けるので書くこと（新規モニターを混ぜた列も生成対象にする）。

### 3-4 [nice-to-have] hardDeadlineの強制確定は「イベントが続いている」場合だけ正しい
- 238-240行：hardDeadline以降に`MonitorTopologyObserved`が届けば、そのstepで確定する。これは§7.3の「イベントが続いていても強制的に確定する」を正しく満たす。
- ただし242-246行で、debounceを`now + SettleMs`のまま再始動するので、hardDeadlineの直前で最後のイベントが来ると、確定は`hardDeadline + SettleMs`近くまで延びる。上限は守られるので実害は小さい。§7.3の「上限30秒」を厳密に守るなら、`deadline = min (now + SettleMs) hardDeadline`とし、`StartTimer`の期間もそれに合わせる。
- 3-1の修正後も、`ReconfigurationCap`（256-263行）の`awaitDeadline (Some (since + cap))`の判定はそのままでよい。

---

## 4. `WmState.step`の`WindowRemoved`処理

行番号は`src/Wm.Core/WmState.fs`。

### 確認できたこと
- Workspaces（78行）、`Focus.History`（82行）、`Health.Responsiveness`/`Reprobes`（79-81行）を同じstepで除去している。§4.4「消滅した時点で除去」と(X3)前半を満たし、性質テスト（CrossDomainInvariantTests.fs）もある。
- `Foreground`の信念が消滅した窓のIDを持ち続けても、`focused`は`workspaceOf = None`で`Elsewhere`になるので無害。グローバルな単一スロットの値なので、除去しないのが正しい（LatestWinsの水位は保つ必要がある）。

### 4-1 [should-fix] `SwitchSettle`のfocusTargetが消滅した窓を指したまま残る（WmState.fs:75-83 / Focus.fs:72-76）
- **失敗シナリオ**：`SwitchWorkspace 2`で、渡す先がWS2のzipperのFocus Aになり、`SwitchSettle = Some (Some A, t+settle)`。待ちの間にAが閉じる（または異常終了する）。WS2のzipperは隣のBへ移る。OSは隠されつつあるWS1の窓Xをフォアグラウンドにしている。期限が来ると`onSwitchSettleTimerFired`がAを返し、Placementを結線した後は`RequestForeground A`になる。ゲートが`TargetNotAlive`で弾き、Pendingは`Cancelled`。結果、フォアグラウンドはhiddenなWSの窓Xのまま、`focused = Elsewhere`になり、WS2に移ったのにキー操作が効かない。本来はBへ再要求すべきだった。
- **修正**：`WindowRemoved w`で、`SwitchSettle = Some (Some w, d)`なら、除去後の`workspaceOf`で求めたWSのzipperのFocus（Stackが空ならNone）に置き換える。§4.4の「Workspacesと`Focus.History`から除去」の列挙に`SwitchSettle`も加えること（設計書の取りこぼし）。

### 4-2 [should-fix] 「消滅」と「管理外化」の除去が融合している（WmState.fs:75-83）
- 現状は生の`WindowRemoved`入力にマッチして、Workspaces/History/Healthを一度に除去している。§4.4/§7.1では、Workspaces/Historyからの除去は`WindowUnmanaged w`（DomainEvent。消滅・Excluded化・Unplaced化のいずれでも出る）を契機にし、信念の除去は**消滅の時だけ**（「Excludedの窓も信念を持ち続ける」）に限る。さらに、Sticky規則のProcess不一致（HWND再利用）は「消滅と同じに扱う」ので、Healthの信念も除去すべきだが、その入力は`WindowRemoved`ではない。
- **失敗シナリオ（Windows実装時）**：実装者が自然に「`WindowUnmanaged`を受けたらこのブロックを呼ぶ」と書き換えると、Excludedへ再分類された窓のResponsivenessの信念と再probeが消える。逆に`WindowRemoved`のまま残すと、HWND再利用で旧IDのHealthの信念が残り続ける（`Map`が漏れ、旧IDの再probeタイマーも残る）。
- **提案**：今のうちに2つの集合へ分ける。`vanished`（`WindowRemoved` ∪ Process不一致）は全ドメインの信念と待ちを除去する。`unmanaged`（`vanished` ∪ Excluded化 ∪ Unplaced化）はWorkspaces/History/SwitchSettleから除く。前者は段①、後者は段⑥で処理する。

### 4-3 [nice-to-have] Reprobesを除去しても`CancelTimer`を出さない（WmState.fs:79-81）
- 後で届く`ResponsivenessReprobe w`の発火は、`onReprobeTimerFired`が無害に捨てるので、正しさは保たれる（#21の後半）。ただし、Simulation/Hostのタイマー表に死んだ窓のタイマーが残る。`recoveredFromHung`（Health.fs:92-96）は`CancelTimer`を出しているので、それと同じ扱いにそろえること。

---

## 5. 全体設計・モジュール境界

### 5-1 [should-fix] TimerIdのケース名がモジュール名・型名と衝突する（Timers.fs:43-48）
- `TimerId = Placement | Windows | Topology | Focus | Health`。これが、`Wm.Core.Topology`/`Focus`/`Health`のモジュール、`WmState`のフィールド`Topology`/`Focus`/`Health`、`RuntimePolicy.Topology`と同じ名前空間に開かれている。WmState.fsは`open Wm.Contracts.Timers`したうえで`Topology.update`/`Focus.removeFromHistory`と書いていて、F#の名前解決の優先順位でたまたま通っている。
- 近い将来、§6.2の`ValidatedPlan.Placements: Placement list`（Wm.Layoutsの`Placement`型）、`Wm.Core.Placement`モジュール、`Wm.Windows`プロジェクトの名前空間が加わる。`Placement x`や`Windows.…`の解決が、openの順序に依存して変わり始める（型の推論エラーで済めばよいが、パターン`| Placement p ->`が意図しない型に束縛される形の誤りは読みにくい）。
- **提案**：`TimerId`（と各サブDU）に`[<RequireQualifiedAccess>]`を付け、`TimerId.Topology ScreenSettle`と書く。今なら変更箇所はJournalWriter・Topology/Health/Focus・テストだけ。設計書§3.3のコード例も合わせる。

### 5-2 [nice-to-have] EffectiveManagementのEff接頭辞
- 衝突を避ける判断自体は正しいが、`[<RequireQualifiedAccess>]`を`EffectiveManagement`（または`Management`）に付ければ、設計書の名前（`Tiled | Floating | Unplaced | Excluded`）のまま書ける。Eff接頭辞は、Explainの表示や`%A`を使う箇所（JournalWriter.fs:113、DecisionTraceの文字列）にそのまま漏れる。5-1と同じ方針でそろえると一貫する。

### 5-3 [should-fix] `step`が§7.1の段の順で組まれていない（WmState.fs:64-153）
- 実際の順序：Topology.update（段②）→ WindowRemovedの除去 → Healthのタイマー → Focusの信念（段①）→ SwitchSettleのタイマー → `focused`の差分（段⑥）→ ゲート。段①（全ドメインの信念の修正）がTopologyの段②より後にある。
- `reviseForeground`は新しいスロットを返すだけで、段①が出すべき`ForegroundChanged w`（§7.1の表）を誰も出していない。`Topology.update`が返す`DomainEvent`は148行で捨てている。
- 今は相互に読まないので顕在化しないが、Windowsを入れた時点で、段⑤のTopologyの`current`追従（`ForegroundChanged`を読む）と、段⑦のPlacement（`WorkspaceShown`/`WindowUnmanaged`を読む）が必要になる。そのたびに`match envelope.Input with`を1つずつ足していくと、段の順序（#22）とDomainEventの受け渡しが場当たりになる。
- **提案**：Windowsに着手する前に、`step`を「段ごとの関数 + DomainEventの累積」のパイプラインに組み直す（例：`stage1 : Input -> WmState -> WmState * DomainEvent list`…）。各段の関数の型で、§7.1の表の「読むもの」を表すこと。今のコード量（約90行）なら小さな作業で済む。

### 5-4 [should-fix] Journalが、stepの消費する入力を記録できない（JournalWriter.fs:96-123）
- `inputEventToJson`が扱うのは`MonitorTopologyObserved`/`ScreenReconfigurationInProgress`/`TimerElapsed`だけ。一方`step`は、`ForegroundObserved`/`WindowRemoved`/`UserCommand`でも状態を変える。これらは`unsupported-input-event`（`%A`の文字列）として書かれ、`readAll`はそのタグでfail-fastする。
- **失敗シナリオ**：今のビルドで、Reducerに`ForegroundObserved`を1回でも投入したJournalは、Replayで最初から読めない。§15.1 #10/#19（Replayの一致、信念が入力列の関数）を、実装済みの範囲についても検査できない。
- **提案**：「stepが分岐に使う入力のケース ⊆ Journalが往復できるケース」を、アーキテクチャテストか往復の性質テスト（`readAll (write x) = x`）で固定する。`Observation<'T>`と`NativeCallRecord`のエンコードが必要になるが、Windowsの`WindowObserved`でどのみち必要になる。

### 5-5 [should-fix] Healthが根拠の無いConfirmedを書き込む（Health.fs:20, 31-32, 37-38, 41-42）
- `FromEffectFailure`（TargetHung以外）と`PumpTimedOut`（判定前）で、Mapに無い窓に`respondingDefault now`（`Confirmed`）を**書き込む**。§4.5/R6-12：「Mapに無い」は「まだ証拠が無い」という意味。根拠無しの`Confirmed`は§4.5の`Confirmed`の定義（直接の観測で裏付けられた）にも反する。Explainが「ハングしていないことを確認済み」と誤って説明する。
- `IsHungAppWindowTrue`を`Inferred []`にしている（41-42行）。§4.5で`Inferred []`は「証拠の無い事前の仮定」の意味であり、ここは`Inferred [Sample seq]`であるべき。`reviseResponsiveness`がSequenceを受け取らないので作れない。1-1と同じく、信念の修正関数が入力の`EventEnvelope.Sequence`/`SampleSequence`を受け取る形にそろえること。
- **修正**：Mapに無い場合に変化させない証拠は、`current`をそのまま返す（`option`のまま保つ）。

### 5-6 [nice-to-have] DecisionTraceが文字列の1ケースだけ（DecisionTrace.fs:9）
- `InconsistencySuspected of domain: string * detail: string`。§15.1 #10は「Event/Effect/Traceのハッシュ」の一致を要求する。自由文の`detail`（特に`%A`を使ったもの）は、ビルドが変わると文字列が変わり、Replayのハッシュが壊れる。`domain`もドメイン名の文字列で、閉じた集合になっていない。
- **提案**：`InconsistencySuspected of Domain * InconsistencyKind`（どちらもDU）にする。拡張点として残すなら、文字列ではなく型で残すこと。

### 5-7 [nice-to-have] RuntimePolicyが`TopologyConfig`だけを持つ（WmState.fs:21）
- SwitchSettleのms（`startSwitchSettle`の引数。まだ誰も渡していない）、Healthの`reprobeMaxAttempts`/`BaseDelay`/`MaxDelay`/`judgedHungThresholdUs`（Health.fs:18, 46-48）は、モジュールレベルの定数になっている。§7.3「最大試行数、Backoff…をPolicyまたは既定設定として持つ」に合わせ、今のうちに`FocusConfig`/`HealthConfig`をRuntimePolicyへ入れておくと、後でシグネチャを全部変えずに済む。
- `WmState.initial runtimePolicy`は引数を使わず、`Topology.initial Map.empty`を呼んでいる。Policyの`WorkspaceAssignments`を渡す経路が無い。Policyを結線するときに忘れないよう注記しておくこと。

### 5-8 [問題なし] Wm.CoreがWm.Layoutsを参照していないこと
- 現状は不要で、後から`ProjectReference`を1行足すだけで済む。§17の依存方向（Core → Contracts・Policy・Layouts）にも反しない。足かせにはならない。ただし、DecisionTraceに`ValidationError`を載せる計画（DecisionTrace.fs:6-7のコメント）は、5-6の型付けと一緒に決めること。

### 5-9 テストの欠落（まとめ）
- GatedEffect：0本（2-11）。
- Topology：Reconfiguring経由の遷移（3-1）、変化しないトポロジ（3-2）、#18（3-3）。
- Focus：順序が入れ替わって届く観測（1-2、§15.1 #20）。
- Journal：往復の性質（5-4）。

---

## 推奨する着手順
1. GatedEffectを表駆動に書き直し、全セルのテストを付ける（2-1〜2-11）。`gate`をinternalにする。
2. Topologyに`LastObserved`を入れ、Reconfiguringの解除と変化しないトポロジの扱いを直す。テストと#18を追加する（3-1〜3-3）。
3. `LastRevisedAt`の単位を設計書で固定し、Contractsに`LatestWins<'T>`と単一の修正関数を置き、Focusを移す（1-1〜1-3）。Healthの修正関数の引数もそろえる（5-5）。
4. `TimerId`に`RequireQualifiedAccess`を付ける（5-1）。`step`を段ごとのパイプラインへ組み直す（5-3）。WindowRemovedを「消滅」と「管理外化」に分ける（4-1, 4-2）。
5. ここまで終えてから、Windowsドメインに着手する。

設計書側に反映が必要な穴：§4.4 `ScreenPhase`/`TopologyState`（最後の観測の置き場所）、§4.4の除去対象の列挙（`SwitchSettle`）、§4.5 `LastRevisedAt`の単位とLatestWinsの水位、§4.2 SampleSequenceの単調性の範囲、§6.2 `GateInput`（`NeedsRestore`の追加、`RestoreIssuedInBatch`の削除、`gate`のinternal化）、§6.2 `TargetHiddenByWm`の型コメントと表の食い違い、§7.1 `ObserveAll`への応答にトポロジを含むかどうか。
