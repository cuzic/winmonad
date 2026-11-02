# Phase A 修正（e59540a..9cf4a93）再確認レビュー（第2ラウンド）

対象：`git diff e59540a..9cf4a93`（6コミット）。前回の指摘：`opus-review-phase-a.md`（指摘番号は前回の本文見出しの番号で書く。5-3＝stepの段順、5-4＝Journal、5-5＝Health）。
行番号は9cf4a93時点のもの。

## 検証の方法
- リポジトリは編集していない。`git archive 9cf4a93` をscratchpad（`.../scratchpad/build-9cf4a93`）へ展開し、`~/.dotnet/dotnet test winmonad.sln -c Release` を実行した：**ビルド成功、警告0件、59テストすべて成功**（Core 50、Journal 8、Runtime 1）。
- テストに中身があるかを確かめるため、同じコピーで**ミューテーション確認**を行った（修正を1つずつ元に戻して再テストした。リポジトリには触れていない）：

| # | 戻した修正 | 結果 |
|---|---|---|
| M1 | 3-3：`bySmallestUnshown`の`not (Map.containsKey ws returns)`を削除 | **50/50 成功（検出できない）** |
| M2 | 3-1：`latestForForcedSettle ()`が`[]`を返す（元のバグ） | **50/50 成功（検出できない）** |
| M3 | ゲート表の4セルを反転：`HiddenByWm×RequestForeground`、`NotAlive×SetPresence`、`Excluded×SetPresence`、`Excluded×RequestClose` | **50/50 成功（検出できない）** |
| M4 | 4-1：`vanishWindow`のSwitchSettle付け替えを削除 | **50/50 成功（検出できない）** |
| M5 | 3-2：Graceの「変化なし」ガードを削除（Stableだけ残す。無限周回の本体） | **50/50 成功（検出できない）** |
| M6 | Journal：`"floating"→Excluded`、`"hresult"→Win32LastError`、`"column-count"→NoArgs`に読み違えさせる | **8/8 成功（検出できない）** |

- **補足（重要）**：`origin/main`は`a0d42c1`のままで、HEADより19コミット遅れている。Phase Aのコミットは1つもpushされていないので、`phase-a-build.yml`（Linux CI）は今回の修正を一度も実行していない。上の結果はこのサンドボックスで実行したものである。

---

## サマリ

| 指摘 | 判定 | 一言 |
|---|---|---|
| 2-1〜2-5（blocking） | **修正済み** | `appliesTable`の全42セルが§6.2の表と本文（RequestCloseの例外）に一致することを1セルずつ確認した。ただしテストが固定しているのは一部のセルだけ（T-1） |
| 3-1（blocking） | **ロジックは修正済み／テストが無い** | `LastObserved`でReconfiguringの解除が直った。3-1bも同時に直った。Reconfiguringを通るテストが1本も無く、M2でバグを元に戻しても検出できない（T-2） |
| 1-1 | 修正済み | `LastRevisedAt`/`Stale asOf`がFocusとHealthの両方で`now`（ms）にそろった。`LatestWins<'T>`と、修正関数`revise`を1つだけContractsに置いた |
| 1-2 | **部分的に修正（新しい不整合あり）** | Believed状態では直った。Unformed状態では、同じ「Errorより古いOk」を捨てる。2つの状態で意味が食い違う（N-1）。「Errorより古いOkをConfirmedにする」という選択も、設計書に書かれていない（N-2） |
| 2-6 | 型だけ修正 | `NeedsRestore`を追加し、判定も実装した。生産元が無いので常に空集合（コメントに明記済み。今の段階では妥当） |
| 2-7 | 修正済み | 各バッチは空集合から始まる。`RestoreIssuedInBatch`は削除した。トップレベルの扱いが小さく残る（N-4） |
| 2-8 | 修正済み | 入れ子のバッチを再帰的に検査する。空のバッチは出さない（テストあり） |
| 2-9 | 修正済み | `gate`をinternalにし、`InternalsVisibleTo("Wm.Core.Tests")`を足した |
| 2-10 | 修正済み | 表どおりになった（ただしM3のとおり、テストで固定されていないセルがある） |
| 3-2 | 修正済み（テストは自明なケースだけ） | Stable/Graceで、変化の無い観測を無視する。テストは「NoMonitorsで`[]`」だけで、無限周回が起きるGrace側は検査していない（M5、T-3） |
| 3-3 | 修正済み（テストは空振り）／副作用あり | 予約済みのWSを奪わなくなった。#18テストはこのバグを再現できない入力しか作らない（M1、T-4）。予約が解放されない経路（N-3） |
| 4-1 | 修正済み（テストが無い） | M4 |
| 4-2 | 修正済み（構造として） | `vanishWindow`に分離した。「管理外化」の経路はまだ存在しないので、分離はコメントで約束しただけ |
| 5-1 | 修正済み | `TimerId`に`[<RequireQualifiedAccess>]`を付けた。サブDUには付けていない（衝突しにくいので可） |
| 5-3 | 意図的に先送り | `ForegroundChanged`を作るようになったが、`ignore`で捨てている。段ごとのパイプライン化はWindows着手時に回した（コメントに明記） |
| 5-4 | 修正済み（再発防止が無い） | 3種類の入力が往復できるようになった。`| other -> unsupported-input-event`のフォールバックが残っている。テストの値の幅が狭い（M6、T-5） |
| 5-5 | 修正済み | 根拠の無い`Confirmed`を書かなくなった。`Inferred [Sample seq]`は既知のギャップとしてコメントに明記した |

