# ADR-019ドラフトv2 批判的レビュー 第2ラウンド

対象：`docs/adr-019-draft.md`（v2）
照合先：`docs/architecture.md` v2.8、`docs/review-adr019-round1.md`
範囲は第1ラウンドと同じで、combinator vs DSLの判断は扱わない。

**結論：未収束。** 第1ラウンドの指摘のうち、R1-1/R1-2/R1-3/R1-8/R1-12/R2-1/R2-2/R3-1/R3-3/R4-1/R4-3は妥当に反映されている。しかし次の3種類の問題が残る。(1) 新しく書いたファイル順のうち2か所が、そのままではコンパイルできない（R2-01、R2-02）。(2) 規則Aの「DTO境界から到達可能」という条件が広すぎ、§17と規則A自身の後半を同時に壊している（R2-03）。(3) 第1ラウンドのR2-3（JournalTypes）とR2-5（Contractsのロジックへのゲート適用）が、変更点の説明では反映済みのように書かれているが、実際には反映されていない（R2-07、R2-08）。

重大度の定義は第1ラウンドと同じ（[致命]／[重大]／[軽微]）。

---

## 1. コンパイル順の誤り（新たに書いた順序そのもの）

### R2-01 [致命] `StepResult`を`WmState.fs`より前に置いているが、`StepResult`は`WmState`を持つ
- **ドラフト**決定4：「… → `Core.Workspaces`/…/`Core.Placement` → `StepResult`/`RuntimePolicy` → `WmState.fs`（ドメインを合成し`step`を組む）」。
- **architecture.md** §7.1：`type StepResult = { Next: WmState; Effects: GatedEffect list; Timers: …; Trace: … }`。`WmState`のレコード型は§4.4で`WmState.fs`に属する（§19「WmState.fs（4.4の親の状態と`step`の合成…）」）。
- **問題**：F#では`StepResult`は`WmState`の型定義より後でなければならない。逆に`RuntimePolicy`は、各ドメインの更新関数（Windowsの段⑤の分類、Placementの段⑦のLayout呼出）が引数として受け取るなら、ドメインより**前**になければならない。2つを1つの位置にまとめたため、少なくとも一方が誤っている。正しい形は「`RuntimePolicy`（またはドメインが受け取るその部分）をShared.fsかそれ以前に置き、`WmState`型→`StepResult`→`step`の順にする」である。
- 第1ラウンドR1-6で「`StepResult`は`WmState`を持つのでCoreに置く」と指摘した点を、位置まで含めて反映できていない。

### R2-02 [致命] Contracts内で`GatedEffect`を`GateInput`より前に置いている
- **ドラフト**決定3：「… → `GatedEffect`（internal構築子） → `GateInput`/`ValidationInput` → DTO/`ofDto`基盤」。
- **architecture.md** §6.2：`module GatedEffect = … let gate (input: GateInput) (effects: Effect list) : GatedEffect list * (RequestId option * GateRejection) list`。`GateRejection`も同じブロックで定義されている。
- **問題**：`GatedEffect`モジュールの`gate`は`GateInput`と`GateRejection`を参照するので、それらは`GatedEffect`より前に必要である。ドラフトは決定3の見出しで「型参照を実際に1つずつ確認したうえでの順序」と述べているが、この箇所は確認されていない。

---

## 2. 規則Aそのものの問題

### R2-03 [致命] 「DTO境界（`ofDto`）から到達可能な値はContracts」は、Checkpointを通じてCoreの状態全体をContractsへ移してしまう
- **ドラフト**規則A：「`InputEvent`／`Effect`／`EventEnvelope`／`GateInput`／`ValidationInput`／**DTO境界（`ofDto`）から型として到達可能な値**は…Wm.Contractsに置く」。同じ規則の末尾：「各ドメインモジュール（Wm.Core）が持つのは…**状態の内部表現**（`private`な表現、zipper等）と更新関数」。
- **architecture.md**：
  - §9.2 DTOタグ付けの対象「Journal・**Checkpoint**・PolicyArtifact・Incident Bundle・IPCに出る全てのDU」。v2.6で追加された対象に「`ScreenPhase`、`Displays`、`MoveCause`、および**各ドメインの状態レコード（Checkpointに出る）**」。
  - §3.3「**`WmState`から到達可能な全ての型とCheckpointのDTO**」。
  - §17「各ドメインの**状態型**・更新関数・信念の修正関数…はWm.Coreの中で…ドメインごとのモジュール…に分けて置く」。
  - §4.4 `type WorkspaceSet = private { … }`、`type TopologyState = private { … }`。
