> **反映状況（v2.4）**：本レビューの指摘はarchitecture.md ADR-014および関連節に反映済み。レビューでは決まらなかった3つの判断は以下で確定した：
> - `EffectFailure.Denied`の粒度（1-8）：3種（IntegrityMismatch/ForegroundDenied/OtherDenial）で開始し、Phase 0の失敗カタログで必要になった分だけ`classify`の対応表に追加する。
> - `SetRect`発行前のハング事前確認（1-3の代案）：毎回必ず`SendMessageTimeout(WM_NULL, SMTO_ABORTIFHUNG)`で確認する（F06で0ms実証済みのためコストは無視できる）。
> - 起動時・Rollback時の`PinnedPolicy`読込み（5-1）：現行Analyzerで再分析しない。`ArtifactIdentity.analyzerVersion`を信頼する。REPL経由の新規Deployのみ、Host側で必ず再分析する（5-2）。

# Opusレビュー：「Result型によるエラー耐性設計」提案5点（対 architecture.md v2.3）

対象：architecture.md v2.3（全文）、failure-catalog.md、feasibility.md §4-§5
レビュー方針：読み取りのみ。指摘ごとに「提案番号／章番号・引用／壊れるシナリオ／代案」を示す。重要度は【高】【中】【低】。

---

## 0. 結論サマリ

- 方向性（OS非依存の小さな失敗型をContractsに置く、Validatorの全違反報告、境界での例外捕捉、CEの見送り）はいずれも妥当。ただし**型で保証できると書いている範囲が実際より広い**。特に#2と#5は「シリアライズ境界」「Replay」「Retry経路」「プロセス境界（REPL）」で型の保証が途切れ、そこが設計書の重要な要件（12.2 Fixed-log Replay、16.2 最後の正常版の維持、F-5）とぶつかる。
- 最も重要な抜けは次の5つ。
  1. 【高】#1：リッチな`Win32CallDetail`をJournalに残すと書いているが、17章は「Journalのイベント型は契約側に置く」と決めている。Wm.Windows内部の型はJournalPayloadに入れられない。また、どちらの形を永続化しReplayで何をCoreに流すかが、ADR-004とADR-013の考え方に照らして決まっていない（→1-1, 1-2）。
  2. 【高】#1：`TimedOut`は、6.2/7.1で確定した`SWP_ASYNCWINDOWPOS`既定方針の下では`SetWindowPos`からは発生しない（F08/F10：0〜1msでリターン）。素朴に書いたSimulationは「タイムアウト」を返し、実機は「成功→観測なし→Pending期限切れ」を返すので、ADR-002の「同じCore」でも**入力列が食い違う**（→1-3）。
  3. 【高】#2：`ValidatedPlan`のprivateコンストラクタで守れるのは「Desiredを作る経路」だけ。Effectは公開DU（7.1）であり、Reconcilerの再送（7.2 Retry/Backoff）や`RestoreBeforeMove`/`RequestForeground`は、検証時点から時間が経ってからEffectを作る。受け入れ条件4番と15.1 #1は型だけでは成立しない（→2-1）。
  4. 【高】#5：PolicyArtifactを「Analyzerを通った場合のみ構築可能」にすると、Fixed-log Replay（12.2「ピン留めした当時のPolicy」）、Rollback（10.2 Deploy）、起動時の最後の正常版の読み込み（16.2）で、**今のAnalyzerが拒否する過去のPolicy**を読めなくなる（→5-1）。
  5. 【高】#4：生のP/Invoke（`[<DllImport>]`）は失敗時に`Win32Exception`を投げず、戻り値と`GetLastError`/HRESULTで知らせる。提案の`tryNative`は、Wm.Windowsの実際の失敗の大半を素通りさせる。さらに、例外は「Core/Policy/Layoutsに流れ込まない」のではなく、**ユーザーのLayout関数（5.6）の内部で発生する**（→4-1, 4-2）。

---

## 提案1：EffectFailureの二層分離

### 1-1【高】リッチ型「Wm.Windows内部」は17章の依存規則上、Journalに入れられない
- 引用：17章「JournalWriterのI/OはShell側、**Journalのイベント型は契約側に置く**」。9.2の`JournalEntry.Payload: JournalPayload`。提案は「Wm.WindowsはJournal用にリッチな情報を残す」としている。
- 壊れるシナリオ：`Win32CallDetail`はWm.Windowsに定義されている。JournalPayload（Contracts）がこの型を参照すると、Contracts→Windowsの逆依存になる（3.1違反）。避けるためにペイロードを`string`/`JsonElement`の不透明な塊にすると、Linux上のJournal Reader・Replay・Explain（17.1「Linux：…Journalの純粋部分…Replay」）がこの詳細を型付きで読めなくなる。そうなると、13章Step 2「ネイティブ呼出結果」をExplain Viewで表示できない。
- 代案：OS非依存の名前で「生の呼出記録」をContractsに置く。Wm.Windowsはそれを埋めるだけにする。
  ```fsharp
  // Wm.Contracts
  type ErrorSpace = Win32LastError | HResult | NtStatus | BoolFalseNoCode
  type NativeCallRecord = {
      Api: string            // "SetWindowPos" 等。Simulationも同じ語彙を使う
      Space: ErrorSpace
      Code: int              // BoolFalseNoCode のときは 0
      ElapsedUs: int64       // 計測値。入力として記録する事実
  }
  ```
  `Message`（FormatMessage）はロケール依存であり、端末ごとに文字列が変わる。表示時に再計算すれば足りるので永続化しない。`Params: string`は自由文字列で、9.6の機密フィールド（タイトル等）が紛れ込む穴になる。必要なら`WindowId`と矩形だけを型付きで持つ。

