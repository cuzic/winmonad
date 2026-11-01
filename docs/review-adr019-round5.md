# ADR-019ドラフトv5 批判的レビュー 第5ラウンド

対象：`docs/adr-019-draft.md`（v5）
照合先：`docs/architecture.md` v2.8、`docs/review-adr019-round4.md`
範囲は前回までと同じで、combinator vs DSLの判断は扱わない。

**結論：未収束（重大1件、軽微4件）。** 第4ラウンドの10件（重大4件、軽微6件）は、すべて正しく反映されている（末尾で確認）。新たに見つかったのは、v4/v5で`Displays`をContractsへ移したことが、architecture.mdで`Displays`がsmart constructorを持つ型として扱われていることと衝突する、という重大な問題1件である。これを直せば収束と判断できる。

---

## 重大

### R5-01 [重大] `Displays`は、architecture.mdではsmart constructorで不変条件を守る型である。公開の値型としてContractsへ移すと、その保証が失われる
- **ドラフト**：決定2(i)「`ScreenPhase`、`Displays`、`FocusTarget`…」。基準(1)。決定6の層5「`ScreenPhase`/`Displays`/`FocusTarget`/…」。決定9「§17の型の配置の段落に、「ただし`ScreenPhase`/`Displays`/`FocusTarget`等、ドメイン横断で読まれる値型は例外としてWm.Contractsに置く」という一文を追加する」。
- **architecture.md**：
  - §3.4（216行）「**Parse, don't validate**：…`Displays`/`WorkspaceSet`/`Zipper`（4.4）は全てこの原則の適用である」。
  - §4.4（399行）「`// コンストラクタはprivate。Displaysを作り変える関数（view、モニターの追加・除去）は全て「同じWorkspaceを2画面に出さない」を保つ。`」。
  - §15の表（1544行）「`Displays`/`WorkspaceSet`のsmart constructorが不正な状態を作れないこと」。
  - §15.1 #17（1568行）「`Displays`内の性質はsmart constructorの単体テストで検査する」。
  - ADR-018「`Displays`（xmonadの`StackSet`のcurrent/visibleと同形）・`WorkspaceSet`（private）・…により、それらの不変条件の多くが「後から検査する性質」から「型で表現できない状態」に変わる」。
- **問題**：`Displays`は、`view`とモニターの追加・除去という、Topologyの遷移関数でしか作れないように意図された型である（「同じWorkspaceは高々1つのモニターに出る」を型で保証する）。v5はこれを「小さな値型」としてContractsへ移すが、次のどちらにしても矛盾が出る。
  1. **表現を公開する場合**：Wm.Runtime・Wm.Simulation・テスト・Wm.Policyのどこからでも、2画面に同じWorkspaceを持つ`Displays`を作れる。§15の表とADR-018が言う「不正な状態を作れない」が成り立たなくなる。
  2. **表現をContracts内で`private`にする場合**：`view`等の構築関数もContractsに置くことになり、Topologyの中核の遷移ロジックがCoreから出ていく（§17「各ドメインの状態型・更新関数…はWm.Core」に対する、例外の一文では済まない変更になる）。あるいは`GatedEffect`と同じく`internal`＋`InternalsVisibleTo("Wm.Core")`にする手はある。しかしドラフトはそれを決めていない。
- **修正の方向**（いずれかを明記すること）：
  - (A) `Displays`はCore.Topologyに`private`のまま残し、他ドメインが読む用には読み取り専用の射影（例：`current: Screen option; visible: Screen list`のレコード、または`Map<MonitorId, WorkspaceId>`と現在のモニター）を決定2(ii)の射影として`step`が組み立てる。`Screen`は`MonitorSnapshot`と`WorkspaceId`だけでできているので、この射影はContractsの型だけで書ける。これが決定2の考え方（「状態は渡さず射影を渡す」）に最も合う。
  - (B) `Displays`をContractsに置き、ケースの構築子を`internal`＋`InternalsVisibleTo("Wm.Core")`にする（§6.2の`GatedEffect`と同じ手法）。この場合、§3.4・§15・ADR-018の「smart constructorを持つ」を「Contracts側で構築子を制限する」と読み替える旨を、決定9の反映先に加える。
  - `ScreenPhase`と`FocusTarget`は不変条件を持たない普通のDUなので、今の扱いのままでよい。

---

## 軽微

### R5-02 [軽微] `EffectiveManagement`の根拠に`ValidationInput`を挙げているが、`ValidationInput`は基準(2)の起点ではない
- **ドラフト**決定2「`EffectiveManagement`は、`GateInput`/`ValidationInput`（Map<WindowId, EffectiveManagement>）が参照するために基準(2)でContracts行きになる」。
- **問題**：基準(2)の起点は`CoreCommand`／`InputEvent`／`Effect`／`EventEnvelope`／`GateRejection`／`GateInput`であり、`ValidationInput`は決定3の末尾でWm.Layoutsに残すとしている型である。根拠は`GateInput`だけで足りるので、`ValidationInput`への言及を外すこと。結論は変わらない。

