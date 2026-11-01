# ADR-019（v6、Opus批判的レビューで収束。architecture.md v2.10へ統合済み）：Phase Aのプロジェクト割当と実装順序を確定する

## 状態
**採用・統合済み**（第1〜6ラウンド[review-adr019-round1.md][round2][round3][round4][round5][round6.md]のOpus批判的レビューを経て、第6ラウンドで構造上の指摘が無いことを確認。残る指摘（R6-01、`Displays`射影の文言）は本ファイルに反映済み。architecture.md本体（§0・§3.1・§3.4・§4.4・§17・§17.1・§19・21章）への統合をv2.10で実施済み——決定9「How to apply」参照。本ファイルはADR-019の決定の詳細記録として残す）

## 背景・問題
architecture.md §19は、Phase Aの実装対象ファイルを29個、単一の順序リストとして列挙している。(1)各ファイルのプロジェクト（§17）が未指定、(2)識別子・`Effect`/`InputEvent`等の基盤ファイルがリストに無い、(3)リスト順が実際の型参照の依存順と一致しない、という3つのギャップがある。

v1〜v4ドラフトは4回のOpusレビューを経た。最大の論点はドメイン間の`'Ctx`の扱いで、v4で次のように解決した：architecture.md §7.1の処理順序表（8段）を1段ずつ当たり直すと、ドメイン間で実際に読まれるのは、もう一方のドメインの完全な状態ではなく、(i) 少数の小さな値型、(ii) `step`が識別子レベルで組み立てる射影、のどちらかであり、ドメインのモジュール同士は互いの型を一切参照しなくてよい（第4ラウンドでこの結論自体は検証済み）。本v5は、その(i)の一覧の漏れ（`FocusTarget`、`PendingResolved`の結果型）、Checkpoint DTOの変換とI/Oの混同、`DecisionTrace`の配置漏れという、第4ラウンドで指摘された4件の重大と6件の軽微を修正する。

## 決定

### 0. プロジェクト間の依存の原則（名前を付けず、ここでだけ述べる。R4-06反映）
- PolicyとLayoutsは互いに依存しない。
- Wm.CoreはWm.Contracts・Wm.Policy・Wm.Layoutsの3つに依存する。
- I/Oを行うもの、環境時刻・例外捕捉を行うものはWm.Coreに置かない。

### 1. プロジェクト間の依存グラフ（Phase Aの範囲。Wm.DebugProtocol/Wm.ReplClient等Phase C以降の層は対象外）
```
Wm.Contracts（依存なし）
  ├── Wm.Policy          （Contractsのみに依存）
  ├── Wm.Layouts          （Contractsのみに依存。PolicyとLayoutsは互いに依存しない）
  └── Wm.Normalization    （Contractsのみに依存）
Wm.Journal（Contractsのみに依存。ADR-004の「永続化する一次事実」の封筒の**書込み・読込（I/O）**だけを持つ）
Wm.Core（Contracts・Policy・Layoutsに依存）
Wm.Runtime（Contracts・Core・Journalに依存。Windowsには依存しない。Reducerループ、**Checkpointファイルの書込み・読込（I/O。DTOへの変換自体はWm.Core側、決定4参照）**、Timer実行器の利用側をここに置く）
Wm.Simulation（Core・Runtime・Normalizationに依存）
Wm.Windows（Contracts・Normalizationに依存。Core/Policy/Layoutsには依存しない）
Wm.Host（Core・Runtime・Journal・Windows・Normalizationに依存）
Wm.Diagnostics（Core・Journal・Policy・Layoutsに依存。Incident BundleのDTOはここで組み立てる。Explainのための実際のReplayはWm.Cli側（Runtime・Simulationに依存）が回し、DiagnosticsはそのTraceを受け取って整形するだけ、という分担にする——DiagnosticsからRuntimeへの依存を増やさないための推奨）
Wm.Cli（Diagnostics・Runtime・Simulationに依存）
```