- **問題**：Checkpointは`WmState`を丸ごとDTOにする。したがって規則Aを文字どおり適用すると、`TopologyState`/`WorkspaceSet`/`Zipper`/`PendingRequest`/`ClassificationState`/`DesiredPresence`…がすべてContractsに移ることになる。これは§17の「状態型はWm.Core」と、規則A自身の末尾「private な表現…はWm.Core」の両方に反する。しかも`private`な表現を持つ型の`ofDto`はContracts側からは書けない。
- **修正の方向**：DTOを2種類に分けて規則に書く。「Journal・IPC・PolicyArtifactのDTO（＝外部の事実と共有語彙）→Contracts」と「Checkpoint DTO（＝Coreの状態の写し）→Wm.Core内でドメインごとに」の2つである。そのうえで、規則Aの起点のリストから「DTO境界」を外すか、限定する。

### R2-04 [重大] `ValidationInput`をContractsに置く理由が無く、その結果`LayoutContext`までContractsに引き込まれる
- **ドラフト**規則Aの起点に`ValidationInput`、決定3の順序に「`GateInput`/`ValidationInput`」。
- **architecture.md** §6.2 `type ValidationInput = { Context: LayoutContext; Management: …; Alive: … }`。`LayoutContext`/`LayoutWindow`/`Placement`は§5.6（Layout Registry）の型である。§17「`ValidationError`/`ValidatedPlan`/`Disposition`はWm.Layouts」。
- **問題**：`ValidationInput`はCoreが組み立ててLayoutsの`validate`に渡す型である。規則CによりCoreはLayoutsに依存するので、`ValidationInput`はLayoutsに置けば足りる。Contractsへ移す理由は無い（Contractsに置くべきなのは`GateInput`の方で、これはShell/Simulationも`GatedEffect`経由で関わるためである）。それにもかかわらず規則Aの起点に`ValidationInput`を入れたため、`LayoutContext`・`LayoutWindow`もContractsに移ることになる。ところが決定3の「1つずつ確認した」順序には、どちらも現れない。
- `ClassificationView`も同様である。これはCoreが組み立ててPolicyの評価器に渡す型なので、Wm.Policyに置けば規則B・Cを満たす。規則Aの基準（`InputEvent`等から到達可能）からは導かれず、Contractsへ移す理由がドラフトに書かれていない。

### R2-05 [重大] 規則Aの基準が、規則Aで列挙した型を導いていない
- **ドラフト**規則Aの2番目の箇条：「各ドメインの「決定が読む」値：`WorkspaceId`、`EffectiveManagement`、`ClassificationView`、`ObservedPresence`、**`ShownBy`、`MoveCause`**…」。
- **architecture.md** §9.2「`DomainEvent`はCoreの内部でだけ使い」。`ShownBy`と`MoveCause`は`DomainEvent`のペイロード（§7.1の段②`MoveAttributed (w, cause)`、段⑤`WorkspaceShown (ws, ShownBy)`）である。
- **問題**：`ShownBy`と`MoveCause`は`InputEvent`/`Effect`/`GateInput`のどれからも到達できない。ドラフト自身が`DomainEvent`をCore/Shared.fsに置く（決定4）以上、そのペイロードもShared.fsに置くのが規則に合う。規則が型を導くのではなく、型の一覧が先にあって規則が後付けされている。代替案3で退けた「型を1つずつ列挙する」方式に戻っているうえ、その一覧の根拠になる規則の記述が足りない。

### R2-06 [重大] 決定3は、代替案3で退けた「型の全列挙」を再び行い、しかも§17が明記する型を落としている
- **ドラフト**代替案3「型を1つずつ列挙してファイル順を決める（v1ドラフトの方式）：…本v2は代わりに規則A〜Fという少数の判断基準を書き、型の全列挙は実装時にF#コンパイラ…に委ねる」。一方で決定3は、30個近い型名を並べて「型参照を実際に1つずつ確認したうえでの順序」と称している。
- **architecture.md** §17「`Belief`/`BeliefSlot`/`Confidence`/`EvidenceRef`/`BeliefKey`と**各ドメインの信念の小さなレコード（`IdentityBeliefs`等）**はWm.Contractsに置く」。§4.5の`Movability`/`Responsiveness`/`DpiBehavior`。
- **問題**：決定3には`IdentityBeliefs`/`ClassInputBeliefs`/`GeometryBeliefs`も`Movability`/`Responsiveness`/`DpiBehavior`も無い。これらはドラフトがContractsに置く`ClassificationView`の中身そのものである。R2-01・R2-02・R2-04と合わせると、「1つずつ確認した」という主張は成り立っていない。
- **修正の方向**：決定3を、「識別子→ErrorTypes→観測→EvidenceGap→信念→共有語彙→タイマー→コマンド・入力・Effect→ゲート」程度の**層の順序**に縮め、層の中の順序はコンパイラに任せる、と明記する。代替案3と同じ立場をとること。