### R5-03 [軽微] 層9の例に、Phase Aの範囲外で、かつContractsに無い型を運ぶ種別が入っている
- **ドラフト**層9「Contractsの型だけで書ける永続化対象の種別…`PendingSatisfied`/`PendingTimedOut`/`ReplIntervention`/`ProbeRequested`/`EvidenceCaptured`等」。
- **architecture.md** §11.1 `type ProbeRequest = { …; ApprovalToken: ApprovalToken option // Host側のUIが発行する期限付き能力トークン… }`。§10.2/§18 Phase C・Dで、`ReplIntervention`はIPCの介入、`ProbeRequested`/`EvidenceCaptured`はActive Diagnosisである。
- **問題**：`ApprovalToken`（とIPC介入の記録の形）は、層1〜8のどこにも無い。これらの種別は「Contractsの型だけで書ける」とは言えない。決定1が依存グラフを「Phase Aの範囲」に限定したのと揃えて、層9の例を`InputReceived`/`EffectDispatchStarted`/`EffectCallReturned`/`EffectBatchReturned`/`InternalFault`/`JournalDegraded`/`JournalRecovered`/`SafetyRecoveryAction`/`ObservationReceived`/`PendingSatisfied`/`PendingTimedOut`に絞り、Phase C以降の種別は対象外とすること。

### R5-04 [軽微] ドメインの更新関数が`DecisionTrace`を出すなら、§4.4の`update`の型を書き換えることになる。その反映が無い
- **ドラフト**決定7「`Core/DomainEvents.fs` → **`DecisionTrace`** → 6ドメイン → `WmState.fs`」。`DecisionTrace`をドメインより前に置いているので、ドメインがそれを出す前提である。
- **architecture.md** §4.4 `val update : 'Input -> 'Ctx -> 'State -> 'State * Response<'Timer, Effect> * DomainEvent list`（戻り値にTraceが無い）。§7.1「各ドメインの更新関数・…起こり得ない組合せ…に出会ったら、例外を投げずに入力を捨て、`DecisionTrace`に「導出不整合の疑い」を記録する」。
- **問題**：§7.1はドメインの更新関数がTraceを記録することを求めているが、§4.4の型にはその出口が無い（architecture.md内部の食い違い）。ドラフトは配置を決めたので、出口も決める必要がある。(a) `update`の戻り値に`DecisionTrace list`を加える（§4.4の型の変更）か、(b) `DomainEvent`に診断用のケースを設けて`step`が`DecisionTrace`に変換するか、のどちらかである。どちらにしても、決定9の反映先に§4.4を加えること。

### R5-05 [軽微] 縦切りで検査する#3は、文言どおりには`ScreenSettle`に当てはまらない
- **ドラフト**決定8「性質ベーステスト#21（`ScreenPhase`の`Settling`に関わる部分）／#3」。
- **architecture.md** §15.1 #3「古いTimerが新しい**Intent**を完了/失効させない」。`IntentEpoch`/Intentは§7.2のPendingの概念である（Placement）。`ScreenSettle`について「古い発火は状態を変えない」を述べているのは、#21の後半「待ちが存在しない／`Deadline`が入力時刻より後の`TimerElapsed`は状態を変えない（古いTimerの発火の無害性。#3の具体化）」である。
- **修正**：縦切りの検査対象を「#21（`Settling`の部分。前半の開始の対応と、後半の古い発火の無害性の両方）」とし、#3はPlacementの`PendingDeadline`を実装した時点の対象とすること。

---

## 第4ラウンドの指摘の反映確認
| ID | 状態 |
|---|---|
| R4-01 | 反映済み。`FocusTarget`を(i)へ、`PendingResolved`の結果のDUを`Core/DomainEvents.fs`へ。「無い想定」を削除 |
| R4-02 | 反映済み。根拠を§7.1の表に変更。`MoveCause`/`ShownBy`をDomainEvents.fsへ。`EffectiveManagement`を基準(2)へ（文言はR5-02） |
| R4-03 | 反映済み。DTOの変換はWm.Core、ファイルのI/OはWm.Runtimeに分離し、決定1・4・9で一貫している |
| R4-04 | 反映済み。`DecisionTrace`をWm.Coreに確定（`update`の出口はR5-04） |
| R4-05 | 反映済み。`PolicyStaleAnalysis`を永続化の側として扱い、揮発性の種別は`DecisionTrace`へ（例の範囲はR5-03） |
| R4-06 | 反映済み。決定0に原則を直接書いた。未定義の規則名は残っていない |
| R4-07 | 反映済み。JournalWriterの一文は残し、別の文を追加。§17のツリーのコメント、型の配置の段落、§3.1も反映先に入れた |
| R4-08 | 反映済み |
| R4-09 | 反映済み。フィールドを後から足す方式と、骨格のスタブとの違いを明記 |
| R4-10 | 反映済み |

## 収束の条件
R5-01について(A)か(B)のどちらかを選び、決定2・3・6・9を整合させれば、構造上の未解決点は残らない。R5-02〜R5-05は文言の修正で済み、再レビューを要しない程度の指摘である。