### 2. ドメイン間の読み取り（R3-01の解決。第4ラウンドで§7.1の8段すべてについて検証済み）
各段が「読むもの（Ctx）」として挙げているのは次の2種類だけである。
- **(i) ドメイン横断で読まれる小さな値型**：`ScreenPhase`、`FocusTarget`（R4-01で追加。§4.4で`WmState`の節のそばに定義される、`ManagedWindow of WindowId | Elsewhere`）、各ドメインの信念の値の型（`Movability`/`Responsiveness`/`DpiBehavior`等、段⑤でWindowsが`ClassificationView`経由で読む）と信念の小レコード（`IdentityBeliefs`等）。
- **(ii) `step`開始時点の値から、その場で組み立てる識別子レベルの射影**：例えば段②でTopologyが読む「窓の所属」は`WorkspaceSet.Index`（`Map<WindowId, WorkspaceId>`、R4-10で型を訂正）であり、Workspacesの完全な状態ではない。

**`Displays`自身は(i)にも(ii)にも含めない（R5-01反映）。** `Displays`はsmart constructorで「同じWorkspaceは高々1つのモニターに出る」という不変条件を守る型であり（§3.4のParse don't validate、§4.4のprivateコンストラクタの注、§15の表、§15.1 #17、ADR-018）、公開の値型としてContractsへ置くと、Wm.Policy・Wm.Runtime・テストのどこからでも不正な`Displays`を作れてしまい、この保証が失われる。`Displays`はCore.Topologyの中で`private`なままとし、他ドメイン・他段が読む分（①のDPI、⑤のWorkspaces、⑦のPlacementの`WorkArea`/`Dpi`）は、Topology自身が公開する読み取り専用の射影──§4.4の`Screen`（`MonitorSnapshot`とWorkspaceId）と同じ情報を運ぶレコード、識別子（`MonitorId`/`WorkspaceId`）と`MonitorSnapshot`（DPI/WorkArea含む。いずれもContracts層1の型）だけでできている──を(ii)として`step`がその場で組み立てる（R6-01反映：「識別子だけ」という記述はDPI/WorkAreaの運び方を説明できていなかったので訂正）。これは決定2の基本的な考え方（「他ドメインの状態そのものではなく射影を渡す」）をそのまま`Displays`にも適用しただけであり、例外ではない。

**根拠はarchitecture.md §9.2のDTOタグ付け一覧ではない。** §9.2は「Checkpointに出る＝状態の一部」という別の理由で型を挙げており、実際にはそこに無い型（`FocusTarget`は§9.2にはあるが`ShownBy`は無い）や、そこにあってもContractsへ移していない型（`ClassificationState`、`MonitorSelector`、`DesiredPresence`）が混在する。根拠は**§7.1の8段の表で、他のドメインがCtxとして直接読む値そのもの**である（R4-02反映）。

`MoveCause`・`ShownBy`は、`DomainEvent`（`MoveAttributed (w, cause)`／`WorkspaceShown (ws, ShownBy)`）のペイロードとしてだけ現れ、ドメインが直接Ctxとして読むわけではない。§9.2は`DomainEvent`自体をCheckpoint/Journal非対象と明記しているので、この2つはWm.Contractsではなく`Core/DomainEvents.fs`（決定7）に置く。`EffectiveManagement`は、`GateInput`（Map<WindowId, EffectiveManagement>）が参照するために基準(2)でContracts行きになるのであって、ドメイン間の直接の読み取りが理由ではない（R5-02反映：根拠を`GateInput`だけに絞り、`ValidationInput`——基準(2)の起点ではなくWm.Layouts側に残る型——への言及を外した）。`PendingResolved`の結果を表すDU（`Satisfied`/`Mismatch`/失敗。段③④でPlacementが出し、Healthが読む）も同様の理由で`Core/DomainEvents.fs`に置く（R4-01）。

これにより、**6ドメインのモジュール同士は互いの型を一切参照しない**（§17.1のテストの文言はそのまま保てる）。

### 3. Wm.Contractsへ型を下ろす基準
- **基準(0)**：architecture.mdが既に明示している配置（§14の`RegistryManifest`、§17のBelief系・TimerCommand系・各ドメインの信念の小レコード、§12.1の`executeEffectProtocol`/`ProbeOutcome`）。
- **基準(1)**：決定2(i)で確定した、ドメイン横断で直接読まれる小さな値型（`ScreenPhase`/`FocusTarget`、信念の値の型）。`Displays`はsmart constructor型なのでこの基準から明示的に除外する（決定2参照）。
- **基準(2)**：`CoreCommand`／`InputEvent`／`Effect`／`EventEnvelope`／`GateRejection`／`GateInput`が型として**推移的に**参照する（`Management`・`LayoutRef`・`LayoutArgs`・`EffectiveManagement`はこれで確定）。
- **基準(3)**：Wm.PolicyとWm.Layoutsの両方から参照され、それを置かないと両者が互いに依存してしまう。
- **基準(4)**：Shell/Simulationが共有する決定的な純粋ロジック（`classify`、`executeEffectProtocol`、Timer実行器、Timerのcombinator）。