### 1-2【高】「何を永続化し、Replayで何をCoreに流すか」が未定。ADR-013と同型の決定が必要
- 引用：ADR-004「『Coreの外から来た事実』（入力、**OS呼出結果**…）を永続化の中心」。ADR-013「Raw観測…をJournalの入力として記録する…疑似OSはRaw層から駆動できるようにする」。14章`normalizerSchemaVersion`。
- 選択肢と壊れ方：
  - (a) 要約後の`EffectFailure`だけを記録する場合：`toEffectFailure`の分類バグ（例：`ERROR_ACCESS_DENIED`=5を`PermissionDenied`にせず`NativeCallFailed`にした）を後から修正しても、過去のJournalからは正しい分類を再計算できない。ADR-013がNormalizerについて否定した構造と同じものを、Effect結果の層で作り直すことになる。
  - (b) リッチ情報と要約を両方記録する場合：ADR-004の「再計算できるものは永続化しない」に反する重複になる。しかも両者が食い違ったときにどちらが正しいかの規則がない。
- 代案：(c) **生の`NativeCallRecord`を永続化し、分類関数`classify: NativeCallRecord -> EffectFailure`を純粋関数としてNormalizerの一部にする**（Shell/Simulation共用、ADR-013と同じ扱い）。分類関数のバージョンは14章の`normalizerSchemaVersion`に含める。Fixed-log Replayでは当時のNormalizerを固定して使う。これで分類バグもReplayで再現・回帰テスト化できる。23章の「必ず独立レビューするもの：記録済みRaw入力」の対象にもそのまま入る。

### 1-3【高】`TimedOut of waitedMs`は既定方針の下で`SetRect`からは発生しない。Sim/Windowsの入力列が乖離する
- 引用：6.2「Shell側の`ApplyBatch`実装は個別`SetWindowPos` + `SWP_ASYNCWINDOWPOS`を既定とする」。failure-catalog F08「`SWP_ASYNCWINDOWPOS`付きで…**即時（0ms）**にリターン」、F10「3窓すべて1ms以内」。F06「`SendMessageTimeout(…SMTO_ABORTIFHUNG…)`は…**即時（0ms）**に失敗」。
- 壊れるシナリオ：
  1. 実機でハング窓に`SetRect`を送ると、呼出は成功（`EffectSubmitted`）で返り、観測が来ないまま7.2の`Timeout`（Pending期限切れ）に至る。`EffectTimedOut`/`TimedOut`はどこからも発生しない。
  2. 同じシナリオをSimulationに実装する人は、`EffectFailure.TimedOut`という型があれば自然にそれを返す。これでCoreに入る入力列が実機と違ってしまい、7.2の状態遷移のうち実機で通らない枝（`EffectTimedOut → Suspend`）だけがテストされる。実機で通る枝（`Timeout → Retry/Backoff/Suspend`）は、ハング窓に対しては**Retryしてしまう**ことになる。7.2は「EffectTimedOut → Suspend（ハングした対象。再試行しない）」と区別しているのに、実機ではこの区別が効かない。
  3. SMTO_ABORTIFHUNGによる即時中断を`TimedOut 0`と表すことになり、「待った時間」という意味が崩れる。
- 代案：
  - 「待って期限を越えた」と「OSが既にハングと判定済み」を分ける：`TargetHung`（SMTO_ABORTIFHUNG／IsHungAppWindow由来）と`DeadlineExceeded of waitedMs`（ASYNCを使えない呼出が専用スレッド上で期限を越えた）。
  - 7.2の`Timeout`枝に「ハング判定済みの窓ならRetryしない」という条件を入れる。Shellが`SetRect`発行前に`SendMessageTimeout(WM_NULL, SMTO_ABORTIFHUNG)`で事前確認し、ハングなら`TargetHung`で拒否する方式なら、F06の0ms特性を使えるので安価に済む。
  - Simulationの`WindowBehavior`（12.1）に`Hung`部品を追加し、その振る舞いを「呼出は成功、観測は来ない、事前確認ではTargetHung」と**実機の観測（F06/F08）に合わせて**定義する。12.1の「WindowBehaviorは…Phase 0の失敗カタログ…から選ぶ」と整合する。

### 1-4【高】既存の`InputEvent.EffectRejected of RequestId * string`／`EffectTimedOut`との関係が未定義
- 引用：7.1`EffectRejected of RequestId * string`、`EffectTimedOut of RequestId`。
- 壊れるシナリオ：`EffectFailure.TimedOut`と`InputEvent.EffectTimedOut`が並存すると、同じ事実を2通りに表現できる。Reducerの片方の分岐が未テストのまま残る。また`string`が残ると、Coreが文字列で分岐する実装が紛れ込み（例：`if msg.Contains "Access"`）、ロケールが変わるとReplayの決定性が崩れる。
- 代案：`EffectRejected of RequestId * EffectFailure`に置き換えて`EffectTimedOut`を削除する（あるいはその逆）。どちらに寄せるかを明記する。

### 1-5【高】`ApplyBatch`は1つのRequestIdで複数窓を包む。窓ごとの失敗を運べない
- 引用：7.1`ApplyBatch of RequestId * Effect list`。6.2「バッチの一部だけが失敗した場合の扱い…はPending追跡（7.2）と合わせて定義する」。7.2「`IntentEpoch`は窓単位で発行する」。15.1 #12。
- 壊れるシナリオ：個別`SetWindowPos`方式では、3窓中1窓だけが`ERROR_INVALID_WINDOW_HANDLE`（窓が消えた）になるのが普通に起きる。`EffectRejected of RequestId * EffectFailure`ではバッチ全体の失敗としか言えない。その結果、生きている2窓のPendingまで`Retry/Backoff/Suspend`へ進んでしまう。
- 代案：子Effectそれぞれが自分のRequestIdを持ち（`ApplyBatch of BatchId * Effect list`）、結果を子ごとに返す。または`EffectBatchReturned of BatchId * (RequestId * Result<unit, EffectFailure>) list`という入力を追加する。15.1 #12の「明示された失敗単位」をここで「窓単位」と決める。