**残るblocking：無し。** ただし、blockingの修正（2-x、3-1）のうちテストで固定されているのは一部だけである。次のドメインで手を入れると、検出されずに後退し得る。テストを補強するまでは「直っているが、守られていない」状態と見るべきである。

---

## 1. blocking 6件の再確認

### 2-1〜2-5：GatedEffect表 — 修正済み
`src/Wm.Contracts/GatedEffect.fs:118-153`の`appliesTable`を、architecture.md:978-985の表と本文986行（RequestClose）に照らして、42セルすべて確認した。
- 2-1 `(RHiddenByWm, PresenceKind) = false`、2-2 `(RBelievedHung, PresenceKind) = false`、2-3 `RectMutating`の行のHung/HiddenByWmが`true`、2-4 `(RNotAlive, ForgetKind) = false`、2-5 `(RExcluded, ObserveKind) = false`。いずれも一致している。
- `checkOne`（157-186）は表を引くだけの実装になった。`Map.find`が失敗しうるのは表にキーが欠けたときだけで、型の上ではそれを防げない。ただし30のキーがすべて列挙されているので、今は問題ない。気になるなら`match kind, rejection with`の網羅的なmatchにすると、コンパイラが欠落を検出してくれる（nice-to-have）。
- 判定の優先順位（NotAlive → Excluded → NotMovable → Hung → HiddenByWm → 矩形）は、適用されない拒否理由を飛ばして次へ進む。たとえばForgetWindowが`NotAlive`かつ`Excluded`でも通過する。正しい。

### 3-1：Reconfiguring/latest=[] — ロジックは修正済み
- `Topology.fs:232-236`：どのPhaseでも`LastObserved`を更新する。Reconfiguringの間もPhaseは変えずに観測を残す。
- `229`：解除・Cap満了・UserCommandでSettlingへ入るときは、`LastObserved`を使う（無ければ現在のDisplays）。ロック/アンロック（失敗シナリオA）は、reconcileが何もせずに確定する。RDP（B）は、Reconfiguringの間の最後の構成で確定する。
- 3-1b（Settling中にReconfiguring trueが来て`latest`を捨てる）も解消した。Settlingの`latest`は直前の`MonitorTopologyObserved`と同じ値なので、`LastObserved`に残っている。
- 残る挙動：観測を伴わない解除でも、`TopologySettled current`と`ObserveAll`を必ず出す。トポロジが変わっていなくても、Placementを結線した後は全Pendingが`Superseded`になる。§7.3の「解除でSettlingへ入る」の文言どおりであり、ロック解除のたびに1回だけ起きることなので許容範囲。ただし、設計書にそう書いておく価値はある。
- **テストが無い（T-2）**。

---

## 2. should-fix 15件の再確認（要点だけ）

