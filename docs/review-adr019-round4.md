# ADR-019ドラフトv4 批判的レビュー 第4ラウンド

対象：`docs/adr-019-draft.md`（v4）
照合先：`docs/architecture.md` v2.8（主に§4.4、§7.1の8段の表、§9.2、§17）、`docs/review-adr019-round3.md`
範囲は前回までと同じで、combinator vs DSLの判断は扱わない。

**結論：未収束。ただし致命の指摘は無い。** 決定2の考え方（ドメイン間で読むのは「小さな値型」と「識別子のレベルの射影」だけで、射影を組み立てるのは`step`）は、§7.1の8段の表を1段ずつ当たり直した結果、**成り立つ**と判断した。R3-01は解消している。

残る問題は次の4つである。
- 決定2の「小さな値型」の一覧には抜けがあり、根拠として引いた§9.2の記述も一部誤っている（R4-01、R4-02）。
- Checkpoint DTOの置き場所について、決定4の原則と同じ決定4の箇条が矛盾している（R4-03）。
- Phase Aの`StepResult`に必要な`DecisionTrace`の型が、どこにも置かれていない（R4-04）。
- 修正は小さく済む事実誤認と、参照先を失った用語がいくつかある（R4-05〜R4-10）。

いずれも、決定の骨格を変えずに文言の修正と型の追加で直せる。

---

## 決定2の検証結果（§7.1の8段について、読み取り対象を1段ずつ確認した）

| 段 | 他ドメインから読むもの | v4での表現 | 判定 |
|---|---|---|---|
| ① | なし（MinWidthのDIP正規化にモニターのDPIが要る＝`Displays`内の`MonitorSnapshot.Dpi`） | (i) `Displays` | 可 |
| ② Topology | step開始時の窓の所属 | (ii) `Map<WindowId, WorkspaceId>` | 可 |
| ② Focus | `SwitchSettle`満了時に「フォアグラウンドがhiddenなWSの窓か」 | (ii) 所属＋表示中のWSの集合 | 可 |
| ③④ Placement/Health | `PendingResolved (rid, outcome)` | DomainEventのペイロード | **outcomeの型の置き場所が未定**（R4-01） |
| ⑤ Windows | `ClassificationView`（PlacementのMinWidth/Movabilityの信念）、アクティブWS、`Foreground`、`Desired = HiddenByWm`の窓（§4.4「分類の述語は`Desired = HiddenByWm`の窓の…変化を再分類の契機として扱わない」） | (i)＋(ii)。`HiddenByWm`は`Set<WindowId>`の射影で表せる | 可 |
| ⑤ Topology | 分類結果（DomainEvent）、所属 | (i) `EffectiveManagement`、(ii) | 可 |
| ⑤ Workspaces | 今回のDisplays、最小化された窓（§4.4「`ObservedPresence = HiddenOther`は飛ばす」） | (i) `Displays`、(ii) `Map<WindowId, ObservedPresence>` | 可 |
| ⑥ Focus | step開始時と終了時の`focused`（`FocusTarget`） | **一覧に無い**（R4-01） | 型の置き場所が要る |
| ⑦ Placement | Displays、ScreenPhase、WSごとのzipperの順・Floating・LayoutOverride・Focus、EffectiveManagement、Responsiveness | (i)＋(ii)。zipperは`WindowId list`等へ射影すれば、`Zipper<'T>`の型を移す必要は無い | 可 |

結論：(i)と(ii)の2種類で全段を表せる。§17.1のテストの文言を変えずに済む、という主張は成り立つ。ただし(i)の一覧は、v4が書いているものでは尽きていない（次のR4-01）。

---

## 重大

### R4-01 [重大] (i)の「小さな値型」の一覧が尽きていない。`FocusTarget`と、`PendingResolved`の結果の型が抜けている。決定7の「他には無い想定」は誤り
- **ドラフト**決定2(i)の一覧：「`ScreenPhase`/`Displays`/`MoveCause`/`ShownBy`/`EffectiveManagement`…`Movability`/`Responsiveness`/`DpiBehavior`等」。決定7：「`Core/DomainEvents.fs`（…ペイロードは基準(1)でContractsへ移したもの以外は**無い想定**だが…）」。
- **architecture.md**：
  - §4.4 `type FocusTarget = | ManagedWindow of WindowId | Elsewhere`。これは親（`WmState`）の節で定義され、§7.1の段⑥でFocusが「`focused`の値がstep開始時から変わり`ManagedWindow w`になったら」と読む。
  - §7.1の段③「出すDomainEvent：`PendingResolved (rid, outcome)`」、段④でPlacementとHealthがこれを受ける（Healthは`TargetHung`の結果で`Responsiveness`を修正する）。outcomeは`Satisfied`/`Mismatch`/失敗（`EffectFailure`）を表すPlacement固有のDUで、§9.2の一覧にも無い。