### 1-6【中】`toEffectFailure`の例は全件を`NativeCallFailed`に写しており、`TargetGone`/`PermissionDenied`がどこからも生成されない
- 壊れるシナリオ：ケースを定義しても、分類規則がなければ実装者ごとに写し方がばらつく。たとえば昇格窓（UIPI）への`SetWindowPos`は通常`ERROR_ACCESS_DENIED`(5)で返るが、これを`PermissionDenied`にするのか`NativeCallFailed{Code=5}`にするのかが未定である。18章Phase 0の検証項目「昇格（管理者）窓への操作がどう失敗するか」は、まさにこの写し方を決めるための項目である。
- 代案：1-2の`classify`に、コードから分類への対応表（1400→TargetGone、5→PermissionDenied、…）をテーブルとして持たせる。Phase 0の結果で表を埋め、表そのものを回帰テストにする。

### 1-7【中】`Code: int option`のNoneの意味が曖昧。HRESULT系APIと混ざる
- 引用：4.2「`DWMWA_EXTENDED_FRAME_BOUNDS`を優先的に取得し失敗時のみ`GetWindowRect`にフォールバック」。`DwmGetWindowAttribute`はHRESULTを返し、`GetLastError`は使わない。8.1「SetForegroundWindowはOSの制限で拒否され得る」。この関数は`FALSE`を返しても拡張エラーコードを設定しない。
- 壊れるシナリオ：HRESULT `0x80070005`（E_ACCESSDENIED）とWin32エラー`5`が同じ`int`に入り、どちらの空間のコードか判別できない。`None`が「コードなし（SetForegroundWindowの拒否）」なのか「取り損ねた」なのかも区別できない。フォーカス拒否（B-3、Phase Bで最重要の検証項目）はJournal上で他の失敗と見分けがつかなくなる。
- 代案：1-1の`ErrorSpace`で空間を明示する。フォーカス拒否は頻出かつCoreの扱いが違う（Retryせずユーザー操作を待つ）ので、`ForegroundDenied`を独立したケースにする。

### 1-8【中】クローズドDUの切り方が「失敗の原因」ベースで、早すぎる確定（premature closure）になる
- 根拠：failure-catalogで実際に出た失敗は、ブロック（F07/F09）、即時中断（F06）、`1409`（F11、ただしEffectではなく起動時の登録）、窓が見つからない（F02）、黙って誤った値を返す（4.2「生成直後は`DwmGetWindowAttribute`が`GetWindowRect`と同値を返す」）と多様である。このうち最後の「誤った値」は**呼出失敗ですらない**。原因ベースのDUで受け止めようとすると、Phase 0〜Bで新しい失敗が見つかるたびにContractsのDUが増える。Contractsの変更はCore・Simulation・Journalスキーマ（9.5）・DTOに波及する。
- 代案：**Coreが分岐する軸は7.2の状態遷移が決める**と割り切る。Contractsの型は「Coreの対応の違い」だけを表す。
  ```fsharp
  type EffectFailure =
      | TargetGone                         // → Cancelled
      | TargetHung                         // → Suspend, 再試行しない
      | Denied of DenialKind               // → Suspend/Floating（DenialKind = Integrity | Foreground | Other）
      | Transient                          // → Retry/Backoff
      | Unclassified                       // → Retry上限付き、診断で要調査フラグ
  ```
  原因の多様さは`NativeCallRecord`（1-1、生の事実）の側で受け止める。新しい失敗モードが見つかっても、`classify`の表に1行足すだけで済み、Coreは変わらない。`Unclassified`を明示的に持たせることで「分類漏れ」をJournal上で数えられるようになる。これは11.1 EvidenceGapの考え方（「何をまだ知らないか」を機械可読にする）と同じである。
- 範囲の明記：ホットキー登録（F11）、WinEventフック登録、パイプ作成（10.1）などはEffectではない（7.1の`Effect`に含まれない）。これらを`EffectFailure`に押し込まず、Wm.Host側の起動時エラー型として分ける。そう明記しないと、DUがなし崩しに肥大化する。

### 1-9【中】観測（Observe系）の失敗を表す型と入力イベントがない
- 引用：7.1`ObserveWindow of WindowId`/`ObserveAll`はEffectである。しかし`InputEvent`には観測失敗のイベントがない。
- 壊れるシナリオ：`GetWindowText`（`WM_GETTEXT`を同期送信するため、ハング窓ではブロックする）や`DwmGetWindowAttribute`が失敗したとき、`WindowSnapshot`のフィールドが`None`（Unknown、5.4）になるのか、`ObserveWindow`がEffect失敗として返るのかが決まっていない。前者なら「取得していない」と「取得に失敗した」が同じ`None`になり、11.1の`MissingVisibleBounds`等のGapを作る根拠が失われる。
- 代案：`Observation<'T>`に取得失敗を表す枝を持たせる（`Value: Result<'T, EffectFailure>`、または`Failed of NativeCallRecord`）。こうすると、EvidenceGapの「取得できなかった」と「まだ要求していない」を区別できる。

---

## 提案2：PlanValidatorのaccumulate＋smart constructor

### 2-1【高】privateコンストラクタが守るのはDesiredの生成経路だけ。受け入れ条件4番は型では成立しない
- 引用：22章4「任意のLayout関数出力もPlanValidatorを通過しなければEffectを発行できない」。7.1`Effect`は公開DU（`SetRect of RequestId * WindowId * PixelRect`）。6.1「Desired Update → **Reconciler** → Effect IR」。7.2「Timeout → Retry/Backoff/Suspend」。
- 壊れるシナリオ：
  1. Reconcilerは`ValidatedPlan`を受け取らなくても`SetRect`を構築できる（Effectはpublicなので）。誰でも書けてしまう。
  2. **鮮度の問題**：時刻t0に検証した`ValidatedPlan`からDesiredを作り、t1でTimerが切れて7.2のRetryが同じDesiredからSetRectを再発行するとする。その間に対象がExcluded化・破棄されていても、手元にあるのは「検証済み」の証明書である。6.2 #1（生存確認）と15.1 #1（「生存していないWindowIdへ新しいSetRectを発行しない」）を破るEffectが、型チェックを通ったまま出ていく。
  3. `RestoreBeforeMove`、`RequestForeground`（CoreCommandの`FocusNext`由来）、REPLのIntervene（10.2）はLayoutを通らないEffectである。これらはPlanValidatorの守備範囲外なのに、16.1は「全てのOS変更EffectはPlanValidatorや権限ゲートを通る」と求めている。
