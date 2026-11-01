# ADR-019ドラフト 批判的レビュー 第1ラウンド

対象：`docs/adr-019-draft.md`（Opusレビュー前ドラフト）
照合先：`docs/architecture.md` v2.8
範囲：実装順序・プロジェクト割当の決定のみ。Timers.fsを3 combinatorにする判断（DSL/FSM生成を採らない）は対象外とし、扱わない。

重大度：**[致命]**＝このまま採用すると決定(1)の順序でコンパイルが通らない、または確定済みADR/§17と矛盾する。**[重大]**＝決定の根拠が誤っているか、ADRが解くと称する問題を解いていない。**[軽微]**＝事実誤認・表現の問題。

---

## A. 依存順序の主張そのものの誤り（観点1）

### R1-1 [致命] 「Wm.Core（Contractsにのみ依存）」は誤り。CoreはPolicyとLayoutsに依存する
- **ドラフト**：決定1(b)「Wm.Core（Contractsにのみ依存）の内部で：…」、決定1(c)「Wm.Policy / Wm.Layouts：…並行して書く」（Coreの後に置いている）。
- **architecture.md**：
  - §7.1 `val step: RuntimePolicy -> WmState -> EventEnvelope<InputEvent> -> StepResult`。§3.3規約1末尾「`step`が引数として受け取る`RuntimePolicy`（Layout Registryの関数を含む）」。
  - §7.1 段⑤「Windows：分類（5.4）」＝Policy評価器の呼出、段⑦「可視WSごとにLayout→`validate`/`decide`（6.2）→`Desired`の更新」＝Wm.Layoutsの`validate`/`decide`/`invokeLayout`の呼出。
  - §3.4 Impureim Sandwich「純粋な中身（`step`：信念の修正・分類・Layout・PlanValidator・Reconcile）」。
  - 型レベルでも：§4.4 `WorkspaceState.LayoutOverride: LayoutRef option`、`TopologyState.Assignments: Map<WorkspaceId, MonitorSelector>`、`WindowRecord.UserOverride: Management option`。`LayoutRef`/`Management`/`MonitorSelector`は§5.2のPolicy IRの型である。
- **問題**：Core.Windows・Core.Placement・Core.Topology・Core.Workspacesと`WmState.fs`は、Wm.Policy（とPlacementはWm.Layouts）の後でなければコンパイルできない。決定1の(b)→(c)の並びは逆である。
- **なぜ重要か**：ADRの目的は「コンパイルが通らない順序で手を付けるリスク」（ドラフト背景の最終段落）を除くことなのに、ADR自身の順序がまさにそれになっている。

### R1-2 [致命] Wm.Contracts内部の順序で、`NativeCallRecord`が`Observation<'T>`より後にある
- **ドラフト**：決定1(a)「識別子・座標型（4.1/4.2）→ `Belief`/… → `TimerCommand`/… → `NativeCallRecord`/`EffectFailure`/`classify`（ADR-014）→ …」。
- **architecture.md** §4.2：`type Observation<'T> = { Value: Result<'T, NativeCallRecord> // v2.4で変更（ADR-014、1-9対応）…`。
- **問題**：4.2の型（`Observation`、それを使う`WindowSnapshot`/`AppIdentity`）は`NativeCallRecord`を参照する。F#のファイル順では`ErrorTypes.fs`（少なくとも`ErrorSpace`/`NativeCallRecord`）が4.2より前になければならない。さらに`WindowSnapshot`は§4.5の`ObservedPresence`（`Presence: Observation<ObservedPresence> option`）と§4.2の`TitleMatchVector`を参照するので、「4.1/4.2を一塊で最初」という分け方自体が成り立たない。
- **なぜ重要か**：決定1(a)は「依存順」と称しているが、実際の型参照を1つずつ確認して作った順序ではないことを示している。他の行も同じ方法で検証し直す必要がある。

### R1-3 [致命] `Belief`より前に`EvidenceGap`（§11.1）が必要だが、ドラフトのContracts一覧に無い
- **architecture.md** §4.5：`type BeliefSlot<'T> = | Believed of Belief<'T> | Unformed of EvidenceGap list // …11.1のEvidenceGapそのもの`。§11.1で`EvidenceGap`は`WindowId`だけを参照するDUとして定義されている。
- **問題**：決定1(a)の`Belief`/`BeliefSlot`の前に`EvidenceGap`を置く必要がある。§17の配置規則にも`EvidenceGap`の配置先は書かれていない（`Belief`はContractsと明記されているので、`EvidenceGap`もContractsに置かざるを得ない）。ドラフトはこれを導き出していない。