- **問題**：(1) `FocusTarget`はFocusドメインの入力になる値型である。`WmState.fs`に定義したままでは、Focusより前にコンパイルできない。(2) outcomeの型は、HealthがPlacementの型を参照しないためには、`Core/DomainEvents.fs`（またはContracts）に置く必要がある。したがって決定7の「無い想定」は、すでに具体例が1つある時点で誤りである。
- **修正**：(i)に`FocusTarget`を加える（§9.2の一覧に**含まれている**ので、v4自身の根拠付けとも合う）。決定7に「`PendingResolved`の結果のDU（Placementで定義せず、DomainEvents.fsに置く）」を例として明記する。

### R4-02 [重大] 「§9.2が既にDTOタグ付けの対象として明示している」は`ShownBy`について誤り。§9.2の一覧を基準にすると、Contractsへ移していない型も含まれてしまう
- **ドラフト**背景「(i) §9.2が既にDTOタグ付けの対象として明示している小さな値型（`ScreenPhase`/`Displays`/`MoveCause`/`ShownBy`…）」。決定2(i)「これらは§9.2が既にDTOタグ付けの対象として明示している（Checkpoint・Journal・IPCに出るため）」。
- **architecture.md** §9.2（1285行）の一覧は「…`FocusTarget`、`TimerId`とドメインごとのタイマーDU、`MonitorSelector`、`ClassificationState`、`ClassifiedBy`、`EffectiveManagement`、`DesiredPresence`、`ScreenPhase`、`Displays`、`MoveCause`、および各ドメインの状態レコード（Checkpointに出る）。**`DomainEvent`はCoreの内部でだけ使い、Journal・Checkpointに出ないので対象外**」。
- **問題**：
  1. `ShownBy`は一覧に**無い**。`ShownBy`は`DomainEvent WorkspaceShown`のペイロードとしてだけ現れ、§9.2はまさにそれを対象外としている（第3ラウンドまでのドラフトが`ShownBy`をCoreに置いていたのは、この一文による）。
  2. 逆に、一覧にある`ClassificationState`/`ClassifiedBy`/`DesiredPresence`/`MonitorSelector`は、v4ではContractsへ移していない（決定3の末尾は`DesiredPresence`をWm.Coreに残すとしている）。したがって「§9.2に載っている」はContractsへ移す基準として使われておらず、根拠として引くと読者を誤らせる。§9.2の一覧は「Checkpointに出る＝状態の一部」という理由で載っているのであって、「ドメインをまたいで読まれる」とは別の性質である。
- **修正**：基準(1)の根拠を「§7.1の8段の表で、他のドメインがCtxとして読む値の型」と書き直す（v4が実際に行った確認作業そのものである）。§9.2への言及は削るか、「結果として多くは§9.2の一覧と重なる」程度にとどめる。`ShownBy`は、DomainEventのペイロードであるという性質と矛盾しないように、Contractsではなく`Core/DomainEvents.fs`に置く方が、第3ラウンドまでの判断とも§9.2とも整合する。`ShownBy`はTopologyとWorkspaces・FocusとをDomainEvent経由でつなぐだけなので、Contractsにある必要は無い。同じ理由で`MoveCause`も（§9.2には載っているが）DomainEvent経由でしか読まれないので、DomainEvents.fsで足りる。どちらを選んでもよいが、選んだ理由を一貫させること。

### R4-03 [重大] Checkpoint DTOの置き場所について、決定4の原則と箇条が矛盾している。`private`な状態はWm.Runtimeからは写せない
- **ドラフト**決定4「**DTOは、それが写す型と同じプロジェクトに置く。**」。同じ決定4の3つ目の箇条「**Checkpoint DTO**（`WmState`全体の写し）は、Wm.Runtimeが保持する」。決定1「Wm.Runtime（…Checkpointの読み書き…）」。決定9「「JournalWriterのI/OはShell側」の一文を「Checkpointの読み書きはWm.Runtimeが持つ」に改める」。
- **architecture.md** §4.4 `type WorkspaceSet = private { … }`、`type TopologyState = private { … }`。§9.2「各ドメインの状態レコード（Checkpointに出る）」もDTOタグ付けの対象。
- **問題**：決定4の原則に従えば、Checkpoint DTO（＝Wm.Coreの状態型の写し）はWm.Coreに置かれる。しかも`private`な表現のDTO変換は、その型を定義したモジュールの中でしか書けない。Wm.Runtimeが持てるのは、Checkpointの**I/O**（Coreが`toDto`したものの書き込み、および読み込み後に`ofDto`へ渡すこと）だけである。第3ラウンドのR3-02が示した修正の方向「(b) checkpointの**実装**をWm.Runtimeに移し」は、DTOの変換ではなく永続化の手続きを意図していた。v4の文言は、変換も含むように読める。
- **修正**：「Checkpoint DTOとその`toDto`/`ofDto`はWm.Coreのドメインごと（決定4の原則どおり）。Checkpointファイルの書き込み・読み込み・Replayの開始点としての利用はWm.Runtime」と、2つを分けて書く。