- 代案：二段構えにする。
  - (a) `ValidatedPlan`は維持する（Layout出力からDesiredへの経路の保証）。
  - (b) Effectを外へ出す唯一の地点に**毎ステップの安価な最終ゲート**を置く：`gateEffects: CoreState -> Effect list -> Effect list * GateRejection list`（生存・Managed・非Excluded・矩形正値のみを検査）。Effectのコンストラクタをinternalにするのは、Simulationのテストが煩雑になるので勧めない。代わりに`StepResult.Effects`の型を`GatedEffect list`（privateコンストラクタ）にし、Shellが受け取る型を`GatedEffect`だけにする。こうすると、型レベルの保証がDesiredではなく**Shellへの入口**にかかる。受け入れ条件4番が実際に求めているのはこちらである。
  - 15.1 #1/#6の性質ベーステストは型があっても省略しない（型が保証するのは「どこかで検証した」ことだけで、「直前に検証した」ことではない）。

### 2-2【高】`validate`のシグネチャでは6.2の必須検査が実装できない
- 引用：4.4の`WmState`は`Workspaces / ActiveWorkspace / WindowWorkspace`しか持たない。Management状態、生存、モニター/WorkAreaの情報はない。6.2 #4「モニター/WorkArea参照の整合性」、#6「そのレイアウトに必要な対象の未配置を検出する」、#3「座標演算で整数オーバーフローがない」。5.6`Placement = { Window; Target }`にはMonitorがない。
- 壊れるシナリオ：
  - `checkManaged state`/`checkNotExcluded state`/`checkMonitors state`は、引数の`WmState`に情報がないので書けない。実装時に`WmState`へ場当たり的にフィールドが足され、4.4（ADR-011で「先に固定」とした状態）が崩れる。
  - `UnknownMonitor of MonitorId`は、`Placement`にモニター参照がないので**検出の材料がない**。実際に必要な検査は「TargetがWorkArea内にあるか」である。
  - 6.2 #6（未配置検出）と#3（オーバーフロー）に対応するエラーケースがない。Master–Stackが窓を1つ落としても`Ok`になる。
- 代案：
  ```fsharp
  val validate : ValidationInput -> Placement list -> Result<ValidatedPlan, ValidationError list>
  // ValidationInput = { Context: LayoutContext; Management: Map<WindowId, Management>; Alive: Set<WindowId>; PartialAllowed: bool }
  type ValidationError =
      | ... // 既存
      | MissingPlacement of WindowId            // 6.2 #6
      | OutsideWorkArea of WindowId * PixelRect // 6.2 #4（UnknownMonitorの代替）
      | ArithmeticOverflow of WindowId          // 6.2 #3
  ```
  `ValidatedPlan`には検証に使った`LayoutContext`のダイジェスト（またはIntentEpochの集合）を持たせる。2-1(b)の鮮度判定にも使える。

### 2-3【高】accumulateと6.3の「明示された安全な単位」が噛み合わない
- 引用：6.3「Validatorの失敗：**Plan全体または明示された安全な単位**を拒否し、診断Journalを残す」。6.2「重なりを一律に禁止しない：Monocleは意図的に同位置へ重ねる」。
- 壊れるシナリオ：エラーがフラットな`ValidationError list`で返るので、呼び出し側は「WindowIdが付いているエラーの窓だけ除いて残りを適用する」という素朴な部分適用を書きがちである。ところがMaster–Stackの幾何は全窓を前提に計算されている（stackの高さ = WorkArea高さ / stack数）。1窓を除いて適用すると、**隙間が空いた配置**がOSへ出ていく。これは6.2 #6（未配置検出）が防ごうとしている状態そのものである。逆に、`UnknownMonitor`のような全体に関わるエラーと窓単位のエラーが混ざるため、どの単位で拒否すべきか機械的に決められない。
- 代案：「集める（診断）」と「決める（拒否単位）」を別関数に分ける。
  ```fsharp
  type Disposition =
      | RejectWholePlan                     // 既定。6.3の前回Desired維持へ
      | RelayoutWithout of Set<WindowId>    // 窓がvalidate時点で死亡・Excluded化していた場合
  val decide : ValidationError list -> Disposition
  ```
  「安全な単位」は**プランから窓を引くこと**ではなく、**LayoutContextから窓を引いてLayoutを1回だけ再計算すること**と定義する（再計算後も失敗すれば全体を拒否）。こうすると、Layout関数の幾何的な一貫性を保ったまま、窓の死亡という頻出ケースに対応できる。再計算が1回で止まることは性質ベーステストで保証する（15.1 #7の要求数の無制限増殖防止と同系統）。
- 補足：同じ死亡窓から`UnmanagedOrDeadWindow`、`ExcludedTarget`、`DuplicateInPlan`が重複して出るため、診断出力が膨らむ。死亡チェックを最初に行い、死亡した窓は後続の検査から除外する（「部分的fail-fast」）と読みやすくなる。

### 2-4【中】永続化：`ValidatedPlan`/`ValidationError`自体はJournalに不要（ADR-004）。ただしCheckpointとIncident Bundleで必要になる
- 引用：9.2「揮発性リングバッファ＋Replay再構成で足りるもの：…`PlanAccepted`/`PlanRejected`…」。9.5「Snapshotには…」。13.1`checkpoint.json  # snapshot + initial Core state`、`input-journal.ndjson  # reducer inputs and **relevant decisions**`。
- 整合点：Journalについては、提案は永続化を必要としない（Replayで再計算するので）。ここは良い。
- 壊れるシナリオ：
  1. CoreStateのDesiredを`ValidatedPlan`で持つと、Checkpointでシリアライズすることになる。復元時には`validate`を再実行する必要があり、そのための`ValidationInput`が復元時点で揃っていなければならない。さらに**Validatorの規則を強化したバージョン**で古いCheckpointを読むと復元に失敗し、9.5の「低頻度Checkpoint + 増分Journal」による再生が始められない。
  2. 13.1は「relevant decisions」をBundleに入れると書いており、ADR-004の改訂と食い違っている。決定を入れるなら`ValidationError`のDTOが要る。
