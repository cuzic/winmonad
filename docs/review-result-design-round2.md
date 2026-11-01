> **反映状況**：「収束のために最低限必要な修正」6項目（R2-1, R2-5, R2-9/R2-10, R2-12/R2-13, R2-2/R2-4, R2-6/R2-15/R2-16/R2-19）およびその他の指摘（R2-3, R2-7, R2-8, R2-11, R2-14, R2-17, R2-18, R2-20〜R2-26）を全てarchitecture.mdに反映済み。R2-11は「報告のみモードで通知」とユーザーが決定。第3ラウンドで収束確認を依頼済み。

# Opus再レビュー（第2ラウンド）：ADR-014 / architecture.md v2.4

対象：/home/cuzic/winmonad/docs/architecture.md（v2.4、1199行）。前回の指摘：review-result-design.md。読み取りのみ。
ユーザー判断3点（DenialKindを3種で開始、SetRect前にハング事前確認を毎回行う、PinnedPolicyを再分析しない）は前提として受け入れたうえで、それぞれの帰結を検査した。

---

## 0. 総評

- 前回の指摘はおおむね意図どおりに反映された。特に4-1（P/Invokeの戻り値規約）、4-3（例外の3層原則とpoison input）、1-2/X-3（EffectFailureを永続化せずclassifyで再計算）、5-1（Pinned/Deployableの分割）、3-1（CEを見送る理由の書き換え）は、意図をよく汲んだ反映になっている。
- ただし、**特に厳しく見てほしいと指定された4点のうち3点で、まだ穴が残っている**。
  1. 【高】1-3：ハング事前確認の結果が`TargetHung`にならない。8.1の`classify`対応表に`SendMessageTimeout`の行がないので`Unclassified`に落ち、**再試行される**。前回の問題がそのまま残っている（→R2-1）。加えて「毎回の事前確認はコストを無視できる」は、ハング判定前（0〜5秒）の窓には当てはまらない（→R2-2）。Simulationが`TargetHung`を生成する方法も未記述（→R2-3）。
  2. 【高】2-1：`GatedEffect.gate`の引数が`WmState`である。4.4の`WmState`には生存・Management情報がないので検査が書けない。前回2-2で`validate`について直した問題を、ここで再導入している。また`StepResult.Effects`（7.1）は`Effect list`のままである。ゲートで弾かれたEffectのPendingが永久に残る点（→R2-5）、Host（8.4の復旧処理）からの正規の迂回経路が未記載である点（→R2-6）もある。
  3. 【高】5-1/5-2：「以前に承認済み」の判定根拠が、ファイル自身に書かれた`analyzerVersion`になっている。同一ユーザーのプロセスが`artifacts/policy/`に未分析のPolicyを置けば、起動時に再分析なしで採用される。5-2のHost側必須再分析を迂回できる（→R2-9）。Registryとのリンク検査も抜けている（→R2-10）。
  4. 【高】X-1：前方互換の`Unrecognized → Unclassified`を**Fixed-log Replayに適用すると、Coreへの入力を黙って書き換えることになる**（→R2-12）。タグ規約の対象も、永続化されない型（EffectFailure/ValidationError）を挙げる一方で、本当に永続化される`InputEvent`/`Effect`/`ErrorSpace`等が抜けている（→R2-13）。
- その他：`JournalDegraded`が7.1の`InputEvent`に存在しない、`classify`のバージョンの置き場所が矛盾している、22-16の文言が3層原則と矛盾している、など文書内の不整合が数点ある。

---

## A. 指定4点の検証

### A-1. 1-3（ハングとTimedOutの整合）：部分的に解消。主経路にまだ穴がある

**R2-1【高】ハング事前確認の結果が`TargetHung`に分類されない**
- 引用：7.1「ハングと判定されれば…`EffectCallReturned(RequestId, Error { Api="SendMessageTimeout"; ...})`を返す。これが`classify`で`TargetHung`に分類され」。8.1の対応表は`1400→TargetGone`、`5→Denied OtherDenial`、`BoolFalseNoCode→ForegroundDenied`、「上記以外→`Unclassified`」となっている。
- 壊れるシナリオ：`SendMessageTimeout`は、中断・タイムアウトのどちらでも0を返し、`GetLastError`は`ERROR_TIMEOUT`(1460)になる。表に`(SendMessageTimeout, Win32LastError, 1460)`の行がないので`Unclassified`になり、7.2の分岐では「Retry上限付き」へ進む。**ハング窓を再試行するという前回1-3の問題が、そのまま残っている**。
- 代案：表のキーを`(Api, Space, Code)`の3つ組にすることを明記し、`(SendMessageTimeout, Win32LastError, 1460) → TargetHung`を初期エントリに入れる。`classify`がApiを見ずにCodeだけで分岐すると、`SetWindowPos`が返した1460（通常は起きない）まで`TargetHung`になる。キーにApiを含めることが必須である。`Error { Api="SendMessageTimeout"; ...}`の「...」も、`Space=Win32LastError; Code=1460`と具体的に書く。