### R1-4 [重大] Timersをドメインより前に置く理由として挙げた具体例が誤っている
- **ドラフト**：背景2つ目の箇条「各ドメインの状態（例：Placementの`Pending`のDeadlineフィールド）がその型を参照するため」。
- **architecture.md** §7.2「PendingRequestは最低限、RequestId/WindowId/IntentEpoch/TargetRect/IssuedAt/Deadline/Retry（3.3の`RetryState`…）/Statusを持つ」。§3.3の擬似型も`Deadline: int64 option`。
- **問題**：`Deadline`は`int64`で、`TimerId`を参照しない。結論（Timersがドメインより前）は正しいが、正しい理由は別のところにある。(a) 各ドメインの`update`の戻り値`Response<'Timer, Effect>`（§4.4 `val update : … -> 'State * Response<'Timer, Effect> * DomainEvent list`）、(b) `HealthState.Reprobes`と`PlacementState.IntegrityReprobes`、および`PendingRequest.Retry`が持つ`RetryState`。
- **なぜ重要か**：実装者がこの根拠を読んで「Deadlineは`int64`だからTimersは後でよい」と判断すると、順序の誤りが再発する。根拠は実際の型参照で書くこと。

### R1-5 [致命] `CoreCommand`をContractsに置くと、Contractsが依存しない制約を破る
- **ドラフト**：決定1(a)「… → `CoreCommand` → `GatedEffect`」（Contractsの中）。
- **architecture.md** §4.4 `CoreCommand`：`| SwitchWorkspace of WorkspaceId`、`| ChangeLayout of LayoutRef`、`| SetManagement of WindowId * Management`。`WorkspaceId`は§4.4の「Workspaces ドメイン」の見出しの下で定義、`LayoutRef`と`Management`は§5.2のPolicy IRで定義されている。§17の配置規則は`CoreCommand`の置き場所を明記していない。
- **問題**：`CoreCommand`をContractsに置くなら、`WorkspaceId`・`LayoutRef`・`LayoutArgs`・`LayoutId`（すでに4.1）・`Management`もContractsへ移す必要があり、これは§5.2/§17（「`PinnedPolicy`/`DeployablePolicy`はWm.Policy」等）の型の所在の変更になる。ドラフトはこの移動を述べず、`CoreCommand`がContractsに置けるものとして扱っている。
- **なぜ重要か**：同じ連鎖は`InputEvent`にも及ぶ（R1-6）。これはドラフトが挙げていない、Phase Aで最大の配置上の結び目であり、ADR-019が明示的に決めるべき事項である。

### R1-6 [致命] `InputEvent`/`Effect`/`EventEnvelope`/`StepResult`の配置が決定1のどこにも無い
- **architecture.md**：
  - §7.1 `InputEvent`は`UserCommand of CoreCommand`、`TimerElapsed of TimerId`、`MembershipHintsLoaded of (WindowId * WorkspaceId * Management option) list`を含む。
  - §3.1「1. Domain / Contracts：ID、座標、イベント、Effect、Journal上の共通型」、§17のツリーのコメント「`Wm.Contracts/ # OS非依存の識別子、座標、イベント、Effect`」。
  - §6.2 `type GatedEffect = internal GatedEffect of Effect`（Contracts）。`Effect`は`Reprobe of WindowId * BeliefKey`を含む。
  - §9.2 DTOのタグ付け規約の対象一覧「`InputEvent`の各ケース、`Effect`、`CoreCommand`、`ErrorSpace`、`Management`、`LayoutArgs`、`WindowPredicate`…」。
- **問題**：(1) `Effect`がContractsの順序に無いのに`GatedEffect`だけがある。`GatedEffect`は`Effect`を包み、`Effect`は`BeliefKey`の後に来る必要がある。(2) Normalizer（Shell/Simulation）とJournalが共有する`InputEvent`はContracts以外に置けない（§3.1）。ところが`InputEvent`は`Management`（Policy IR）と`WorkspaceId`を参照するので、R1-5と同じくPolicy IRの一部の型をContractsへ移すことが前提になる。(3) `StepResult`は`WmState`を持つのでContractsには置けず、Coreに置くことになる。これは明記すべきである。
- **なぜ重要か**：Contractsの「一番下の層」の中身として最も参照される型が抜けている。この一覧から実装を始めると、最初のPRの段階で行き詰まる。

### R1-7 [致命] `GateInput`（Contracts）が`EffectiveManagement`（4.4の「Windows ドメイン」）を参照している
- **architecture.md** §6.2：「`// Wm.Contracts。…` `type GateInput = { … Management: Map<WindowId, EffectiveManagement> …; Monitors: MonitorSnapshot list …}`」。`EffectiveManagement`は§4.4の`// ===== Windows ドメイン =====`の中で定義されている。§6.2の`ValidationInput`（Wm.Layouts）も`Management: Map<WindowId, EffectiveManagement>`を持つ。
- **問題**：§17の規則に従えば`EffectiveManagement`はCore.Windowsの型に見えるが、ContractsとLayoutsがCoreを参照することはできない。したがって`EffectiveManagement`はContractsになければならない。ドラフトは`GatedEffect`をContractsの最後に置くとだけ言い、`GatedEffect.gate`がどのドメイン型を必要とするかを確かめていない。
- **なぜ重要か**：§4.4のコードブロックで「ドメインの見出しの下にある型」と「実際に置けるプロジェクト」が食い違う箇所を体系的に洗い出すことが、このADRに本当に求められている作業である。ここには`EffectiveManagement`のほか、`WorkspaceId`、`ShownBy`、`MoveCause`、`ClassificationView`、`RuntimePolicy`（R1-9、R1-10）が該当する。