- 代案：
  - CheckpointにはDesiredを「生の`Placement list` DTO」として保存する。復元時には**Desiredを信用しない**（16.2「保存状態をOSの真実とみなさず、全体再観測で再同期してから再開」と同じ方針）。再観測の後に再Plan・再validateする。こうすれば`ValidatedPlan`のDTOは不要になる。
  - Fixed-log Replayのために正確なCore状態の復元が必要な場合に限り、ピン留めしたCoreBuildIdと同じValidatorで再検証する（1-2と同じ「当時の版を固定」の原則）。
  - 13.1の「relevant decisions」は削除するか、「Replayで再計算した派生物（原記録ではない）」と注記する（9.5「派生ビューと原記録を区別する」）。

### 2-5【中】F#の`private`表現の保証範囲と、リフレクション系シリアライザーによる迂回
- 壊れるシナリオ：
  - F#の`private`は**囲んでいるモジュール／名前空間宣言グループ**に対するアクセス制限である。名前空間直下に型を置いた場合、同じアセンブリの同じ名前空間内の別ファイルから構築できてしまう、という保証範囲のずれが起き得る。`ValidatedPlan`は必ず`module PlanValidator`の中に置き、コンパイルが通らないことを確認するテスト（negative compile test、または`Assembly`の公開メンバーを検査するテスト）を1本用意する。
  - `FSharpValue.MakeRecord`は`allowAccessToPrivateRepresentation`引数を受け付ける。リフレクションベースのJSONコンバーター（F#用のSystem.Text.Json拡張など）が設定次第でprivateレコードを直接デシリアライズできるため、**`ofDto`を通らずに検証済み型が生成される経路**がライブラリの設定一つで生まれる。
- 代案：「smart constructor型を直接シリアライズしない（必ずDTO経由）」を5.2の注意書きと同じ箇所に明文化する。17.1の共通ゲートに、Contracts/Layouts/Policyのsmart constructor型がシリアライザーのコンバーター登録対象に入っていないことを検査するアーキテクチャテストを入れる。

### 2-6【低】`ApplyBatch`/`RestoreBeforeMove`の検証が`ValidatedPlan`から抜けている
- 引用：18章Phase A「独立したPlanValidator（`ApplyBatch`/`RestoreBeforeMove`を含む、A-4）」、19章「PlanValidator.fs（`ApplyBatch`/`RestoreBeforeMove`を含む）」。
- 壊れるシナリオ：`ValidatedPlan`は`Placements`しか持たない。「最大化窓は`RestoreBeforeMove`を先に出す」（6.2）という契約をValidatorが検査する場所がない。結果として最大化窓にSetRectだけが出て意図通りに動かず、Mismatchが続いてSuspend化する。
- 代案：この検査は2-1(b)の最終ゲート（Effect列が対象）に置くと明記し、PlanValidator（Placement列が対象）と役割を分ける。19章の記述も合わせて修正する。

---

## 提案3：result { } CEの見送り

### 3-1【中】6.1での見送り自体は妥当。ただし理由が違う。本当の障害は「Traceの持ち回り」と「エラー型の不一致」
- 引用：7.1`StepResult = { Next; Effects; Trace: DecisionTrace list }`。9.1「DecisionTrace：直近の判断は…リングバッファ…即時のExplainに使う」。5.6`Result<Placement list, LayoutError>`と提案2の`Result<ValidatedPlan, ValidationError list>`。5.4 Classifyは`Truth`（Unknownはエラーではない）を返す。
- 壊れるシナリオ：
  - `Result.bind`は失敗した時点で、それまでの段のTrace（どのRuleを選び、どのLayoutを呼んだか）を捨てる。Explain（9.1）が最も必要とするのは**失敗時**の経路であり、bindの連鎖をそのまま書くと、PlanRejectedの直前に選ばれたRuleがTraceに残らない。
  - 段ごとにエラー型が違うので、`Result.bind`だけでは連結できない。いずれにせよ`Result.mapError`で`PlanningFailure`に統一する必要がある。
  - Classify（三値）はResultに乗らない。Unknownは「保留して観測を追加する」という正常系の分岐（5.4 A-5）である。
- 代案：6.1は「各段が`(結果, Trace断片)`を返し、`step`が明示的にTraceを連結する」形で書く。必要なのはresult CEではなく、Traceを蓄積しながら進む小さなヘルパー（Writer相当、10行程度）である。見送りの判断は維持してよいが、「ADR-008/3.2に合わせて」ではなく「CEは本質的な問題（Trace）を解決しないから」と理由を書き直す。そうしないと、後でCEを入れたときにTrace欠落の問題が再発する。

### 3-2【中】DTO→ドメイン変換（ofDto）ではネストが急増する。導入の発動条件を先に書いておく
- 該当：5.2「SchemaVersionを明示したDTOから変換する」、2-4/5-1の`ofDto`、9.5のSchema互換、Journal Reader。
- 壊れるシナリオ：`PolicyIR.ofDto`は`WindowRules: WindowRuleDto list`をそれぞれ`Result`に変換し（`WindowPredicate`は再帰DU）、全体を`Result<PolicyIR, DtoError list>`にまとめる。FSharp.Coreには`List.traverseResult`がないので、各所で`List.fold`による自作のtraverseが繰り返される。Journal Readerも全Kindの`ofDto`を持つので、同じ形のコードが数十箇所に散らばる。
- 代案：「必要になってから」の発動条件を明記する。例：「ofDtoが3段以上の`match ... with Ok/Error`のネストを要する最初の時点で、Contractsに`Result.traverse`と最小の`result`ビルダー（自作、20行未満、外部依存なし）を追加する」。外部パッケージを入れないという方針とも両立する。