**R2-2【高】「毎回の事前確認はコスト無視」は、ハング判定前の窓では成り立たない**
- 引用：7.1「F06で0msと実証済みのため、毎回確認してもコストは無視できる」。feasibility.md §5「スレッドが5秒以内に`GetMessage`…を呼んでいない場合…『応答していない』と判定」。failure-catalog F06は「OSが**既にハング判定済み**の窓へは即座に中断」。
- 壊れるシナリオ：F06が実証したのは「既にハング判定済みの窓」と「正常な窓」の2状態だけである。メッセージポンプが止まってから5秒未満の窓（OSがまだハングと判定していない）に対して`SMTO_ABORTIFHUNG`を送ると、**指定したタイムアウトの満了まで待つ**。Effect適用スレッドは1本で、`ApplyBatch`の子Effectを順に事前確認する。そのため1窓が固まり始めた直後のレイアウト切替では、正常な他の窓の配置まで`タイムアウト × 該当窓数`だけ遅れる。2.2で宣言した「数十ms台」のレイテンシ予算を、タイムアウトを2000ms（F06の設定値）にすれば確実に超える。
- 代案：
  - 事前確認のタイムアウト値を、レイテンシ予算から逆算した短い値（例：50ms以下）に固定し、明記する。
  - タイムアウト満了による1460と、即時中断による1460は`GetLastError`で区別できない。`ElapsedUs`（現状は常に`0L`、R2-15）を実測して「満了 = Busy（`Transient`）」「即時 = `TargetHung`」と分ける。または、ハング判定前の窓は`Transient`扱いにして次回の事前確認に任せる。
  - バッチ内の事前確認は並列化するか、少なくとも「1窓の事前確認待ちが他の窓のSetWindowPosを遅らせない」ことを15.1 #13の具体例として性質に加える。
- 補足（TOCTOU）：事前確認の直後にハングし始めた窓では、`SetWindowPos`がASYNCで成功→観測なし→7.2 `Timeout`→Retry→次回の事前確認（判定済みなら0ms）で`TargetHung`、と収束する。これは許容できる経路だが、「Retryが少なくとも1回は起き得る」ことを7.2に注記しておくと、Phase BでJournalを読み誤らない。

**R2-3【高】Simulationが`TargetHung`をどう生成するかが書かれていない**
- 引用：12.1`WindowBehavior`は v2.3から変わっていない（`Hung`部品がない）。`applyEffect: VirtualWorld -> Effect -> VirtualWorld * SimEvent list`。12.1「`SimEvent`は…Normalizerを通してから`InputEvent`になる」。
- 壊れるシナリオ：
  1. `EffectCallReturned`はWinEvent由来の観測ではなく、Effect適用スレッドの戻り値である。Simulationでは`SimEvent→Normalizer→InputEvent`の経路に乗らないので、どこで生成するのかが未定になっている。
  2. 「事前確認→呼出」という手順はShell（Wm.Windows）の手続きであり、Coreには見えない。Simulationがこの手順を独自に再実装すると、実機との乖離が再び起きる（前回1-3の根本原因と同じ）。たとえば実装者が「Hungの窓には`EffectCallReturned(Error 1460)`を即座に返す」と書くと、R2-2のハング判定前の期間とTOCTOUの経路が再現されない。
- 代案：
  - `WindowBehavior`に、実測（F05/F06/F08）に基づく部品を追加する：`HungAfterMs of startMs: int`。意味は「start以降はメッセージを処理しない。start+5000ms以降はOSハング判定済み」とする。
  - Shellの「事前確認→ASYNC呼出」手順を、Contracts（またはR2-14で提案する共有の純粋層）に**Effect実行プロトコル**として1つの純粋関数で書く：`executeProtocol: (probe: WindowId -> ProbeOutcome) -> GatedEffect -> CallPlan`。Windows版とSimulation版は`probe`の実装だけを差し替える。こうすると、ADR-002（同じCore）に加えて「同じShell手順」も保証される。
  - Simulationで、`EffectCallReturned`をどの経路でCoreへ入れるかを12.1に1行で明記する（例：`applyEffect`が`SimEvent`とは別に`CallReturn list`を返す）。

**R2-4【中】`EffectCallAbandoned`の対象が、構造上は存在しない（ただし`RestoreBeforeMove`は同期の`ShowWindow`のままなので、実際にはブロックし得る）**
- 引用：7.1`EffectCallAbandoned of RequestId * waitedMs`「SWP_ASYNCWINDOWPOSが使えない呼出（`GetWindowText`等、…観測系API）」。7.1`RestoreBeforeMove … // ShowWindow(SW_RESTORE)相当`。7.2「EffectCallAbandoned → Suspend」。
- 壊れるシナリオ：
  1. 観測系API（`GetWindowText`）は`ObserveWindow`から呼ばれるが、`ObserveWindow`はRequestIdを持たない。`EffectCallAbandoned of RequestId`を作れない。観測の失敗は本来4.2の`Observation.Value = Error`で表すはずで、二重定義になっている。
  2. 逆に、本当にブロックし得るEffectは`RestoreBeforeMove`である。`ShowWindow`は同期で、ハング窓に対してはブロックする（ASYNC相当は`ShowWindowAsync`）。このままでは、ハング窓が最大化状態だった場合にEffect適用スレッドが止まる。ADR-012に反する。
  3. ブロックしたネイティブ呼出は中断できない。「期限を越えたら`Abandoned`」と言っても、そのスレッドは戻ってこない。次のEffectを誰が実行するのかが書かれていない。