基準(0)〜(4)のどれも満たさない型は、必要とするプロジェクトに残す：`ValidationInput`／`LayoutContext`／`LayoutWindow`／`Placement`（Layout戻り値）はWm.Layouts。`ClassificationView`／`MonitorSelector`はWm.Policy。`MoveCause`・`ShownBy`・`PendingResolved`の結果DU・`DecisionTrace`（決定7）はWm.Core。`AnalyzerReport`／`ValidationError`／`DesiredPresence`／`ClassificationState`／`ClassifiedBy`はそれぞれWm.Policy／Wm.Layouts／Wm.Coreに残す。

### 4. DTOの配置原則
**DTOは、それが写す型と同じプロジェクトに置く。** Wm.Contractsに置くのは、(a) DTOの共通基盤（タグ付け規約、`Unrecognized`ケース、§21の`result`ビルダー）と、(b) Contracts自身の型（基準(0)〜(4)で確定した型）のDTOだけである。したがって：
- `PolicyArtifact`（`PinnedPolicy`/`DeployablePolicy`）のDTOはWm.Policyに置く。
- Incident BundleのDTO（`ValidationError`・`AnalyzerReport`等、複数プロジェクトの型を束ねる）はWm.Diagnosticsで組み立てる。
- **Checkpoint DTOとその`toDto`/`ofDto`はWm.Core**に置く（ドメインごと。本原則どおり、`private`な状態の変換はその状態を定義したモジュールでしか書けないため）。**Checkpointファイルの書込み・読込（I/O）とReplay開始点としての利用はWm.Runtime**が持つ（R4-03反映。決定4の第3版がこの2つを混同していたのを訂正：DTO変換とファイルI/Oは別の関心事であり、前者はWm.Core、後者はWm.Runtime）。

### 5. 決定性ゲートの適用範囲の拡大
§3.4/§17.1が定める「クロージャ・`Async`/`Task`非含有」「環境の時刻・乱数API非参照」のアーキテクチャテストの対象を、Wm.Core（既定）に加えて**Wm.ContractsとWm.Normalization**にも明示的に拡大する。

### 6. Wm.Contracts内部の層順序（層内はコンパイラに委ねる）
1. 識別子・座標（4.1/4.2の土台）
2. `ErrorSpace`/`NativeCallRecord`/`EffectFailure`/`classify`（ADR-014）／`ProbeOutcome`/`executeEffectProtocol`（§12.1）── `Observation<'T>`より前に必要
3. `Observation`/`ObservedPresence`等（4.2）
4. `EvidenceGap`（11.1）→ `Belief`/`BeliefSlot`/`Confidence`/`EvidenceRef`/`BeliefKey`と各ドメインの信念の小レコード・信念の値の型（`Movability`/`Responsiveness`/`DpiBehavior`等、ADR-016）
5. 基準(1)(2)(3)で確定した共有語彙（`ScreenPhase`/`FocusTarget`/`Management`/`LayoutRef`/`LayoutArgs`/`EffectiveManagement`/`RegistryManifest`/`WorkspaceId`。`Displays`はここに含めない）
6. `TimerId`（各ドメインTimerサブDU含む）/`TimerCommand`/`Response`/`RetryState` → Timer実行器
7. `CoreCommand` → `InputEvent`/`Effect`/`EventEnvelope`
8. `GateRejection`/`GateInput` → `GatedEffect`（internal構築子）
9. **ADR-004の一次事実のうち、Contractsの型だけで書ける永続化対象の種別**（`InputEvent`/`Result<unit, NativeCallRecord>`相当。具体的には`InputReceived`/`EffectDispatchStarted`/`EffectCallReturned`/`EffectBatchReturned`/`InternalFault`/`JournalDegraded`/`JournalRecovered`/`SafetyRecoveryAction`/`ObservationReceived`/`PendingSatisfied`/`PendingTimedOut`等、Phase Aの範囲に収まる種別に絞る。R5-03反映：`ReplIntervention`/`ProbeRequested`/`EvidenceCaptured`（`ApprovalToken`等、層1〜8に無い型を運ぶ）はPhase C/D向けであり、本ADRの対象外として層9の例から外す。`PolicyStaleAnalysis`のように永続化はするがPolicy等の型（`AnalyzerReport`）を運ぶ種別のペイロードの具体的な表現は21章の未決事項とする。R4-05反映：`PlanRejected`/`DesiredUpdated`等の**揮発性**エントリはJournalの対象ではなく`DecisionTrace`（決定7、Wm.Core）の話であり、ここには含めない）
10. Contracts自身の型のDTO/`ofDto`基盤（決定4参照）