### R1-8 [重大] Wm.Layoutsはすでに型の上でPolicy IRに依存している。「止める条件」は初日に発火する
- **ドラフト**：決定1(c)「実装中にLayouts側がPolicy IRの型を要することが分かった場合は、本ADRの前提が崩れた印なので実装を止めてアーキテクチャ側を見直す」。
- **architecture.md** §5.6 `type LayoutFunction = LayoutArgs -> LayoutContext -> Result<Placement list, LayoutError>`。`LayoutArgs`は§5.2のPolicy IRの型（`| NoArgs | MasterRatio of decimal | ColumnCount of int`）。`LayoutImplementation`も`Id: LayoutId`を持つ。
- **問題**：「分かった場合」ではなく、すでに分かっている。`LayoutRegistry.fs`の最初の1行で、`LayoutArgs`をContractsへ移すか、Layouts→Policyの依存を認めるかを選ぶことになる。ADRが事前に解ける問題を、実装中に止める条件として先送りしている。
- **なぜ重要か**：観点3にも関わる。Policy↔Layoutsの方向の問題は、すでに「両方とも`LayoutArgs`/`LayoutRef`/`Management`/`MonitorSelector`をContractsから読む」か「Layouts→Policy」のどちらかを選べば解ける状態にある（R3-2）。

### R1-9 [重大] ドメイン間の`'Ctx`と`DomainEvent`の型の置き場所が無く、「モジュール同士は直接参照しない」と両立する順序が示されていない
- **ドラフト**：決定1(b)「各ドメインが…持ち、モジュール同士は直接参照しない」。6ドメインを順序の無い集合として列挙している。
- **architecture.md**：
  - §4.4 `val update : 'Input -> 'Ctx -> 'State -> 'State * Response<'Timer, Effect> * DomainEvent list`。`'Ctx`は「先に処理された他ドメインの状態の読み取り専用の値」。
  - §7.1の表：Topologyは「step開始時のWorkspaces（窓の所属）」を読み、Placementの段⑦は「今回の全ドメイン」を読む。
  - DomainEventのペイロード：`WorkspaceShown (ws, ShownBy)`（`ShownBy`はTopologyの見出しの下で定義）、`MoveAttributed (w, cause)`（`MoveCause`）、`WindowClassified (w, mgmt, home)`（`EffectiveManagement`）。
  - §17.1「Coreのドメインのモジュール同士が直接参照し合わないことのアーキテクチャテスト」。
- **問題**：`'Ctx`が他ドメインの状態型そのもの（例：`WorkspaceSet`）であれば、F#のファイル順でCore.TopologyはCore.Workspacesより後になり、§17.1のテストにも違反する。`'Ctx`が投影レコードであれば、その投影型と`DomainEvent`・`ShownBy`・`MoveCause`を、全ドメインより前の共有ファイル（例：`Core/DomainEvents.fs`）に置く必要がある。§19にも決定1にもそのファイルは無い。
- **なぜ重要か**：これは実装順序のADRがまさに決めるべき内容で、決めないと最初に書いた2番目のドメインで、この結び目をその場しのぎの方法で解くことになる。

### R1-10 [重大] `RuntimePolicy`と`ClassificationView`の配置・順序が無い
- **architecture.md**：§7.1 `step`の第1引数`RuntimePolicy`（定義なし。§3.3「Layout Registryの関数を含む」）。§14 `val link : RegistryManifest -> PinnedPolicy -> Result<ActivePolicy, LinkError>`。§4.5「7.1の段⑤で窓ごとに`ClassificationView`（述語が読む信念だけを各ドメインから集めた読み取り専用の値）を…評価器に渡す」。
- **問題**：`ClassificationView`はCoreが組み立てて`PolicyEvaluator`が受け取るので、Policyより下（Contracts、またはPolicy自身）に置く必要がある。`RuntimePolicy`は`ActivePolicy`（Policy）とRegistry（Layouts）の両方を束ねるので、Core（またはその上）に置くしかない。どちらもドラフトの順序に現れない。

### R1-11 [重大] `Reducer.fs`はWm.Coreに置けず、Journalより前にも置けない
- **ドラフト**：決定1(b)「… → `WmState.fs` → `Reducer.fs`」（Wm.Coreの中、(d)のJournalより前）。
- **architecture.md**：
  - §19 `Reducer.fs（入力キューの処理とJournalへの書き込み順序。…）`。
  - §7.1末尾「Reducerループは「**入力をJournalに書いてから`step`を呼ぶ**」順序を固定する」。
  - §7.1 例外の3層「スレッド単位の最上位ハンドラ（Reducerループ、…）で捕捉し、`InternalFault`…」。
  - §17「JournalWriterのI/OはShell側」。§3.4「Core（Wm.Core、Wm.Policy、Wm.Layouts）は…その他の環境状態を決して読まない」。