- 代案：`RestoreBeforeMove`の実装を`ShowWindowAsync(SW_RESTORE)`と明記する。これはcallBoolで扱える（「開始に成功すれば非0」）。なお`ShowWindow`の戻り値は「以前に可視だったか」であり成否ではないため、`callBool`に通すと誤判定する。事前確認の対象にも`RestoreBeforeMove`を含める。そうすると`EffectCallAbandoned`が必要になるのは「同期APIを使わざるを得ない例外的なEffect」だけになる。そのリストを明記し（現状は空のはず）、もし発生したら**Effect適用スレッドを捨てて新しく立て直す**（古いスレッドはリークを許容する）という手順を7.1に書く。観測系の期限超過は`Observation.Value = Error`の側に寄せ、`EffectCallAbandoned`の説明から外す。

### A-2. 2-1（GatedEffect）：方向性は正しいが、型・状態・フィードバックが閉じていない

**R2-5【高】`gate`の入力が`WmState`である。弾いたEffectのPendingの扱いも未定義**
- 引用：6.2`let gate (state: WmState) (effects: Effect list)`。4.4`WmState = { Workspaces; ActiveWorkspace; WindowWorkspace }`。7.1`StepResult = { Next: WmState; Effects: Effect list; ... }`。
- 壊れるシナリオ：
  1. 「生存・非Excluded」の確認には、Management状態と生存集合が必要だが、`WmState`にはどちらもない。前回2-2で`validate`を`ValidationInput`に直したのと同じ問題が、ここで再発している。実装時に`WmState`へフィールドが継ぎ足され、ADR-011で「先に固定する」と決めた状態モデルが崩れる。
  2. `StepResult.Effects: Effect list`が更新されていない。6.2の「Shell/Simulationが受け取る型を`GatedEffect`だけにする」は、型定義の上では成立していない。
  3. 弾かれた`SetRect`は、すでにPendingに`Planned`として登録されているはずである（Reconcilerが生成した時点で）。Shellへ出ないので`EffectCallReturned`は永久に来ず、そのPendingは`Deadline`まで残って`Timeout→Retry`になる。Retryも同じゲートで弾かれ、Attempts上限までこれを繰り返す。7.3の「未完了の同一Targetへの重複要求を発行しない」により、その窓への新しい意図までブロックされる。
- 代案：
  ```fsharp
  type StepResult = { Next: CoreState; Effects: GatedEffect list; Trace: DecisionTrace list }
  val gate : GateInput -> Effect list -> GatedEffect list * GateRejection list   // GateInput = { Alive; Management; Monitors }
  ```
  `step`の最後の手順を「Next候補を計算→gate→**弾かれたRequestIdのPendingを`Cancelled(GateRejected)`へ遷移させたNextを確定**」と規定する（gateは純粋なので、この処理も決定的でReplayできる）。`GateRejection.Reason: string`はCore内の文字列なので、DU（`TargetNotAlive | TargetExcluded | NonPositiveRect | …`）にする。

**R2-6【高】受け入れ条件22-4「発行元を問わず全Effectは…GatedEffectを経由」には、正規の迂回経路が設計内に存在する**
- 引用：8.4「直前に隠した窓がある場合のベストエフォート復旧を実装する」「安全停止の『独立した制御経路』は…別のスレッド、できれば別プロセス」。4.4「8.4の安全停止・全体再観測で各窓の所属Workspaceを復元できることを要件とする」。8.1「Wm.Windows内の全P/Invoke呼出しはこの3関数のいずれかを経由」（`Native`モジュールは公開されている）。
- 壊れるシナリオ：安全停止時の復旧は、Coreが止まっている（あるいはハングしている）ことを前提に、Hostの監視コンポーネントが直接`SetWindowPos`/`ShowWindow`を呼ぶ。これはgateを通らないし、通ってはいけない（Coreに依存しない経路であることが要件だから）。22-4が文字どおりに読まれると、この復旧経路を実装した時点で受け入れ条件違反になる。逆に、例外が書かれていないので「Hostなら直接呼んでよい」という慣習が生まれ、迂回が広がる。
- 代案：22-4と6.2に「**例外：8.4の安全停止・復旧経路（Coreから独立していることが要件）。この経路が呼べるOS操作は『表示の復元』と『画面内への移動』に限定し、Journalに`SafetyRecoveryAction`として記録する**」と明記する。`Native`モジュールをWm.Windows内部（internal）にし、外から呼べるのは「GatedEffectを受け取るShell」と「安全停止用の限定API」の2つだけにする、という構成上の担保も書く。