### 7. Wm.Core内部のファイル順
`Core/DomainEvents.fs`（`DomainEvent`のDU。ペイロードのうち基準(1)〜(3)でContractsへ移さなかった小さな型——`MoveCause`、`ShownBy`、`PendingResolved`の結果DU——をここに置く） → **`DecisionTrace`**（`StepResult.Trace`の要素。`ValidationError`（Layouts）や`PolicyRuleId`等Policy/Layoutsの型を運びうるのでWm.Coreに置く。Journalに永続化しない揮発性の値で、Explainは§18どおりReplayで再構成する。R4-04反映：21章へ先送りしていたのを撤回し、この位置に確定する。**R5-04反映**：§4.4の`val update : 'Input -> 'Ctx -> 'State -> 'State * Response<'Timer,'Effect> * DomainEvent list`は戻り値に`DecisionTrace`の出口を持たないという、architecture.md内部の食い違いがあるため、本ADR採用時に`update`の戻り値へ`* DecisionTrace list`を加える改訂を§4.4に加える。これにより§7.1が要求する「ドメインの更新関数が導出不整合の疑いを`DecisionTrace`に記録する」という挙動の出口ができる） → 6ドメイン（`Core.Topology`[`MonitorLifecycle.reconcile`を同梱]/`Core.Workspaces`/`Core.Focus`/`Core.Windows`/`Core.Health`/`Core.Placement`。この6つの間に強制順序は無い） → `WmState.fs`（`WmState`型 → `RuntimePolicy`型 → `StepResult`型[`WmState`を含むので型定義の後] → `step`関数。`step`の本体が決定2(ii)の射影の組み立てと、各ドメインのstage別更新関数への配線を行う）。各ドメインは`RuntimePolicy`を丸ごと受け取るのではなく、Policy評価器やLayout Registryが必要な段（⑤・⑦）だけがその部分を直接受け取る。

### 8. 最初の縦切り
`Timers.fs`のcombinator（`awaitDeadline`/`nextRetry`/`debounce`。3 combinatorでドメインの多チック待ちを覆うという判断自体は別セッションで確定済みであり本ADRの対象外）はWm.Contractsに置く（決定5のゲート拡大の対象）。最初の縦切りは`Core.Topology`の`ScreenSettle`について、性質ベーステスト**#21（`ScreenPhase`の`Settling`の開始対応と、古い発火の無害性の両方）**を、次の前提をすべて揃えたうえで検証する（R5-05反映：#3は`IntentEpoch`/Pendingの概念でありPlacementの`PendingDeadline`実装時点の対象とし、`ScreenSettle`の縦切りでは扱わない。古いTimerの発火の無害性は#21後半が「#3の具体化」として既に含んでいる）：Wm.Contracts全10層、Wm.Journal（一次事実の封筒のみ）、Wm.Runtimeの最小のReducerとTimer実行器のSimulation版、`Core/DomainEvents.fs`、`DecisionTrace`、`Core.Topology`、`WmState.fs`。

**`WmState.fs`の「最初はTopologyの関わる段だけ配線する」とは**、`WmState`型が最初はTopologyのフィールドだけを持ち、他のドメインを実装するたびにそのドメインのフィールドを`WmState`へ追加していくことを意味する（R4-09反映）。6フィールドを最初から用意して空の型で埋める（退けた代替案「29ファイルを骨格だけ一括で用意する」）とは異なる——後者は結局スタブと本実装の二度書きになるが、前者はフィールドを後から追加するだけで二度書きが起きない。6ドメインに強制順序が無い（決定2）ため、Topologyを他のどのドメインより先に書いても他ドメインの実装を妨げない。