- **問題**：Reducerはキューを回し、Journalへの書き込み（I/O）を順序付け、例外を捕捉するループである。これは§3.4と§17.1の共通ゲート（Coreのアセンブリが環境APIを参照しない）に照らしてWm.Coreには置けない。置き場所としてはWm.Host、またはSimulationと共有するランタイム層が候補になる。またJournalの型を参照するので、(d)のJournalより後になる。
- **なぜ重要か**：決定3の「各ステップで共通ゲートを確認する」を守ると、Reducer.fsをCoreに入れた時点でゲートが落ちる。ADRの決定同士が衝突している。

### R1-12 [重大] `MonitorLifecycle.reconcile`の位置が根拠なく「全ドメインの後」になっている
- **ドラフト**：決定1(b)「6ドメイン → `MonitorLifecycle.reconcile` → `WmState.fs`」。
- **architecture.md** §4.4「Topologyドメインの純粋関数`MonitorLifecycle.reconcile : TopologyState -> MonitorSnapshot list -> TopologyState * DomainEvent list`」。§4.4 `type TopologyState = private { … }`。§7.1の段⑤ではTopologyの中で`MonitorLifecycle.reconcile`を呼ぶ。
- **問題**：(1) `reconcile`はTopologyだけに依存し、Topologyの段⑤から呼ばれるので、正しい位置は「Core.Topologyと同じファイル（またはその直前）」であって、全ドメインの後ではない。(2) `TopologyState`の表現が`private`なので、別ファイル・別モジュールの`MonitorLifecycle`は、Topologyが公開した構築関数経由でしか状態を作れない。`MonitorLifecycle.fs`を独立ファイルにするなら、Topologyの公開APIの範囲を決めておく必要がある。
- **なぜ重要か**：§15.1 #18（Phase AのGate項目）は`reconcile`の単体の性質なので、ドラフトの「ドメインが1つ増えるごとに性質テスト」という方針（決定3）に従えば、Topologyの直後に通すべき最初期のテストである。今の並びはそれと合わない。

---

## B. 確定済みADR／§17との矛盾（観点2）

### R2-1 [致命] 「§19は説明順であって実装順ではない」という再解釈は、§18・ADR-011の明文と矛盾する
- **ドラフト**：代替案1「§19のリストは「読者が中核状態から理解する」ための説明順であり、実装順ではないと判断する」。How to apply「§19のファイルリストの表記順はそのまま残し「概念の理解順」として扱う」。
- **architecture.md**：
  - §19「第一の縦切り（Phase A）として次のファイル相当を**優先実装**する」。
  - §18 Phase A 1つ目の項目「WmStateをドメインごとの型…、CoreCommand（ADR-011）を**最初に固定する。Policy IRより先にこれを決める**」。
  - ADR-011「…CoreCommand）をCoreの第一級の状態として4章で定義する。**Policy IRや診断機構より先にこれを固定する**」。
  - §4.4冒頭「Policy IR・Journalスキーマ・Snapshot形式・Incident Bundleは、この状態モデルを前提に設計し直す」。
- **問題**：§19の順序は説明のための便宜ではなく、ADR-011が定める優先順位を反映したものである。ドラフトはADR-011の「先に固定する」を書き換えるのに、それを改訂として明示していない（§0の表にも触れていない）。正しい整理はおそらく、「**固定する**（型と不変条件を決める）」順と「**コンパイルする**」順を分け、前者はADR-011どおり中核状態が先、後者はR1-1/R1-5のとおりPolicy IRの一部の型をContractsに下ろしたうえでの依存順、とすることだろう。
- **なぜ重要か**：§23によればAIエージェントはarchitecture.mdを正本として読む。§19の文言が「優先実装」のまま別ファイルで「理解順」と読み替えると、正本と正本の読み替えが食い違う。

### R2-2 [致命] 決定2（combinator優先、ドメインは後回し）がADR-011／§18と逆になっている
- **ドラフト**：決定2「最初の縦切りは`Timers.fs`の3 combinator…を…書くことから始める。…ドメインモジュール（`Core.Placement`等）は、combinatorを実際に使うまで書かない」。
- **architecture.md**：R2-1で引いた§18/ADR-011の「最初に固定する」。§18 Phase Aの3つ目の項目「待ちとタイマーのデータ表現（3.3、ADR-017）」は中核状態（1つ目）・信念（2つ目）の後にある。
- **問題**：combinator vs DSLの判断自体には触れない。触れるのは順序だけである。中核状態より前にタイマーの補助関数を最初の縦切りにするのは、ADR-011の優先順位を変えることになる。変えるのであれば、その旨と理由をADRに書く必要があるが、ドラフトは「直前のセッションで合意済み」とだけ書き、根拠を示していない。

