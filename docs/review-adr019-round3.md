# ADR-019ドラフトv3 批判的レビュー 第3ラウンド

対象：`docs/adr-019-draft.md`（v3）
照合先：`docs/architecture.md` v2.8、`docs/review-adr019-round1.md`、`docs/review-adr019-round2.md`
範囲は前回までと同じで、combinator vs DSLの判断は扱わない。

**結論：未収束。** 第2ラウンドで指摘したコンパイル順の誤り（R2-01/R2-02）、Checkpointを通じた過剰な引き込み（R2-03）、Reducerの置き場所（R2-09）、Gate番号の取り違え（R2-12）、§17の誤った引用（R2-14）は、正しく直っている。しかし新しく書いた部分に、致命的な問題が2件ある。
- **`'Ctx`の多相化（決定4）**：型パラメータのままでは、ドメインは`'Ctx`の中身を読めない。R2-13の循環は、解消されたのではなく見えなくなっただけである（R3-01）。
- **Wm.Journalの依存関係（決定1）**：「Contractsのみに依存」と書きつつcheckpointの実装を持たせているが、Checkpoint DTOはドラフト自身がWm.Coreに置いている（R3-02）。

重大の問題は5件ある。条件(a)(b)が「この2条件のどちらかを満たす型**だけ**」と言いながら、実際にはそれ以外の根拠でContractsに置いた型が多い（R3-03）。DTOの層10とJournalの型の層9に、PolicyやLayoutsの型を持つものが入っている（R3-04、R3-05）。§17のツリー、§3.1、§3.4、§17.1を書き換えるのに、その反映がHow to applyに無い（R3-06）。縦切りの前提も過小に書かれている（R3-07）。

---

## 致命

### R3-01 [致命] `'Ctx`を型パラメータのまま「多相に保つ」と、ドメインは`'Ctx`から何も読めない。循環は解消されていない
- **ドラフト**決定4：「`'Ctx`は各ドメインごとの型パラメータであり、ドメインモジュール自身は他ドメインの具体的な型を一切参照しない（多相のまま定義する）。従って6ドメインの間に強制順序は無く…`'Ctx`の具体的な値は、`WmState.fs`の`step`が…その場で組み立てる」。Why「§7.1の`update`の型シグネチャ自体がすでに型パラメータとして書かれている」。
- **architecture.md**：
  - §4.4「`// 概念上の契約。'Inputはそのドメインが受け取る入力（親がInputEventから振り分ける）、'Ctxは先に処理された他ドメインの状態の読み取り専用の値（**関数ではない**）`」。
  - §7.1の表：Topologyの段②は「step開始時のWorkspaces（窓の所属）」を読む。Placementの段⑦は「今回の全ドメイン、`ScreenPhase`」を読み、可視WSごとにLayoutを呼ぶ（`Displays`の`current`/`visible`、zipperの順、`EffectiveManagement`、`Desired`/`ObservedPresence`）。
- **問題**：
  1. §4.4の`val update : 'Input -> 'Ctx -> …`は、「全ドメインが同じ形の関数を持つ」ことを示す**概念上の契約**の型変数である。各ドメインの`update`が`'Ctx`について汎用（parametric）であることを意味してはいない。'Inputも同じ形で書かれているが、ドラフトもそれを多相にするとは言っていない。
  2. F#でパラメトリックな`'Ctx`は、関数の中から一切検査できない。読むにはフィールドアクセスが必要で、それには具体型が要る。アクセサ関数を渡す方法は§4.4が「関数ではない」として禁じている（関数を渡せば、§3.3規約1の検査対象外であっても、`'Ctx`はクロージャを運ぶ値になる）。SRTP（`inline`とメンバー制約）なら書けなくはないが、ドラフトはそれに触れておらず、6つの更新関数全体を`inline`にするのはこのADRで決める内容を超える。
  3. したがって実際には、各ドメインは**自分専用の具体的なCtxレコード**（例：`PlacementCtx`）を自分のモジュールに定義することになる。そのフィールドの型は、`Displays`/`Screen`/`ScreenPhase`（Topologyの型）、zipperの並び（Workspacesの型）、`ClassificationState`（Windowsの型）を含む。ここでR2-13とまったく同じ問題が戻る。他ドメインが読む型を、どこで定義するのか。