---

## 3. 第1ラウンドの指摘で、反映済みとされたが実際には反映されていないもの

### R2-07 [重大] R2-3（Journalのイベント型の配置）は未反映
- 変更点の説明では「R2-3/R3-1/R3-2: Normalizer…」とまとめているが、第1ラウンドのR2-3はNormalizerではなく、**`JournalTypes`をWm.Journalに置くのは§17の「Journalのイベント型は契約側に置く」に反する**という指摘だった。
- **v2の現状**：`JournalTypes.fs`/`JournalWriter.fs`の配置が規則A〜Fのどこにも無い。決定3（Contractsの順序）に`JournalEntry`/`JournalKind`/`JournalPayload`が無い。依存グラフではWm.JournalがWm.Coreの下にある。§17の「JournalWriterの**I/OはShell側**」と§17ツリーの「`Wm.Journal/ # writer, …`」の食い違いにも触れていない。
- **なぜ重要か**：Phase Aの「最小Journal」（§18）は必須で、Phase Aでは`ScenarioRunner`がJournalを書く。型がContractsに無ければ、SimulationとJournalで型の重複が起きる（v1の背景がまさに恐れていたこと）。

### R2-08 [重大] R2-5（ロジックをContractsに置くとゲートの外に出る）は未反映。v2でかえって範囲が広がった
- **architecture.md** §3.4「**Core（Wm.Core、Wm.Policy、Wm.Layouts）**は、環境の時刻…乱数…を決して読まない。…17.1の共通ゲートで、これらのAPIへの参照が**Coreのアセンブリ**に無いことを…検査する」。§17.1も同じく「Coreのアセンブリ」。
- **v2の現状**：Contractsには`classify`、Timer実行器（規則F）、DTO/`ofDto`、§12.1の`executeEffectProtocol`があり、新しいWm.Normalizationには正規化のロジックがある。決定5のcombinatorは配置プロジェクトすら書かれていない（R2-12）。そのどれもが、§3.4の「Core」の定義に入っていない。
- **なぜ重要か**：§23は「記録済みRaw入力（9.1/ADR-013、NormalizerのバグをReplayで再現・回帰テスト化するための一次証拠）」としている。つまりNormalizerはReplayで再実行され、決定的でなければならない。`classify`も§15.1 #14でReplay時の決定性が要求されている。時刻・乱数APIの禁止とクロージャ非含有のゲートを、Wm.ContractsとWm.Normalizationへ広げるという一文が規則に要る。

---

## 4. 新設した構造（Wm.Normalization、Core/Shared.fs、Reducerの移動）と既存記述の衝突

### R2-09 [致命] Phase AのReducerをPhase BのWm.Hostに置くと、Phase AのSimulationが動かない
- **ドラフト**規則D：「`Reducer.fs`…はWm.Host（またはSimulationと共有するランタイム層）に置く」。依存グラフ：「Wm.Host（Core・Journal・**Windows**・Normalizationに依存。Reducerはここ）」。
- **architecture.md**：§19のPhase Aファイル一覧に`Reducer.fs`がある。§18 Phase Aは「単一ReducerとDesired/Observed/Pending」「最小Simulation」を含む。§17.1の「Linux」のビルド・テスト対象にWindows（Wm.Windows/Wm.Host）は入っていない。§7.1「Reducerループは入力をJournalに書いてから`step`を呼ぶ」。§12.1の「手順を共有し、手段だけ差し替える」（`executeEffectProtocol`と同じ方針、§17のTimer実行器）。
- **問題**：Phase Aの`ScenarioRunner`と`Tests/ConstrainedWindowScenario.fs`は、Linux上で「Journalに書く→`step`→タイマー指令を実行器へ→発火を入力キューへ」というReducerのループを回す必要がある。ReducerをWm.Windowsに依存するWm.Hostに置くと、SimulationからReducerが使えない。結果としてScenarioRunnerが同じループを自前で書き直すことになり、§12.1が防ごうとしている「Simulation側の簡略版で実機と乖離する」が再発する。決定5の縦切り（Simulation用の実行器まで貫通）も、発火を戻すループが無いので完結しない。
- **修正の方向**：括弧書きの「Simulationと共有するランタイム層」の方を、今の時点で確定させる必要がある（例：Contracts・Core・Journalにだけ依存し、Windowsに依存しない`Wm.Runtime`。HostとSimulationがそれを使う）。21章へ先送りしてよいのはそのランタイム層の内部分割だけで、置き場所は先送りできない。