### R2-3 [重大] `JournalTypes`をWm.Journalに置くのは§17の配置規則と矛盾する
- **ドラフト**：決定1(d)「Wm.Journal（`JournalTypes` → `JournalWriter`）」。
- **architecture.md** §17「JournalWriterのI/OはShell側、**Journalのイベント型は契約側に置く**」。§9.2 DTOのタグ付け規約は`InputEvent`/`Effect`等のContractsの型をJournalに出す前提である。
- **問題**：§17はイベント型をContractsに置くと明記している。一方、§17のツリーには`Wm.Journal/ # writer, reader, checkpoint, projections`ともあり、architecture.md自身が「writerはJournalプロジェクト」と「writerのI/OはShell側」の間で揺れている。ドラフトはこの揺れを指摘せず、片方を黙って選んでいる。ドラフトは§17の配置規則を「実装順に翻訳する」と称しているのだから（Why第2項）、規則と食い違う選択をするならその旨を明記すべきである。
- **なぜ重要か**：Phase Aは「最小Journal」（§18）を含む。`JournalEntry`の`Payload`の型がContractsに無いと、Simulation（ScenarioRunner）がJournalを書く経路で依存がContracts→Journal→Simulationと分岐し、Journal型の重複定義（ドラフト背景が恐れていること）がまさに起きる。

### R2-4 [重大] Timer実行器と`executeEffectProtocol`が決定1から抜けている
- **architecture.md**：§17「`TimerCommand`の実行器（同じIDの再始動・取消を処理する小さな表）はWindows版…とSimulation版…で共有する純粋なロジックとして書く（7.1の`executeEffectProtocol`と同じ「手順を共有し、手段だけ差し替える」方針）」。§12.1「Contractsの純粋関数`executeEffectProtocol`」。§19「Timers.fs（3.3の`TimerId`/`TimerCommand`/`RetryState`と**Simulation用の実行器**）」。§18 Phase A「`TimerCommand`の実行器（Simulation版）」。
- **問題**：ドラフトの決定2はTimers.fsの中身を「識別子＋`TimerId`/`TimerCommand`/`Response`/`RetryState`」と3 combinatorだとしており、§19と§18が明示しているTimers.fsの構成要素（実行器）を落としている。`executeEffectProtocol`（と`ProbeOutcome`）は§19にも決定1にも無い。
- **なぜ重要か**：§15.1 #21（Phase AのGate項目）の後半「待ちが存在しない／`Deadline`が入力時刻より後の`TimerElapsed`は状態を変えない」は、実行器（取消後の発火がキューに残る競合）が無いとSimulationで検査できない。

### R2-5 [軽微] 決定3の「共通ゲートをWm.Contractsができた時点で確認する」は、§17.1のゲートの対象範囲とずれている
- **ドラフト**：決定3「§17.1の共通ゲート（クロージャ非含有・時刻/乱数API非参照のアーキテクチャテスト）を…Wm.Contractsができた時点…で確認する」。
- **architecture.md**：§17.1「**Coreの状態型**がクロージャ・`Async`/`Task`を含まないこと」「**Coreのアセンブリ**が環境の時刻・乱数のAPI…を参照しないこと」。§3.4「Core（Wm.Core、Wm.Policy、Wm.Layouts）」。§3.3「**`WmState`から到達可能な全ての型**とCheckpointのDTO」。
- **問題**：(1) §3.4の「Core」の定義にWm.Contractsは含まれない。Contractsにcombinatorや`classify`・`executeEffectProtocol`・Normalizer（ドラフト決定4）といったロジックを置くなら、時刻API禁止のゲートをContractsへ拡大することを明示的に決める必要がある。決めなければ、ロジックをContractsへ置くことがゲートの抜け道になる。(2) クロージャ非含有のテストは「`WmState`から到達可能な型」で定義されているので、`WmState.fs`ができるまでは定義どおりには実行できない。ドメインごとに確認するなら、テストの定義（例：各ドメインの状態型を起点に到達可能な型）を別途決める必要がある。

---

## C. 「未決として残す」項目は、すでに解ける（観点3）

### R3-1 [致命] `WindowEventNormalizer.fs`の退避先「Wm.Windowsへ移す」は、ADR-013と§3.1に反する
- **ドラフト**：決定4「実装中にWm.Windows固有の型（HWND等）への依存が生じた場合はWm.Windowsへ移し、本ADRを更新する」。決定1(d)では同じファイルを「Wm.Simulation（`WindowEventNormalizer`—配置未確定、本ADRでは暫定的にWm.Contractsに置く—→ …）」と、Simulationの並びの中に置いている。
- **architecture.md**：
  - §3.1-6「Simulation：…Raw Event層とNormalizer（5.と同じ純粋関数）を経由して観測を生成し」。
  - §17.1「Linux：F# Core、Policy、Layout、…Simulation、Replay…のビルドとテスト」。
  - §19「WindowEventNormalizer.fs（純粋関数。Shell/Simulation両方から参照する、ADR-013）」。
  - §4.1「WindowIdは…生の`HWND`ではない。Shellが`HWND → WindowId`を対応付け」。
  - §17のエラー型の配置「`NativeCallRecord`/`EffectFailure`/`classify`はWm.Contractsに置く（Windows固有の型をCoreが参照しないため、**ADR-013のNormalizerと同じ理由**）」。