- **なぜ致命か**：「6ドメインの間に強制順序は無い」という結論も、§17.1の「ドメイン同士は直接参照しない」と両立するという主張も、この前提の上に立っている。実際に書けるのは次の2案のどちらかである。(i) 他ドメインが読むドメイン型（`Displays`、`Screen`、`ScreenPhase`、`DesiredPresence`、`ClassificationState`、`PendingRequest`の結果型等）を`Core/DomainEvents.fs`（または`Core/Shared.fs`）に移し、ドメインのモジュールには`private`な状態表現とsmart constructorだけを残す。(ii) ドメイン間に一方向の順序（例：Topology → Workspaces → Windows → … → Placement）を認め、§17.1のテストを「後段は前段の公開型を読んでよいが、関数は呼ばない」に緩める。どちらを採るかを決めなければ、Phase Aの2つ目のドメインで実装が止まる。

### R3-02 [致命] Wm.Journalは「Contractsのみに依存」と書いているのに、checkpointを実装させている。Checkpoint DTOはドラフト自身がWm.Coreに置いている
- **ドラフト**決定1：「Wm.Journal（Contractsのみに依存。イベント型はContracts、writer/reader/**checkpoint**の実装はここ）」。決定2の最後の箇条：「**Checkpoint DTOはCoreの状態の写しなので対象外**：各ドメインの状態の`ofDto`/DTO化はWm.Core内でドメインごとに行う」。
- **architecture.md** §17のツリー「`Wm.Journal/ # writer, reader, checkpoint, projections`」。§9.5「Snapshotには`SchemaVersion`、`LastSequence`、Core/Policy/Registry/Normalizerの成果物識別子を持たせる」。§4.5「Checkpoint（9.5）は**Replayの開始点専用**」。
- **問題**：checkpointの書き出し・読み込みは、`WmState`を（Wm.Coreにあるドメインごとの）DTOへ変換して永続化する処理である。Contractsだけに依存するWm.Journalからは、`WmState`もそのDTOも見えない。projections（診断の投影）も同様に、Coreの型を読むものがある。(a) Wm.JournalをWm.Coreに依存させるか、(b) checkpointの実装をWm.Runtimeに移し、Wm.Journalはバイト列（またはContractsの封筒型）を書くだけにするか、のどちらかを決める必要がある。(b)は、ドラフト自身の「Wm.RuntimeはContracts・Core・Journalに依存」とも整合する。
- **なぜ致命か**：依存グラフの1行が、決定2と両立しない。このままではプロジェクトを作った段階で参照が通らない。

---

## 重大

### R3-03 [重大] 条件(a)(b)を「のどちらかを満たす型**だけ**」と言いながら、実際には第3・第4の根拠でContractsに置いている。基準が型を正しく分類していない
- **ドラフト**決定2：「次の2条件のどちらかを満たす型**だけ**をContractsに置く」。条件(a)「`CoreCommand`／`InputEvent`／`Effect`／`EventEnvelope`／`GateInput`／`GateRejection`…が、型として**直接**参照する」。
- 次の型について、ドラフトは(a)(b)以外の理由でContractsに置いている。
  - `EvidenceGap`：「`BeliefSlot`（Contracts確定済み）が直接参照するので、条件(a)に**準じて**」。`BeliefSlot`は(a)の起点ではない。
  - `RegistryManifest`：「§14で明示的にContractsの型と確定済み」。(a)でも(b)でもない（§17の既存の配置規則）。
  - `TimerCommand`/`Response`/`RetryState`：「ADR-017・§17で既にContracts確定」。`TimerId`は(a)を満たす（`InputEvent.TimerElapsed`）が、`Response`と`RetryState`はどの起点からも到達しない。
  - Timer実行器・`ProbeOutcome`・`executeEffectProtocol`・`classify`：(a)(b)とは無関係な、「Shell/Simulationで共有する純粋ロジック」という別の根拠。
  - 層4の「各ドメインの信念の小レコード（`IdentityBeliefs`等）」：(a)の起点のどれからも到達しない。(b)についても、`ClassificationView`（ドラフトではWm.Policy）が参照するだけで、Layoutsは`LayoutWindow`経由で`Belief<int>`だけを読むので、成り立たない。根拠は§17の「Explain・Checkpoint・IPCのDTOに出るため」である。ところが、ドラフトはCheckpointを基準から外したので、この根拠も一部失われている。
  - `Movability`/`Responsiveness`/`DpiBehavior`（§4.5の信念の値の型）：(a)(b)のどちらも満たさない。基準を文字どおり適用すると、`Movability`はWm.Policy（`ClassificationView`が読む）に置かれ、`Responsiveness`はどこにも定まらない。ドラフトは分類していない。
- 「直接」という語も問題である。`InputEvent`は`WindowSnapshot`を直接参照するが、`AppIdentity`・`AppIdSource`・`CloakState`・`TitleMatchVector`・`ProcessKey`・`ResponsivenessSample`は間接的にしか参照されない。また`LayoutArgs`は、ドラフトが(b)に分類しているが、実際には`CoreCommand.ChangeLayout → LayoutRef → LayoutArgs`という推移的な経路で(a)を満たす。基準は「**推移的に**参照する」でなければ、Contractsそれ自体がコンパイルできない。
- **修正の方向**：基準を次の4つに書き直し、「だけ」を外す。(0) architecture.mdが既に明示している配置（§14、§17、§12.1）。(a') 起点の型から**推移的に**到達する型。(b) PolicyとLayoutsが共有する型。(c) Shell/Simulationで共有する決定的なロジック（`classify`、`executeEffectProtocol`、Timer実行器、combinator）。そのうえで`Movability`/`Responsiveness`/`DpiBehavior`と信念の小レコードを(0)としてContractsに置く、と明記する。

### R3-04 [重大] 層10の「PolicyArtifact／Incident BundleのDTO/`ofDto`基盤」は、Contractsには置けない
- **ドラフト**決定3の層10：「Journal／IPC／PolicyArtifact／Incident BundleのDTO/`ofDto`基盤（Checkpoint DTOは対象外）」。決定2「DTOはJournal／IPC／PolicyArtifact／Incident BundleのDTOのみをこの基準の対象にする」。
- **architecture.md**：
  - §17「`PinnedPolicy`/`DeployablePolicy`はWm.Policyに置く」。§14 `val analyze : RegistryManifest -> PinnedPolicy -> Result<DeployablePolicy, AnalyzerReport>`、`PinnedPolicy.ofStored`。
  - §21「`PolicyIR.ofDto`等が3段以上の`match Ok/Error`ネストを要した最初の時点で、Contractsに自作（20行未満…）を追加する」。ここでContractsに置くとされているのは`result`ビルダーであり、`ofDto`そのものではない。
  - §9.2のDTOタグ付けの対象には`WindowPredicate`（Policy）と`ValidationError`（§17によりLayouts）が含まれる。§9.2「`EffectFailure`…はExplain出力とIncident Bundleには表示用DTOとして出る」。
- **問題**：PolicyArtifactのDTOを`ofDto`すると、`PolicyIR`/`WindowPredicate`/`PinnedPolicy`（Wm.Policyの型）ができる。Incident BundleのDTOには`ValidationError`（Layouts）や`AnalyzerReport`（Policy）が入る。これらの`ofDto`はContractsには書けない（Contractsは依存を持たない）。v2でCheckpointについて起きた誤り（R2-03）が、PolicyとLayoutsについて形を変えて残っている。
- **修正の方向**：「DTOは、それが写す型と同じプロジェクトに置く。Contractsに置くのは、DTOの共通基盤（タグ付けの規約、`Unrecognized`のケース、§21の`result`ビルダー）と、Contractsの型のDTOだけ」とする。Checkpointについての例外は、この一般則の特殊な場合になる。

### R3-05 [重大] 層9の`JournalKind`/`JournalPayload`には、PolicyとLayouts（とCore）の型を運ぶ種別がある
- **ドラフト**決定3の層9「Journalイベント型（`JournalEntry`/`JournalKind`/`JournalPayload`）」。決定2「§17が明示する「Journalのイベント型は契約側に置く」にそのまま従い、Contracts」。
- **architecture.md** §9.2の永続化する種別に「`PolicyStaleAnalysis`（…起動時・Rollback時の報告のみ**Analyzer実行結果**）」（＝`AnalyzerReport`、Wm.Policy）、`ProbeRequested`（§11.1 `ProbeRequest`の`ApprovalToken`）がある。揮発性リングバッファの種別には`RuleSelected`、`PlanRejected`（`ValidationError`、Layouts）、`DesiredUpdated`（`DesiredPresence`、Core）、`BeliefRevised`、`TimerCommanded`がある。
- **問題**：「Journalのイベント型」をすべてContractsに置くと、`JournalPayload`のケースが`AnalyzerReport`/`ValidationError`/`DesiredPresence`を参照するため、Contractsがコンパイルできない。§17の「Journalのイベント型は契約側」が意味するのは、**永続化する**入力・OS呼出結果の封筒（§9.1、ADR-004）だと読むべきである。`PolicyStaleAnalysis`のペイロードは、Contractsに置ける要約形（文字列やコードの列）にするか、Policy側のDTOとして不透明に運ぶかを決める必要がある。揮発性の種別（リングバッファ、DecisionTrace系）は、Core以上の層に置くことになる。
- **なぜ重要か**：Phase Aの「最小Journal」の型をどこまでContractsに置くかが、ここで決まる。決めなければ、Journalの型がContractsとCoreに分かれて重複する。これはv1の背景が避けようとしていた事態である。

### R3-06 [重大] 新しいプロジェクト2つとゲートの範囲の拡大で書き換わる本文が、How to applyに入っていない（R2-11の指摘のうち、この部分は未反映）
- **ドラフト**決定8とHow to applyが反映先に挙げるのは、§0のテーブル、§19への注記と付録、§21だけである。
- **書き換えが要るのに挙がっていない箇所**：
  - §17のツリー（`Wm.Runtime/`と`Wm.Normalization/`が無い。`tests/`にも対応するテストプロジェクトが無い）。§17の依存方向の段落（「Windows/Host/Simulation/DebugがCoreの契約を参照し」）と、「JournalWriterのI/OはShell側」の文言（決定2はこれを「実行する文脈」と**読み替えている**。読み替えるなら本文を直すこと）。
  - §3.1の層の一覧（5.「Windows Shell：…純粋関数としてのWindows Event Normalizer」は、NormalizerをShell層に置いている。Runtimeの層も無い）。
  - §3.4の「**Core（Wm.Core、Wm.Policy、Wm.Layouts）**は、環境の時刻…を決して読まない」と、§17.1の共通ゲートの「Coreのアセンブリ」。決定5はこの対象をWm.Contracts/Wm.Normalizationへ広げるので、どちらの本文にもその旨を書かない限り、ゲートを実装する人はarchitecture.mdのとおり3アセンブリだけを検査する。
  - §17.1の「Linux：F# Core、Policy、Layout、PlanValidator、Journalの純粋部分、Simulation、Replay、FsCheck、CLI」（RuntimeとNormalizationが無い。「Journalの**純粋部分**」という表現は、Wm.Journal全体（I/Oを含む）をLinuxでビルドするというv3の想定とずれている）。
- **なぜ重要か**：ドラフト自身が決定8で「正本を分割しない」と決めている。そうであれば、反映先の一覧が不完全なことは、そのまま正本の不整合になる。

### R3-07 [重大] 縦切りの前提が過小に書かれている
- **ドラフト**決定7：「Wm.Contractsの層3〜8がほぼ全て先に揃っている必要がある」「「Contracts完成後の最初のCore動作確認」である」。
- **問題**：
  1. 層1〜2（識別子、`NativeCallRecord`）は層3の前提である。層9（Journalのイベント型）も必要になる。Simulationでタイマーの発火を戻すループはWm.RuntimeのReducerであり（決定1）、Reducerは「入力をJournalに書いてから`step`を呼ぶ」（§7.1）ので、Wm.JournalとWm.Runtimeも縦切りの前提になる。
  2. `ScreenSettle`を回すには、Topologyの`update`をWmState.fsの`step`に組み込む必要がある。R3-01の解決（Topologyの`Ctx`が何を読むか）も前提になる。
  3. #21は全ての待ちについての性質なので、縦切りで通るのは「#21のうち`ScreenPhase`の`Settling`に関する部分」と#3である。Gateの項目として「通る」と書くなら、範囲を限定すること。
- 正しくは「Contracts全層＋Journal（最小）＋Runtimeの最小のReducer＋Core.Topology＋WmState（Topologyだけを合成）＋Simulationの実行器」が前提である。これは「縦切り」としては妥当だが、名前と前提を正しく書かないと、着手の時点でPRの分割（How to apply「本ADRの§1〜§4、§7の順序に従う」）と合わなくなる。

---

## 軽微

### R3-08 [軽微] `ProbeOutcome`と`executeEffectProtocol`が層の一覧に無い
- 決定2では「`ProbeOutcome`はContracts」としているが、決定3の層1〜10のどこにも`ProbeOutcome`と`executeEffectProtocol`が無い。`executeEffectProtocol`は`WindowId`と`NativeCallRecord`だけを使うので、層2の後ならどこでもよい。層の一覧を決定とするなら、載せておくこと。

### R3-09 [軽微] `RuntimePolicy`をドメインの後に置くなら、ドメインの`update`が何を受け取るかを書くこと
- 決定4では`RuntimePolicy`をWmState.fsの中に置いている。一方、Windowsの段⑤（Policyによる分類）とPlacementの段⑦（Layout Registryの呼出）は、ドメインの中で`ActivePolicy`とRegistryを必要とする。各ドメインが`RuntimePolicy`ではなく、その部分（Policy/Layoutsの型）を直接受け取るのであれば、この順序で問題ない。ただし、そのことを1文で明記しておかないと、実装者は`RuntimePolicy`をドメインより前に移したくなる（第2ラウンドR2-01で指摘した点）。

### R3-10 [軽微] 依存グラフからWm.DebugProtocol/Wm.ReplClientが消えた
- v2のグラフにはWm.DebugProtocolがあったが、v3には無い。Phase Aの対象外なら、「Phase Bより後の層は本ADRの対象外」と書いておくこと。今のままだと、グラフが§17のツリーの全体なのか、Phase Aの部分なのかが読み取れない。

### R3-11 [軽微] 指摘件数の数え間違い
- 背景「計43件の指摘（致命13件）」。第1ラウンドは29件（R1-1〜12、R2-1〜5、R3-1〜3、R4-1〜3、R5-1〜3、R6-1〜3）、第2ラウンドは15件で、合計44件である（致命は13件で合っている）。

---

## 反映を確認した第2ラウンドの指摘
R2-01（WmState型→RuntimePolicy→StepResult→step）、R2-02（GateRejection/GateInput→GatedEffect）、R2-03（Checkpoint DTOをCoreへ）、R2-04/R2-05/R2-15（`ValidationInput`/`LayoutContext`→Layouts、`ClassificationView`/`MonitorSelector`→Policy、`ShownBy`/`MoveCause`→Core）、R2-06（層の順序に縮めた）、R2-07（Journalのイベント型をContractsへ。ただし範囲はR3-05）、R2-08（決定5。ただし本文への反映はR3-06）、R2-09（Wm.Runtime。ただしJournalとの関係はR3-02）、R2-10、R2-11（責務を変えない形に縮めた）、R2-12、R2-14。R2-13は撤回された案の代わりに、R3-01の問題が生じている。