**R2-7【中】ゲートの検査範囲：幾何を見ないと、モニター構成変化後のRetryが消えたモニターへ窓を送る**
- 引用：6.2「PlanValidatorの幾何的な整合性チェック（WorkArea内か…）はここでは行わない」。7.3「`ScreenReconfigurationInProgress`…安定…後に全体Snapshotを再観測してから再開」。
- 壊れるシナリオ：外部モニターを抜いた直後に`MonitorTopologyObserved`が届いても、以前に発行したSetRectのPendingは生き残る。その後のTimeout→Retryは、古いDesired（消えたモニター上の矩形）のまま再発行され、gateは矩形の正値性しか見ないので通過する。結果として窓が画面外へ移動する（ユーザーから見えなくなる）。7.3の抑止はReconfiguration中の新規発行を止めるだけで、**安定後に残っている旧Pendingの扱い**は書かれていない。
- 代案：(a) `MonitorTopologyObserved`を受けたら全Pendingを`Superseded`にする（7.2の「Superseded intent」に「モニター構成の変化」を追加）、または(b) gateに「目標矩形が現在のいずれかのモニター矩形と交差する」という安価な検査を1つ足す。(a)が本筋で、(b)は保険である。

**R2-8【中】前回2-6（`RestoreBeforeMove`の前置契約）の検査場所がまだ決まっていない**
- 引用：19章「PlanValidator.fs（`ApplyBatch`/`RestoreBeforeMove`を含む…）」、18章Phase Aも同様。一方6.2のgateは「生存・非Excluded・矩形正値」だけを検査する。`ValidatedPlan`は`Placements`しか持たない。
- 壊れるシナリオ：「最大化窓には`RestoreBeforeMove`を先に出す」という契約を、PlanValidator（Placement列が対象でEffectを知らない）もgate（明示的に対象外）も検査しない。19章の記述だけが残り、実装者はPlanValidatorに入れようとして型が合わず困る。
- 代案：gateの検査項目に「Observedで最大化/Snap状態の窓への`SetRect`は、同じバッチ内に先行する`RestoreBeforeMove`を伴う」を追加する。19章・18章からPlanValidatorについての当該記述を削除する。

**補足（設計判断として妥当な点）**：gateは`step`の中で、Effect生成と同じ状態に対して走る。したがって、gateが保証するのは「Coreの現在状態との整合（Reconcilerが古いDesiredを使うバグの検出）」であり、**Shellで実行される瞬間の実世界の鮮度**ではない。後者は、Shellの事前確認（ハング）とOSの戻り値（1400）が受け持つ。6.2の「直前に変化していないかの安価な再確認」という表現は誤読を招く（実世界の直前と読める）ので、「Core状態に対する最終整合チェック」と言い換えるとよい。

### A-3. 5-1/5-2（PolicyArtifactの分割とRollbackで再分析しない判断）

**R2-9【高】「以前に承認済み」の信頼の根拠が、ファイル自身の申告になっている**
- 引用：14章「起動時・Rollback時に『以前にすでに承認済み』の`PinnedPolicy`を読む場合は、現行Analyzerで再分析しない（`ArtifactIdentity.analyzerVersion`を信頼する）」。`PinnedPolicy.ofStored`の構築条件は「canonical hashの一致のみ」。10.2「Rollbackは既に採用履行済みの`PinnedPolicy`を読むだけであり再分析しない」。10.1 F-2「同一ユーザーで動く任意のプロセス…」。
- 壊れるシナリオ：
  1. `analyzerVersion`もhashも、ファイルを書いた者が自由に設定できる。同一ユーザーの任意プロセスが、未分析のPolicyを正しいhashとともに`artifacts/policy/`へ置き、それを「最後の正常版」に見せかければ、次回の起動で再分析なしに採用される。5-2で塞いだIPC経由のDeployの穴が、**ファイル経由で開いたまま**になっている。
  2. RollbackがREPLから「このDTOに戻せ」という形で指示されると、それは新規Deployと区別がつかず、同じ迂回になる。
- 代案：
  - 「承認済み」の根拠は、**Host自身が書いた採用記録**に置く。具体的には、JournalのDeploy/`PolicyActivated`エントリに`artifactHash`を記録しておき、起動時・Rollback時は「採用記録にhashが載っているPinnedPolicyだけを再分析なしで採用する」とする。採用記録にないhashは、ファイルがどこにあっても`DeployablePolicy.analyze`を通す。
  - Rollbackの要求はDTOではなく`artifactHash`の参照だけを受け付け、Host自身のストアから読む（10.2に明記）。
  - 脅威の大きさに見合った対策として、これで十分である（同一ユーザーの攻撃者は他にも多くのことができるが、少なくとも「5-2で塞いだ」と主張している穴は閉じる）。

**R2-10【高】再分析しないことと「Registryとのリンク検査」は別物。後者は起動時・Rollback時に必須**
- 引用：5.6「`ImplementationDigest`は…再現時に同じ実装であるか検証する。異なるアセンブリを同一バージョンとみなさない」。14章PolicyArtifactは`layoutRegistryManifest`と`coreBuildId`を含む。5.5の未登録LayoutId検査はAnalyzerの一部である。
- 壊れるシナリオ：v2.4の規則では、Analyzerを飛ばすと、Analyzerが担っていた「未登録LayoutId／ApiVersion不一致」の検査まで飛ぶ。WMを更新して`columns`レイアウトのApiVersionを上げたあとに旧PinnedPolicyへRollbackすると、Registry解決が毎ステップ失敗する。6.3のフォールバック（「前回の有効なDesired」）にずっと留まり、窓が一切タイルされない。Rollbackの目的（既知良好な状態へ戻る）が達成されず、その原因もJournal上で`LayoutCrashed`/`InvalidInput`に紛れる。
- 代案：14章に第3の段階を足す。
  ```fsharp
  val link : RegistryManifest -> PinnedPolicy -> Result<ActivePolicy, LinkError>  // 起動時・Rollback時・Deploy時すべてで必須
  ```
  「Analyzerによる品質判定は再実行しない」と「現在のビルドで実行可能かのリンク検査は必ず行う」を分けて書く。リンクに失敗した場合は16.2の「最後の正常版」をさらに1つ遡るか、組込みの既定Policy（monocle）へ落とす。どこまで遡るかの規則も明記する。