### R4-04 [重大] `StepResult.Trace: DecisionTrace list`の`DecisionTrace`がどこにも置かれていない。決定6の層9で§21へ先送りした「診断用エントリ」は、Phase Aの`WmState.fs`の前提である
- **ドラフト**決定6の層9「診断用の揮発性エントリ——`PolicyStaleAnalysis`/`PlanRejected`/`DesiredUpdated`等…——はここに含めず、どこに置くかは21章の未決事項とする」。決定7「`WmState.fs`（`WmState`型 → `RuntimePolicy`型 → `StepResult`型 → `step`関数）」。
- **architecture.md** §7.1 `type StepResult = { Next: WmState; Effects: GatedEffect list; Timers: …; Trace: DecisionTrace list }`。§9.2の揮発性の種別（`PolicyPredicateEvaluated`、`RuleSelected`、`LayoutInvoked`、`PlanAccepted`/`PlanRejected`、`DesiredUpdated`、`EffectPlanned`、`BeliefRevised`、`TimerCommanded`）は、`step`が出す`DecisionTrace`そのものである。§6.2「発火したら`DecisionTrace`に「導出不整合の疑い」を記録する」、§7.1「`DecisionTrace`に「導出不整合の疑い」を記録する」。§18 Phase A「最小JournalとCLI Explain（…判断は永続化せずReplay再構成）」。
- **問題**：`DecisionTrace`は`StepResult`の一部なので、`WmState.fs`をコンパイルするには`DecisionTrace`が先に定義されていなければならない。各ドメインの更新関数も、段の中で`DecisionTrace`を出す。またその中身は`ValidationError`（Layouts）や`PolicyRuleId`を運ぶので、Contractsには置けず、置けるのはWm.Core（`Core/DomainEvents.fs`と同じ位置か、その直前）である。21章へ先送りすると、縦切り（決定8）の`WmState.fs`の時点で行き詰まる。
- **修正**：「`DecisionTrace`はWm.Coreの最初のファイル群（DomainEvents.fsと同じ位置）に置く。Journalに永続化しない揮発性のエントリであり、Explainは§18どおりReplayで再構成する」と決める。21章に残してよいのは、`PolicyStaleAnalysis`のような**永続化する**診断エントリのペイロードの形だけである（R4-05）。

---

## 軽微

### R4-05 [軽微] `PolicyStaleAnalysis`は「揮発性」ではなく、§9.2の「永続化するもの」である
- **ドラフト**層9「診断用の揮発性エントリ——`PolicyStaleAnalysis`/`PlanRejected`/`DesiredUpdated`等」。How to apply「Journalの診断用揮発性エントリ（`PolicyStaleAnalysis`等）」。
- **architecture.md** §9.2「**永続化するもの**（9.1参照）：…`PolicyStaleAnalysis`（v2.4で追加、R2-11対応。起動時・Rollback時の報告のみAnalyzer実行結果）…」。同じ永続化の側に`PendingSatisfied`/`PendingTimedOut`、`ReplIntervention`、`ProbeRequested`、`EvidenceCaptured`もある。
- **修正**：先送りの対象を「永続化されるがPolicy等の型を運ぶ種別（`PolicyStaleAnalysis`）のペイロードの形」と正しく書く。`PlanRejected`/`DesiredUpdated`は揮発性で、R4-04の`DecisionTrace`として決着させる。層9の「`InputEvent`/`Result<unit, NativeCallRecord>`を運ぶ最小限の形」も、`PendingSatisfied`等の永続化する種別を除外しているように読めるので、範囲を「§9.2の永続化する種別のうち、Contractsの型だけで書けるもの」とする。

### R4-06 [軽微] 本文に存在しない「規則B／規則C／規則D」を参照している
- 決定1のグラフ「PolicyとLayoutsは互いに依存しない＝**規則B**」「Contracts・Policy・Layoutsに依存＝**規則C**」「Windowsには依存しない＝**規則D**」。基準(3)「Policy⊥Layouts（**規則B**）」。
- v4の本文には規則A〜Fの定義が無い（v2/v3の名残）。採用後にarchitecture.md側へ転記すると、参照先の無い名前になる。本文中で定義し直すか、参照を外すこと。