- **1-1**：`Beliefs.fs`の`LatestWins<'T> = { Slot; Watermark }`と`LatestWins.revise`は、提案どおりの形になった。`Focus.reviseForeground`は薄いラッパーになった。`WmState.step`は`now = envelope.MonotonicTime`を渡している（WmState.fs:109,124）。Healthも`now`を使うので、`combineAll`の`max LastRevisedAt`は単位がそろった。
- **1-2**：N-1、N-2を参照。
- **2-6/2-7/2-8/2-9/2-10**：上の表のとおり。
- **3-2**：`Topology.fs:237-241`。比較は`Set.ofList snapshots = Set.ofList (currentMonitors state1)`で、MonitorSnapshotの全フィールド（Id/StableKey/Bounds/WorkArea/Dpi/IsPrimary）を構造的に比べる。順序には依存しない。settle後のDisplaysのモニター集合は`latest`と一致するので（kept＝fresh、added＝latest）、`ObserveAll`への応答が同じ構成でも周回しないことを、コードを読んで確認した。
- **3-3**：`Topology.fs:161-164`。予約（他のモニターへの`Returns`を持つWS）を除くようになった。N-3を参照。
- **4-1**：`WmState.fs:79-87`。除去した後のzipperのFocusへ付け替える。Stackが空なら`Some (None, deadline)`にする。正しい。
- **4-2**：`vanishWindow`として分離した。注意点が1つある。`vanishWindow`は今もWorkspacesからの除去（`WorkspaceSet.removeWindow`）を直接行っている。設計上、Workspaces/Historyからの除去は`WindowUnmanaged`（DomainEvent）を契機にする側の責務である。Windowsを実装するときは、`vanishWindow`がWorkspacesを直接触るのをやめ、「信念の除去＋`WindowUnmanaged`の発行」に縮める必要がある。そうしないと、消滅した窓の除去が2つの経路で起きる。今の時点では無害。
- **5-1**：`Timers.fs:47`。JournalWriter・テストも含めて、すべて`TimerId.X`に置き換わっている。
- **5-3**：先送り。`ForegroundChanged`は作るようになった（WmState.fs:121-128）が、テストも利用者も無い。
- **5-4**：T-5を参照。
- **5-5**：`Health.fs:80-103`。`None`を返して「Mapに書かない」形になった。`applyEvidence`も`None`で状態を変えない（138行）。正しい。

---

## 3. テストが性質を検査できているか

### T-1 [should-fix] GatedEffectTestsは42セル中およそ24セルしか固定していない（M3）
- 固定されていないセルの例：`NotAlive × {RestoreBeforeMove, RequestForeground, SetPresence, ObserveWindow, Reprobe}`、`Excluded × {RestoreBeforeMove, RequestForeground, SetPresence, RequestClose}`、`NotMovable × {RestoreBeforeMove, ObserveWindow, Reprobe, ForgetWindow, RequestClose}`、`Hung × {ObserveWindow, ForgetWindow}`、`HiddenByWm × {RequestForeground, ObserveWindow, Reprobe, ForgetWindow}`。
- M3で、`HiddenByWm × RequestForeground`（hiddenなWSの窓をフォアグラウンドにする。最終防衛線そのもの）を`false`にしても、テストは成功する。
- 前回の提案は「全セルを列挙する」だった。テスト側に**実装とは独立した期待表**（`(拒否理由, Effect生成関数, 期待)`のリスト42行）を置き、`[<Theory>]`か`List.forall`で全行を回す。表がデータになったので、残りはこの1つだけである。
- 2-1のテストは`SetPresence(false)`しか見ていない。2-2は`SetPresence(true)`しか見ていない。表の意味は`shown`の値によらないので、両方の値で確認すること。

### T-2 [should-fix／blocking修正の固定] Reconfiguringを通るテストが1本も無い（M2）
- `tests/`には`ScreenReconfigurationInProgress`を使う行が1つも無い（grep済み）。3-1は前回唯一のblocking（Topology）なのに、バグを元に戻してもテストが通る。
- 追加すべき性質（前回3-1で挙げたもの）：「Stable/Settlingから、任意に `ScreenReconfigurationInProgress true` →（0回以上の`MonitorTopologyObserved`）→ `false` または `UserCommand` または Cap満了 → ScreenSettle の発火 と進めたとき、確定したDisplaysのモニター集合は、最後に観測したトポロジに等しい（観測が無ければ確定前のDisplaysに等しい）」。この1本で、M2と3-1bを両方とも捕まえられる。