**R2-11【中】Analyzerが後から検出した「安全上の問題」を、黙って運用し続ける**
- 引用：14章「Analyzerが後から厳しくなっても…読めなくなることはない」。
- 評価：ユーザーの判断（ブロックしない）は16.2と整合しており妥当である。ただし現状の書き方だと、現行Analyzerで見れば`Error`になるPolicyが、利用者に**何も知らされず**に採用され続ける。
- 代案：起動時・Rollback時には現行Analyzerを**報告のみのモード**で実行する。`Error`が出たらトレイ通知やJournalに`PolicyStaleAnalysis`として記録し、採用はブロックしない。分析コストはPolicy数十ルール規模（5.5 A-7）なら無視できる。ユーザーの判断「再分析しない」とは、「判定に使わない」という意味で両立する。そう解釈してよいか、ユーザーに確認する価値がある。

---

### A-4. X-1（DTOタグ）：具体性はあるが、適用対象と、Replayでの扱いが誤っている

**R2-12【高】`Unrecognized → Unclassified`をFixed-log Replayに適用すると、入力の改ざんになる**
- 引用：9.2「Readerは未知のタグを`Unrecognized…`として保持し、Coreへの入力に落とす段階（`classify`）で`Unclassified`に変換する」。12.2「Fixed-log Replay：…同一決定が再現されるか検査する」。15.1 #10。
- 壊れるシナリオ：新しいビルドが`ErrorSpace`に`NtStatus`を追加して記録したJournalを、古いビルドでReplayすると、未知のタグが`Unclassified`に化ける。元の実行では（例えば）`TargetGone`→`Cancelled`だった経路が、Replayでは`Retry`になる。決定が食い違うのに、Replayツールはそれを「再現の失敗（Coreのバグ）」として報告する。**Readerの互換処理が、Replayの判定そのものを汚染する**。
- 代案：Readerに2つのモードを持たせる。
  - 表示/Explain/Bundle閲覧モード：`Unrecognized`をそのまま表示する（現行の規約）。
  - **Replayモード：`Unrecognized`を1つでも含むJournalは、ReplayをFail-fastで拒否する**（「このJournalはより新しいスキーマで書かれている」と報告）。12.2の「ピン留めした当時のCore」を使えば、本来は未知のタグに出会わないはずなので、出会ったこと自体がピン留めの失敗である。
  - 9.2の文言「Coreへの入力に落とす段階で`Unclassified`に変換」はLive運用時に限る、と限定する。

**R2-13【高】タグ規約の対象リストが、永続化しない型を挙げ、永続化する型を落としている**
- 引用：9.2「`NativeCallRecord`/`EffectFailure`/`ValidationError`等のDU」。同じ9.2で`EffectFailure`は「Payloadに含めない」、`PlanRejected`（`ValidationError`）は「揮発性」とされている。
- 壊れるシナリオ：永続化されるのは`InputEvent`のケース（`JournalKind`）、`CoreCommand`（`UserCommand`／`ReplIntervention`のペイロード）、`Effect`（`EffectDispatchStarted`）、`ErrorSpace`、`Management`、`LayoutArgs`、`WindowPredicate`（Policy）、`StackSlot`などである。これらが規約に列挙されていないので、ここで最も事故が起きやすい。たとえば`CoreCommand.SwapWithMaster`を`SwapMaster`にリネームしただけで、過去のJournalのReplayが壊れる。
- 代案：規約の対象を「**Journal・Checkpoint・PolicyArtifact・Incident Bundle・IPCに出る全てのDU**」と定義し直す。対象型の一覧を`docs/contracts.md`（17章に予定されている）に置き、「全DUケースに安定タグが割り当てられ、過去のフィクスチャを読める」ことを17.1の共通ゲート「Schema互換性」の具体的なテストにする。`EffectFailure`/`ValidationError`は「IPC/Bundleの派生表示でのみ使う」と区別して書く。

**R2-14【中】`NativeCallRecord.Space`は閉じたDUで、`Unrecognized`の置き場所がない**
- 引用：7.1`type ErrorSpace = Win32LastError | HResult | BoolFalseNoCode`。前回1-1の案にあった`NtStatus`は落とされている（それ自体は問題ない）。
- 指摘：`Unrecognized of tag * raw`は「Readerが保持する」とあるが、Coreへの入力型`NativeCallRecord`のどこにそれを入れるのかが書かれていない。Reader専用のDTO型（`NativeCallRecordDto`）でしか保持できないなら、Live運用時の`Unclassified`変換もDTO→ドメインの変換（`ofDto`）で行うことになる。その場所を明記する（`classify`ではなく`ofDto`）。