### 3-3【中】REPLクライアント（Wm.ReplClient）ではtask＋Resultの二重ネストが常態化する
- 引用：10.3は`task { let! ... }`で書かれている。10.2のInterveneは、前提検査による拒否（F-4）、トークン不正（F-1）、IPCの切断という複数の失敗を持つ。
- 壊れるシナリオ：`let! r = wm.InterveneAsync(...)`が`Task<Result<_, ProtocolError>>`を返すと、利用者のREPLスクリプトは`match r with Error e -> ... | Ok v -> let! ...`のピラミッドになる。10.3のAPIイメージのように数手順を連ねるのが困難になり、利用者が`Result.get`のような雑な取り出し方をし始める。
- 代案：Wm.ReplClientはCoreの決定性の制約（3.1）の外にあるので、`taskResult`の導入（FsToolkit.ErrorHandlingなど、または自作）を**Coreとは別に**認める、と範囲を分けて書く。Core/Policy/Layoutsは外部依存なし、クライアントSDKは利便性優先、という線引きにする。

### 3-4【低】JournalWriterにはResultより「劣化シグナル」が適する
- 引用：16.2「Journal Writer異常：記録劣化を可視化し、安全性上不可欠な操作は停止する方針を選択可能にする」。
- 指摘：書き込みのI/O失敗を`Result`で呼び出し元に返しても、呼び出し元（Reducerループ）にできるのは「劣化状態へ遷移する」ことだけである。JournalWriterは例外を内部で捕捉し、`JournalDegraded`という入力イベント（InputEventに追加）としてReducerへ渡すほうが、3.1の「すべて入力キューに送る」と整合する。ここにもCEは不要。

---

## 提案4：例外→Resultの変換点を境界に集約

### 4-1【高】`Win32Exception`を捕捉するだけでは、生のP/Invokeの失敗をほとんど拾えない
- 事実：`[<DllImport(..., SetLastError = true)>]`で宣言したWin32 APIは、失敗しても例外を投げない。`BOOL`/`NULL`/HRESULTを返し、詳細は`Marshal.GetLastPInvokeError()`（.NET 6以降、旧`GetLastWin32Error`）で取る。`Win32Exception`を投げるのは`System.Diagnostics.Process`などの.NETラッパーだけである。
- 壊れるシナリオ：Wm.Windowsの主要な呼出（`SetWindowPos`、`DwmGetWindowAttribute`、`SetForegroundWindow`、`SendMessageTimeout`、`RegisterHotKey`）はすべて戻り値で失敗を知らせる。`tryNative "SetWindowPos" (fun () -> SetWindowPos(...))`は、失敗しても`Ok false`を返す。「唯一の入口」に集約したつもりで、失敗は1件も`Error`にならない。
- 代案：戻り値の規約ごとに入口を分ける。いずれも1-1の`NativeCallRecord`を作る。
  ```fsharp
  let callBool  api (f: unit -> bool)   : Result<unit, NativeCallRecord>   // false → GetLastPInvokeError（呼出直後に読む）
  let callHr    api (f: unit -> int)    : Result<unit, NativeCallRecord>   // HRESULT < 0
  let callBoolNoCode api (f: unit -> bool) : Result<unit, NativeCallRecord> // SetForegroundWindow 等。BoolFalseNoCode
  ```
  `GetLastPInvokeError`は**呼出の直後、同じスレッドで、他のP/Invokeを挟まずに**読む必要がある。ラッパーの中に閉じ込めて実装者が順序を間違えられないようにすること自体が、この集約の本当の価値になる。

### 4-2【高】例外の発生源はWm.Windowsだけではない。ユーザーのLayout関数（5.6）が投げる
- 引用：5.6`Calculate: LayoutFunction`は「自由なF#製レイアウトアルゴリズム」（1.2）。6.3「レイアウトの計算エラー：前回の有効なDesired、または既定の安全レイアウトへ移行」。16.2「Core例外：自動操作を止めて記録し、安全な停止へ」。
- 壊れるシナリオ：ユーザーのColumnsレイアウトが`TiledWindows = []`で`0`除算する、あるいは`List.item`で範囲外アクセスすると、`Result`を返す型でも例外が出る。提案の「Core/Policy/Layoutsに例外を一切流れ込ませない」は、例外がLayoutsの**内部から**湧くケースを扱っていない。この例外がReducerループまで伝わると、6.3の「前回のDesiredへ」ではなく16.2の「安全な停止」になる。1つのレイアウトのバグでWM全体が止まる。
- 代案：Registryが`Calculate`を呼ぶ箇所（Core内）にも第二の変換点を置く：`try impl.Calculate args ctx with ex -> Error (LayoutCrashed (ex.GetType().FullName))`（例外メッセージは非決定的になり得るので型名だけにする）。`LayoutError`に`LayoutCrashed of exnType: string`を追加する。これでReplay上も同じ入力から同じ`LayoutCrashed`が再現され、決定性は保たれる。`StackOverflowException`と無限ループは捕捉できないので、20章の既存リスク「任意F#拡張がHostを止める」に残す。

### 4-3【高】「例外とResultの境界」の一般原則が提案に書かれていない
- 壊れるシナリオ：原則がないと、`NullReferenceException`や`ObjectDisposedException`（SafeHandleの寿命切れ、8.1「ネイティブ資源の解放をShellが管理」）まで`tryNative`のcatch-allで`Error`に変えたくなる。すると**バグがEffect失敗として7.2の`Retry/Backoff`に吸収され**、Journal上は「アプリが拒否した」ように見える。これは13章のような調査を誤らせる最悪の結果である。逆に何も捕捉しないと、Effectスレッドが死に、ADR-012の「Effect適用スレッド」そのものが止まったまま、Coreはそれに気づけない。
- 代案（設計書に追記する原則）：
  1. **予期された外部の失敗**（エラーコード、拒否、消滅、ハング）→ 境界で`Result`/`NativeCallRecord`にする（4-1）。
  2. **ユーザー提供コードの失敗**（Layout関数）→ 呼出点で捕捉して`LayoutError`にし、6.3のフォールバックへ（4-2）。
  3. **自分たちのバグ・不変条件違反**（それ以外の全例外）→ 変換しない。**スレッド単位の最上位ハンドラ**（Reducerループ、Effect適用スレッド、JournalWriter、IPCハンドラ、8.4の監視コンポーネント）で捕捉し、`InternalFault of component`という入力イベントとして安全停止経路（8.4）へ渡す。Effectスレッドが落ちた場合は、Pendingを即座に全件`Cancelled`扱いにする。
  4. Reducerループでは、**入力をJournalに書いてから`step`を呼ぶ**順序を固定する。こうすれば`step`で例外を起こした入力（poison input）が必ずJournalに残り、Replayで再現できる。9.4の「Input accepted → Core decision」の順序の具体化である。