### T-3 [should-fix] 3-2の回帰テストは自明なケースだけを見ている（M5）
- `TopologyScreenSettleTests.fs:72-80`は、「NoMonitorsの状態に`[]`が来る」だけを検査する。無限周回の本体である**Graceの間に、同じ構成が再び届く**ケースを検査していない。M5（Graceのガードを外す）で検出できない。
- 追加：任意の非空トポロジ T で、Stable → `Observed T` → ScreenSettleの発火（Grace）→ `Observed T` としたとき、Phaseが`Grace`のままであり、`TimerCommands = []`であること。

### T-4 [should-fix] #18のテスト（MonitorLifecycleTests）は、3-3のバグを作れない入力空間しか生成しない（M1）
- 生成するのは、最初から接続されている3台（id 0,1,2）の部分集合の列だけである。この入力空間では「一度も見たことが無い新しいモニター」が現れない。そのため、追加されたモニターには必ず自分への`Returns`があり、`bySmallestUnshown`には一度も到達しない。帰納的に、すべてのモニターは`byReturn`で戻る。**修正前のコードでも必ず成功する**（M1で実証した）。
- 前回3-3の失敗シナリオは、新しいモニターDが途中で接続されることが本質だった。提案：モニターのidを0..4から生成する。最初の接続は0..2に固定し、3,4は途中の列にだけ現れるようにする。最後に「0..2だけ」（3,4は外す）を接続し、0→ws0、1→ws1、2→ws2 であること、さらに`Returns`が空であることを検査する。
- 「`Returns`が最後に空になる」ことも検査していない。予約の漏れ（N-3）を見つけるには、これが必要。

### T-5 [should-fix] Journalの往復テストは値の幅が狭く、デコーダの取り違えを検出しない（M6）
- `SetManagement`は`Tiled`だけ、`LayoutArgs`は`MasterRatio 0.5m`だけ、`ErrorSpace`は`Win32LastError`だけ、`Code`は5に固定、MonitorSnapshotの座標は(0,0,1920,1080)に固定（負の座標や異なるDPIが無い）。M6で3つのデコーダを取り違えさせても、8/8で成功する。
- 「`UserCommand`の全ケース」は手書きのリストである。CoreCommandにケースが増えても、テストは自動では失敗しない（`coreCommandToJson`の網羅性は、F#の不完全マッチ警告が守るだけ。`TreatWarningsAsErrors`は設定されていない）。
- 提案：FsCheckの`Arb`で`Management`/`LayoutArgs`/`ErrorSpace`/`CoreCommand`を生成する（判別共用体はFsCheckが自動で生成できる。`decimal`は範囲を絞る）。あわせて`<TreatWarningsAsErrors>`を有効にするか、少なくとも`<WarningsAsErrors>FS0025</WarningsAsErrors>`を指定する。
- **5-4の再発防止が無い**：`JournalWriter.fs`の`inputEventToJson`には、まだ`| other -> "unsupported-input-event"`がある。次にWindowsが`WindowObserved`を`step`の分岐に加えたとき、また黙ってReplay不能になる。前回提案した「stepが分岐する入力 ⊆ Journalが往復できる入力」の構造的な検査（アーキテクチャテスト。あるいは、全InputEventケースを列挙して`unsupported`にならないことを検査するテスト）が入っていない。

### T-6 [nice-to-have] 4-1・ForegroundChangedのテストが無い（M4）
- `vanishWindow`のSwitchSettle付け替えを消しても、テストは通る。CrossDomainInvariantTestsの生成器に`SwitchSettle`の初期値と`WindowRemoved`を入れ、「`SwitchSettle`の対象は常に`None`か、生きている窓である」を不変条件として加えること。

### 良かった点
- `GatedEffectTests`の期待値は、実装の表を参照せずにハードコードしている（実装の表をそのまま写した同語反復のテストになっていない）。
- 1-2の回帰テスト（`FocusInvariantTests.fs:49-62`）は、`sOkB`と`sError`の大小を問わずに生成している。意図した場合をきちんと覆っている（ただしN-1のUnformed側は覆っていない）。
- Healthのテストを`option`化したとき、`None -> false`として、本来`Some`であるべき箇所を厳しく判定している。空振りしていない。

---

## 4. 修正で新たに入った問題