### R2-10 [重大] 依存グラフで、SimulationからNormalizationへの依存が欠けている
- **ドラフト**の依存グラフでは、Wm.Simulationの依存先はWm.Core（の下）だけで、Wm.Normalizationへの辺が無い。
- **architecture.md** §3.1-6「Simulation：…Raw Event層とNormalizer（5.と同じ純粋関数）を経由して観測を生成し」。§12.1「`applyEffect`が生成する`SimEvent`は、Windows Shellと同じNormalizer（純粋関数）を通してから`InputEvent`になる」。
- **問題**：規則Eの根拠そのもの（Normalizerを「Wm.WindowsとWm.Simulationの両方から参照する」）が、グラフに反映されていない。CLI Explain（§18）が使うWm.Diagnostics（§17「`Wm.Diagnostics/ # explain, incident bundle, privacy`」）もグラフに無い。

### R2-11 [重大] 規則Eは§3.1・§4.4・§5.4の文言を変えるのに、How to applyに入っていない。「状態を持たない」は新しい制約である
- **ドラフト**規則E：「「候補条件（可視、非Cloak等）の評価」はNormalizer自身ではなくCore.Windowsが行うと確定する（Normalizerは**状態を持たない**純粋なRaw→InputEvent関数であり…）」。How to applyの反映先は§0・§19・§21だけである。
- **architecture.md**：
  - §3.1-5「Windows Shell：…純粋関数としてのWindows Event Normalizer（Raw Event → 正規化済みInputEvent）」（NormalizerをShell層に置いている）。
  - §4.4 `| NotCandidate // Normalizerの候補条件（可視・非ツール窓・非Cloak等）を満たさない`。
  - §5.4「分類と期限の対象は、**Normalizerの候補条件**（可視、非ツール窓、非Cloak等、12.1のRaw層からの駆動と同じ判定）を満たす窓に限る」。
  - §17のツリーには`Wm.Normalization`が無い。
- **問題**：(1) 候補条件を「Normalizerの」と呼んでいる本文は3か所あり、規則Eはそのすべての意味を変えるのに、反映先に入っていない。§3.1-5（NormalizerはShell層）と§17のツリーも同様である。(2) architecture.mdは「純粋関数」としか言っておらず、「状態を持たない」とは言っていない。純粋な畳み込み（前のRaw Eventの文脈を持つ正規化、例えば表示直後のスタイル変更やUWPのゴーストフレームの判定。§3.1-6が「Raw層からでなければ再現できない」として挙げる類型）を禁じることは、ADR-013の範囲を狭める新しい決定である。採るのであれば、ADR-013の改訂として理由を書く必要がある。

### R2-12 [重大] 決定5の縦切りが、対応するGate項目を取り違えている。combinatorの配置も書かれていない
- **ドラフト**決定5：「Topologyの`ScreenSettle`（debounceで書ける、§18 Gate **#18の`MonitorLifecycle.reconcile`性質テスト**と対応する）」。
- **architecture.md** §15.1 #18「任意の切断列の後に任意の順序で全モニターを再接続すると…最初に退避した時点のモニターに表示される。**`MonitorLifecycle.reconcile`の単体性質**としてPhase Aで検査する」。`ScreenSettle`のタイマーに関わるのは#21（待ちとタイマーの整合）と#3（古いTimer）である。
- **問題**：(1) #18は`reconcile`の単体性質で、タイマーもdebounceも通らない。ScreenSettleの縦切りが実際に検査すべきなのは#21/#3である。(2) `Timers.fs`のcombinatorをどのプロジェクトに置くのか（Contractsか、Coreか）が規則A〜Fのどこにも無い。Contractsに置くならR2-08のゲートの問題に直接当たる。(3) Topologyの1ドメインを動かすだけでも、`MonitorTopologyObserved`を含む`InputEvent`、つまり決定3のContractsのほぼ全層が先に必要になる。「最初の縦切り」の実際の前提が「Contractsを全部書いた後」であることを明記した方がよい。