### 4-4【中】`tryNative`は同期関数なので、ADR-012のタイムアウトはその外側で別に組む必要がある
- 引用：7.1「専用のEffect適用スレッドを設け…タイムアウトした場合は`EffectTimedOut`をReducerへ投入」。
- 指摘：`SWP_ASYNCWINDOWPOS`が使えない呼出（観測系の`GetWindowText`は`WM_GETTEXT`を同期送信する、`SendMessage`系）は、`tryNative`の中で無期限にブロックし得る。「唯一の入口」と言うなら、タイムアウト付きで実行するか、`SendMessageTimeout`への置き換えが必須である、という分類（ブロックし得るAPIのリスト）も入口の責務として明記するべきである。少なくとも「ASYNCで呼ぶ／SMTOで呼ぶ／ブロックしない（ローカル）」の3分類を、API一覧として持つ。

---

## 提案5：PolicyArtifactのsmart constructor

### 5-1【高】Replay・Rollback・起動時の読み込みで、「今のAnalyzerが拒否する過去のPolicy」を読めなくなる
- 引用：12.2「Fixed-log Replay：保存した入力イベント列を、**ピン留めした当時のCore/Policy/Registry**へ流し」。14章`analyzerVersion`。9.2`PolicyArtifactHash`。10.2 Deploy「既知良好版へのRollback経路が必要」。16.2「設定失敗：新Artifactを採用せず最後の正常版を維持」。
- 壊れるシナリオ：
  1. Analyzerのv2で、5.5の「できればやる」項目（PotentialOverlap→Error等）を導入したとする。v1で配備していたPolicyはv2のAnalyzerでは`Error`になる。`PolicyArtifact`の唯一のコンストラクタがAnalyzerを通す設計だと、
     - 起動時に`artifacts/policy/`から最後の正常版を読もうとして構築に失敗し、16.2の「最後の正常版を維持」が成り立たない。
     - Rollback先の既知良好版が読めない。
     - 3か月前のIncident Bundle（13.1`policy.json`）をFixed-log Replayしようとしても、当時のPolicyを構築できない。Replayの目的は「当時の判断をそのまま再現する」ことであり、現在の基準で検証し直すことではない。
  2. 逆に、Replay用に`unsafeCreate`のような裏口を足すと、その裏口からDeployされる経路が生まれ、F-5の型保証が消える。
- 代案：用途で型を分ける。
  ```fsharp
  type PinnedPolicy   = private { Ir: PolicyIR; Artifact: ArtifactIdentity }  // 構築条件：canonical hashの一致のみ
  type DeployablePolicy = private { Pinned: PinnedPolicy; Report: AnalyzerReport } // 構築条件：現行Analyzerで Error なし
  module PinnedPolicy   = val ofStored : PolicyDto -> ArtifactIdentity -> Result<PinnedPolicy, IntegrityError>
  module DeployablePolicy = val analyze : Registry -> PinnedPolicy -> Result<DeployablePolicy, AnalyzerReport>
  ```
  Replay/Fork/Inspectは`PinnedPolicy`を、Deploy/起動時の新規採用は`DeployablePolicy`を要求する。最後の正常版は「当時のAnalyzerで通った」という事実を`ArtifactIdentity.analyzerVersion`とともに保存しておき、**再Analyzeせずに**`PinnedPolicy`として採用する（一度採用されたものは、現行Analyzerが厳しくなったという理由で外さない）。起動時の方針として明記する。

### 5-2【高】型の保証はREPLプロセスとHostの境界で途切れる。HostはDeploy受信時に必ず再Analyzeする
- 引用：10.1「F# Interactive…をWM Hostと**別プロセス**で起動」。F-1「`UserApproved`は、要求を送るクライアント自身が設定できるフィールドにしない」。F-5。
- 壊れるシナリオ：クライアント側で`DeployablePolicy`を構築してIPCで送っても、ワイヤ上はDTOである。悪意ある同一ユーザープロセス（10.1 F-2）はAnalyzerを通していないDTOを直接送れる。提案の「F-5を型レベルでも保証」がワイヤを越えて成り立つと誤解されると、Host側の受信ハンドラでの再検証が省略される危険がある。
- 代案：「型による保証はプロセス内に限る。Host側の`Deploy`ハンドラは、受信したDTOに対して必ず`ofStored`→`analyze`（Host自身のRegistry・Analyzerで）を実行する」とF-5の段落に明記する。F-1の「クライアントの自己申告を信用しない」と同じ原則である。

### 5-3【中】smart constructorの入力にRegistryと各種識別子が必要。所属レイヤーを明記する
- 引用：5.5「未登録LayoutId / ApiVersion不一致：Error」（Registryが必要）。14章PolicyArtifactは`layoutRegistryManifest`、`coreBuildId`、`normalizerSchemaVersion`、`analyzerVersion`を含む。
- 壊れるシナリオ：Wm.Policy内で構築しようとすると、Layout Registry（Wm.Layouts）への依存が必要になる。17章の構成上、PolicyとLayoutsは同じ層の兄弟なので、どちらがどちらを参照するかを決めないと循環しかける。`coreBuildId`はビルド時の定数なので、Wm.Policyの純粋関数にとっては外部入力になる。
- 代案：`analyze`はRegistryマニフェスト（`(LayoutId, ApiVersion, Digest) list`、Contractsの型）だけを受け取る。関数オブジェクトとしてのRegistryには依存させない。ビルド識別子は引数で渡す。これでWm.PolicyはWm.Layoutsに依存しないまま、5.5の未登録検査ができる。