### R4-07 [軽微] 決定9の§17の書き換えが、JournalWriterのI/Oの所在を消し、§17のツリーのコメントを直していない
- **ドラフト**決定9「「JournalWriterのI/OはShell側」の一文を「Checkpointの読み書きはWm.Runtimeが持つ」に改める」。
- **問題**：元の一文はJournalWriterについて述べている。置き換えた文はCheckpointについてだけで、JournalWriterのI/Oがどこにあるか（v4ではWm.Journal自身）が本文から消える。また§17のツリーのコメント「`Wm.Journal/ # writer, reader, checkpoint, projections`」から`checkpoint`と`projections`を外す変更、および§17の型の配置の段落（「各ドメインの状態型…はWm.Core」。`ScreenPhase`/`Displays`はTopologyの状態の構成要素なので、これをContractsへ移すのはこの段落の例外になる）の変更が、反映先の一覧に無い。§3.1の層の一覧にWm.Runtimeを加えることも挙がっていない。

### R4-08 [軽微] 「Journalの純粋部分」を「Journal」にする理由が誤っている
- **ドラフト**決定9「「Journalの純粋部分」という限定表現を「Journal」に簡略化する（Wm.JournalはContracts専用になったため全体が該当する）」。
- **問題**：Wm.Journalはファイルへの書き込み・読み込み（I/O）を行うので、依存がContractsだけでも「純粋」ではない。結論（Linuxで全体をビルド・テストする）は正しい。理由を「Windows依存を持たないため」とすること。

### R4-09 [軽微] 縦切りの`WmState.fs`の「部分的な配線」は、退けた代替案（骨格のスタブ）との違いを明記すること
- **ドラフト**決定8「`WmState.fs`（最初はTopologyの関わる段だけを実際に配線し、残り5ドメインはGateが要求する範囲で後から足してよい）」。代替案「Phase Aの29ファイルを骨格だけ一括で用意する」を退けている。
- **問題**：§4.4の`WmState`は6つのフィールドを持つ。最初にTopologyだけを持つ`WmState`から始めて、ドメインを書くたびにフィールドを足すのであれば、スタブとは違う（二度書きにならない）。一方、6フィールドのまま他の5つを空の型で埋めるのであれば、退けた案と同じになる。前者であることを1文で明記すること。

### R4-10 [軽微] アクセサの例の型が誤っている
- **ドラフト**決定2「`Workspaces.membershipOf : WorkspaceState -> Map<WindowId, WorkspaceId>`」。
- **architecture.md** §4.4：窓→Workspaceの索引を持つのは`WorkspaceSet`（`Index: Map<WindowId, WorkspaceId>`）であり、`WorkspaceState`は1つのWorkspaceのレコードである。正しくは`WorkspaceSet -> Map<WindowId, WorkspaceId>`。

---

## 依存の追加（Wm.Diagnostics/Wm.Runtime）と他の記述との整合（依頼された確認）
- **Wm.Runtime**：Contracts・Core・Journalに依存し、Windowsに依存しない。§17.1のLinuxビルド、§7.1の「入力をJournalに書いてから`step`」、§12.1の「手順を共有し、手段だけ差し替える」と矛盾しない。残る問題はR4-03（DTOの変換とI/Oの分離）だけである。
- **Wm.Diagnostics**：Core・Journal・Policy・Layoutsに依存する。§17のツリー（`explain, incident bundle, privacy`）と矛盾しない。Explainが§18でいう「Replay再構成」を必要とする場合、Replayを回すのはWm.Cli（Runtime・Simulationに依存、§17のツリー「`Wm.Cli/ # inspect, replay, simulate, export`」）で、DiagnosticsはそのTraceを受け取って整形する、という分担で依存グラフどおりに組める。この分担を1文書いておくと、DiagnosticsからRuntimeへの依存を後から足す誘惑を防げる（指摘ではなく推奨）。

## 反映を確認した第3ラウンドの指摘
R3-01（決定2。上の表で検証済み）、R3-02（Wm.Journalを一次事実に限定。ただしDTOの扱いはR4-03）、R3-03（基準(0)〜(4)、推移的な参照）、R3-04（DTOの一般則。Checkpointの箇条はR4-03）、R3-05（層9の縮小。範囲の書き方はR4-04/R4-05）、R3-06（反映先の列挙。漏れはR4-07）、R3-07、R3-08、R3-09、R3-10、R3-11。