---

## B. その他の新たな矛盾・見落とし

**R2-15【中】`classify`のバージョン管理の場所が矛盾している**
- 引用：7.1「`classify`…Windows Event Normalizer（ADR-013）と同じ扱いの純粋関数…`normalizerSchemaVersion`で版管理」。7.1`EffectCallReturned of RequestId * Result<unit, NativeCallRecord>`（Coreへの入力は生の記録）。17章「`classify`はWm.Contractsに置く」。
- 壊れるシナリオ：InputEventが生の記録を運ぶので、`classify`を呼ぶのは**Coreの`step`の中**であり、Normalizer（Shell側でRaw→InputEventに変換する層）ではない。そうであれば、classifyの版はCoreBuildId（Contractsもビルドに含まれる）で決まり、`normalizerSchemaVersion`は関係ない。逆にNormalizerの側でclassifyを呼ぶなら、InputEventは`EffectFailure`を運ぶべきで、9.2の「EffectFailureはPayloadに含めない」と食い違う。どちらで呼ぶかによって、Replayで何をピン留めすべきかが変わる。
- 代案：「classifyはCoreの`step`内で呼ぶ。版はCoreBuildIdでピン留めされる」と統一し、`normalizerSchemaVersion`への言及を削除する。7.1、9.2、14章の3か所を直す。前回の私の提案（1-2(c)）が「Normalizerの一部」と書いたことが混乱の元である。入力が生の記録である以上、Core側に置くのが正しい。

**R2-16【中】`JournalDegraded`が7.1の`InputEvent`に存在しない。しかも「書けなかったこと」の入力は、そもそも記録できない**
- 引用：9.4「`JournalDegraded`という`InputEvent`（7.1）としてReducerへ渡す」。7.1の`InputEvent`一覧にはこのケースがない。
- 壊れるシナリオ：(1) 型の定義漏れ。(2) より本質的な問題として、7.1は「入力をJournalに書いてから`step`を呼ぶ」と順序を固定した。ところが`JournalDegraded`は、Journalへ書けない状況で発生する入力なので、Journalに残らないまま`step`に入る。CoreはそれによってEffectの停止などの状態遷移をするが、Replayではこの入力が欠けているので、以降の決定がすべて食い違う。
- 代案：`InputEvent`に`JournalDegraded of since: Sequence`と`JournalRecovered of gap: Sequence * Sequence`を追加する。回復時に「欠落区間」と「その間に投入した`JournalDegraded`」を書き戻す（メモリ上のバッファに残っていれば）。Replayは欠落区間をまたげない（区間の後の最初のCheckpointから再開する）、と9.5に明記する。

**R2-17【中】`EffectSubmitted`と`EffectCallReturned(Ok)`が重複している**
- 引用：7.1に`EffectSubmitted of RequestId`と`EffectCallReturned of RequestId * Result<unit, …>`の両方がある。7.2の遷移図は`EffectSubmitted`を参照していない。
- 壊れるシナリオ：Shellの実装者が両方を投入するか片方だけを投入するかで、Pendingの`Dispatched`への遷移タイミングが変わる。SimulationとWindowsで食い違う余地になる（前回1-4と同種の「同じ事実を2通りに表せる」問題）。
- 代案：`EffectSubmitted`を削除し、「`EffectCallReturned(Ok)` = 受付成功（実現ではない、ADR-003）」とする。9.2の`EffectDispatchStarted`（発行直前）と`EffectCallReturned`（戻り値）の2点で十分である。

**R2-18【中】`DenialKind.IntegrityMismatch`が、対応表から生成されない**
- 引用：7.1`DenialKind = IntegrityMismatch | ForegroundDenied | OtherDenial`。8.1の表は「`Win32LastError, 5`（ERROR_ACCESS_DENIED。**昇格窓等**）→ `Denied OtherDenial`」。
- 壊れるシナリオ：昇格窓は`IntegrityMismatch`を作った動機そのものなのに、表は`OtherDenial`に写している。さらに、エラーコード5だけではUIPIによる拒否とそれ以外の拒否を区別できない。`classify: NativeCallRecord -> EffectFailure`の入力には対象の整合性レベルがないので、**原理的に`IntegrityMismatch`を生成できない**。このケースは死んでいる。
- 代案：(a) Shellが失敗時に対象プロセスの整合性レベルを取得し、`NativeCallRecord`に`TargetIntegrity: IntegrityLevel option`を持たせて、classifyが`5 && TargetIntegrity > Self`のときに`IntegrityMismatch`とする。または(b) Phase 0の「昇格窓への操作がどう失敗するか」（18章）の結果が出るまで、表に`5 → OtherDenial`と書いたうえで「`IntegrityMismatch`はPhase 0の結果待ち」と注記する。ユーザーの判断（3種で開始）とは、(b)で両立する。
- 付記：10.2の`AuthorizationError.IntegrityMismatch`と同名のDUケースになっている。F#では同じスコープで両方をopenすると、後に宣言された側が優先される。パターンマッチで意図しない型に推論される事故が起きるので、どちらかを改名する（例：`ClientIntegrityTooLow`）。