### N-1 [should-fix] `LatestWins.revise`：Errorが水位を進めるかどうかが、UnformedとBelievedで逆になっている（Beliefs.fs:63-79）
- Believed：Errorは`Watermark`を進めない（1-2の修正）。Unformed：Errorは`Watermark`をErrorのSequenceまで進める（1-3の対策）。
- **失敗シナリオ**：起動直後に、Shellの初回スナップショットの`GetForegroundWindow`が失敗し（ロック画面・セキュアデスクトップなど）、`ForegroundObserved Error` seq=9 → `Unformed`、wm=9 になる。イベント経路で遅れて届いた`Ok(Some C)` seq=8 は、`8 > 9`が偽なので捨てられる。`ForegroundObserved`は次にフォアグラウンドが変わるまで来ないので、`focused = Elsewhere`のまま固定される。FocusNext/FocusMostRecentは、zipperの基準が無い状態で動く。これは前回1-2の失敗シナリオそのもので、それがUnformed状態で再発している。
- 同じ「Errorより古く、値（無し）より新しいOk」を、Believedでは採用し、Unformedでは捨てる。1つの規則が2つの意味を持っている。
- **修正案**：前回の提案どおり、水位を2本に分ける。`{ Slot; ValueSeq: int64 option; ErrorSeq: int64 option }`とし、Okは`> ValueSeq`なら採用する（`ErrorSeq`がそれより新しければ確度は`Stale`）。Errorは`> max ValueSeq ErrorSeq`ならStaleにして`ErrorSeq`を進める。こうすると、1-2と1-3を同じ1つの規則で満たせる。

### N-2 [should-fix／設計書] 「新しい失敗を知っていても、古いOkをConfirmedにする」という選択が暗黙のまま
- Believed A（seq 5）→ Error（seq 7）→ Ok B（seq 6）は、`Confirmed B`になる。seq 7で問い合わせが失敗したことを知っているので、§4.5の`Confirmed`（直接の観測で裏付けられた**最新**の値）としては`Stale`の方が正確である。テスト（`FocusInvariantTests.fs:62`の`b.Confidence = Confirmed`）は、この選択をそのまま固定している。
- N-1の修正案（`ErrorSeq`）を採ればStaleにできる。どちらを採るにしても、§4.5に1行書くこと。Windowsの`ClassInputBeliefs`（5フィールド）と`GeometryBeliefs`（5フィールド）が、この関数をそのまま使うことになる。

### N-3 [should-fix] 3-3の修正により、`Returns`の予約が解放されない経路が実害を持つようになった（Topology.fs:161-164, 139-145, 171-174）
- `Returns`のエントリが消えるのは、`byReturn`でそのモニターにWSが戻ったときだけである。修正前は、漏れたエントリは割当に影響しなかった。修正後は、エントリが残る限り、そのWSは新しいモニターへの割当候補から**永久に**外れる。
- **漏れる経路1（MonitorIdが安定しない）**：`Returns`は`MonitorId`をキーにする。Shellが再接続時に別の`MonitorId`を振ると（`StableKey`は同じでも）、`byReturn`は一致しない。エントリは残り続け、そのWSは予約されたままになる。MonitorIdが再接続をまたいで同じかどうかは、設計書（§4.2/§4.4）に契約が無い。
- **漏れる経路2（予約済みのWSが別の経路で表示される）**：`byAssignment`（147-153）は予約を見ない。`Assignments {ws1 → PrimaryMonitor}`で、primaryのPを外すと → `Returns {ws1→P}`。新しいprimaryのDを接続すると → `byAssignment`がws1をDに出す（予約は残る）。その後Pを再接続すると → ws1は表示中なので`byReturn`は使えない → Pは別のWSを取る。`Returns {ws1→P}`は永久に残り、Dを外して戻しても、Pは二度とws1を受け取らない。将来の`SwitchWorkspace`（view意味論で、hiddenなWSを今のモニターに出す）も同じ形でエントリを残す。
- **提案**：(a) WSが`byReturn`以外の経路で表示された時点で、そのWSの`Returns`を消す（予約より明示的な表示を優先する）。(b) `byAssignment`の候補からも予約済みを外すか、外さないなら(a)で必ず消す。(c) `Returns`のキーを`StableKey`にするか、MonitorIdの安定性を§4.2の契約に書く。T-4の「最後に`Returns`が空」の検査を入れれば、(a)/(b)の漏れは性質テストで捕まえられる。