### 5-4【低】Analyzerの`Warning`/`EvidenceRequirement`の扱い
- 引用：5.5「Analyzerは`Error / Warning / Unknown / EvidenceRequirement`を返す。『検査に成功したので全実行が安全』とは扱わない」。
- 指摘：構築条件を「Errorなし」にするなら、`Warning`と`Unknown`をArtifactに同梱しておかないと、REPLのInspect（10.2「Analyzerを見る」）がデプロイ済みPolicyの警告を再計算しなければならない（Analyzerのバージョンが変わると結果も変わる）。5-1の`DeployablePolicy.Report`として保持し、canonical hashの対象からは外す（Reportは派生物なので）。

---

## 横断：提案に欠けている観点

### X-1【高】Result/DUのJSON・NDJSON永続化のバージョニング戦略
- 引用：9.5「フォーマット移行は古い記録を上書きせず」、5.2「DUシリアライザーに暗黙依存せず」、21章「Journal永続形式（NDJSON/SQLite/組合せ）」。
- 壊れるシナリオ：
  - `EffectFailure`にケースを追加すると（1-8で`Unclassified`を入れても将来は増える）、古いReader（例：前バージョンのCLIでIncident Bundleを開く）が未知のタグで読み込みに失敗する。Bundleの共有・回帰テスト資産が、バージョンの境目で読めなくなる。
  - DUケース名をそのままタグに使うと、F#側でケース名をリネームしただけで過去のJournalとの互換性が壊れる。
- 代案：
  - DTOのタグは**明示的な安定文字列コード**にする（`"target-gone"`等）。F#のケース名とは切り離した対応表を持つ。
  - 前方互換として、ReaderはTagが未知のものを`Unrecognized of tag: string * raw: JsonElement`として保持する。Coreへの入力では、`classify`の段階で`Unclassified`に落とす。
  - `Result<'T,'E>`そのものはDTOにしない。`{ "outcome": "ok" | "failed", "failure": {...} }`の形に固定する。
  - 15章「Policy DTO/IR：SchemaのRound-trip」を、EffectFailure/NativeCallRecord/ValidationErrorのDTOにも広げる。加えて「過去バージョンのフィクスチャを読めること」を17.1の共通ゲート「Schema互換性」に具体化する。

### X-2【中】REPL越しにResultを渡すときのエラー型の層分け
- 引用：10.2 Intervene「Revisionチェック」、F-4「窓単位の前提検査」、10.1「ストリーム上のメッセージはスキーマとサイズ上限・タイムアウトを持つ」。
- 壊れるシナリオ：DebugProtocolの応答型が`Result<_, string>`のようにエラーを一つにまとめると、「前提不成立で拒否（再試行に意味がある）」「権限不足（トークン再取得が必要）」「IPC切断（再接続）」「受理されキュー投入済み（成否はまだ不明）」を区別できない。特に最後のケースを`Ok`とすると、Interveneの`Ok`が「OSに反映された」と誤読され、ADR-003の受付/実現の区別がREPL APIで崩れる。
- 代案：DebugProtocolに3層の型を置く。`TransportError`（接続・タイムアウト・サイズ超過）、`AuthorizationError`、ドメインの`InterveneRejection`（`PreconditionFailed of WindowId * expected * actual`）。成功は`Queued of Sequence`とし、結果はそのSequence以降のJournalを購読して確かめる、という形にする。

### X-3【中】Effect失敗のJournal Kindとの対応
- 引用：9.2の永続化Kind：`EffectDispatchStarted`、`EffectCallReturned`、`PendingTimedOut`。
- 指摘：提案は`EffectFailure`がどのKindのPayloadになるかを述べていない。1-2の代案に従うなら、`EffectCallReturned`のPayloadは`NativeCallRecord`（成功時は`Code=0`）であり、`EffectFailure`はPayloadに**現れない**（Replay時に`classify`で再計算する）。ここを明記しないと、実装者が両方を書き込んで1-2(b)の重複が生まれる。`EffectTimedOut`（ADR-012の専用スレッドのタイムアウト）は呼出が戻っていないので`EffectCallReturned`にならない。別のKind`EffectCallAbandoned of elapsed`を設ける必要がある。

### X-4【低】`TimedOut of waitedMs`などの計測値の扱い
- 指摘：計測値（ms）はCoreへの入力として記録された事実なので、決定性（2.2）は壊さない。ただし、Coreがこの値の大小で分岐すると、同じシナリオでも計測ノイズによってテストが不安定になる。原則として「Coreは計測値で分岐しない（診断用の付帯情報）」と書いておく。分岐が必要なら、閾値判定をShell側の`classify`に寄せる。

---

## 優先順位付きの修正提案（設計書への反映順）

1. 例外/Resultの一般原則（4-3）と、P/Invokeの戻り値規約別ラッパー（4-1）、Layout呼出点での捕捉（4-2）を6.3と8章に追記する。
2. `NativeCallRecord`をContractsに置き、`classify`をNormalizerの一部とする（1-1, 1-2, X-3）。`EffectFailure`は7.2の遷移軸で切る（1-8）。`EffectRejected`/`EffectTimedOut`と統合する（1-4）。ApplyBatchの結果を窓単位にする（1-5）。
3. ハングの扱いをF06/F08の実測に合わせる（1-3）。Simulationの`Hung`部品の意味を定義する。
4. PlanValidator：入力型を`ValidationInput`にし、#3/#4/#6のエラーケースを追加する（2-2）。`decide`を分離し「安全な単位＝窓を除いて再Layout」と定義する（2-3）。Shell入口の`GatedEffect`を導入する（2-1）。
5. PolicyArtifactを`PinnedPolicy`と`DeployablePolicy`に分割する（5-1）。Host側で再Analyzeすることを明記する（5-2）。
6. DTOタグ規約と前方互換の`Unrecognized`（X-1）、CheckpointはDesiredを信用しないこと（2-4）、smart constructor型の直接シリアライズ禁止（2-5）。
7. CE：6.1は見送りを維持し、理由をTraceの問題に書き換える（3-1）。ofDto用の自作traverse/ビルダーの発動条件（3-2）と、ReplClientでの`taskResult`許可（3-3）を21章「未決事項」に追加する。