### 9. §19との関係
§19の「次のファイル相当を優先実装する」は、ADR-011が定める各ドメインの意味・不変条件を固定する優先順位を表し、本ADRはこれを変更しない。本ADRが追加するのは、固定されたその意味をF#プロジェクトとして実際にコンパイルする順序であり、別の軸である。採用時にarchitecture.mdへ次を反映する（正本を分割しない）：
- §0のADRテーブルにADR-019の行を追加する。
- §19の各ファイルにプロジェクト名を付記し、欠落ファイル（`Core/DomainEvents.fs`、`DecisionTrace`含む）を追加した対応表を付録として添える。
- **§17のツリー**に`Wm.Runtime/`・`Wm.Normalization/`と対応するテストプロジェクトを追加し、`Wm.Journal/`のコメントから`checkpoint`/`projections`を外す（checkpointのI/OはWm.Runtime、projectionsはWm.Diagnostics／Wm.Cliの分担に移る）。
- **§17の依存方向の段落**にWm.Runtimeを明記する。**「JournalWriterのI/OはShell側」の一文**は残しつつ（Wm.Journal自身のI/Oについての記述として）、別文として「CheckpointのDTO変換はWm.Core、ファイルI/OはWm.Runtimeが持つ」を追加する（R4-07反映：元の一文を書き換えるのではなく、別の文を足す）。
- **§17の型の配置の段落**（「各ドメインの状態型…はWm.Core」）に、「ただし`ScreenPhase`/`FocusTarget`等、ドメイン横断で直接読まれる小さな値型は例外としてWm.Contractsに置く（`Displays`自身は`private`なままWm.Coreに残り、他ドメインへは射影だけを渡す。R5-01反映）」という一文を追加する。
- **§4.4の`val update`の型**に`DecisionTrace list`を戻り値として加える（R5-04反映）。
- **§3.1の層の一覧**に、NormalizerがWm.Windows専属でないことと、Wm.Runtimeという層の存在を追記する。
- **§3.4**の決定性ゲートの対象列（「Core（Wm.Core、Wm.Policy、Wm.Layouts）」）に、Wm.ContractsとWm.Normalizationを追加する。
- **§17.1**の「Coreのアセンブリ」という表現を同様に拡張し、Linuxのビルド・テスト対象一覧にWm.Runtime・Wm.Normalizationを追加し、「Journalの純粋部分」を「Journal」に簡略化する（理由：Wm.JournalがWindowsに依存しないため。「純粋」という語は使わない。I/Oを行うので厳密には純粋ではない。R4-08反映）。

## 代替案として退けたもの
（v1〜v4から変更なし）§19のリスト順をそのまま実装順にする、Phase Aの29ファイルを骨格だけ一括で用意する、型を1つずつ列挙してファイル順を決める、Normalizerの配置を実装中に決める、DTO境界から到達可能なら全部Contracts、ReducerをWm.Hostに置く、ドメイン間の`'Ctx`を型パラメータのまま多相にする、Wm.Journalにcheckpointの実装（DTO変換を含む）を持たせる、条件(a)(b)の2条件だけで全型を分類する。

## Why
- 決定2は、§7.1の8段の表を実際に1段ずつ当たり直して検証した結果であり、第4ラウンドのレビューで別の形で再検証された。
- 決定4のCheckpoint分割（DTO変換＝Wm.Core、I/O＝Wm.Runtime）は、`private`な状態の変換はその状態を定義したモジュールでしか書けないという、F#の可視性規則そのものから導かれる。
- `DecisionTrace`をWm.Coreに確定させたのは、`StepResult`（Wm.Core、`WmState.fs`）がこれを直接保持するフィールドとして持つため、先送りすると縦切りの時点で行き詰まるからである。

## How to apply
- Phase A着手時のPR/コミット分割は、本ADRの§1・§6・§7・§8の順序に従う。
- 21章の未決事項表に、`PolicyStaleAnalysis`のペイロードの具体的な表現、Normalizerの「候補条件」判定とCore状態の関係（本ADRの範囲外）を追記する。
- 本ADR採用時、決定9に列挙したarchitecture.mdの各箇所への反映を、本ADRの採用作業そのものに含める。