### N-4 [nice-to-have] トップレベルの（バッチに入っていない）Effectの間で`restoreSeen`を共有している（GatedEffect.fs:194-214）
- `go Set.empty effects`のトップレベルでも、`RestoreBeforeMove w`の後に続く**バッチ外の**`SetRect w`が通る。§6.2の文言は「同一ApplyBatch内」なので、バッチ外の並びはShellが別々に適用しうる（2-7と同じ理由）。トップレベルでは`restoreSeen`を常に空にするか、「バッチ外の`SetRect`はNeedsRestoreなら必ず拒否する」にそろえること。今は`NeedsRestore`が常に空なので顕在化しない。

### N-5 [nice-to-have] Settling中は、同じ構成の観測でもdebounceを延長する（Topology.fs:247）
- 3-2の後半（「Settling中は、同じ内容でもdebounceを延長しない、を検討」）は採用されていない。hardDeadlineで頭打ちになるので、実害は確定が最大30秒遅れることに限られる。定期的な全体Snapshotがトポロジを含むShellでは、Settlingが毎回hardDeadlineまで伸び得る。`Settling (latest, …)`で`Set.ofList snapshots = Set.ofList latest`ならタイマーを再始動しない、で済む。

### N-6 [should-fix／プロセス] コードのコメントが、リポジトリに無いレビューを27か所で参照している
- `grep -rn "Opusレビュー" src`は27件ある（「Opusレビュー2-9」「3-1」など）。参照先の`opus-review-phase-a.md`はセッションのscratchpad（`/tmp/...`）にしか無いので、セッションが終わると参照先が消える。これまでのレビューは`docs/review-*.md`としてコミットされている。前回のレビュー（と本ファイル）を`docs/review-phase-a-round1.md`/`round2.md`としてコミットするか、コメントの参照をADR・節番号に置き換えること。

### N-7 [確認のみ／問題なし]
- `gate`の非末尾再帰：Effectのリストは高々数十件なので、スタックの問題は無い。拒否の並び順は入力順を保っている（`childRej @ restRej`、`(rid, rej) :: restRej`）。
- `WmState.step`の`let state = … vanishWindow …`による束縛の置き換え：後続の`state.Focus`/`state.Health`/`state.Topology`はすべて置き換え後の値を読み、`stateAfterBeliefs = { state with … }`も置き換え後の値から作る。`vanishWindow`の結果が取りこぼされる箇所は無い。`focusedBefore`は置き換え前の値から取るが、消えた窓は`workspaceOf = None`で`Elsewhere`になる。`updateHistory`は先頭へ追加しないので、無害。
- `Topology.update`の`| Stable | Grace _ when … ->`：F#の`when`は、or-patternの全体にかかる。意図どおり。
- 4-3（Reprobesを除去しても`CancelTimer`を出さない）は手付かず（前回もnice-to-have）。

---

## 5. コードと設計書の不一致のリスク（一言）

**中程度。Windowsドメインに着手する時点で高に上がる。** 不一致は、次の実装者が規範として読む箇所に集中している。§4.5の信念表は、今も`ClassInputBeliefs`/`GeometryBeliefs`を`BeliefSlot<'T>`で定義している（architecture.md:606-607）。§6.2の`GateInput`は、今も`RestoreIssuedInBatch`を持ち、`gate`を公開関数として示している（955行と973行付近）。§4.4の`TopologyState`には`LastObserved`が無い（408-413行）。このため、設計書どおりに書くと、今回直した1-1と2-7がそのまま再発する。コメントの参照先（N-6）も消えるので、「なぜコードが設計書と違うのか」を辿れなくなる。Windowsドメインの最初のコミットより前に、§4.4/§4.5/§6.2の3か所だけでも反映することを強く勧める。

---

## 推奨する次の一手（優先順）
1. pushして`phase-a-build.yml`をCIで回す（現状、CIは一度もこのコードを見ていない）。
2. T-2（Reconfiguringの性質テスト）とT-4（新しいモニターを含む#18と、`Returns`が空になる検査）を追加する。どちらも、blockingだった修正を初めて固定するテストになる。
3. N-1/N-2：`LatestWins`の水位を2本にして、§4.5に1行書く。`ClassInputBeliefs`がこの関数を使い始める前にやること。
4. T-1（ゲート表の全42セル）、T-3、T-5（Arbと`FS0025`のエラー化、unsupportedフォールバックの検査）。
5. N-3（Returnsの解放規則）は、`SwitchWorkspace`をTopologyに入れるときに必ず一緒に入れる。
6. 設計書の§4.4/§4.5/§6.2を反映する。レビューファイルを`docs/`にコミットする（N-6）。