**R2-19【中】受け入れ条件22-16の文言が、3層原則の第3層と矛盾している**
- 引用：22-16「Core/Policy/Layoutsへ生の例外が伝播しない」。7.1の第3層「それ以外の例外…→変換せず、スレッド単位の最上位ハンドラ…で捕捉」。
- 壊れるシナリオ：Core内のバグ（`Map.find`の失敗など）は、Coreの中で発生し、Reducerループの最上位まで**伝播する**のが原則どおりの挙動である。22-16を字義どおりに満たそうとすると、Core内部をtry/withで覆う実装が出てくる。これは4-3で警告した「バグをResultに吸収する」方向そのものである。
- 代案：「外部の失敗とユーザーLayoutの失敗は、境界で値に変換されてからCoreに入る。Core自身のバグによる例外は値に変換されず、スレッドの最上位ハンドラで`InternalFault`になる」と書き換える。

**R2-20【中】`GatedEffect`がWm.Coreにあるので、Wm.WindowsがWm.Coreに依存することになる**
- 引用：17章「`GatedEffect`はWm.Core」「依存方向：Windows/Host/Simulation/DebugがCoreの**契約**を参照」。12.1`applyEffect: VirtualWorld -> Effect -> …`（`GatedEffect`ではない）。
- 壊れるシナリオ：Shellの入口の型を`GatedEffect`にすると、Wm.WindowsはWm.Coreのアセンブリを参照しなければならない。17章の書き方は「Contractsだけを参照」とも読めるので、どちらなのかが曖昧になる。Wm.Coreを参照するなら、Wm.Windowsから`step`等の内部APIも見えてしまう。
- 代案：(a) Wm.Windows→Wm.Coreの参照を明示的に許可し、Core側で公開するのは`GatedEffect`と`GatedEffect.effect`アクセサだけにする。または(b) `GatedEffect`の型をContractsに置き、コンストラクタを`internal`にして`InternalsVisibleTo("Wm.Core")`でCoreだけに構築を許す。(b)のほうが依存方向が綺麗である。Simulationの`applyEffect`も`GatedEffect`を受け取るのか（テストで生のEffectを渡せなくなる）、生の`Effect`のままにするのかを決めて12.1を直す。

**R2-21【中】`decide`の`RelayoutWithout`は、純粋な1ステップの中では本来発生しない。発生したときは別のバグを隠す**
- 引用：6.2「`RelayoutWithout`…検証時点で死亡/Excluded化していた窓を除いて1回だけ再計算する」。`ValidationInput`の`Context`/`Management`/`Alive`は、同じ`step`内で同じCoreStateから導出される。
- 壊れるシナリオ：Layoutの入力`TiledWindows`も`Alive`/`Management`も同じ状態から作られる。したがって「TiledWindowsに入っているのに死んでいる窓」は、**導出の不整合（Coreのバグ）**でしか生じない。前回の私の2-3は、検証時点とLayout計算時点の間に時間が経つことを前提にしていたが、純粋な`step`の中ではその時間差がない（ここは私の前回指摘の誤りである）。この状態で`RelayoutWithout`が黙って再計算すると、導出の不整合バグが隠れる。さらに、再計算でその窓を除いても`WmState.Order`には残るので、OrderとDesiredが食い違ったまま進む。
- 代案：`RelayoutWithout`は残してよい（ユーザーLayoutが存在しないWindowIdを出力した場合などの防御として）。ただし発火したら`DecisionTrace`に「導出不整合の疑い」を記録し、性質ベーステストに「`TiledWindows ⊆ Alive ∩ Managed(Tiled)`が常に成り立つ」を加える。死亡窓を扱う本当の経路は、OSの1400（`TargetGone`→`Cancelled`）と`WindowRemoved`入力によるOrderからの除去である、と6.3に書き添える。

**R2-22【低】`NativeCallRecord.ElapsedUs`が常に`0L`**
- 引用：8.1の`Native`の3関数は、いずれも`ElapsedUs = 0L`を入れている。
- 指摘：R2-2の「タイムアウト満了か即時中断か」の判別に、この値がちょうど必要になる。計測するか（`Stopwatch.GetTimestamp`で呼出を挟む）、使わないならフィールドを削除する。中途半端な常時0は、Journal読者に「0msで返った」という誤った事実を見せる。

**R2-23【低】`callBool`の前提条件の明記漏れ**
- 指摘：`Marshal.GetLastPInvokeError()`が意味を持つのは`DllImport(SetLastError = true)`の場合だけである。また`GetWindowText`のように「失敗」と「正当な0」が戻り値で区別できないAPIでは、呼出前に`Marshal.SetLastPInvokeError(0)`が必要になる。8.1の`Native`の説明に「全DllImportはSetLastError=true」「0が正当な戻り値になり得るAPIは呼出前にlast errorをクリアする専用ラッパーを使う」の2行を足す。

**R2-24【低】13.1の「relevant decisions」が未修正**
- 引用：9.5「13.1の…『relevant decisions』は…注記を付ける」。しかし13.1の903行目は`# reducer inputs and relevant decisions`のままである。