### R2-13 [重大] Core/Shared.fsの「投影レコード」に何が入るかが未定で、§7.1の「今回の全ドメイン」を読む段と照らすと、ドメインの型の多くがShared.fsへ移る
- **ドラフト**決定4：「`'Ctx`の実体は「先行ドメインの状態そのもの」ではなく「読み取り専用の投影レコード」と確定する」。
- **architecture.md** §7.1：ドメインが読めるのは「(a) この段より前に処理されたドメインの今回の状態、(b) **任意のドメインのstep開始時点の状態**」。段⑦のPlacementは「**今回の全ドメイン、`ScreenPhase`**」を読み、`LayoutContext.TiledWindows`（zipperの順）や`Displays`を必要とする。
- **問題**：投影レコードをShared.fs（全ドメインより前）に置くなら、投影に入る型、たとえば`ScreenPhase`・`Displays`・`Screen`・`Zipper`・`ClassificationState`・`DesiredPresence`は、Shared.fsかContractsに定義されていなければならない。決定4はこれを決めておらず、決定3（Contracts）にも入っていない。投影の中身を決めずに「両立する」と結論するのは早い。最低限、「ドメイン固有の型のうち、他ドメインが投影で読むものはShared.fsで定義する。ドメインのモジュールには、`private`な表現とsmart constructorだけを残す」という規則が要る。
- 細かい点：決定4は「Topology → 残り5ドメイン（強制順序は無い）」としているが、Shared.fsで依存を断つなら、Topologyを先頭に置く理由も同じく無い（MonitorLifecycleを同梱することは順序の理由にならない）。

---

## 5. 引用の誤り

### R2-14 [重大] 規則F・Whyが§17を誤って引用している
- **ドラフト**Why：「規則Fは**§17本文に既に書かれている配置（Timer実行器もContracts）**を、v1ドラフトが落としていたので復元したものである」。規則F：「§17本文が明記する`executeEffectProtocol`と同型の…」。
- **architecture.md** §17の該当箇所：「`TimerCommand`/`Response`/`TimerId`/`RetryState`はWm.Contractsに置き、`TimerCommand`の実行器…はWindows版（Hostのタイマーサービス）とSimulation版（仮想時計）で**共有する純粋なロジックとして書く**（7.1の`executeEffectProtocol`と同じ「手順を共有し、手段だけ差し替える」方針）」。
- **問題**：§17はTimer実行器の**プロジェクトを指定していない**。Contractsにあると書かれているのは`executeEffectProtocol`（§12.1「Contractsの純粋関数`executeEffectProtocol`」）の方である。実行器をContractsに置くこと自体は妥当な選択だが、それは本ADRの新しい決定であって「復元」ではない。根拠を「`executeEffectProtocol`と同じ置き場所を類推して選ぶ」と正しく書き直すこと。また§17は実行器の利用者をHostとしているので、R2-09のランタイム層の決定とも整合させる必要がある。

### R2-15 [軽微] 規則Aの最初の箇条の理由が、列挙した型の一部しか説明していない
- **ドラフト**：「Policy IRの一部（`Management`、`LayoutRef`、`LayoutArgs`、`MonitorSelector`）。`GateInput`/`ValidationInput`が`EffectiveManagement`を参照し、`CoreCommand`が`LayoutRef`/`Management`を参照するため」。
- **問題**：`EffectiveManagement`はPolicy IRの型ではない（§4.4の「Windows ドメイン」）。`MonitorSelector`は、`CoreCommand`・`InputEvent`・`Effect`・`GateInput`のどれからも到達できない（到達するのは`TopologyState.Assignments`と`PolicyIR.WorkspaceAssignments`だけ）。したがって`MonitorSelector`をContractsに置く根拠は、R2-03で問題にした「Checkpoint DTO」経由しか無い。規則CによりCoreはPolicyに依存できるので、`MonitorSelector`をWm.Policyに残しても何も壊れない。

---

## 反映を確認した第1ラウンドの指摘（参考）
R1-1（規則C）、R1-2/R1-3（決定3の先頭部分）、R1-4（記述を削除）、R1-5/R1-6の主旨（`CoreCommand`/`InputEvent`/`Effect`をContractsへ）、R1-7（`EffectiveManagement`をContractsへ）、R1-8/R3-3（規則B。`RegistryManifest`をContractsとする事実も訂正済み）、R1-11の主旨（ReducerをCoreから外した。ただし移動先の問題がR2-09）、R1-12（MonitorLifecycleをTopologyに同梱）、R2-1/R2-2/R6-2（§19の意味の整理と、正本を1つにする運用）、R3-1（Wm.Windowsへの退避案を撤回）、R4-1、R4-2/R4-3（背景とHow to applyに記載）、R5-2（縦切りに使う側のドメインを含めた）。