- **問題**：(1) NormalizerをWm.Windowsへ移すと、Linuxでビルドする必要のあるWm.SimulationがWm.Windowsを参照することになり、§3.1-6・§17.1・ADR-013が同時に壊れる。HWNDへの依存が生じたら、それは「移す」理由ではなく、Normalizerの入力（Raw Event）の設計が誤っているという合図である。(2) §17の「ADR-013のNormalizerと同じ理由」は、Normalizerが「Windows固有の型を持たない、共有される場所」にあることを前提にしている。置き場所の候補は「Wm.Contracts」か「Contractsだけに依存する専用プロジェクト」の2つに絞られ、Wm.Windowsは候補にならない。(3) 決定1(d)はSimulationの並びの中にこのファイルを置いているが、決定4ではContractsに置くと言っている。Contractsに置くならコンパイル順は(a)の中になる。
- **未決のまま残せない理由**：Raw Eventの型（`SimEvent`がNormalizerを経て`InputEvent`になる、§12.1）をどこに置くかがここで決まり、VirtualWorld.fsの最初の型定義がそれに依存する。

### R3-2 [重大] Contracts案とアセンブリ独立案のどちらを採るかは、文書内の根拠ですでに選べる
- **architecture.md**：§9.5「Snapshotには…Core/Policy/Registry/**Normalizer**の成果物識別子を持たせる」。§9.2/§7.1のコメント「`normalizerSchemaVersion`はShell側のADR-013 Normalizerの版であり、`classify`とは無関係」「`classify`は…`CoreBuildId`でピン留め」。
- **問題**：Normalizerは`CoreBuildId`とは別の成果物識別子（`normalizerSchemaVersion`）を持つことが§9で確定している。ContractsにNormalizerを入れると、Contracts（`classify`を含み、`CoreBuildId`でピン留めされる側）とNormalizerの版が1つのアセンブリに同居することになり、2つの識別子を分けて持つ意味が薄れる。これは「Contractsだけに依存する独立プロジェクト（例：Wm.Normalization）」を支持する根拠になる。少なくとも、ADRはこの根拠に照らして案を選ぶべきで、「暫定」として先送りする理由は無い。
- **補足**：§4.4の「Normalizerの候補条件（可視、非Cloak等）は、`ClassificationState = NotCandidate`の窓についてだけ評価する」（R5-2、§4.4本文）は、候補条件の評価がCoreの状態（`ClassificationState`）に依存することを意味する。Normalizerを状態の無いRaw→InputEventの関数とするなら、「候補条件」の判定はNormalizerではなくCore.Windowsの側にあるはずである。architecture.md自身の揺れだが、Normalizerの置き場所を決めるADRなら、どちらで判定するのかを確定させる必要がある。

### R3-3 [重大] Policy/Layoutsの依存方向はすでに確定しており、「未決」ではない。ドラフトの言い換えも誤っている
- **ドラフト**：背景「§17本文は「Wm.PolicyはWm.Layoutsに依存しない」という片方向しか明記しておらず」。決定1(c)「現状の「PolicyはLayoutsの`RegistryManifest`型だけを読む片方向」を崩さない前提で」。How to apply「Wm.PolicyとWm.Layoutsの依存方向…は、21章の未決事項表に追記する」。
- **architecture.md** §14「`analyze`はLayout Registryへの関数オブジェクト依存ではなく、`RegistryManifest`（`(LayoutId, ApiVersion, Digest) list`、**Contractsの型**）だけを受け取る。これにより**Wm.PolicyはWm.Layoutsに依存しないまま**5.5の未登録検査ができる（17章の依存方向を保つ）」。
- **問題**：(1) `RegistryManifest`はContractsの型であり、「Layoutsの型」ではない。ドラフトの決定1(c)は事実を取り違えている。(2) Policy→Layoutsは§14で明示的に禁止されている。(3) 逆方向（Layouts→Policy）は、R1-8のとおり`LayoutArgs`を巡ってすでに型の上で問題になっている。残る選択肢は「`LayoutArgs`/`LayoutRef`（とR1-5の`Management`/`MonitorSelector`）をContractsに下ろし、Policy・Layoutsは互いに独立」と「Layouts→Policyを許す」の2つだけである。前者は§14の「Contractsの型を介して独立を保つ」と同じ手法なので、文書の既存方針から導ける。21章へ先送りする必要は無い。
- **追加の欠落**：決定1(a)に`RegistryManifest`が無い。§14によりContractsに置くことが確定している型である。

---

## D. §19のファイルの欠落・数え間違い（観点4）

### R4-1 [軽微] §19のファイル数は26ではない
- **ドラフト**：背景「Phase Aの実装対象ファイルを26個」。
- **architecture.md** §19：1704〜1727行。1705行に6ファイル（Topology/Workspaces/Focus/Windows/Health/Placement）があるので、本体ファイルは28、`Tests/ConstrainedWindowScenario.fs`を含めて29になる。
- **なぜ重要か**：この数を基準に「全ファイルが割り当てられたか」を確認すると、3つ漏れても気づかない。ドラフトの決定1は結果的に29ファイル全てに触れているが、それは数え方とは無関係である。

### R4-2 [重大] §19自体に無いファイル（実装に不可欠）を、ドラフトも補っていない
- §19には、識別子・座標（§4.1/4.2）、`Effect`/`InputEvent`/`EventEnvelope`（§7.1）、`EvidenceGap`（§11.1）、`RegistryManifest`（§14）、`ProbeOutcome`/`executeEffectProtocol`（§7.1/§12.1）、`DomainEvent`/`'Ctx`の共有型（R1-9）、`StepResult`/`RuntimePolicy`（§7.1）、DTO/`ofDto`（§9.2「規則はPhase Aの最初のofDtoから適用する」）、Normalizer入力のRaw Event型（§12.1）に対応するファイルが無い。
- ドラフトは§19の欠落を「プロジェクト割当が無い」「順序が依存順でない」の2点だと述べているが、§19には**ファイルそのものが無い**という3つ目の欠落がある。しかもそれがContractsの大部分を占める。決定1(a)は「識別子・座標型（4.1/4.2）」を型の名前で挙げるだけで、ファイルとしての割当をしていない。
- **なぜ重要か**：「§19の各ファイルをどのプロジェクトに割り当て、どの順で書くか」を正本にするADRなのに、最も基礎になるファイル群が正本に載らない。

### R4-3 [重大] §18 Phase Aの「CLI Explain」が、§19にもドラフトにも無い
- **architecture.md** §18 Phase A「最小JournalとCLI Explain（9.1の方針：判断は永続化せずReplay再構成、B-1）」。§17のツリー「`Wm.Cli/ # inspect, replay, simulate, export`」。
- **問題**：Phase Aのスコープに含まれる成果物でありながら、§19にファイルが無く、ドラフトの順序にも現れない。Phase Aのゲート(2)「RuleId/RequestIdで追跡できること」を何で確かめるのかにも関わる。ADRが「§19をPhase Aの実装順の正本で置き換える」と言う以上、§18のPhase Aの項目と§19の対応も取る必要がある。

---

## E. 最初の縦切りの実現可能性（観点5）

### R5-1 [重大] 「最小限の支持型」と称する集合は、TimerIdの定義の時点で最小にならない
- **ドラフト**：決定2「Wm.Contractsの最小限の支持型（識別子＋`TimerId`/`TimerCommand`/`Response`/`RetryState`）の上に書く」。
- **architecture.md** §3.3：`PlacementTimer = PendingDeadline of RequestId | IntegrityReprobe of WindowId`、`TopologyTimer`、`FocusTimer`、`HealthTimer`、`WindowsTimer`、`TimerId = Placement of … | Windows of … | …`。`Response<'TimerId, 'Action> = { Actions: 'Action list; …}`。
- **問題**：識別子（`RequestId`/`WindowId`）だけで`TimerId`は定義できるので、型の面では成り立つ。しかし、combinatorが現実に扱う対象は「ドメインの状態に`Deadline`を書き、同時に`StartTimer`を返す」（§3.3「期限の正本はCore状態側にある」）ことであり、その状態の側（`PendingRequest`、`ScreenPhase`、`SwitchSettle`）が無い。`Response<_, 'Action>`の`'Action`の具体型（`Effect`）も無い。結局combinatorのテストは、汎用の型パラメータに対するものになるか、テスト専用の仮の状態型に対するものになる。後者は、ドラフトが代替案2で退けた「スタブと本実装の二度書き」そのものである。
- **なぜ重要か**：「最初の縦切り」と言いながら、縦（Contracts→Core→Simulation→テストシナリオ）を貫いていない。縦切りであれば、少なくとも1つのドメインの1つの待ち（例：Topologyの`ScreenSettle`のdebounce→`MonitorLifecycle.reconcile`→#18）を、Simulationの実行器まで通す必要がある。

### R5-2 [重大] 決定2の原則が決定2自身に適用されていない
- **ドラフト**：決定2「ドメインモジュール（`Core.Placement`等）は、combinatorを実際に使うまで書かない——抽象を使う側のコードが無いまま太らせない、という既存方針をPhase A全体の進め方にも拡張する」。同じ段落で「`response { }` CEは域ドメインコードが実際に書かれて初めて要否を判断する」。
- **問題**：combinatorは抽象であり、使う側はドメインモジュールである。「使う側が無いまま抽象を太らせない」を守るなら、ドメインモジュール（の最初の1つ）がcombinatorより先か、同じPRになるはずだ。CEについては「使う側が書かれてから」と言い、combinatorについては「使う側を後で書く」と言っている。これは同じ原則の逆向きの適用である。また「combinatorを実際に使うまで書かない」と「combinatorはドメインが使う」を合わせると、ドメインを書き始める契機が定義されない（循環）。
- 細かい点：「域ドメインコード」は誤字と思われる。

### R5-3 [軽微] 決定3の性質テストの番号がPhase AのGateと一致しない
- **ドラフト**：決定3「`WmState.fs`での合成後にドメイン横断の性質テスト（#17/#22等）を追加する」。「Phase A全体のGate（§18、性質ベーステスト#1-8,11-13,16-21）」。
- **architecture.md** §18 Phase AのGate「15.1 #1〜#8、#11〜#13、および…#16〜#21」。§15.1 #22は「1 `step`の処理順序」で、Phase AのGateに**含まれない**。
- **問題**：#22を例に挙げつつ、Gateの範囲として別の集合を引用しており、内部で食い違っている。また、ドメインをまたぐGate項目として本当に挙げるべき#16（(F2)はFocus×Workspacesのドメイン横断の性質）が抜けている。#22（処理順序）をPhase Aで検査したいなら、それはGateの変更であり、§18を改訂する話になる。

---

## F. 粒度・構造（観点6）

### R6-1 [重大] ADRが型の一覧として書かれており、型を追加するたびに古くなる。それでいて肝心の判断は書かれていない
- **ドラフト**：決定1(a)は型名を20個近く列挙して順序を与えている。
- **問題**：R1-2・R1-3・R1-6・R1-7が示すとおり、型の列挙は不完全で、しかも§4.4/§7.1のコードブロックに型が1つ増えるたびに古くなる。一方で、ADRとして残すべき**判断**は、本来は次の数行で済む。
  1. Contractsに下ろす型の基準（「`InputEvent`/`Effect`/`GateInput`/`ValidationInput`/DTOから到達可能な型はContracts」）と、それによってContractsへ移るPolicy IRと§4.4の型（`Management`、`LayoutRef`、`LayoutArgs`、`MonitorSelector`、`WorkspaceId`、`EffectiveManagement`、`ClassificationView`、`ObservedPresence`等）。
  2. プロジェクト間の依存グラフ（Contracts ← Policy, Layouts ← Core ← Simulation/Journal/Host、Policy⊥Layouts）。
  3. Core内のドメイン共有型ファイル（`'Ctx`投影・`DomainEvent`）の存在と、ドメインのファイル順。
  4. Normalizer・Reducer・JournalWriterの置き場所。
  
  型ごとの順序は、F#コンパイラが最初のビルドで強制するので、ADRに書く必要は無い。
- **なぜ重要か**：今の粒度は「すぐ古くなる詳細」と「書かれていない判断」の両方を抱えており、背景で述べた問題（§17の規則が§19と対応付けられていない）を、3つ目の文書で同じ形のまま再生産している。

### R6-2 [重大] 正本を2つにする運用（§19は「理解順」、ADR-019が「実装順」）は、ドリフトを制度化する
- **ドラフト**：How to apply「§19のファイルリストの表記順はそのまま残し「概念の理解順」として扱うが、実装順の正本は本ADR」。
- **architecture.md**：§0の表がADRの正本になっている（ADR-001〜018は全てarchitecture.md §0に載っている）。§17のツリーには`docs/adr/`があるが、そのディレクトリは実在しない（リポジトリの`docs/`配下に`adr/`は無い）。§23は、AIエージェントへの引き継ぎ規則をarchitecture.md内に置いている。
- **問題**：ドラフトは、§0の表への追記も、§19の見出し「優先実装する」の文言変更も、How to applyに含めていない（§21への追記だけを挙げている）。正本の§19が「優先実装する」と書いたまま、§0に載らない別ファイルが「実装順の正本」を名乗ると、architecture.mdだけを読むエージェント（§冒頭のAudience）は§19に従う。
- **提案ではなく指摘**：ADR-011〜018と同じく、§0にADR-019の行を追加し、§19の各ファイルにプロジェクト名を付記し、§19の見出しの「優先実装」の意味（固定する順なのか、コンパイル順なのか）を本文で定義し直す必要がある。正本を1つにしなければ、ADRが名指しした問題は解決しない。

### R6-3 [軽微] 「Why」の第1項は、このADRの判断ではなく範囲の説明になっている
- **ドラフト**：Why第1項は、combinator判断が別セッションで合意済みであり、本ADRが決めるのはそれではない、と述べるだけである。
- **問題**：これは範囲の説明であって、「なぜこの順序・この割当なのか」の理由になっていない。Whyに書くべきは、例えばR6-1の1〜4それぞれの根拠（§3.1、§14の手法の踏襲、§17.1のドメイン非参照テスト、ADR-013のLinuxビルド要件）である。