**R2-25【低】REPLのIntervene：前提検査をどこで評価するかで、同期の戻り値の型が変わる**
- 引用：10.2「`InterveneRejection`はドメインの拒否として別に扱う」。F-4「窓単位の前提検査」。
- 指摘：前提検査をHostの受信時に行うと、検査からReducerが処理するまでの間に状態が変わり得る（TOCTOU）。決定的でJournalに残る`step`内で評価するのが筋であり、そうすると`PreconditionFailed`は同期の戻り値ではなく、`Queued`の後にJournal上の`InterveneRejected`イベントとして届く。同期APIの戻り値は`Result<Queued, TransportError | AuthorizationError>`だけになり、10.2の「別に扱う」が何を指すかが確定する。どちらで評価するかを明記する。

**R2-26【低】`invokeLayout`のcatch-allには、非決定的な例外も含まれる**
- 指摘：`OutOfMemoryException`や`InsufficientExecutionStackException`は入力ではなく実行環境に依存する。これらを`LayoutCrashed`にすると、Replayで同じ結果が出ない（5.6の「同じ入力からは同じ`LayoutCrashed`が再現される」が崩れる）。この2つは捕捉対象から外し、第3層（`InternalFault`）に回す。

---

## C. 前回指摘ごとの判定

| 前回 | 判定 | 備考 |
|---|---|---|
| 1-1 | 解消 | NativeCallRecordをContractsへ。Message/Paramsを除外 |
| 1-2 | 解消（要修正） | 方針は正しい。classifyの版の置き場所が矛盾（R2-15） |
| 1-3 | **未解消** | 対応表にTargetHungがない（R2-1）。ハング判定前のコスト（R2-2）。Simulationの生成方法（R2-3） |
| 1-4 | 概ね解消 | EffectSubmittedが残存（R2-17） |
| 1-5 | 解消 | BatchId化と窓単位の結果 |
| 1-6 | 解消（要修正） | 表のキーにApiを含めること（R2-1）、IntegrityMismatchが生成不能（R2-18） |
| 1-7 | 解消 | ErrorSpace導入、ForegroundDeniedを独立 |
| 1-8 | 解消 | 反応軸で切る、Effectではない登録系を除外 |
| 1-9 | 部分解消 | Observation<'T>は対応済み。IsVisible等の`bool option`フィールドは失敗をNoneに潰したまま |
| 2-1 | **部分解消** | 型・状態・フィードバックが閉じていない（R2-5〜R2-7）。正規の迂回が未記載（R2-6） |
| 2-2 | 解消 | ValidationInputと#3/#4/#6のケース |
| 2-3 | 解消（再評価） | 前回の前提に誤りがあった（R2-21） |
| 2-4 | 解消 | 13.1の文言のみ未修正（R2-24） |
| 2-5 | 解消 | |
| 2-6 | **未解消** | R2-8 |
| 3-1〜3-4 | 解消 | JournalDegradedの型漏れ（R2-16） |
| 4-1 | 解消 | 前提条件の明記漏れ（R2-23）、ShowWindowの戻り値の罠（R2-4） |
| 4-2 | 解消 | 非決定的な例外の除外（R2-26） |
| 4-3 | 解消（文言矛盾） | 22-16（R2-19） |
| 4-4 | 部分解消 | EffectCallAbandonedの対象が不整合（R2-4） |
| 5-1 | 解消（要追補） | 信頼の根拠（R2-9）、リンク検査（R2-10）、報告のみのモード（R2-11） |
| 5-2 | 解消（要追補） | Rollbackはhash参照のみに（R2-9） |
| 5-3/5-4 | 解消 | |
| X-1 | **部分解消** | Replayモード（R2-12）、対象型（R2-13）、保持場所（R2-14） |
| X-2 | 概ね解消 | 前提検査の評価場所（R2-25） |
| X-3 | 解消 | |

## D. 収束のために最低限必要な修正（優先順）

1. R2-1：classify表に`(SendMessageTimeout, Win32LastError, 1460) → TargetHung`を追加し、キーを3つ組にする。
2. R2-5：`gate`の入力を`GateInput`に、`StepResult.Effects`を`GatedEffect list`にし、弾いたEffectのPendingを`Cancelled`へ遷移させることを`step`の規定に入れる。
3. R2-9/R2-10：「承認済み」の根拠をHostの採用記録（Journal）にし、`link`段階を追加する。
4. R2-12/R2-13：ReplayモードではUnrecognizedをFail-fastにし、タグ規約の対象を永続化される全DUにする。
5. R2-2/R2-4：事前確認のタイムアウト値と`ElapsedUs`の計測、`RestoreBeforeMove`を`ShowWindowAsync`にする。
6. R2-6/R2-15/R2-16/R2-19：文書内の不整合（安全停止の迂回の明記、classifyの版、JournalDegradedの型、22-16の文言）。

これらを反映すれば、ADR-014は収束と判断できる。R2-3（Simulationの実行プロトコル共有）とR2-11（報告のみの再分析）はユーザー判断を要する設計選択なので、収束条件からは外し、21章の未決事項に載せる扱いでもよい。
