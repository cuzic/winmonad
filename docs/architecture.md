# Windows プログラマブル・ウィンドウマネージャー 統合設計書

Version: 2.33（§4.4「コマンドの意味」の`FocusNext`/`FocusPrevious`を実装した——`WorkspaceSet.rotateNext`/
`rotatePrevious`（既存のzipper回転、Tiled/Floating両方を巡回し、端では反対側へ巡回する）をアクティブな
Workspaceへ適用し、回転後の新しい`Focus`へ一度だけ`RequestForeground`する。`focused`が`Elsewhere`でも
zipper自身の`Focus`を基準に次/前へ移る（`focused`の値を経由しない）。窓が1つしか無くFocusが変わらない
場合は無意味な`RequestForeground`を発行しない。ユーザーが最小化した窓を巡回から外す規則（R7-5）は
対象外のまま：FocusドメインはPlacementの`ObservedPresence`/`IsMinimized`信念を読む経路を持っていない
ため、専用のラウンドへ切り出す。`PromoteToMaster`/`SwapWithMaster`/`CloseFocused`/`FocusMonitor`/
`ChangeLayout`/`FocusMostRecent`は依然未配線のまま。
Version: 2.32（§4.4「SwitchWorkspaceの意味」（xmonadのview semantics）を実装した——`Topology.update`の
`UserCommand (SwitchWorkspace ws)`が、`ws`が既にvisibleなら`current`をそのモニターへ入れ替えるだけ
（`CurrentChanged ws`）、`ws`がhidden（未知のwsも含む——`WorkspaceSet`は全域なので空のWorkspaceとして
扱える）なら`current`の画面の表示を`ws`へ入れ替えそれまでのWorkspaceがhiddenになる
（`WorkspaceHidden`/`WorkspaceShown (ws, UserView)`）、`NoMonitors`なら何もしない、の3分岐で実装した。
ユーザーの明示操作は自動復帰より優先し`ReturnMonitor`を解除する（§4.4規則3）。`greedyView`（2画面の
表示を交換する）は既定にしない。抑止区間（Reconfiguring）中はSwitchWorkspaceも他のUserCommandと
同じく強制的にSettlingへ入らせる。v2.31で消費を始めた`WorkspaceShown`/`WorkspaceHidden`
（Placement側のHiddenByWm/Shown None反映）はこれで`MonitorLifecycle.reconcile`（モニター接続・切断）
だけでなく`SwitchWorkspace`からも発行されるようになった。`WmState.step`は新たに`CurrentChanged`/
`WorkspaceShown (_, UserView)`を消費し、切り替え先Workspaceのzipperの`Focus`（`Stack = None`なら
`RequestForeground`を発行しない）へ一度だけ`RequestForeground`する（§4.4「ユーザーの操作で
Workspaceが表示されたとき…」）。アプリの自己アクティブ化（`ForegroundFollow`）やモニター再接続
（`Reconcile`）による表示ではフォーカスを要求しない。`SwitchSettle`（切替直後の揺れの吸収）は対象外の
まま——実際のOS側のタイミング挙動の実測が無く、適切なSettleMsや再要求の発火条件を決める根拠が
無いため、専用のラウンドへ切り出す。
Version: 2.31（§4.4「WMが見せているべきか。非表示の実装方式に依存しない」の`HiddenByWm`/`Shown None`を
初めて実装した。`Placement.onWorkspaceHidden`/`onWorkspaceShown`が、該当Workspaceに属する窓の
`Desired`を更新し`SetPresence`を発行する。`SwitchWorkspace`自体はまだ未配線だが、`MonitorLifecycle.
reconcile`（モニター接続・切断、`settle`の中で`TopologySettled`と同時に発行）は既に`WorkspaceShown`/
`WorkspaceHidden`DomainEventを発行していたのに`WmState.step`がこれまで捨てていたため、モニター切断で
非表示になったWorkspaceの窓がOS側では表示されたまま取り残される、という実在する不具合を閉じた
（WMは今回`SetPresence`を発行するまで、この種の窓を一度も隠していなかった）。再接続時は`Desired`を
`Shown None`にし（目標矩形は不明のまま）、同じstepのReconcile（`TopologySettled`が既に`shouldReconcile`
を真にしている）が新しい`SetRect`を無条件に発行し直す——`dispatchPlacements`の`desiredChanged`判定は
`Shown (Some _)`以外を常に「変化あり」と見なすため。「実際に非表示/表示になったか」の観測による確認
（`Satisfied`相当、`ObservedPresence`信念との一致判定）はまだ無く、`SetPresence`呼出の失敗も今は
検知しない——専用の信念・タイムアウト機構が必要な別の課題として対象外のままにする。`CurrentChanged`
は`SwitchWorkspace`自体が未配線のため依然捨てる。
Version: 2.30（`onEffectCallReturned`の`classify`結果のうち`Transient`（ERROR_TIMEOUTのハング判定前）
にもRetryを拡張した。`classify`の各分岐を個別に検討した結果——`TargetGone`（ハンドル無効、再試行
しても同じ結果）、`Denied`（拒否は持続的）、`TargetHung`（Healthの管轄）は根拠が無いので単発の諦め
のまま、`Unclassified`（未知の失敗）も当てなく再試行するのは危険なので対象外のまま——`Transient`
だけは「一時的」という性質自体がリトライの根拠になるため、`onPendingDeadlineTimerFired`と共有する
`retryOrGiveUp`ヘルパーで即座に再試行するようにした。この変更で`onEffectCallReturned`が
`Effect list`/`TimerCommand<TimerId> list`を返せるようになったので、`rid`自身の`PendingDeadline`
タイマー（`EffectCallReturned`はタイマー発火より先に届くことがあるため、まだ生きている状態で
呼ばれる）を取り消していなかった既存の取りこぼしも同時に塞いだ——以前はこの関数がTimerCommandを
一切返せず、`WmState.step`の呼び出し側が`[], []`を固定で渡していたため、修正自体ができなかった。
Version: 2.29（§7.2「TopologySettled → Superseded」を実装した。`Topology`が出す`TopologySettled`
DomainEventをこれまで`WmState.step`が捨てていた（Opusレビュー5-3）のをやめ、`Placement.onTopologySettled`
を呼ぶようになった：既存の全Pendingは古いモニター配置に基づく目標なので、観測を待たず即座に
`CancelTimer`付きで一括取り消す（`Desired`はあえて残す）。これだけでは取り消した代わりが二度と
発行されない空白ができるため、`shouldReconcile`（段⑦の引き金）も`TopologySettled`で真になるよう
拡張した——同じstepのReconcileが新しいモニター配置で目標を計算し直し、窓のモニターが変わらなければ
`dispatchPlacements`のdesiredChanged判定により何もしない（偶然同じ座標になる場合に無駄な再発行を
しない）。`RetryAttempts`も同時にリセットする。Backoffと同様、個々のPendingが実際に影響を受けたかを
判定する根拠が無いため、モニターが変わったかに関わらず一律で全Pendingを取り消す簡略化。
`onEffectCallReturned`側のRetryと同じ理由で、`WorkspaceShown`/`WorkspaceHidden`/`CurrentChanged`
（`SwitchWorkspace`自体が未配線）は依然捨てたままにする。
Version: 2.28（§7.2「Timeout → Retry/Backoff」のうちRetry部分を実装した。`onPendingDeadlineTimerFired`
が、上限（既定3回、`pendingRetryMaxAttempts`）に達するまで、同じ`TargetRect`/`Epoch`へ新しい
`RequestId`で`SetRect`を即座に再発行し新しい`PendingDeadline`を始動する。Backoff（試行間隔を指数的に
空ける、`IntegrityReprobe`と同じ仕組み）は意図的に対象外のまま：`IntegrityReprobe`の間隔には「外部
プロセスを急かさない」という明確な理由があったが、`SetRect`の再試行に同じ理由は無く、適切な遅延量を
Phase 0の実測無しに決める根拠が無いため、即座に再試行し試行回数だけを数える単純な形にした。
`PlacementState`に`RetryAttempts: Map<WindowId, int>`を追加し、Satisfied/GateRejected/`EffectCallReturned`
の終端・窓の消滅・新しい意図（`Targetが変わったReconcile`）のいずれでも0へ戻す（古いTargetへの再試行
回数を新しい意図へ引き継がない）。`onEffectCallReturned`側（`Transient`/`Unclassified`を含む）は
依然単発の諦めのまま——`classify`の各分岐ごとにリトライの安全性を個別に検討する必要があるため
（`Denied`系はおそらく再試行しても無駄、`TargetHung`はHealthの管轄）、Timeoutだけに絞った。
`Wm.Simulation.Tests`の既存シナリオ（1回のTimeoutで即座に諦めることを前提にしていた）を、新しい
Retry優先の挙動に合わせて更新した。
Version: 2.27（§7.4のCoreCommand`MoveFocusedToWorkspace`を`WmState.step`へ配線した——xmonadの
`delete`と同じ意味論で、フォーカス中の窓を別のWorkspaceへ送り、元のWorkspaceのzipperは送った窓を
除いた隣の窓へ`RequestForeground`する。`WorkspaceSet.moveToWorkspace`が内部で呼ぶ`Zipper.remove`
（「取り除いた窓がFocusだった場合は隣へ移る」）が既に隣への移動を実装していたので、配線する側は
移動とRequestForegroundの発行だけで済んだ。移動先が既に所属するWorkspaceと同じ場合は並び順を乱す
だけの無意味な操作として何もしない。実装の過程で、`RequestId`の採番元がこれまで`PlacementState`
だけに閉じていて（`dispatchPlacements`が唯一の消費者）、Focus/Workspaces発のEffect（本バージョンの
`RequestForeground`、将来のFocus.SwitchSettleの再要求）が新しいRequestIdを安全に発行する手段が無い
ことに気づいた：`EffectCallReturned`はRequestIdだけで相関するので、採番元が2つに割れると
（例えば両方が0から独立にカウントする）異なる発行元の要求が同じIdを名乗る衝突が起こり得る。
`Placement.allocateRequestId`（カウンタの物理的な所在はPlacementのままだが、`WmState.step`が
Placement以外の理由でも呼べる窓口）を追加し、単一の採番元を保ったまま共有できるようにした。
Version: 2.26（Placement.fsのファイル冒頭「対象外」が挙げていた「複数の可視Workspaceへの拡張」を
閉じた。`WmState.step`の段⑦（Reconcile）が、アクティブなWorkspace1つだけでなく、現在表示中の
全Workspace（他モニターに表示中の可視Workspaceも含む）を`Topology.displays`の`current :: visible`
の順に`Placement.reconcileWorkspace`へ渡すようになった。実装中、`PlanValidator.checkNoMissingPlacement`
（§6.2「`TiledWindows ⊆ Alive ∩ Managed(Tiled)`」、実際にはこの呼び出しでは等号を要求する）が、
渡された`Management`/`Alive`のドメイン全体に含まれるTiled×Aliveな窓が、この1回の`placements`に
必ず現れることを要求する既存の実装を初めて踏んだ：単一のアクティブWorkspaceしか呼んでいなかった
これまでは、たまたま全てのTiled×Alive窓が同じWorkspaceに属していたため表面化していなかったが、
2つ目のWorkspaceを同じグローバルな`managementByWindow`/`aliveWindows`で呼ぶと、他のWorkspaceの
窓が「この計画に無い」という誤検知で`RejectWholePlan`になり、無言で何も配置されなくなる既存の
潜在的な設計上の誤り（新規に導入した不具合ではない）を見つけて修正した。`WmState.step`が
`reconcileWorkspace`を呼ぶたびに、そのWorkspaceのStack（Tiled∪Floating、`WindowClassified`の
反映先と一致）に属する窓だけへ`Management`/`Alive`を絞り込むようにした。「WorkspaceShown/Hidden
経由のReconcileトリガー」はまだ無い（`shouldReconcile`は依然`windowsEvents`/`WindowRemoved`だけが
引き金で、モニター接続等でWorkspaceが新たに表示されただけではReconcileされない——対象外のまま）。
Version: 2.25（`Wm.Simulation`の`WindowBehavior`に`DelayedByMs`（§12.1の`ScheduledSimEvent`相当：
`SetRect`の呼び出し自体は即座に成功するが、要求どおりの矩形の観測は指定ミリ秒だけ遅れて届く）を追加し、
`VirtualWorld`に`Scheduled`（未発火の予定観測）と`advanceClock`（仮想時計を進め期限到達分だけ発火する）
を実装した。`ScenarioRunner.fs`が冒頭コメントで明示的に「タイマー駆動の往復はシナリオ側の責務のまま
結合しない」としていた部分を初めて専用シナリオ（`tests/Wm.Simulation.Tests/DelayedWindowScenario.fs`）
として書き、疑似OS側の`Scheduled`とCore側の`PendingDeadline`タイマー（`Reducer.AdvanceToNextTimer`）
という互いに無関係な2つの時計の競合——遅延観測がPendingDeadlineより先に届く場合（`advanceClock`の
発火でSatisfied）と、PendingDeadlineより後にしか届かない場合（`AdvanceToNextTimer`がTimeoutを先に
発火してPendingを打ち切る）——の両方を検証した。実装中、`settleTopology`ヘルパーが`ScreenSettle`→
`Grace`遷移で新たに始動する`ScreenGrace`タイマー（250ms後）を汲み取らずに残していたため、
`AdvanceToNextTimer`を明示的に呼ぶシナリオでは最速の期限としてこれが先に誤ってポップされてしまう
取りこぼしを見つけて修正した（`ConstrainedWindowScenario.fs`等、`AdvanceToNextTimer`を二度と呼ばない
既存シナリオでは無害だったため、これまで気付かれていなかった）。
Version: 2.24（§4.5のMovability信念規則のうち未実装だった「`Satisfied`（Pendingが観測と一致）の瞬間に
`Movable`（`Confirmed`）へ確定する」を`checkPendingSatisfied`に実装した。「試したかどうか」の判定は
不要で、この遷移は無条件に「配置できた」という直接の証拠であり、既にMovableの窓に対しても確度を
`Inferred []`から`Confirmed`へ進める意味を持つ。あわせて`Mismatch` → `Reevaluate`を依然未実装のまま
とする理由を明文化した：観測が`TargetRect`と一致しない場合に即座に`Desired`を取り消して再試行させる
実装も検討したが、`SetRect`完了前に届く無関係な観測（他の属性変化による全体Snapshot等）が、まだ
反映されていない古い矩形を運んで「不一致」と誤判定し、実際には進行中のPendingを早まって取り消して
しまう危険がある（`IntentEpoch`照合や観測の鮮度判定が無いこのラウンドでは安全に区別できない）ため、
安全側に倒して対象外のままにする）
Version: 2.23（v2.22で見つけ記録した既知のギャップ（`Denied OtherDenial`で`Movability`が`NotMovable`に
なっても、Windows側がまだこれを読んで`Unplaced`へ降格しない）を閉じた。`Core.Windows`の
`ClassificationCtx`に`Movability: Map<WindowId, Belief<Movability>>`（Placementの信念の読み取り専用の
写し、段①⑤でWindowsがPlacementより先に処理されるためstep開始時点の値を渡す）を追加し、
`toEffective`が`Movability = NotMovableならTiled/Floatingを Unplacedに読み替える`（§4.4）を実際に行う
ようになった。`Denied OtherDenial`の帰結として`WmState.step`が新たに呼ぶ`Windows.onMovabilityBecameNotMovable`
は、既に`Tiled`/`Floating`で確定している窓だけを`Unplaced`へ降格し`WindowUnmanaged`を出す
（`ByUserOverride`の窓は対象外、§4.4のUnplaced読み替えはPolicyの分類結果に対する規則なのでR4-20と
同じ理由でユーザーの明示的な指定を上書きしない）。`classificationEvent`も`EffUnplaced`を`EffExcluded`
と同じく`WindowUnmanaged`として扱うようになった（§4.4「動かせない窓はどのWorkspaceにも属さない」）。
これにより、前バージョンで「安全だが非効率」としていた無駄な再試行・ゲート拒否の繰返しが止まる）
Version: 2.22（Wm.Simulationが`EffectCallReturned`を実際に返せるようになったので（v2.21）、
`InputEvent.EffectCallReturned`をPlacement/Healthへ配線した。`Placement.onEffectCallReturned`は
`Ok ()`では何もせず（§7.2「目標矩形を観測するまではSatisfiedにしない」）、`Error`は`classify`で
`EffectFailure`へ変換してPendingを`Cancelled`にし、`Denied OtherDenial`ならさらに`Movability`を
`NotMovable`へ修正する。`TargetHung`時の`Health.Responsiveness`修正はPlacementの管轄外（ADR-018）
なので、`onEffectCallReturned`の戻り値（`(WindowId * EffectFailure) option`）を`WmState.step`が見て
`Health.applyEvidence`を呼ぶ——ドメインをまたぐ配線が`step`の責務であることを体現する実例になった。
`Denied OtherDenial`で`Movability`が`NotMovable`になっても、Windows側がまだこれを読んで`Unplaced`へ
降格する配線が無い（Windows.fsのファイル冒頭コメント参照）ため、該当窓は無駄な再試行とゲート拒否を
繰り返す既知のギャップとして残った（安全だが非効率、Placement.fsのファイル冒頭コメントに明記）。
Retry/Backoff、`Mismatch`→`Reevaluate`、`Movability`の`Satisfied`→`Movable`遷移は引き続き対象外）
Version: 2.21（ADR-019実装順17・18：Wm.Simulation（VirtualWorld.fs/WindowBehaviors.fs/ScenarioRunner.fs）
とWm.Simulation.Tests（Tests/ConstrainedWindowScenario.fs）を新設した。`VirtualWorld.applyEffect`は
このラウンドでは`SetRect`を`Immediate`挙動（即座に成功し要求どおりの矩形がそのまま観測される）で
適用するだけを実装し、`WindowBehavior`の他の部品（`DelayedByMs`/`HungAfterMs`等）、`SimEvent`の
Raw層・Normalizer経由化、`RequestForeground`等の他のEffect種別は対象外とした（VirtualWorld.fsの
ファイル冒頭コメント参照）。`Wm.Runtime.Reducer`と`VirtualWorld`を実際に結びつけ、モニターが現れて
トポロジが確定し、窓が現れて分類・Reconcile・SetRectの発行・疑似OSの応答・Satisfiedでの収束という
往復が、本物のEffect実行経路を通じて成立することを確認した——1章の最優先仮説の土台となるシナリオの
最初の実例である。`GatedEffect`のコンストラクタがinternal（Wm.Core/Wm.Core.Testsのみ）であるため、
Wm.Simulation.Testsは手作りのGatedEffectを作れず、必ず実際のReducerパイプラインが生成した本物を
使う——これは設計上の制約であり、意図どおりに機能していることを確認した）
Version: 2.20（§7.2のPending/Desiredの往復を最小の範囲で実装した——`reconcileWorkspace`（§7.1段⑦。
アクティブなWorkspace1つだけを対象に、`Wm.Policy.selectLayout`で選んだLayoutを`Wm.Layouts`
Registryから解決して呼び、`PlanValidator`で検証し、差分だけを`SetRect`として発行する）、
`checkPendingSatisfied`（`WindowObserved`のたびに`Geometry.VisibleFrameRect`と照合しSatisfiedへ）、
`onPendingDeadlineTimerFired`（Timeout→単発の諦め、Desiredも取り消して次のReconcileで再試行させる）、
`onGateRejected`（`GatedEffect.gate`の拒否リストを同じstep内で即時にPendingへ反映）。`Wm.Core`が
初めて`Wm.Layouts`に依存するようになった（§17の依存方向表どおり、実装順14のこのラウンドまで
不要だった）。対象外にしたもの（Placement.fsのファイル冒頭コメントに明記）：Retry/Backoff、
`Mismatch`→`Reevaluate`、`EffectCallReturned`/`classify`の分岐全体（Shell/Simulation側の応答経路が
まだ無い）、トポロジ確定時の一括`Superseded`、複数の可視Workspaceへの拡張）
Version: 2.19（21章で解決した方針（v2.18）どおりに、Core.PlacementへTargetIntegrity（ProbeOnce規則）と
Movability（発見時の楽観的既定値、TargetIntegrity probeで`Higher`が観測された場合の`NotMovable`遷移）を
実装した。`onWindowObserved`が窓の発見時に`snapshot.TargetIntegrity = None`なら`Reprobe (w, KTargetIntegrity)`
Effectを発行し、`Error`ならHealthの`ResponsivenessReprobe`と同じ指数バックオフ（`PlacementTimer.IntegrityReprobe`、
既定3回）で再probeする。`GatedEffect.gate`に実際にEffectを通す配線（`topologyResponse.Actions`との合流）も
このラウンドで初めて行い、`GateInput.NotMovable`を`Placement.Movability`の実際の信念から組み立てるように
した（以前は空集合だった既知のギャップを閉じた）。`Movability`の残りの遷移（`Denied`/`Satisfied`、
ユーザー操作による試し直し）は引き続きPending実装待ち）
Version: 2.18（21章に新しい未決事項を1件追加し即座に解決した：`TargetIntegrity`のProbeOnce規則の起点は、
Shellの自発的な問い合わせに委ねるのではなく、Coreが窓の発見時に能動的に`Reprobe (w, KTargetIntegrity)`
Effectを発行する（ADR-002/ADR-017の一貫性、`Effect.Reprobe`/`BeliefKey`の既存設計に合わせる）。
Core.Placementの次ラウンド（TargetIntegrity/Movabilityの実装）のための前提を先に文書で固定した）
Version: 2.17（ADR-019実装順14の最後のドメイン、Core.Placement（Placement.fs）に着手した。§7.2の
Desired/Observed/Pendingの状態機械・RequestId/IntentEpochのライフサイクル全体は判断点が多く1ラウンドでは
実装できないため、根拠（証拠の供給元）がPendingに依存しない部分だけを最初のラウンドとして切り出した：
`GeometryBeliefs`（矩形・モニター・最小化・表示状態、LatestWins規則）の`WindowObserved`/
`MinimizedStateChanged`からの修正と、消滅時のクリーンアップ。`MinimizedStateChanged`は
ADR-019実装順10のWm.Normalizationが定義していたが`WmState.step`に配線先が無かった入力で、
これで塞がれた。`MinWidth`/`DpiBehavior`（Accumulating規則、証拠が`SetRect`の`Mismatch`/DPI2段目観測）、
`Movability`のPending依存の遷移（`Denied OtherDenial`→`NotMovable`、`Satisfied`→`Movable`）、
`TargetIntegrity`のProbeOnce（「発見時に1回probe」をCoreが`Reprobe`Effectで能動的に行うか
Shellの自発的な列挙に委ねるかが本書に明記が無く設計判断を要する）は、専用のラウンドへ明示的に
持ち越した（Placement.fsのファイル冒頭コメント参照）。`WmState`に`Placement: PlacementState`
（今は`Geometry`だけを持つ）を追加し、`vanishWindow`が消滅時にそれも除去するように拡張した）
Version: 2.16（opus-review-adr019/opus-review-phase-aチームメイトからの再確認で、ADR-019 round6レビュー（R6-01、Displaysの射影の文言）は既にv2.10で反映済みと確認。一方review-phase-a-round1.mdの4-3（窓の消滅時、Health.ReprobesやWindows.AwaitingEvidenceに動いている待ちのタイマーがあっても`CancelTimer`を出していなかった）が未修正のまま残っていたことが判明し、本版で修正した。`vanishWindow`がHealthの`ResponsivenessReprobe`とWindowsの`ClassificationDeadline`の両方について、動いていれば`CancelTimer`を返すようにし、`WmState.step`の`Timers`出力へ合流させた。性質テストでは検出できない種類の後退（タイマー実行器側にエントリが残るだけで、状態の不変条件は破れない）なので、決定的なテスト2件を追加し、mutation testingで両方が検出することを確認した）／2.15（ADR-019実装順14のCore.Windowsドメイン（Windows.fs）を実装し、WmState.stepへ結線した——分類の状態機械（`NotCandidate`/`AwaitingEvidence`/`Classified`）、Identity（Sticky規則）・ClassInputs（LatestWins規則）の信念修正、§5.4の期限超過時Floatingフォールバックと一度だけの再分類、ユーザー指定（ToggleFloating/SetManagement/ClearUserOverride）、消滅時のクリーンアップ、`WindowClassified`/`WindowUnmanaged`のWorkspacesへの反映を実装した。実装の過程で2つの既知のドキュメント/コードの不整合を解消した：(1) `ClassInputBeliefs`/`GeometryBeliefs`はv2.11でドキュメント上`BeliefSlot`から`LatestWins`へ訂正されていたが、Windows/Placementドメインが無かったためContracts側の型に未反映のままだった（本版で反映）。(2) `PolicyEvaluator.ClassificationResult.Resolved`に、マッチしたルールのIdを運ぶ`matchedRule: PolicyRuleId option`を追加した（`WindowRecord.Classification`の`ByPolicy`が実際にどのルールで確定したかを記録するために必要になった）。意図的に対象外にしたこと（Windows.fsのファイル冒頭コメントに明記）：HWND再利用の検知（Sticky規則の`Process`不一致→`ForgetWindow`）、`StyleChanged`/`MinimizedStateChanged`の配線、`ByPolicy`確定済み窓の一般的な増分再分類と7.3の振動閾値、`Unplaced`への読み替え（Placementの`Movability`が必要）。候補条件の判定は「可視・非Cloak」の2属性に限定し、「非ツール窓」の判定はShellの列挙時点で既に行われている前提とした（21章に記録）。性質テストで(X2)（Workspacesに属する窓⟺WindowsでClassified(Tiled|Floating,_)）を追加した。Phase A実装の最初の4ドメイン（Core.Topology/Workspaces/Health/Focus）とWm.Contracts全10層・Wm.Journal・Wm.Runtimeを実装し、Opus批判的レビューを2ラウンド実施（docs/review-phase-a-round1.md、round2.md）。Round1はGatedEffect.gateの実装が§6.2の表と11セル食い違っていたこと（blocking。特にSetPresence×TargetHiddenByWmの誤適用でWM自身の非表示Effectが弾かれる）、Core.TopologyがReconfiguring解除時に`latest=[]`で確定しNoMonitorsになること（blocking）、`Belief<'T>.LastRevisedAt`の単位がドメインごとに食い違うこと等、6件のblockingと15件のshould-fixを発見。修正後のRound2再確認では、修正の多くがテストで固定されていないこと（mutation testingで6箇所の後退が未検出と判明）と、修正自体に新しい不具合が2件（LatestWinsのErrorによる水位進行がUnformed/Believedで非対称、3-3の修正がbyAssignment経由の表示では予約を解放しない）入ったことを発見し、全て修正・テストを追加した。本版で§4.4（`TopologyState.LastObserved`追加）、§4.5（`Belief<'T>`に`LatestWins<'T>`を追加し`ClassInputBeliefs`/`GeometryBeliefs`/`Foreground`の型注記を訂正）、§6.2（`GateInput`の`RestoreIssuedInBatch`削除・`NeedsRestore`追加、`gate`をinternalに訂正）、§3.3（`TimerId`に`RequireQualifiedAccess`を追記）、§9.2（Journal往復の不変条件を追記）、§4.4のWorkspace復帰先の割当規則（予約の解放規則を訂正）を反映した。未決：ReturnsとAssignmentsが同じWorkspaceに競合する場合の優先順位（4.4末尾に記録）。v2.11時点のコードは`origin/main`へpushし、GitHub Actions（phase-a-build.yml）で実際に検証済み（初回green）。Journal往復テストをFsCheck Arbitraryで`Management`/`LayoutArgs`/`ErrorSpace`/`CoreCommand`の全ケースへ拡張し、`FS0025`（不完全なパターンマッチ）を全プロジェクト共通でエラー化した（round2レビューT-5）。CIのテストログをFsCheck反例付きの詳細出力＋trx artifactへ強化した（v2.11）／ADR-019実装順10のWm.Normalization（WindowEventNormalizer.fs）を実装し、§21未決事項（R3-2：Normalizerの候補条件判定とCore状態の関係）を解決した——Normalizerは候補条件を一切判定せず、Raw観測を無条件・無加工でInputEventへ詰め替えるだけに限定し、候補条件の評価は将来のCore.Windowsドメインの責務とした（v2.12）／ADR-019実装順9のWm.Layouts（LayoutRegistry.fs/MasterStack.fs/Monocle.fs/PlanValidator.fs）を実装した（v2.13）／ADR-019実装順8のWm.Policy（PolicyTypes.fs/PolicyEvaluator.fs/PolicyAnalyzer.fs。PolicyArtifact.fsは次の増分へ分離）を実装し、§5.4の`selectManagement`のUnknown処理アルゴリズムを確定した（v2.14）
Date: 2026-11-02
Status: 基本アーキテクチャ決定／個別APIと性能値はPoCで検証／中核状態モデルとPhase順序を本版で見直し／Win32 API挙動の一部をdocs/feasibility.mdの調査結果で具体化／ADR-012・6.2のハング耐性方針はdocs/failure-catalog.mdで実機実証済み／例外・Result・EffectFailure・PlanValidator・PolicyArtifactのエラー表現をADR-014で整理／マルチモニター状態モデル・AppId・DenialKind等の未決事項をdocs/oss-wm-survey.mdの調査結果とADR-015で整理（v2.5）／観測・信念・決定の3層分離（ADR-016）と待ちのデータ表現（ADR-017）を新設し、v2.5レビューの指摘を反映（v2.6）／v2.6の新しい前提をCI実機検証し、ObservedPresenceの非表示方式別の判定要件を具体化。Cloak自己/他者判別（S12）とWH_KEYBOARD_LLタイムアウト（S14）は未解決・要再検証のまま（v2.7）／公開APIのみでCloak理由ビットAPIの動作を確認し、WH_KEYBOARD_LLタイムアウトの説明も得た。Cloakの自己/他者判別は依然未決だが代替案を1つ追記（v2.8）／ADR-015の整合性レベルAPI（Medium→High方向のトークン読み取り）を実機確認し解決済みに更新。Phase A着手（v2.9）／ADR-019でPhase Aのファイル→プロジェクト割当と実装順序を確定、Wm.Runtime・Wm.Normalizationを新設、4.4の`update`の戻り値に`DecisionTrace list`を補う（v2.10）／Phase A最初の4ドメインを実装し、Opus批判的レビュー2ラウンドの指摘を全て修正。§4.4/§4.5/§6.2/§3.3/§9.2を実装に合わせて訂正（v2.11）／origin/mainへpushしGitHub Actionsで初回green確認、Journal往復テストをFsCheck全ケース化しFS0025をエラー化（round2レビューT-5完了）、Wm.Normalization（WindowEventNormalizer.fs、ADR-019実装順10）を実装し§21のR3-2（Normalizerの候補条件判定とCore状態の関係）を解決（v2.12）／Wm.Layouts（ADR-019実装順9：LayoutRegistry.fs/MasterStack.fs/Monocle.fs/PlanValidator.fs、§5.6/§6.2のとおりに実装）を追加（v2.13）／Wm.Policy（ADR-019実装順8：PolicyTypes.fs/PolicyEvaluator.fs/PolicyAnalyzer.fs）を追加し、ClassificationViewを定義してselectManagementのUnknown処理を確定した（v2.14）／Core.Windows（ADR-019実装順14：Windows.fs）を実装し、WmState.stepへ結線。`ClassInputBeliefs`/`GeometryBeliefs`のLatestWins訂正をContracts側に反映し、`ClassificationResult.Resolved`にmatchedRuleを追加、(X2)の性質テストを追加した（v2.15）／opus-review-adr019/opus-review-phase-aチームメイトからの再確認で、review-phase-a-round1.mdの4-3（窓の消滅時にReprobe/ClassificationDeadlineのタイマーをCancelしていない）が未修正と判明し修正。R6-01は既にv2.10で反映済みと確認（v2.16）／Core.Placement（Placement.fs）に着手。PendingのEvidence供給に依存しない部分（GeometryのLatestWins修正、MinimizedStateChangedの配線）だけを最初のラウンドとして切り出し、MinWidth/DpiBehavior/Movability(一部)/TargetIntegrity probeの能動的トリガーは専用のラウンドへ明示的に持ち越した（v2.17）／21章に`TargetIntegrity`ProbeOnceの起点（CoreがReprobeを能動的に発行する）を追加・解決し、次ラウンドの前提を文書で固定した（v2.18）／Core.PlacementへTargetIntegrity（ProbeOnce）とMovability（発見時既定値・Higher観測によるNotMovable遷移）を実装し、GatedEffect.gateへのEffect配線とGateInput.NotMovableの実装を初めて行った（v2.19）／§7.2のPending/Desiredの最小の往復（Reconcile→SetRect→Satisfied/Timeout/GateRejected）を実装し、Wm.CoreがWm.Layoutsに初めて依存するようになった（v2.20）／ADR-019実装順17・18：Wm.Simulation（VirtualWorld/ScenarioRunner）とWm.Simulation.Tests（ConstrainedWindowScenario）を新設し、Reducer+VirtualWorldの実際の往復（分類→Reconcile→SetRect→観測→Satisfied）を初めてEnd-to-Endで確認した（v2.21）／`EffectCallReturned`をPlacement/Healthへ配線し、`classify`の`Denied OtherDenial`→`Movability`、`TargetHung`→`Health.Responsiveness`（ドメインをまたぐのでWmState.stepが仲介）を実装した（v2.22）／Core.Windowsが`Placement.Movability`を実際に読み、`NotMovable`の窓を`Unplaced`へ降格してWorkspacesから除くようになった。無駄な再試行・ゲート拒否の繰返しという既知のギャップを閉じた（v2.23）／`checkPendingSatisfied`が一致観測を検出した瞬間に`Movability`を`Movable`（`Confirmed`）へ確定するようにした。`Mismatch` → `Reevaluate`は安全上の理由（鮮度判定無しでは誤判定の危険）で意図的に未実装のままとすることを明文化した（v2.24）／`Wm.Simulation`の`WindowBehavior`に`DelayedByMs`と`VirtualWorld.advanceClock`を実装し、疑似OS側の遅延観測とCoreの`PendingDeadline`タイマーという2つの独立した時計が絡むシナリオを初めて専用テストとして書いた。既存シナリオには影響しない`settleTopology`の`ScreenGrace`タイマー取りこぼしを発見・修正した（v2.25）／段⑦のReconcileをアクティブなWorkspace1つだけでなく現在表示中の全Workspaceへ拡張し、`PlanValidator`の`checkNoMissingPlacement`が要求する「渡されたTiled×Aliveの集合は呼び出し先のWorkspaceに属する窓だけへ絞り込む」という既存の暗黙の前提を初めて踏んで修正した（v2.26）／§7.4の`MoveFocusedToWorkspace`を配線し、Focus/Workspaces発のEffectが安全にRequestIdを発行できる`Placement.allocateRequestId`という単一の共有採番元を新設した（v2.27）／§7.2の`Timeout → Retry`を実装し、`PendingDeadline`満了を上限回数まで同じTargetへの即座の再試行として扱うようにした（Backoffは実測値が無いため依然対象外）（v2.28）／§7.2の`TopologySettled → Superseded`を実装し、`WmState.step`が捨てていた`TopologySettled`DomainEventを初めて消費するようになった。`shouldReconcile`も拡張し、取り消した代わりが同じstepで発行し直されるようにした（v2.29）／`onEffectCallReturned`の`Transient`分類にRetryを拡張し、まだ生きている`PendingDeadline`タイマーを取り消していなかった既存の取りこぼしも塞いだ（v2.30）／§4.4の`HiddenByWm`/`Shown None`を初めて実装し、`Placement.onWorkspaceHidden`/`onWorkspaceShown`が`WorkspaceHidden`/`WorkspaceShown`DomainEventを消費して`SetPresence`を発行するようになった。モニター切断で非表示になったWorkspaceの窓がOS側では表示されたまま取り残される実在する不具合を閉じた（v2.31）／§4.4の`SwitchWorkspace`（xmonadのview semantics）を実装し、`WmState.step`が`CurrentChanged`/`WorkspaceShown (_, UserView)`を消費して切り替え先WorkspaceのzipperのFocusへ`RequestForeground`するようになった。`SwitchSettle`（切替直後の揺れの吸収）は実測が無いため依然対象外（v2.32）／§4.4「コマンドの意味」の`FocusNext`/`FocusPrevious`を実装し、アクティブなWorkspaceのzipperを回して新しいFocusへ`RequestForeground`するようになった。最小化した窓を巡回から外す規則（R7-5）は対象外のまま（v2.33）
Implementation language: F# / .NET
Target: Windows 11を初期検証対象とするWindowsデスクトップ。主なCore開発はLinux
Audience: 開発者、設計レビュアー、AIコーディングエージェント
設計の一文要約：Windowsの実ウィンドウを管理しながら、意思決定・命令・実観測をUnified Journalに蓄積し、稼働中のWMをF# REPLから調査・介入でき、実測に基づく疑似OSで障害を再現・修正・回帰テスト化できる、観測・実験可能なプログラマブルWMを構築する。

本書は従来のv1.0〜v1.3を統合し、最新の採用方針で再編集した単独で読める基準設計書である。F*、全ポリシーのDeep Embedded DSL化は採用しない。設計中のF#コードは契約例であり、コンパイル済みの完成実装を意味しない。

関連資料：[feasibility.md](./feasibility.md)（Win32 API挙動の文献調査、既存Windows用タイル型WMの実装調査）、[spikes.md](./spikes.md)（Phase 0技術スパイクの計画）、[failure-catalog.md](./failure-catalog.md)（GitHub Actions windows-latestランナーでの実機検証結果。特に7章ADR-012と6.2のハング耐性方針はここで実証済み）、[review-result-design.md](./review-result-design.md)（ADR-014のエラー表現設計に対するOpus批判的レビュー第1ラウンド全文）、[review-result-design-round2.md](./review-result-design-round2.md)（修正後の収束確認・第2ラウンド。GatedEffectの型不備、ハング分類の欠落、PolicyArtifactの信頼根拠の穴等、1ラウンドでは見つからなかった実装レベルの不整合を多数指摘）、[oss-wm-survey.md](./oss-wm-survey.md)（xmonad/i3/sway/bspwm/dwm/awesome/Hyprland/yabai/komorebi/GlazeWMのソースコード調査。状態モデル・失敗分類・DPI・IPC等のアーキテクチャ上の設計判断をfeasibility.mdより広い範囲で比較し、v2.5の変更点の根拠になっている）、[review-oss-survey.md](./review-oss-survey.md)（v2.5に対するOpus批判的レビュー全文。R3-1〜R3-13。v2.6で全件反映）、[review-belief-layer.md](./review-belief-layer.md)（v2.6のADR-016/ADR-017とR3反映に対する独立したOpus批判的レビューと収束確認）、[adr-019-draft.md](./adr-019-draft.md)（ADR-019の全決定事項の詳細。本文の§0・§3.1・§3.4・§4.4・§17・§17.1・§19・21章への反映は本版で完了済み）、[review-adr019-round1.md](./review-adr019-round1.md)〜[round6.md](./review-adr019-round6.md)（ADR-019に対するOpus批判的レビュー全6ラウンド、収束確認）、[review-phase-a-round1.md](./review-phase-a-round1.md)（Phase A最初の実装12コミットに対するOpus批判的レビュー。GatedEffect/Topology/Belief/WindowRemoved/TimerId/step/Journal/Healthの指摘、v2.11で全件反映）、[review-phase-a-round2.md](./review-phase-a-round2.md)（round1の修正に対する再確認。mutation testingでテストの固定漏れを検出し、修正自体の新しい不具合（N-1〜N-3）を発見、v2.11で全件反映）。
## 0. 意思決定と設計状態
| ID | 判断 | 状態 |
|---|---|---|
| ADR-001 | F#純粋関数 + 宣言的Policy IRを採用。任意レイアウト計算とReconciliationはF#、構造解析が必要な分類・選択・優先順位はIR | 採用 |
| ADR-002 | Functional Core / Imperative Shell。Windows Shellと疑似OSは同じCoreを実行 | 採用（v2.6で、Impureim Sandwich・Sans-IO・決定的シミュレーションテスト等の外部の先行例との対応と、Coreが環境の時刻・乱数を読まない規則を3.4に明記） |
| ADR-003 | Desired・Observed・Pendingを分離し、OS要求の受付と実現を混同しない | 採用 |
| ADR-004 | Unified Journalは「Coreの外から来た事実」（入力、OS呼出結果、Artifact識別子、Checkpoint）を永続化の中心とし、判断（Rule選択、Predicate評価等）はCoreの決定性を前提にReplayで再構成する。判断の全量を都度永続化しない | 採用（v2.1で改訂。旧ADR-004は判断ログの全量永続化を前提にしていたが、決定的Coreとの重複によりホットパス遅延とJournal容量を不必要に増やすため撤回） |
| ADR-005 | Live REPLは別プロセス。Hostへの接続は型付きIPCに限定 | 採用 |
| ADR-006 | Fixed-log ReplayとCounterfactual Simulationを別機能として設計 | 方向性採用（Phase Dで価値を実測してから本採用を判断。疑似モデルが実不具合をどれだけ再現できるかの実証が無い段階での全面採用は時期尚早） |
| ADR-007 | Active Diagnosisで不足証拠を再収集し、疑似窓の振る舞いモデルを更新 | 方向性採用（選択アルゴリズム・モデル合成はPhase D以降に縮小スコープで評価。Phase A〜Cの型設計をこのADRに合わせて先行複雑化させない） |
| ADR-008 | F*による証明・コード抽出は初期構成に採用しない。型検査・Analyzer・PlanValidator・性質ベーステストを組み合わせる | 採用 |
| ADR-009 | 初期対象は同一ユーザーの対話デスクトップ上で動く、非特権のWindowsアプリ。OS合成・Explorerは置き換えない | 採用 |
| ADR-010 | ビルトイン設定の更新方式、任意F#拡張コードのプロセス隔離、journal保存媒体はPoCで決定 | 未確定 |
| ADR-011 | タイル型WMの中核状態（Workspace、窓順序/Stack、フォーカス履歴、CoreCommand）をCoreの第一級の状態として4章で定義する。Policy IRや診断機構より先にこれを固定する | 採用（v2.1で新設。旧版はこの状態がどこにも定義されておらず、WM本体が空白になっていた。**v2.5でモニター↔ワークスペース対応（`Screens`/`HomeMonitor`）とFocusHistoryの不変条件・スコープを追加し、xmonadの`StackSet`（current/visible/hidden screens）を模範に引用していたにもかかわらずマルチモニター状態が欠落していた構造的な穴を修正。4.4、OSS調査[oss-wm-survey.md]参照**。**v2.6でv2.5の追加分の不変条件と更新規則を確定（R3-1〜R3-4）：設定上のモニター割当（`PolicyIR.WorkspaceAssignments`）と切断時の復帰先（`WorkspaceState.ReturnMonitor`、退避時にのみ記録しフォールバック先で上書きしない）を分離、同じWorkspaceは1画面だけ・アクティブなモニターは表示中の画面の1つ・モニター0台の表現、`SwitchWorkspace`はxmonadの`view`意味論、Workspace内のフォーカスはzipper（xmonadの`Stack`と同形）、「今フォーカスされている窓」はフォアグラウンドの信念から導出する唯一の値（`focused`）、直前の窓へ戻る用の履歴はグローバルMRUに一本化。**ADR-018により中核状態をドメインごとの型に分け、`Displays`（xmonadのcurrent/visibleと同形）と`WorkspaceSet`（private）でこれらを型で保証**。独立レビュー[review-belief-layer.md]のR4/R5を反映**） |
| ADR-012 | OS呼出（Effect適用）は専用スレッド・タイムアウト付きで行い、ハングした対象アプリがCoreの入力処理・安全停止経路をブロックしないようにする | 採用（v2.1で新設） |
| ADR-013 | Windows Event Normalizerを純粋関数として切り出し、Raw観測（WinEvent種別＋その時点でShellが問い合わせた属性集合）をJournalの入力として記録する。疑似OSはRaw層から駆動できるようにする | 採用（v2.1で新設。旧版は疑似OSが正規化済みイベントより後段からしか駆動できず、分類・正規化起因の不具合を再現できなかった） |
| ADR-014 | エラー表現を3層に分ける：(1)予期された外部の失敗は境界で`Result`/`NativeCallRecord`に変換、(2)ユーザー提供のLayout関数の失敗は呼出点で捕捉し`LayoutError`に変換、(3)それ以外の例外は変換せずスレッド単位の最上位ハンドラで捕捉し安全停止へ。`EffectFailure`はCoreの反応軸（TargetGone/TargetHung/Denied/Transient/Unclassified）で切り、原因の多様性は`NativeCallRecord`（生の事実、ADR-013のNormalizerと同じ扱いの`classify`関数が解釈する）側で持つ。PlanValidatorの型保証は検証時点のDesiredだけでなく、Shell直前の`GatedEffect`にも及ぶ。PolicyArtifactは`PinnedPolicy`（当時のまま）と`DeployablePolicy`（現行Analyzer通過済み）に分離する | 採用（v2.4で新設。Opusレビュー[review-result-design.md]で「型による保証はシリアライズ・Replay・Retry経路・プロセス境界で途切れる」という指摘を受け、保証の及ぶ範囲を精密化した。**v2.11でGatedEffectの実装（§6.2）を表駆動に書き直した**：[review-phase-a-round1.md]で、手書きif/elif分岐が§6.2の適用範囲表と11セル食い違っていたことが判明（blocking 5件。例：`SetPresence`への`TargetHiddenByWm`誤適用でWM自身の非表示Effectが弾かれる）。`gate`自体もinternal化した（round1 2-9：公開関数のままでは`GatedEffect`のinternalコンストラクタが実質何も守らない）） |
| ADR-015 | `DenialKind`に`TargetIntegrityHigher`のような原因特化ケースは追加しない。**分類（決定）が使うのは原因ではなく能力の信念**（`Movability`：WMがこの窓を動かせるか、yabaiの`WINDOW_MOVABLE`相当、4.5）であり、`Denied OtherDenial`の結果と、取得できれば整合性レベルのprobe結果の両方を証拠としてこの信念を修正する。対象の整合性レベル（`TargetIntegrity`）は`Movability`の事前証拠とExplainの説明材料に留め、取得できなくても正しい挙動が変わらないようにする。`classify`は`NativeCallRecord`だけを読む純粋関数（窓の状態を読まない）のまま保つ | 採用（v2.5で新設、ADR-014の`DenialKind`初期セットを改訂。**v2.6で、未検証API（Medium整合性から`OpenProcessToken`+`GetTokenInformation(TokenIntegrityLevel)`でHigh整合性プロセスを読めるか）への依存を断った（R3-7）**：v2.5では整合性レベルの属性が分類述語`TargetIntegrityIs`を直接駆動していたため、このAPIが拒否された場合の代替が「開けなかった＝おそらくHigh」という、ADR-015自身が退けたエラーコードからの原因推定しか残らなかった。v2.6では分類は`Movability`の信念だけを読み、その信念は`Denied`の実績だけからでも形成される。整合性レベルのprobeは「最初の1回の失敗を省く」最適化とExplainの材料に格下げした。調査した12個のOSS WM（[oss-wm-survey.md]）のどれも原因特化の権限失敗分類を持たないこと、エラーコード単体では整合性起因かどうか判別できないこと（komorebiのDWMWA_CLOAK/E_ACCESSDENIEDの事例）が根拠。v2.5の「`(Api, Space, Code)`の純粋な3つ組」という表現は誤り（8.1の対応表は`ElapsedUs`も読む）で、正しい不変条件は「`NativeCallRecord`のみの関数」） |
| ADR-016 | **観測（Observation）・信念（Belief）・決定（Decision）を3層に分離する**。観測（4.2の`Observation<'T>`）は1回のOS呼出の生の結果で、Journalに残る事実である。信念（4.5の`Belief<'T>`）は、観測の履歴から推定した窓の属性で、確度（`Confidence`）と最終修正時刻を持ち、新しい証拠で修正され得る。決定（Policy評価、Reconciler、CoreCommandの解釈）は信念だけを読み、生の観測を直接読まない。**信念の計算は、それまでの入力列の決定的な純粋関数でなければならない**（Journalに保存せず、Replayで再計算する。信念はPendingの解決も証拠に使うので、正確には「観測だけ」ではなく「入力列とそれに対するCoreの過去の決定」の関数である、4.5） | 採用（v2.6で新設。窓の内部状態の多くは直接観測できず推定であり、推定は誤り得て、証拠が増えれば修正されるべきだというプロジェクトオーナーの指摘による。v2.5までは整合性レベル・ハング判定・AppIdの解決・DPI対応状況を1回きりの`Observation`として扱っており、スナップショットの統合規則が無い（R3-6）、フォーカスの情報源が2つある（R3-3）、未検証APIに分類が依存する（R3-7）という3つの指摘は、どれも「推定を事実として扱った」ことに起因する。5.4の三値`Truth`はブール値の信念の特殊形として再定義する（4.5）。ADR-004との整合は「信念は入力列の決定的な関数」という不変条件で保つ：信念が後から修正されれば、Replayはそこから導かれた決定も自然に再計算する。ADR-007（Active Diagnosis）が方向性として述べていた「不足証拠を再収集し、モデルを更新する」に、窓属性の単位で操作できる具体的な型を与える。**v2.11で`LatestWins<'T>`を追加（4.5）**：[review-phase-a-round1.md]で、LatestWins規則の新旧比較に必要な`SampleSequence`の置き場所が`Belief<'T>`に無く、実装が`LastRevisedAt`を代用していたため、Accumulating規則（`now`のms）と単位が混在する問題が判明（round1 1-1）。[review-phase-a-round2.md]の再確認で、修正（水位1本）自体にも新しい不具合（Unformedの間はErrorより古いOk観測を永久に捨てる、N-1）が見つかり、水位を`ValueSeq`/`ErrorSeq`の2本に分けて解決した） |
| ADR-017 | **Coreの「待ち」はデータで表す**。何かを待っている状態を、継続を閉じ込めたクロージャ（F#の`async`/`task`の継続等）で表さず、「タグ（DUケース）＋再開に必要なデータ＋期限」というクロージャを含まない値で表す（defunctionalized continuation）。タイマーは`step`が直接呼ぶAPIではなく、`StepResult`が返す`TimerCommand`（開始／取消）というデータとし、Shell/Simulationのランタイムが実行して発火を入力イベントとして戻す（3.3）。F#の`async`と自作`SynchronizationContext`（メッセージ専用ウィンドウ＋`PostMessage`）は**Shell内**で、Win32呼出を正しいメッセージポンプのスレッドに順序付けるためにだけ使う | 採用（v2.6で新設。クロージャはシリアライズもExplain/Journalからの検査もできず、ADR-004のReplay決定性を壊す。ADR-003の`Pending`/`RequestId`/`IntentEpoch`は既にこの原則に従っており、これを明示的な規約にして、後からの追加がクロージャに退行しないようにする。タイマーの形は作者のRust crate `timed-fsm`（遷移が行動とタイマー指令を純粋なデータとして返す設計）をF#へ移植したもの。v2.5までの`ArmTimer`/`CancelTimer`（int64の匿名ID）と、Pending期限・Unknown分類期限・S8のgrace window・7.3の安定待ちがそれぞれ独自の待ち方を持ちかけていた状態を統一する。ADR-012のEffect適用タイムアウト（Shell内の`SendMessageTimeout`の10ms）はCoreの待ちではないので対象外。独立レビューR4-1により、`timed-fsm`の`Consumed: bool`は移植せず、1 stepの処理順序の固定と原因ラベルに置き換えた） |
| ADR-018 | **Coreをドメインごとに分割する**（Topology、Workspaces、Focus、Windows、Health、Placement。4.4）。各ドメインは自分の状態の型・純粋な更新関数・型で表せる不変条件を持ち、単一の`step`の内側で固定順に合成する。合成はElm Architectureの「子のupdateと子のCmdを親の型へ持ち上げる」パターンに倣うが、`Elmish`ライブラリは使わない。信念もドメインごとに個別に持ち、1つの大きな信念オブジェクトに集めない | 採用（v2.6で新設。R3-2（モニター状態の不整合）とR3-3（フォーカスの二重の情報源）は、全ての関心事を1つのレコードに載せ、不変条件を後から検査する規約にしていたことから生じた。ドメインごとの型にすることで、`Displays`（xmonadの`StackSet`のcurrent/visibleと同形）・`WorkspaceSet`（private）・Workspace内のzipper（xmonadの`Stack`と同形）により、それらの不変条件の多くが「後から検査する性質」から「型で表現できない状態」に変わる。3.1の「単一のReducerイベント列」は変えない。ElmishのCmdはクロージャのリストで検査・Replayできないため、ADR-017と両立しない） |
| ADR-019 | **Phase Aの実装対象ファイル（§19）に§17のプロジェクトを割り当て、実際のF#コンパイル依存順に並べ直す**。新設プロジェクト：Wm.Runtime（Reducerループ、CheckpointファイルのI/O。Windowsに依存しないので、Phase AのLinux専用Simulationビルドから使える）、Wm.Normalization（ADR-013のNormalizerをWm.WindowsとWm.Simulationの両方から共有するために独立させる）。ドメイン間の読み取り（4.4の`'Ctx`）の実体は、§7.1の8段すべてを1段ずつ当たり直すと、(i) 少数の小さな値型（`ScreenPhase`/`FocusTarget`等、Wm.Contracts）か、(ii) `step`がその場で組み立てる識別子レベルの射影のいずれかであり、**6ドメインのモジュール同士は互いの型を一切参照しない**。`Displays`はsmart constructorの不変条件（3.4、4.4）を持つのでこの(i)の対象から除外し、`private`なままWm.Coreに残して射影だけを渡す。DTOはそれが写す型と同じプロジェクトに置く（Checkpoint DTOの変換はWm.Core、ファイルI/OはWm.Runtime）。決定性ゲート（3.4/17.1）の対象をWm.ContractsとWm.Normalizationにも拡大する | 採用（Opus批判的レビュー6ラウンドで収束。docs/adr-019-draft.md、docs/review-adr019-round1.md〜round6.md。副次的に、4.4の`update`の戻り値に`DecisionTrace`の出口が無いというarchitecture.md自身の不整合を発見し、本版で修正した） |


最優先で検証する仮説：実Windowsで観測した不具合を、同じCoreと取得した証拠に基づく疑似OS上に持ち込み、失敗する最小シナリオを作成して修正前後の挙動を比較できる。

第二の優先仮説（v2.1で追加）：キーボード中心のタイル操作（ワークスペース、Master–Stack、フォーカス移動）が、ホットキー登録・フォーカス許可・ハング耐性を含めて実際のWindows環境で成立する。この仮説が崩れると診断機構の価値そのものが成立しないため、Phase 0〜Bで最優先に検証する。
## 1. 背景、目的、製品価値
### 1.1 解こうとしている課題
Windowsアプリのウィンドウ制御は、WMが命令すれば必ず成功する同期処理ではない。対象アプリがリサイズを拒否・補正することがあり、通知は遅延・重複・逆転して見えることがある。DPI、所有ダイアログ、手動操作、OSのフォーカス制約、消滅したHWND、アプリ別の独特な挙動が実用性を左右する。

一般的なWMでは、これらの不具合は「特定アプリで位置がずれる」「勝手に動き続ける」などの現象として現れ、再現とデバッグが難しい。本プロジェクトは管理機能に加え、実行中の判断を説明し、原因究明に必要な観測を増やし、再現可能なモデルとテストに変換することを製品上の価値とする。
### 1.2 開発対象
- キーボード中心のタイル型WM。Explorer/DWM、通常のデスクトップおよび既存アプリと共存する。
- WM自身が持つ最小限のワークスペース・窓順序（master/stack）・フォーカス履歴のCore状態モデル（4章で定義。OSの仮想デスクトップ機能との連携ではなく、Core内の状態である）。
- 宣言的なウィンドウ分類・レイアウト選択と、自由なF#製レイアウトアルゴリズム。
- 構造化Journal、原因説明、時間方向の調査、REPL接続、疑似OSへのFork。
- 複数の観測・振る舞い仮説を比較するActive Diagnosisとモデルベーステスト（スコープを縮小し、Phase D以降に評価しながら拡張する）。
- LinuxでCoreとSimulationを高速に反復できる開発フロー。
### 1.3 初期の非目標
- DWM/Explorer/シェルの置換、独自コンポジタ、全プロセスへのフック注入。
- 任意アプリの内部メモリやUI状態の完全な複製、汎用仮想Windows OS。
- 任意F#コードの純粋性・停止性・安全性の自動証明。
- 汎用プログラミング言語/新規パーサー、全ルールのDeep Embedded DSL化。
- 管理者権限やUIAccessによる保護されたUIの操作、セキュアデスクトップへの介入。
- 初期版の独自バー、アニメーション、ランチャー。OSの仮想デスクトップ機能との連携（切替・割当の同期）は非目標とするが、WM内部の最小限のワークスペース状態（1.2参照）はこれとは別に必要であり非目標から除く。
- 外部アプリの任意動作への保証、クラッシュ前のOS操作の厳密なExactly-once保証。
- 全画面アプリ・ゲーム・動画・枠なしウィンドウを自動タイル対象にすること（検出して除外する。20章のリスクE-4参照）。
### 1.4 想定ユーザーとユースケース
常用：複数アプリをキーボードで切り替え、レイアウトをプログラムで選び、アプリ固有の例外を適用する。
不具合調査：ある窓のDesiredが500pxなのにObservedが700pxとなる理由をJournalから追う。
Live診断：稼働中のWMにF# REPLで接続し、WindowId、適用Rule、未完了要求を調査する。
安全な実験：現在のSnapshotをForkして、新しいPolicyを実窓に触らず評価する。
知識の蓄積：実機で得た制約や遅延特性を振る舞いモデルと回帰シナリオに変換する。
## 2. 要件と品質特性
### 2.1 機能要件
| ID | 要件 | 初期優先 |
|---|---|---|
| FR-01 | 管理候補ウィンドウの発見、識別、分類（Tiled/Floating/Excluded/Suspended） | P0 |
| FR-02 | Master–Stack、Monocleの配置とキーボード操作 | P0 |
| FR-03 | Policy IRの構築、参照検査、曖昧条件の可視化 | P0 |
| FR-04 | Plan検証、Desired/Observed/Pendingの収束制御 | P0 |
| FR-05 | イベント・判断・命令・観測を結び付けるJournal | P0 |
| FR-06 | 同じCoreを用いた疑似窓の実行・シナリオテスト | P0 |
| FR-07 | 実Windows Shellと安全停止・全体再観測 | P0 |
| FR-08 | JournalからのExplainとIncident Bundle抽出 | P1 |
| FR-09 | 別プロセスREPL、Inspect・Fork・比較実行 | P1 |
| FR-10 | 安全に許可された追加ProbeとEvidenceGap管理 | P1 |
| FR-11 | REPLによる監査付きIntervene・Policy Deploy | P2 |
| FR-12 | 疑似窓モデル候補生成、反例縮小、回帰シナリオ化 | P2 |
| FR-13 | 複数モニター・混在DPIの基本動作とホットキーによる最小限の常用 | P0〜P1（v2.1で前倒し。18章Phase B） |
| FR-13' | 複数モニター・混在DPIの高度な協調（タスクバー位置変更、WorkArea変更連鎖等）、手動操作の高度な協調 | P2 |
| FR-14 | ワークスペース・窓順序・フォーカス履歴というWM中核状態の管理（v2.1追加、ADR-011） | P0 |
| FR-15 | ハングした対象アプリに対するEffect適用のタイムアウトと、安全停止経路の独立性（v2.1追加、ADR-012） | P0 |


P0/P1/P2は開発段階を示す優先度であり、機能の恒久的な重要度ではない。
### 2.2 非機能要件
- 決定性：同一のCore/設定/Registry成果物/初期状態/正規化イベント列は同じ計算結果を返す。ただしOS側の挙動は非決定的。
- 安全性：無効なPlanをShellへ渡さない。緊急停止を設定コードに依存させない。
- 観測可能性：選択したルールだけでなく、要求の発行から実観測、収束不能まで追跡できる。
- 再現性：Replayに必要なバージョン、Snapshot、時刻・乱数・順序を保持する。
- 耐障害性：破損した設定・Journal・外部アプリ応答でも通常のWindows操作を妨げない。
- プライバシー：ウィンドウタイトル、UIテキスト、パス等は不要なら収集しない。共有前に除去する。
- 開発効率：通常のCore開発とSimulationの大半をLinux上で反復できる。
性能・容量の目標値（例：入力イベントから配置要求までの遅延、Journalのデータ量）は、Phase A/Bでベースラインを測定したうえで固定する。現段階では測定していない数値をSLOとして宣言しない。

**レイテンシ予算の事前宣言（v2.1で追加、G-1対応）**：測定値をSLOとして宣言しないことと、「ホットキーを押してから全窓のSetWindowPos発行完了までの目標時間」を製品要件として先に決めることは矛盾しない。後者は実測結果ではなく製品上の要求であり、Phase 0の失敗カタログを踏まえてPhase A開始時に暫定目標（例：数十ms台）を宣言し、判断ログの永続化（削除済み、B-1参照）・Journal同期書き込み・キュー越しの往復といったホットパス上の選択を、この目標に対して評価する。Phase A/Bの実測結果で目標自体を調整する。
## 3. 全体アーキテクチャ
                  F# Configuration Source
               ┌────────────┴───────────────┐
               ▼                            ▼
     Declarative Policy IR        F# Layout Registry
     ─ classification            ─ master-stack
     ─ priorities                ─ monocle
     ─ layout selection          ─ custom pure functions
     ─ fallback/probe policy     ─ versioned artifacts
               │                            │
               ▼                            ▼
      Policy Analyzer ─────────► PolicyArtifact
                                             │
  WinEvent / EnumWindows                     │
  monitors / input / IPC                      │
          │                                  │
          ▼                                  ▼
  Windows Event Normalizer  ─►  Functional Core ◄─ Simulation Events
                                 │
                  Policy Eval + F# Layout + PlanValidator
                  Desired / Observed / Pending / Reconciliation
                                 │
                    ┌────────────┴─────────────┐
                    ▼                          ▼
             Typed Effect IR             DecisionTrace
                    │                          │
             ┌──────┴──────┐                   │
             ▼             ▼                   ▼
         Windows Shell   Simulated Shell   Unified Journal
             │             │                   │
             └── Observed ─┘          ┌────────┼──────────┐
                                      ▼        ▼          ▼
                                   Logs    Live REPL   Evidence/Model
                                              │          │
                                        Fork/Compare ◄──┘
### 3.1 境界と依存方向
1. Domain / Contracts：ID、座標、イベント、Effect、Journal上の共通型。Windows依存なし。
2. Policy：シリアライズ可能なPolicy IR、三値条件評価、Analyzer、Registry解決。
3. Layout：純粋F#関数と独立PlanValidator。Win32依存なし。
4. Core：状態遷移、要求追跡、収束、操作の抑止・再試行、Trace生成。
5. Windows Shell：OSの生の観測（Raw Event）の取得、純粋関数としてのWindows Event Normalizer（Raw Event → 正規化済みInputEvent）、Effect適用。ここに配置判断を実装しない（ADR-013、C-2対応：Normalizerを純粋関数として切り出すのは、Shellからの単純な実装都合ではなく、Raw Eventを記録してReplay・疑似OSの入力にできるようにするための構造上の要件である）。
6. Simulation：疑似窓と仮想時間。Raw Event層とNormalizer（5.と同じ純粋関数）を経由して観測を生成し、Coreの判断は再実装しない。正規化済みイベントを直接生成する簡易モードも残すが、分類・正規化起因の不具合（UWPのゴーストフレーム、Cloak、表示直後のスタイル変更等）はRaw Event層からの駆動でなければ再現できない。
7. Journal / Diagnostics：意思決定と外部観測を結ぶ型付き記録・閲覧・出力。
8. Debug Host/REPL：公開した型付きコマンドを介して、状態参照・Fork・Probe・介入。
Runtimeは単一のReducerイベント列を持つ。WinEventのコールバック、タイマー、REPLから状態を直接更新しない。すべて入力キューに送る。

**Wm.RuntimeとWm.Normalization（v2.10追加、ADR-019）**：Reducerループの実行とCheckpointファイルの書込み・読込（I/O。DTOへの変換自体はCore側、17章参照）は、Windows Shell/Core本体の双方から分離した独立のプロジェクトWm.Runtimeに置く。Windowsに依存しないため、Phase AのLinux専用Simulationビルドがこれを使える（4.のCore本体はこのReducerループを直接含まない）。5.のWindows Event NormalizerはWindows Shell専属ではなく、Wm.Windows/Wm.Simulationの両方が共有する独立のプロジェクトWm.Normalizationとして切り出す（ADR-013の「純粋関数として切り出す」構造上の要件を、プロジェクト境界としても表す）。

**WindowEventNormalizer.fsの実装（v2.12追加、§21のR3-2解決）**：`RawWindowEvent`（Shellが実際に問い合わせた属性集合を伴うRaw観測。HWND初見判定はShell/Simulation側の非純粋な寿命管理の結果であり、Normalizerはそれを判定する隠れた状態を持たず、呼び出し側が`RawFirstSeen`と他のケースを区別して渡す）を、対応する`InputEvent`（`WindowObserved`/`WindowRemoved`/`ForegroundObserved`/`VisibilityChanged`/`MinimizedStateChanged`/`CloakChanged`/`StyleChanged`/`MoveSizeStarted`/`MoveSizeEnded`）へ無条件・無加工で詰め替える`normalize: RawWindowEvent -> InputEvent list`だけを持つ。候補条件（可視・非ツール窓・非Cloak等）の判定は一切行わない——詳細は§21の該当行（解決済み）参照。
### 3.2 なぜ全面Deep DSLを採用しないか
Deep DSLが有利なのは、設定の構造検査や静的な変換・保存である。いっぽう幾何計算やReconcilerまでASTにすると、演算子追加のたびにEvaluator/Analyzer/Serializer/Visualizerを更新する必要がある。JournalやReplayの成立条件は純粋なCore、構造化した入力/Effect、再現可能な成果物であり、全計算のAST化ではない。
### 3.3 Coreの「待ち」をデータで表す（v2.6で新設、ADR-017）
WMのCoreには「何かが起きるか、期限が来るまで待つ」という関心事が複数ある：Effect要求に対応する観測を待つPending（7.2）、分類に必要な証拠を待つUnknown（5.4）、画面構成変化が落ち着くのを待つ安定待ちと、その直後にOS起因の移動を許容するgrace window（7.3、S8）、古くなった信念の再probe（4.5）。Win32には、X11のシーケンス番号やWaylandのconfigureシリアルのような要求↔応答の対応付けのプリミティブが無い（9.3）ため、これらはすべて「期限付きの推測的な待ち」になる。swayのアトミックトランザクション（全ビューの確認応答を待ち、`txn_timeout_ms`＝既定200msで強制適用する）が最も近い先行例である。

**規約1：待ちはクロージャではなくデータで表す。** Core（Wm.Core、Wm.Policy、Wm.Layouts）の中では、待っている状態を`async`/`task`の継続や、関数値を捕捉したレコードで表してはならない。クロージャはシリアライズできず、Explain/Journalから中身を検査できず、Checkpoint（9.5）に保存できないため、ADR-004のReplay決定性と両立しない。待ちは、再開時の分岐を表すタグ（DUケース）と、再開に必要なデータと、期限の組で表す：
```fsharp
// 説明用の擬似型（実装に置く型ではない。個々の待ちはこの形の具体的なレコード/DUケースとして4.4の各ドメインの状態に定義する。関数値を含めない）
type CoroutineState<'Tag, 'Data> = { Tag: 'Tag; Data: 'Data; Deadline: int64 option }
```
既存の`PendingRequest`（7.2：`Status`がタグ、`TargetRect`/`IntentEpoch`/`Retry`がデータ、`Deadline`が期限）はもともとこの規約に従っている。本節はそれを名前の付いた規約にし、追加の待ちがクロージャに退行しないようにするものである。型としての`CoroutineState`を全ての待ちに強制はしない（Pendingのように独自のフィールド名を持つ方が読みやすい場合はそれでよい）。守るべきは「関数値を含めない」「期限があるならCore状態（4.4の、その待ちを持つドメインの状態の名前のあるフィールド）に記録する」の2点である。v2.6時点の待ちの置き場所は、各ドメインの状態の中にある（4.4）：Placementの`Pending`（観測待ち）と`IntegrityReprobes`（整合性レベルの再probe）、Windowsの`WindowRecord.Classification = AwaitingEvidence`（分類待ち）、Healthの`Reprobes`（ハングの再確認）、Topologyの`ScreenPhase`（画面構成の抑止・安定待ち・grace window）、Focusの`SwitchSettle`（Workspace切替の安定待ち）。17.1の共通ゲートに、**`WmState`から到達可能な全ての型とCheckpointのDTO**が`FSharpAsync`/`Task`/`FSharpFunc`型のフィールドを持たないことを検査するアーキテクチャテストを加える。`step`が引数として受け取る`RuntimePolicy`（Layout Registryの関数を含む）は状態ではなく環境なので対象外である（R4-21対応）。

**規約2：タイマーは`step`が返すデータである。** 作者のRust crate `timed-fsm`の形をF#へ移植する。状態遷移はタイマーAPIを直接呼ばず、行動（Effect）と並べてタイマー指令をデータとして返す。ランタイム（Windows ShellではHostのタイマーサービス、Simulationでは仮想時計）がそれを実行し、発火を`TimerElapsed`入力としてReducerのキューへ戻す（3.1「すべて入力キューに送る」と同じ経路）。
```fsharp
type TimerCommand<'TimerId> =
    | StartTimer of 'TimerId * durationMs: int64 // 同じIDのタイマーが動いていれば再始動する（debounceはこれで書ける）
    | CancelTimer of 'TimerId                     // 動いていなければ何もしない

// Coreの部分状態機械（Pending追跡、分類待ち、画面構成変化、信念の再probe）が1入力ごとに返す形。
// 'Actionは発行候補のEffect（gate前）。stepは7.1の固定順序で各段のResponseを連結してStepResultを作る
type Response<'TimerId, 'Action> = {
    Actions: 'Action list
    TimerCommands: TimerCommand<'TimerId> list
}

// Coreで使うタイマーIDは匿名のint64ではなく、何の待ちかを表すDU（v2.5までのArmTimer of timerId:int64を置き換え）。
// 各ドメイン（4.4）が自分の小さなDUを持ち、親のTimerIdがそれを包む。TimerElapsedはケースで該当ドメインへ振り分ける（Elmの子Msgの持ち上げと同じ）
type PlacementTimer =
    | PendingDeadline of RequestId             // 7.2：観測待ちの期限
    | IntegrityReprobe of WindowId             // 4.5：整合性レベルの再probe（ProbeOnceの上限付き再試行）
type WindowsTimer =
    | ClassificationDeadline of WindowId       // 5.4：Unknown分類の期限（Windowsドメイン）
type TopologyTimer =
    | ScreenSettle                             // 7.3：画面構成変化の安定待ち（イベントのたびにStartTimerで再始動するdebounce）
    | ScreenGrace                              // 7.3/S8：再同期直後のgrace window
    | ReconfigurationCap                       // 7.3：ScreenReconfigurationInProgressの区間の上限（R5-5）
type FocusTimer =
    | WorkspaceSwitchSettle                    // 4.4：Workspace切替直後、OSが一時的に旧Workspaceの窓をフォアグラウンドにする揺れを吸収する（R4-5）
type HealthTimer =
    | ResponsivenessReprobe of WindowId        // 4.5：Hungの窓の再確認
// v2.11（ADR-019適用／Opus批判的レビューdocs/review-phase-a-round1.md 5-1対応）：ケース名
// （Placement/Windows/Topology/Focus/Health）が、Coreのドメインモジュール名・WmStateのフィールド名・
// Wm.Layoutsの`Placement`型・Wm.Windowsプロジェクト名と衝突するため、`[<RequireQualifiedAccess>]`を
// 付け、常に`TimerId.Topology`のように修飾して書く。付けない場合、F#の名前解決がopenの順序に依存して
// 変わり、意図しない型へ束縛される誤りが読みにくい形で起こり得る。
[<RequireQualifiedAccess>]
type TimerId =
    | Placement of PlacementTimer
    | Windows of WindowsTimer
    | Topology of TopologyTimer
    | Focus of FocusTimer
    | Health of HealthTimer
```
- **期限の正本はCore状態側にある。** `StartTimer`を返すとき、そのドメインは同時に対応する待ち状態の`Deadline`（入力の`EventEnvelope`の単調時刻＋`durationMs`）を状態に書く。`TimerElapsed id`を受けたら、その`id`に対応する待ちが今も存在し、かつ記録した`Deadline`が入力時刻以下である場合だけ期限切れとして扱い、それ以外（取り消し済み、再始動済み、対象が消滅済み）は古い発火として捨てる。これにより「`CancelTimer`を返した後、既にキューに入っていた発火が届く」競合が、ランタイムの実装に依存せずCoreの中で決定的に解決する（15.1 #3の具体化）。
- `StepResult`（7.1）は`Effects: GatedEffect list`とは別に`Timers: TimerCommand<TimerId> list`を持つ。タイマー指令はOSの窓に触れないので`GatedEffect.gate`を通さない。`TimerElapsed`は入力としてJournalに永続化され、タイマー指令自体は判断なのでReplayで再計算する（ADR-004）。
- **`timed-fsm`の`Consumed: bool`は移植しない（v2.6、独立レビューR4-1対応）。** 当初は「前段の状態機械が入力を引き受けたら後段に渡さない」用途（grace window中のOS起因の移動を、ドラッグの解釈へ流さない）で導入したが、これは観測を信念にまで届かなくしてしまい（grace中の再観測が信念に反映されず、grace後の再配置が「既に目標位置にある」と誤判定する）、真偽値で「渡さない」は情報を捨てすぎる。代わりに、1 stepの処理順序を7.1で固定し、信念の修正は全入力について最初に無条件で行い、画面構成の状態機械は移動の**原因ラベル**（`MoveCause = OsInduced | UserDrag | Unattributed`）を付けて後段に渡す。後段（4.4の食い違い規則）は原因ラベルで解釈を分ける。
- **何でもタイマーにしない。** 7.3の振動検知（一定時間内の往復回数）は、入力の時刻を状態に記録して比較すれば足り、タイマーを必要としない。ADR-012のEffect適用タイムアウト（Shellが`SendMessageTimeout`に渡す10ms、7.1）はShell内のブロッキング呼出の上限であってCoreの待ちではないので、`TimerCommand`の対象外である。

**規約3：`async`はShellだけで使う。** Win32には、フック（`SetWinEventHook`）やホットキー（`RegisterHotKey`）を登録したスレッドがメッセージポンプを回し続けなければならない、というスレッド親和性の制約がある。Shellの中でこれらの呼出を正しいスレッドへ順序付けるために、F#の`async`と、自作の小さな`SynchronizationContext`（`HWND_MESSAGE`を親にしたメッセージ専用ウィンドウへ`PostMessage`で継続を送り、そのスレッドのメッセージループで実行する）を使うのは適切である。WinForms/WPFの`SynchronizationContext`は、ADR-009の最小主義に対して重すぎるので使わない。ここでのクロージャはShellのスレッド上だけで生きていて、Journal・Checkpoint・Replayの対象にならないため、規約1に反しない。境界は「Coreは何も呼ばず、待ちをデータで返す。Shellはそのデータを実行するためにasyncを使ってよい」である。

**評価したが採用しなかった汎用型（v2.6、ADR-008/3.2の最小主義に照らして判断）**：
- `Gated<'Effect>`（smart constructorで包み、未検証のEffectをShellへ渡すことを型エラーにする汎用ラッパー）：**汎用化しない**。既存の`GatedEffect`（6.2）が既にWm.Contractsで`internal`コンストラクタと`InternalsVisibleTo("Wm.Core")`によって、コンパイラが強制する境界になっている。型パラメータを付けても、ゲートを通す必要のあるEffect型は`Effect`の1種類しかなく、表現力は増えず読みにくくなるだけである。
- `RetryState<'Effect>`：**型パラメータを外した`RetryState`として採用する**。再試行の記録（試行回数、最終試行時刻）は、Pendingの再送（7.2）と信念の再probe（4.5）で同じ形になるため共有する価値がある。一方、Effectは再試行の記録を持つ側（`PendingRequest`や信念の再probe状態）が既に持っているので、型パラメータは同じ情報を二重に持たせるだけになる。再試行の**方針**（何回まで、どの間隔で、どの失敗種別なら再試行しないか）は、失敗種別ごとに個別の純粋関数のまま保ち、汎用化しない。
  ```fsharp
  type RetryState = { AttemptCount: int; LastAttemptAt: int64 }
  ```
  指定案の`NextAllowedAt`は持たない（v2.6の第1版にあった汎用の`BeliefReprobe of WindowId * BeliefKey`タイマーも、ドメインごとのタイマーに分けた）。次に試す時刻は、その待ちの`Deadline`（規約2：期限の正本）が既に持っており、二重に持つと食い違う余地を作るだけだからである（R4-22対応）。
- `EffectBatch<'Effect> = { Effects; Policy: BatchPolicy }`（`AllOrNothing | BestEffort`）：**型としては導入せず、方針を`BestEffort`に固定して明記する**（6.2）。komorebiは窓ループの中の`?`で最初の失敗以降の窓の配置を中止し、GlazeWMはログして続行する（[oss-wm-survey.md]）。本WMがどちらを選ぶかを黙って決めない、という指摘はもっともなので6.2に明記した。ただし、発行時点での`AllOrNothing`はWin32上で実現できない：`SetWindowPos`にはトランザクションも取消しも無く、唯一の「全部まとめて」手段である`DeferWindowPos`はハング耐性のために不採用にした（6.2、F09-F10）。発行前の段階での「全部か無しか」は、`validate`/`decide`の`RelayoutWithout`/`RejectWholePlan`（6.2）が既に担っている。選べる値が実質1つしかない型パラメータは雑音になるので導入しない。
- 汎用のFreeモナド風Effectインタープリター、`IO<'a>`ラッパー、Reader風の環境注入型は導入しない。Coreは何も呼ばない（Effectをデータで返すだけ）ので、これらが解決する問題がCoreには存在しない。F#には高カインド型も無いので、きれいな形にもならない。
### 3.4 外部の先行例との対応と用語（v2.6で追加）
本書の構成は、他分野で確立した設計の型と対応している。名前を明記するのは、それが独自の発明ではなく既知の良い型であることの裏付けになり、また読者（AIコーディングエージェントを含む、本書冒頭のAudience）が共通の語彙で判断できるようにするためである。

- **Impureim Sandwich（Mark Seemann）**：ADR-002の「Functional Core / Imperative Shell」は、本WMでは具体的に「不純な読み取り（Shellの観測→`InputEvent`）→純粋な中身（`step`：信念の修正・分類・Layout・PlanValidator・Reconcile）→不純な書き込み（`GatedEffect`→Shell）」という**一直線のサンドイッチ**を意味する。純粋な中身の途中で不純な呼出を挟まない。OSへの要求とその結果を待つ必要がある場合も、1つの`step`の中で待つのではなく、要求をEffectとして返し、結果を次の入力として受ける（ADR-003のPending）。つまり、複数のサンドイッチが入力キューで連なるのであって、1つのサンドイッチの中身に不純な呼出が混ざることは無い。
- **Sans-IO**：Pythonの`h11`、Rustの`quinn-proto`、OCamlの`httpaf`/`h2`等のプロトコル実装は、ソケット操作を一切持たない純粋な状態機械（バイト列やイベントを受け取り、送るべきデータを返す）として書かれ、I/Oはその外側に置かれる。ADR-013のNormalizerとCore（正規化済みイベントを受け取り、Effect IRを返す）は同じ型である。
- **Actions / Calculations / Data（Eric Normand）**：「Actionを呼ぶ関数はそれ自身もActionになる」という伝染性が、I/OをShellの境界へ押し出す理由である。Core・Policy評価・Layout関数はCalculationでなければならず、1か所でもAction（時計を読む、ファイルを読む）を呼べば、そこから上の全てがReplay不能になる。
- **決定的シミュレーションテスト（Deterministic Simulation Testing、DST）**：FoundationDB、TigerBeetle、Antithesisが広めた手法で、時刻・乱数・ネットワーク・ディスクを差し替え可能な入力として注入し、1つのSeedで障害シナリオ全体を決定的に再現する。本WMの疑似OS（12章）とFork/Replay（ADR-006）はこれと同じ型である。調査した12のOSS WMはいずれもこの仕組みを持たない（[oss-wm-survey.md]）が、それは「この分野では新しい」というだけで、「他分野で確立した型を持ち込む」ものとして位置づけられる。**規則として明記する：Core（Wm.Core、Wm.Policy、Wm.Layouts、**Wm.Contracts、Wm.Normalization**。v2.10でADR-019により後二者を追加）は、環境の時刻（`DateTime.Now`/`UtcNow`、`Stopwatch`、`Environment.TickCount`）、乱数（`Random`、`Guid.NewGuid`）、その他の環境状態を決して読まない。時刻・乱数・ID採番は全て`EventEnvelope`や観測（`Observation<'T>.MonotonicTime`/`SampleSequence`）のデータとして入る**（7.1の既存の規則を、DSTの前提として独立の規則に格上げする）。17.1の共通ゲートで、これらのAPIへの参照がCoreのアセンブリに無いことをアーキテクチャテストで検査する。
- **Parse, don't validate**：不変条件を構築後に検査するのではなく、それを満たす値しか作れない型（smart constructor）に変換する、という原則。`ValidatedPlan`（6.2）、`GatedEffect`（6.2）、`PinnedPolicy`/`DeployablePolicy`（14章）、`Displays`/`WorkspaceSet`/`Zipper`（4.4）は全てこの原則の適用である。型で表しきれない不変条件（ドメインをまたぐもの、4.4の(X1)〜(X3)）だけを、FsCheckによる性質ベーステスト（15章）で検査する。
- **代数的データ型**：本書の型は全てF#の判別共用体とレコードで表し、v2.6で追加した型（`Belief`/`Confidence`、`TimerCommand`/`TimerId`、`Displays`、`Zipper`、`ScreenPhase`等）もこれに従う。関数値を含む型をCoreの状態に置かない（3.3規約1）。
- **代数的エフェクト**：OCaml 5、Koka、Unisonのようなエフェクトハンドラの言語機能はF#に無く、高カインド型も無い。しかし本WMには欠けているものは無い：代数的エフェクトの本質（作用をデータとして記述し、別の場所で解釈し、中身を純粋に保つ）は、Effect IRをデータとして返しShellが解釈する既存の構成で既に得られている。
- **Freeモナド／インタープリターは採用しない**：例えば`GetTime of (DateTimeOffset -> Program<'a>)`のように、ケースのペイロードに継続の関数を埋め込むため、実行はできても中身を検査・シリアライズ・Journal化できない（3.3規約1、ElmishのCmdを退けた理由と同じ）。さらに、本WMには使い道が無い。Freeモナドの価値は「問い合わせ、答えを得て、分岐し、続ける」を1つの同期的な純粋計算の中で書けることにあるが、本WMのCoreは、要求（Effect）と答え（観測）をADR-003により必ず別々の`step`で扱う。「I/Oの結果を待ってから続ける」は、複数の`step`とPendingの追跡が、シリアライズ可能な形で既に解いている。
- **環境インターフェース（Bartosz Sypytkowskiの、インターフェースとジェネリック制約`'env :> ISomething`による依存の受け渡し）は、Shellとテストハーネスでだけ使う**：(1) `ITimerRuntime`（`StartTimer`/`CancelTimer`。3.3の`TimerCommand`を実行するランタイムの実装手段）、(2) `IWin32Ops`（Windows ShellとSimulated Shellの差し替え。3章の図の「Windows Shell / Simulated Shell」の二重性を実現する具体的なF#の手段であり、7.1の`executeEffectProtocol`の`probe`/`dispatch`もここから来る）、(3) `IClock`/`IRandomSource`（DSTのために、Coreへ渡す時刻・乱数のデータを「作る側」を、本番の実時間実装と固定Seedのテスト実装で差し替える）。テスト・シミュレーション用の実装はオブジェクト式で作り、モックフレームワークは使わない。Coreはこれらのインターフェースを一切受け取らない（Coreは入力のデータを受け取りEffectのデータを返すだけで、何も呼ばない）。3.3で退けた「Reader風の環境注入型」は、Coreに対する話である。
- **能力ベースの設計についての注意**：OCamlのEioのような能力安全な標準ライブラリと違い、.NETのBCLは能力安全ではない。F#のコードは、何を引数に渡されたかに関わらず`System.IO`等を直接呼べる。したがって`LayoutFunction`の型（I/Oの手段を受け取らない）は規約を補強する目印であって、コンパイラが強制する保証ではない（5.6の「F#の型だけでは`Calculate`の純粋性を証明できない」と同じ立場）。
- **Elm Architecture**：ドメインごとのCoreを単一のイベントループの中で合成するパターン（子のupdate・子のCmdを親の型へ持ち上げる）を参考にする（4.4、ADR-018）。`Elmish`パッケージは依存に加えない。
- **Deciderパターン／イベントソーシングについての注意**：「状態はイベントの畳み込み」という考え方は本WMのReplayと近いが、判断（決定・信念の修正）の履歴まで永続化する方向へは進まない。旧ADR-004はまさに判断ログの全量永続化を前提にしていて、決定的Coreとの重複によりホットパスの遅延とJournal容量を増やすためv2.1で撤回した。現行の設計（Journalは外部の事実だけを持ち、判断と信念はReplayで決定的に再構成する）は、この考え方の意図的に軽い版である。信念の修正やタイマー指令の履歴を独自のログとして保存し始めたら、同じ誤りの再来である。
- **採用しないもの**：CRDTは、独立した複数の書き手の同時書き込みを統合する問題を解くが、本WMは3.1の単一の入力キューにより書き手が構造上1つしかなく、解くべき問題が無い。`FSharp.Data.Adaptive`（Aardvark/VRVisの増分計算ライブラリ、`aval`/`aset`/`amap`/`alist`）は実在し保守されているF#の選択肢だが、採用しない：窓数十個の規模では1イベントごとの全再計算で十分であり、グラフ規模の問題を持たない。また増分計算ライブラリは効率のために内部で状態を書き換えるので、Coreの中で使うにはADR-004のReplay決定性に対する監査が要る。Hexagonal/Clean Architectureは、FC/ISで既に包含されている。
- **FsCheck**：15.1の性質ベーステストはFsCheckで書く（xmonadがQuickCheckで`StackSet`の性質を検査しているのと同じ型、[oss-wm-survey.md]）。

## 4. ドメインモデルと識別子
### 4.1 IDの契約
[<Struct>] type WindowId = WindowId of int64
[<Struct>] type MonitorId = MonitorId of int64
[<Struct>] type RequestId = RequestId of int64
[<Struct>] type IntentEpoch = IntentEpoch of int64
[<Struct>] type PolicyRuleId = PolicyRuleId of string
[<Struct>] type LayoutId = LayoutId of string
[<Struct>] type EvidenceId = EvidenceId of string
[<Struct>] type ScenarioId = ScenarioId of string
[<Struct>] type BatchId = BatchId of int64 // v2.4で追加（ADR-014/1-5対応）。ApplyBatchの識別子。子Effectは自分のRequestIdを保つ
WindowIdはCore内の寿命管理済みIDであり、生の`HWND`ではない。Shellが`HWND → WindowId`を対応付け、破棄後は同じIDを再利用しない。HWNDの再利用・プロセス再起動・遅延イベントを考慮して、対象の同一性を再確認する。`RequestId`は発行要求の追跡、`IntentEpoch`は新しい意図で古いPendingを失効させるための識別子とする。

**MonitorIdも生の`HMONITOR`ではない（v2.5で追加、OSS調査[oss-wm-survey.md]対応）**：`HMONITOR`はモニター構成が変化すると再列挙され、同じ物理モニターに対しても安定しない。Shellは安定した物理識別（EDIDシリアル番号やデバイスID、komorebiの`MONITOR_CACHE`が同種のキーを使う）から`MonitorId`を割り当て、切断・再接続をまたいで同じ`MonitorId`を維持する。xmonadの`rescreen`はスクリーンを**位置で**新しいスクリーン列へzipし直すため、モニターの抜き差しや並び替えでワークスペースが誤った物理モニターに割り当てられ得る反面教師であり、本WMはこの方式を避ける。

**安定識別キーの順序付きフォールバックと`MonitorSnapshot`（v2.6で追加、R3-10対応）**：同じ機種のモニター2台では、EDIDのシリアル番号が同一または0であることが珍しくない。シリアル番号だけをキーにすると2台が同じ`MonitorId`になり、モニター→Workspaceの対応（4.4の`Displays`）で片方が消える。逆に接続ポート（デバイスパス）を常にキーに混ぜると、同じモニターを別ポートへ挿し直しただけで別IDになる。キーは次の順で決める：(1) EDIDの`(製造者ID, 製品コード, シリアル番号)`。シリアル番号が0でなく、現在の接続中モニターの中で一意ならこれだけで識別する。(2) (1)が衝突するか、シリアル番号が0なら、`(製造者ID, 製品コード, デバイスパス)`。衝突を検出したことはJournalに記録する（Explainで「このモニターはポート依存の識別になっている」と示せるようにする）。

**キーの選び方を接続状況で切り替えない／対応表を永続化する（v2.6で追加、独立レビューR4-17）**：「現在の接続中モニターの中で一意なら(1)」という判定は、同じ機種の2台目が遅れて検出されたとき（ドッキングステーションで典型的）に1台目のキーがセッション中に(1)から(2)へ変わり、`MonitorId`が変わって「1台目が消えて2台が現れた」と解釈される。そこで、ある`(製造者ID, 製品コード)`について一度でも衝突またはシリアル0を観測したら、その機種は以後常に(2)を使う、と記録して固定する。この記録と「安定キー→`MonitorId`」の対応表は、Hostが小さなファイルとして永続化し、起動をまたいで同じモニターに同じ`MonitorId`を与える（`MonitorTopologyObserved`は割当済みの`MonitorId`を運ぶので、Journal/Replay側は対応表を必要としない）。PolicyでモニターをMonitorIdの数値で指定させることはしない（5.2の`MonitorSelector`）。
```fsharp
type MonitorSnapshot = {
    Id: MonitorId                 // Shellが上記の規則で割り当てた安定ID
    StableKey: string             // 安定キーの正規化した文字列表現（例："DEL:A0B1:SN1234"）。PolicyのMonitorSelectorとの照合、REPLでの表示に使う
    Bounds: PixelRect             // 物理ピクセル
    WorkArea: PixelRect           // 物理ピクセル
    Dpi: int                      // GetDpiForMonitor(MDT_EFFECTIVE_DPI)
    IsPrimary: bool
}
```
### 4.2 座標と属性
type PixelRect = {
    X: int
    Y: int
    Width: int
    Height: int
}

type Observation<'T> = {
    Value: Result<'T, NativeCallRecord> // v2.4で変更（ADR-014、1-9対応）。NativeCallRecordは7.1参照
    SampleSequence: int64 // v2.6で契約を明記（R4-10）：Shellが全ての観測（WinEvent由来・問い合わせ由来とも）に取得時点で付ける、単一の単調増加番号。
                          // Reducerへの投入順（EventEnvelopeのSequence）とは別の番号空間。信念の新旧比較（4.5のLatestWins）はこの番号だけで行う
    MonotonicTime: int64
    Source: string
}

// v2.5で追加（OSS調査[oss-wm-survey.md]対応）。UWP/WinUIのApplicationFrameHostのように、
// どの実行ファイルが「実アプリ」かが1つに定まらない対象のため、導出元ごとに別の観測として持つ（5.3参照）
type AppIdSource = Exe | HostExe | Aumid | CoreWindowExe
type AppIdentity = {
    Exe: Observation<string> option          // トップレベルHWNDの実行ファイル名
    HostExe: Observation<string> option       // ApplicationFrameHost.exe等、ホストプロセスの実行ファイル名
    Aumid: Observation<string> option          // Application User Model ID
    CoreWindowExe: Observation<string> option  // Windows.UI.Core.CoreWindowクラスの子窓から辿った実プロセスの実行ファイル名
}

// v2.5で追加（ADR-015対応）。v2.6で位置づけを変更：分類はこれを直接読まず、4.5のMovabilityの信念の事前証拠とExplainの材料にする
type IntegrityRelation = NotHigher | Higher

// v2.6で追加。窓を所有するプロセスの同一性。PIDは再利用されるので作成時刻と組にする（4.5の再probeの発火条件）
type ProcessKey = { Pid: int; CreationTime: int64 }

// v2.6で追加（R3-8対応）。DWMWA_CLOAKEDの理由ビットを捨てない。WM自身のワークスペース切替によるCloakと、
// 他の仮想デスクトップ・UWPのゴーストフレームによるCloakを区別するため
type CloakState = { App: bool; Shell: bool; Inherited: bool } // 全てfalse＝Cloakされていない

// v2.6で追加。ハング判定の証拠。IsHungAppWindowは「ゴーストウィンドウは常にTRUE」「一般利用を意図しない」（feasibility.md §5）ので、
// 生の結果は観測として残し、ハングかどうかの判断は4.5の信念で行う
type ResponsivenessSample =
    | PumpResponded                 // SendMessageTimeout(WM_NULL)が成功
    | PumpTimedOut of elapsedUs: int64 // SendMessageTimeoutがERROR_TIMEOUT。ElapsedUsで判定前／判定済みを区別（7.1）
    | IsHungAppWindowTrue           // 補助的。単独では確定に使わない

type DpiAwarenessDeclared = Unaware | SystemAware | PerMonitor | PerMonitorV2 // v2.6で追加。アプリの申告値

// v2.6で位置づけを明確化（ADR-016）：WindowSnapshotは「今回Shellが問い合わせた属性だけ」を運ぶ部分観測であり、
// 真実の全体像ではない。問い合わせなかったフィールドはNone。Coreはこれを4.5の各ドメインの信念へ振り分けて統合する（Noneは前回の信念を上書きしない）
type WindowSnapshot = {
    Id: WindowId
    Process: Observation<ProcessKey> option // v2.6で追加。GetWindowThreadProcessId＋プロセス作成時刻
    AppIdentity: AppIdentity
    Owner: Observation<WindowId option> option
    IsDialog: Observation<bool> option
    NativeRect: Observation<PixelRect> option
    VisibleFrameRect: Observation<PixelRect> option
    IsVisible: Observation<bool> option
    IsMinimized: Observation<bool> option
    Cloak: Observation<CloakState> option // v2.6でIsCloaked: bool optionを置き換え（R3-8）
    Monitor: Observation<MonitorId> option
    MinWidthHint: Observation<int> option // 物理ピクセル（観測値。信念に入れるときに観測時のモニターのDPIでDIPへ正規化する、4.5。v2.6、R3-5/R4-12）
    TargetIntegrity: Observation<IntegrityRelation> option // v2.5で追加、v2.6で4.5のMovabilityの事前証拠へ格下げ（ADR-015）
    Responsiveness: Observation<ResponsivenessSample> option // v2.6で追加
    Presence: Observation<ObservedPresence> option // v2.6で追加（R5-2）。Shellが自分の非表示方式で判定した、方式に依存しない表示状態（4.5）
    DpiAwareness: Observation<DpiAwarenessDeclared> option // v2.6で追加。GetWindowDpiAwarenessContextの申告値（実際の挙動は4.5の信念で推定、S11）
    TitleMatches: Observation<TitleMatchVector> option // v2.6でTitle: Observation<string> optionを置き換え（R4-8）。下記
}

// v2.6で追加（独立レビューR4-8）。生のタイトル文字列はWindowSnapshotに載せず（＝Journal・Checkpoint・Bundleに残らない）、
// Shellが採用中Policyの全TitleContainsパターンに対して評価した一致ビット列だけを運ぶ。Replayはピン留めPolicyで行うのでこれで再現でき、
// 別Policyで行うCounterfactualでは、パターン集合のハッシュが一致しない限りUnformedとして扱う
type TitleMatchVector = { PatternSetHash: string; Matches: Set<int> } // Matchesは一致したパターンの（正準順序での）添字
X/Yは負値を許容し、`Width/Height`は正でなければならない。実装ではオーバーフローを検出する。`GetWindowRect`とDWM拡張フレーム境界は同一ではなく、DPI仮想化の扱いも異なる。Coreに渡すレイアウト座標系を明示し、Shellはソース座標・DPI・変換規則を保存できるようにする。

**座標系の契約（v2.1で追加、A-3対応／v2.2でfeasibility.md §6により具体化／v2.3でGitHub Actions windows-latest実機検証により確定：自作WinForms窓・notepad.exeの両方で`diff(L,T,R,B)=(7,0,7,7)`pxを実測、failure-catalog.md F01）**：CoreとPolicy/LayoutがやりとりするPixelRectは「可視フレーム矩形（DWM拡張フレーム境界、`DwmGetWindowAttribute(DWMWA_EXTENDED_FRAME_BOUNDS)`で取得）」を指すと定義する。`SetRect` Effect（7.1参照）が渡す値もこの可視フレーム矩形であり、Shellは`SetWindowPos`に渡す実際のウィンドウ矩形（`GetWindowRect`）との差分（Windows 10標準で1辺あたり約7px、DPIにより変動）を自分で補正する。この補正無しでは、ほぼ全ての窓でDesiredとObservedが恒常的に数px食い違い、収束不能（Suspended化）が初日から多発する。実装パターンは、既存のWindows用タイル型WM（komorebi）の`window_rect`/`shadow_rect`関数が確立している：`DWMWA_EXTENDED_FRAME_BOUNDS`を優先的に取得し失敗時のみ`GetWindowRect`にフォールバックする（`window_rect`）、両者の差分を「シャドウ分」として別管理し`SetWindowPos`呼び出し時に加算する（`shadow_rect`）、という2関数構成をShell実装の指針とする。ただし、**ウィンドウがまだ一度も表示されていない生成直後は`DwmGetWindowAttribute`が`GetWindowRect`と同値を返す**という既知の注意点があり、初回配置のタイミング（最初の`ObserveWindow`の発行時点）に反映する。またkomorebi自身もDPIスケーリングが100%でない場合の差分計算を未完了（TODOコメントが残る）としており、本WMもこの点をPhase 0で未解決領域として明示的に扱う。Phase 0の完了条件（18章）に「対象アプリごとに可視フレームとウィンドウ矩形の差分を計測し、補正込みで一致判定できる」ことを含める。

**シャドウオフセットはDPI別に記録する（v2.5で追加、OSS調査[oss-wm-survey.md]対応、S11関連）**：上記`(7,0,7,7)`pxはあくまで実測した1条件での値であり、単一の定数として扱わない。komorebi自身のTODOコメントが、本番運用中でもDPIスケーリング別の差分計算が未完了のまま残っていることを示しており、この差分がDPI依存であることは既知の事実として扱うべきである。Shellは`shadow_rect`相当の差分をモニターのDPI値をキーにしたテーブルとして保持し、単一定数として一度だけ計測・固定しない。

MinWidthHint等は観測・推定・設定の由来を区別する。単発の「500pxを要求して700pxを観測」を「OSが保証する最小幅700px」という確定事実に昇格させない。**v2.6でこの区別を型で表すようにした（ADR-016）**：`WindowSnapshot`の`MinWidthHint`は1回の観測（生の事実）であり、分類やLayoutが読むのは4.5のPlacementドメインの`MinWidth`の信念（DIPに正規化し、確度`Inferred`と根拠を伴う）である。

**`AppIdentity`を単一の`AppId: string option`ではなく派生元別の観測レコードにする（v2.5で追加、OSS調査[oss-wm-survey.md]対応、S5関連）**：UWP/WinUIアプリの最上位HWNDは`ApplicationFrameHost.exe`が所有するため、「実アプリの実行ファイル名」は単一の値に定まらない。sway（`app_id`／`class`／`sandbox_app_id`を異なる出自のまま別フィールドで持ち、統合しない設計）とHyprland（`initialClass`/`initialTitle`を現在値と別に保持する設計）を参考に、`AppIdentity`は`Exe`/`HostExe`/`Aumid`/`CoreWindowExe`という4つの派生元をそれぞれ独立した`Observation<string> option`として持つ。これにより「まだ観測していない」と「観測を試みたが失敗した」の区別（本節既存の`Observation<'T>`の方針）が各フィールドで独立に成立する。**v2.5の「フィールドごとに初期値と現在値を別のサンプルとして記録できるため、A-6にもそのまま答えられる」という記述は撤回する（v2.6、R3-6対応）**：各フィールドは`Observation<string> option`を1つしか持たず、初期値を保持する場所は無かった。v2.6では、AppIdentityの各フィールドを4.5の「最初に`Ok`になった値で固定する（Sticky）」信念として持つ。これはHyprlandの`initialClass`と同じ「初期値」の意味論であり、`CoreWindowExe`のように状態によって揺れる値（アプリが非アクティブな間は取得できない、フルスクリーン時は親子とも`ApplicationFrameHost.exe`のPIDを返す、5.3）で、フォーカスのたびに分類が反転することを防ぐ。後から異なる値が観測された場合は、信念を変えずに矛盾する証拠として記録し、Explainで示す。現在値は生の観測としてJournalに残るので、必要になれば現在値を読む述語を後から追加できる。この変更により、S5は「3つの導出手法から1つを選ぶ」問題ではなく「アプリごとに各フィールドの信頼度を実測する」問題になり、単一の手法が確定しない間もPhase Aをブロックしない。

**観測の失敗を型で区別する（v2.4で追加、ADR-014/1-9対応）**：`Observation<'T>`の`Value`を`Result<'T, NativeCallRecord>`にしたのは、「まだ観測していない」（`WindowSnapshot`の各フィールドが`option`で`None`）と「観測を試みたが`GetWindowRect`/`DwmGetWindowAttribute`等が失敗した」（`Some { Value = Error ... }`）を区別するためである。これにより11.1の`EvidenceGap`（`MissingVisibleBounds`等）が、単に未取得なのか取得に失敗し続けているのかを機械的に判別できる。
### 4.3 管理状態
- Discovered：候補として見つかったが属性の調査中。
- Managed：タイル配置の対象。
- Floating：位置変更を原則自動管理しない／限定管理する。
- Suspended：ユーザー操作や連続失敗により自動管理を一時停止。
- Excluded：システム/特殊窓など対象外。
- Destroyed：寿命終了。関連要求・Timerを失効させる。
所有窓・モーダルダイアログ・ツール窓・タスクバー・保護UI・Cloaked等を、常に自動整列するものとして扱わない。分類不能時は破壊的に移動させるより、観測と保守的なフォールバックを優先する。

**4.4の型との対応（v2.6で追加、独立レビューR5-10）**：上の語彙は説明用であり、Coreの状態は4.4のドメインごとの型で表す。Discovered＝`ClassificationState`が`NotCandidate`または`AwaitingEvidence`。Managed（Tiled）／Floating／Excluded＝`Classified (Tiled | Floating | Excluded, _)`。v2.6で追加した`Unplaced`は「動かせない（昇格窓等）ためどのWorkspaceにも属さない」状態。Suspended＝単一の状態ではなく、Healthの`Responsiveness = Hung`（ハング）、Placementの`Oscillation`（振動による劣化）、`MoveSizeStarted`〜`MoveSizeEnded`の間（ユーザー操作中）という、各ドメインが自分の理由で持つ「配置対象から一時的に外す」条件の和である（理由ごとに独立に解除される）。Destroyed＝`WindowRemoved`で各ドメインから除去された状態。
### 4.4 WM中核状態：ドメインごとのCore（v2.1で新設、ADR-011／v2.6でドメイン分割）
旧版はPolicy IRやJournal/診断機構を先に詳細化し、タイル型WMとしての本体状態（xmonadで言う`StackSet`相当）がどこにも定義されていなかった。これを4章の時点で固定する。Policy IR・Journalスキーマ・Snapshot形式・Incident Bundleは、この状態モデルを前提に設計し直す。

**v2.6でCoreをドメインごとに分割する（ADR-018）**：v2.5の`WmState`は1つのレコードに全ての関心事（モニター、Workspace、フォーカス、分類、配置要求）を載せ、不変条件（同じWorkspaceが2画面に出ない、フォーカスの情報源は1つ、等）を「共有レコードの上で後から検査する規約」として書いていた。R3-2（`Screens`/`ActiveMonitor`の不整合）とR3-3（フォーカスの二重の情報源）は、まさにこの「境界の無い共有状態」から生じた不具合である。v2.6では、Coreを6つのドメインに分け、各ドメインが自分の状態の型・純粋な遷移関数・その型で表現できる不変条件を持つ。不変条件は、できる限り**型と、その型を作れる唯一の関数（smart constructor）**で表し、不正な状態をそもそも表現できないようにする（"Parse, don't validate"、3.4）。複数のドメインにまたがる不変条件だけを、親の`step`の性質ベーステスト（15.1）で検査する。

| ドメイン | 状態 | 主な責務 | 型で保証する不変条件 |
|---|---|---|---|
| Topology | `TopologyState` | モニター構成、各モニターに表示中のWorkspace、アクティブなモニター、画面構成変化の待ち（7.3） | アクティブなモニターはモニターが1台以上あるときだけ存在し、必ず表示中の画面の1つである。各モニターはちょうど1つのWorkspaceを表示する。同じWorkspaceは高々1つのモニターに出る |
| Workspaces | `WorkspaceSet` | Workspaceごとの窓の並びとWorkspace内のフォーカス（zipper）、窓→Workspaceの索引、切断時の復帰先 | 管理下の各窓はちょうど1つのWorkspaceに属する。Workspace内の「最後にフォーカスされた窓」は並びの中に必ず存在する |
| Focus | `FocusState` | フォアグラウンドの信念、グローバルなフォーカス履歴（直前の窓へ戻る用）、Workspace切替の揺れの待ち | 履歴は重複を持たない |
| Windows | `WindowRegistry` | 窓ごとの分類（Unknownの待ちを含む）、ユーザー指定、識別に関する信念（AppIdentity、Process、分類述語が読む属性） | — |
| Health | `HealthState` | 応答性（ハング）の信念と再probe | — |
| Placement | `PlacementState` | `Desired`、`Pending`、配置に関わる信念（矩形、モニター、表示状態、最小幅、DPIの振る舞い、動かせるか、整合性レベル）、振動検知 | — |

```fsharp
[<Struct>] type WorkspaceId = WorkspaceId of int

// ===== Workspaces ドメイン =====
// xmonadのStack（focus/up/down）と同じzipper。Workspace内のフォーカスを構造そのもので表す（v2.6、R3-3）
type Zipper<'T> = { Before: 'T list; Focus: 'T; After: 'T list } // Beforeは逆順（Focusに近い順）

type WorkspaceState = {
    Id: WorkspaceId
    Stack: Zipper<WindowId> option  // このWorkspaceの管理下の窓（Tiled/Floating）。並び順そのものが配置の意味を持ち、先頭のTiled窓がMaster。
                                    // Focusは「このWorkspaceで最後にフォーカスされた窓」。窓が0個ならNone（番兵値を作らない）
    Floating: Set<WindowId>         // Stackの要素のうちFloatingのもの。Layoutに渡すTiledの並び＝Stackの順でFloatingを除いたもの
    LayoutOverride: LayoutRef option // v2.6で追加（R6-2）。ChangeLayoutの結果。NoneならPolicyのLayoutRulesで選ぶ
}
// v2.5のHomeMonitor（v2.6第1版ではWorkspaceState.ReturnMonitor）は、Topologyドメインの`Returns`へ移した（R6-1）

// コンストラクタはprivate。窓の追加・除去・Workspace間の移動・zipperの回転はこのモジュールの関数だけが行い、
// 「各窓はちょうど1つのWorkspaceに属する」「索引とStackが一致する」を構造で保つ。
// 全域である（v2.6、R6-1）：Spacesに無いWorkspaceIdは「空のWorkspace」として振る舞い、窓が入った時点で実体を作る。
// したがって「Topologyが表示するWorkspaceがWorkspacesに存在する」は、作成の依頼をやり取りせずに常に成り立つ
type WorkspaceSet = private { Spaces: Map<WorkspaceId, WorkspaceState>; Index: Map<WindowId, WorkspaceId> }

// ===== Topology ドメイン =====
type Screen = { Monitor: MonitorSnapshot; Workspace: WorkspaceId }
// xmonadのStackSetのcurrent/visibleと同じ形。モニター0台、アクティブなモニター、各モニターに1つのWorkspace、を型で表す（v2.6、R3-2）
type Displays =
    | NoMonitors
    | Displays of current: Screen * visible: Screen list // currentがアクティブなモニター
// v2.6（R7-1）：Workspaceが表示された原因。フォーカスを要求するかどうかが原因で変わる
type ShownBy =
    | UserView          // ユーザーのSwitchWorkspace／FocusMostRecentの行き先の表示：zipperのFocusへフォーカスを移す
    | ForegroundFollow  // hiddenなWorkspaceの窓が自らフォアグラウンドになった：その窓が既に前面にあるので、フォーカスを要求しない
    | Reconcile         // モニターの再接続等（MonitorLifecycle.reconcile）：ユーザーの作業中の窓からフォーカスを奪わない
type ScreenPhase =                                      // 7.3（R4-15/R5-5）
    | Stable
    | Reconfiguring of since: int64                     // ScreenReconfigurationInProgress trueの区間（ロック、RDP切断等）。下記の解除条件まで抑止
    | Settling of latest: MonitorSnapshot list * deadline: int64 * hardDeadline: int64 // トポロジ変化のdebounce
    | Grace of deadline: int64
// コンストラクタはprivate。Displaysを作り変える関数（view、モニターの追加・除去）は全て「同じWorkspaceを2画面に出さない」を保つ。
// 保てない入力（例：既に表示中のWorkspaceを別モニターにも出す）は、型の上でその遷移を書けないようにする（下記view意味論）。
// v2.10（ADR-019）：この不変条件はWm.Contractsへ公開の値型として置くと保てない（どのプロジェクトからでも不正なDisplaysを作れてしまう）。
// Displays・TopologyStateはCore.Topologyの中にprivateなまま置き、他ドメイン・他段が読む分（①のDPI、⑤のWorkspaces、⑦のPlacement）は、
// Topology自身が公開する読み取り専用の射影（Screen = { Monitor: MonitorSnapshot; Workspace: WorkspaceId }と同じ情報を運ぶレコード。
// 識別子とMonitorSnapshot、いずれもWm.Contractsの型だけでできている）をstepがその場で組み立てて渡す（17章参照）。
type TopologyState = private {
    Displays: Displays
    Phase: ScreenPhase
    Assignments: Map<WorkspaceId, MonitorSelector> // 採用中PolicyのWorkspaceAssignmentsの写し
    Returns: Map<WorkspaceId, MonitorId>          // v2.6でWorkspaceState.ReturnMonitorから移動（R6-1）。切断で退避したWorkspaceの退避元。「どのモニターにどのWSを戻すか」はTopologyの関心事なので、Topologyだけが読み書きする
    LastObserved: MonitorSnapshot list option      // v2.11で追加（Phase A実装、Opus批判的レビューdocs/review-phase-a-round1.md 3-1対応）。
                                                    // 最後に観測したモニター構成。段①で入力のたびに無条件に更新する（Phaseを問わない）。
                                                    // ScreenReconfigurationInProgress falseやReconfigurationCap満了のように、
                                                    // それ自身はモニター構成を運ばない入力でSettlingへ入るとき（下記）の`latest`に使う
}

// ===== Focus ドメイン =====
type FocusState = {
    Foreground: BeliefSlot<WindowId option>   // ForegroundObservedから更新するLatestWinsの信念（管理外の窓も含む生の値。4.5）
    History: WindowId list                     // グローバルMRU。「直前にフォーカスしていた窓へ戻る」専用（Workspace内のフォーカスはzipperが持つ）
    SwitchSettle: (WindowId option * deadline: int64) option // Workspace切替直後の揺れの吸収（R4-5）。フォーカスを渡すべき窓を持つ
}

// ===== Windows ドメイン =====
type ClassifiedBy = ByPolicy of PolicyRuleId option | ByTimeout | ByUserOverride
type EffectiveManagement =
    | Tiled
    | Floating
    | Unplaced   // v2.6（R5-3）：動かせない窓（Movability = NotMovable）。どのWorkspaceにも属さず、配置も非表示もしない
    | Excluded
type ClassificationState =
    | NotCandidate          // Normalizerの候補条件（可視・非ツール窓・非Cloak等）を満たさない。分類も期限も持たない（R4-19）
    | AwaitingEvidence of deadline: int64 * homeWorkspace: WorkspaceId option // 5.4の分類待ち。モニター0台の間に始まった場合はNoneで、確定時に最小のWorkspaceIdへ入れる（R6-11）
    | Classified of EffectiveManagement * ClassifiedBy
type WindowRecord = {
    Classification: ClassificationState
    UserOverride: Management option   // ToggleFloating/SetManagementによるユーザー指定。窓の寿命中Policyより優先する（R4-20）。ClearUserOverrideで外す
    LateReclassifyUsed: bool           // 5.4：期限解決後の再分類を1回使ったか
    ManagementChangedAt: int64 list    // 7.3：分類変化の振動検知用（直近8件の時刻）
}
type WindowRegistry = {
    Records: Map<WindowId, WindowRecord>          // 生存している全ての既知の窓
    Identity: Map<WindowId, IdentityBeliefs>      // 4.5。AppIdentityの4派生元とProcess（Sticky規則）
    ClassInputs: Map<WindowId, ClassInputBeliefs> // 4.5。分類述語が読む属性（IsDialog、Owner、Cloak、IsVisible、TitleMatches。LatestWins規則）
}

// ===== Health ドメイン =====
type HealthState = {
    Responsiveness: Map<WindowId, Belief<Responsiveness>> // 4.5。無ければResponding扱い
    Reprobes: Map<WindowId, RetryState * deadline: int64>  // Hungの窓の再確認の待ち
}

// ===== Placement ドメイン =====
type DesiredPresence =                  // R4-6：WMが見せているべきか。非表示の実装方式（21章）に依存しない
    | Shown of PixelRect option         // Tiled：Layoutの目標矩形。Floating：None（位置は管理しない）
    | HiddenByWm                        // hiddenなWorkspaceの窓
type PlacementState = {
    Desired: Map<WindowId, DesiredPresence>
    Pending: Map<RequestId, PendingRequest>                      // 7.2
    NextRequestId: int64                                          // v2.6で追加（R6-10）。ID採番は状態として持つ（3.4：Guid.NewGuid等を読まない）
    NextBatchId: int64
    Epochs: Map<WindowId, IntentEpoch>                            // 窓ごとのIntentEpoch（7.2）
    HeldFocusTarget: WindowId option                              // 抑止区間中に保留したフォーカス要求の最後の1件（7.1、R6-8）
    Geometry: Map<WindowId, GeometryBeliefs>                      // 4.5。矩形・モニター・最小化・ObservedPresence（LatestWins）
    MinWidth: Map<WindowId, Belief<int>>                          // 4.5。DIP。Accumulating
    DpiBehavior: Map<WindowId, Belief<DpiBehavior>>               // 4.5。Accumulating
    Movability: Map<WindowId, Belief<Movability>>                 // 4.5。Accumulating（ADR-015）
    TargetIntegrity: Map<WindowId, BeliefSlot<IntegrityRelation>> // 4.5。ProbeOnce（ADR-015、Movabilityの事前証拠とExplain用）
    IntegrityReprobes: Map<WindowId, RetryState * deadline: int64>
    Oscillation: Map<WindowId, int64 list>                        // 7.3。往復の時刻
}

// ===== 親 =====
type WmState = {
    Topology: TopologyState
    Workspaces: WorkspaceSet
    Focus: FocusState
    Windows: WindowRegistry
    Health: HealthState
    Placement: PlacementState
}

// 導出値。どのドメインの状態にもコピーを持たない
let activeWorkspace (s: WmState) : WorkspaceId option =
    match Topology.displays s.Topology with
    | NoMonitors -> None
    | Displays (current, _) -> Some current.Workspace

type FocusTarget =
    | ManagedWindow of WindowId // 表示中のWorkspaceに属する管理下の窓
    | Elsewhere                 // Excluded/未分類/Unplaced（昇格窓等）の窓、hiddenなWorkspaceの窓、デスクトップ、またはフォアグラウンド無し

// 「今フォーカスされている窓」の唯一の定義（v2.6、R3-3/R4-4/R5-4）。状態として持たず、毎回導出する
let focused (s: WmState) : FocusTarget =
    match s.Focus.Foreground with
    | Believed { Value = Some w } ->
        match WorkspaceSet.workspaceOf w s.Workspaces with
        | Some ws when Topology.isShown ws s.Topology -> ManagedWindow w
        | _ -> Elsewhere
    | _ -> Elsewhere

type CoreCommand =
    | FocusNext      // アクティブなWorkspaceのzipperを1つ回す（xmonadのfocusDown。MRUではない、R3-3）
    | FocusPrevious
    | FocusMostRecent // v2.5で追加。グローバルなFocus.Historyを使い、Workspace/モニターを問わず直前にフォーカスされていた窓へ戻る
    | SwapWithMaster
    | PromoteToMaster
    | CloseFocused   // focused = Elsewhere なら何もしない（ユーザーが見ていない窓を閉じない、R3-3/R5-4）
    | SwitchWorkspace of WorkspaceId // xmonadのview（下記）
    | MoveFocusedToWorkspace of WorkspaceId
    | FocusMonitor of MonitorId // v2.5で追加。アクティブなモニターを切り替える（マルチモニターのキーボード操作、FR-13）
    | ChangeLayout of LayoutRef
    | ToggleFloating of WindowId
    | SetManagement of WindowId * Management
    | ClearUserOverride of WindowId // v2.6で追加（R5-11）。ユーザー指定を外してPolicyの分類に戻す
```

**ドメインの合成：単一のイベントループと、データとしての指令（v2.6、ADR-018。Elm Architectureの合成パターンを参照、ライブラリは使わない）**：3.1の「Runtimeは単一のReducerイベント列を持ち、何も状態を直接更新しない」は変えない。ドメイン分割は、この単一の`step`の**内側**の構造である。各ドメインは、自分が働く段（7.1の表）ごとに、同じ形の純粋な更新関数を持つ（1つのドメインが複数の段で働くことはあるが、どの関数も自分の状態だけを返す）：
```fsharp
// 概念上の契約。'Inputはそのドメインが受け取る入力（親がInputEventから振り分ける）、
// 'Ctxは先に処理された他ドメインの状態の読み取り専用の値（関数ではない）、
// DomainEventは後段のドメインへ伝える事実（例：WindowClassified、WorkspaceShown）。
// DecisionTrace list（v2.10追加、ADR-019 R5-04対応）：起こり得ない組合せに出会ったときに「導出不整合の疑い」を記録する出口（7.1）。
// 旧版の戻り値にはこの出口が無く、7.1の規則（ドメインの更新関数がDecisionTraceに記録する）と食い違っていた
val update : 'Input -> 'Ctx -> 'State -> 'State * Response<'Timer, Effect> * DomainEvent list * DecisionTrace list
```
親の`step`は、入力を7.1の固定順序（ドメイン×段×読むもの×DomainEventの表）で各ドメインへ振り分け、各ドメインが返した`DomainEvent`を後段のドメインへ渡し、各ドメインの`Response`（3.3：Effectとタイマー指令の**データ**）を連結して`StepResult`にする。各ドメインのタイマーIDは自分の小さなDU（例：`PlacementTimer = PendingDeadline of RequestId | IntegrityReprobe of WindowId`）であり、親の`TimerId`はそれを包むDU（`TimerId = Placement of PlacementTimer | …`、3.3）である。`TimerElapsed`が届いたら、親はDUのケースで振り分けて該当ドメインへ返す。これはElmで子のMsg/Cmdを親の型へ包んで持ち上げ、親のupdateが子へ振り分ける合成パターンそのものであり、「1つのイベントループのまま、ドメインごとに境界を持つ」という目的に対する確立した先行例である。ただし`Elmish`パッケージは使わない：Elmishの`Cmd<'msg>`は`(Dispatch<'msg> -> unit) list`というクロージャのリストであり、実行はできても中身を検査・Journal化・Replayできないので、3.3の規約1（待ちはデータで表す）とADR-004に反する。本WMの「Cmd」は、既にEffect IRと`TimerCommand`という検査可能なデータである。またElmishの中心であるview（描画）の段はCoreに存在しない（Live REPLのFork比較UI（FR-09）のような本物の対話UIを作るときには、Elmish系のUIライブラリは正当な選択肢になり得るが、それはCoreとは無関係である）。

**ドメインをまたぐ不変条件（15.1で親の`step`の性質として検査する）**：型で表せるのは各ドメインの内側までである。次の3つはドメインをまたぐので、親の性質ベーステストで守る：
- (X1) Topologyが表示するWorkspaceは全てWorkspacesで参照できる（`WorkspaceSet`が全域なので構造上常に成り立つ。v2.6、R4-16/R6-1。性質テストでは確認だけ行う）。
- (X2) Workspacesに属する窓は、Windowsで`Classified (Tiled | Floating, _)`であり、その逆も成り立つ。`Unplaced`/`Excluded`/未分類の窓はどのWorkspaceにも属さない。
- (X3) `Focus.History ⊆` Workspacesに属する窓。Placementの`Desired[w] = HiddenByWm` ⇔ `w`のWorkspaceがTopologyに表示されていない（モニター0台の間を除く）。

- 新しい窓は、分類が確定した時点でWorkspaceのStackの末尾に挿入する（Stackが`None`なら1要素のzipperを作る）。分類がUnknownの間は挿入しない（5.4のUnknown暫定方針と整合させる）。
- 窓の実効的な管理（`EffectiveManagement`）は、`UserOverride`があればそれ、無ければPolicyの分類結果、ただし`Movability = NotMovable`ならTiled/Floatingを`Unplaced`に読み替える、の順で決まる（v2.6、R4-11/R4-20/R5-3）。ユーザー指定がPolicyより優先されるのは、信念の修正で再分類の契機が増えたv2.6では、そうしないと`ToggleFloating`した窓がタイトル変化等の再分類で元に戻るからである。
- **動かせない窓（`Unplaced`）はどのWorkspaceにも属さない**（v2.6、独立レビューR5-3）。典型は昇格窓で、UIPIにより`SetWindowPos`が拒否され（F23）、非表示の操作も拒否される可能性が高い（未検証、S7r系スパイクで確認）。Workspaceに所属させると、そのWorkspaceがhiddenになったときに隠せない窓へ`SetPresence`を出し続けることになる。全Workspace共通の窓として扱い、`Desired`も`SetPresence`も持たない。フォーカスを持っても`focused`は`Elsewhere`になり、`CloseFocused`等は作用しない（UIPIにより、非昇格のWMから昇格窓へ`WM_CLOSE`を送ることもそもそもできない）。
- `Windows.Records`と各ドメインの信念は窓が消滅した時点でのみ除去する（Discovered/Excludedの窓も、属性が変われば再分類するために信念を持ち続ける）。Workspacesと`Focus.History`からは、窓が消滅・Excluded化・Unplaced化した時点で同じ`step`の中で除去する（WindowsがDomainEvent `WindowUnmanaged`を出し、後段が処理する）。**`Focus.SwitchSettle`が保持する「再要求すべき窓」が消滅した場合も、この除去対象の列挙に含める（v2.11で追加、Opus批判的レビューdocs/review-phase-a-round1.md 4-1対応）**：除去した窓のWorkspaceの、除去後のzipperのFocus（Stackが空ならNone）へ置き換える。これを怠ると、期限到達時にPlacement結線後は消滅した窓へのRequestForegroundがゲートの`TargetNotAlive`で弾かれ、本来再要求すべき隣の窓へフォーカスが渡らない。
- **「消滅」と「管理外化」の除去は別の契機・別の対象範囲として扱う（v2.11、Opus批判的レビューdocs/review-phase-a-round1.md 4-2対応）**：「消滅」（`WindowRemoved`。Sticky規則のProcess不一致によるHWND再利用の検知も同じに扱う、上記）はWorkspaces/Focus.History/SwitchSettleと全ドメインの信念を除去する。「管理外化」（Excluded化・Unplaced化。消滅はしていない）は`WindowUnmanaged`を契機にWorkspaces/Focus.History/SwitchSettleからだけ除き、信念は保持する（Excludedの窓も属性が変われば再分類するため）。実装時にこの2つを同じ関数へ融合すると、片方を書き換えたときにもう片方が壊れる取りこぼしが起きやすい。
- WMプロセス自体のワークスペースは、OSの仮想デスクトップ機能とは独立したCore内の状態である（1.3参照）。実装方式（窓を隠す／画面外へ移動／最小化のいずれかに加え、v2.5でCloakを選択肢に追加。21章参照）はPhase 0〜Aで確定する。Coreは方式に依存しない`DesiredPresence`と`SetPresence` Effect（7.1）だけを扱い、方式はShellが実装する（v2.6、R4-6）。いずれの方式でも「クラッシュ時に非アクティブワークスペースの窓が行方不明になる」リスクがあるため、8.4の安全停止・全体再観測で各窓の所属Workspaceを復元できることを要件とする。
- **WM自身の非表示と候補条件（v2.6、独立レビューR5-2）**：Normalizerの候補条件（可視、非Cloak等）は、`ClassificationState = NotCandidate`の窓についてだけ評価する。一度`Classified`になった窓は、候補条件を外れたことでは`NotCandidate`へ戻らない（戻すのは消滅とExcludedへの再分類だけ）。どの非表示方式でも、WMが隠した窓は候補条件を外れる（`SW_HIDE`なら不可視、Cloakなら`CloakState.Shell`）ので、この規則が無いとhiddenなWorkspaceの窓が全て所属を失い、Workspaceへ戻っても窓が戻らない。同じ理由で、分類の述語は`Desired = HiddenByWm`の窓の`IsVisible`/`IsMinimized`/`Cloak`の変化を、再分類の契機として扱わない。

  **Core.Windowsの実装（v2.15追加、ADR-019実装順14）**：候補条件はR3-2の解決（上記、Normalizerは一切判定しない）のとおりCore.Windows（`Windows.isCandidate`）が判定する。`ClassInputBeliefs`からCoreが実際に読める属性は`IsVisible`（LatestWins、可視＝`Believed true`）と`Cloak`（LatestWins、`App`/`Shell`/`Inherited`の全ビットがfalse）の2つに限られる——「非ツール窓」（`WS_EX_TOOLWINDOW`相当）を判定する属性は`WindowSnapshot`が運ばないため、Shellの列挙時点で既に除外されている前提とする（この設計判断はこのラウンドで確定した。21章参照）。`Cloak`が未観測の間は「まだCloakされていないとみなす」楽観的既定（4.5の他の信念の初期既定値と同じ考え方）で候補条件を満たせるが、`IsVisible`が未観測の間は候補条件を満たさない（可視性は`WindowObserved`で必ず届く前提、候補条件の判定を無条件にYesへ倒す安全側の既定が無い）。
  分類の確定（`WindowRecord.Classification: NotCandidate | AwaitingEvidence of deadline * homeWorkspace: WorkspaceId option | Classified of EffectiveManagement * ClassifiedBy`）は、候補条件を満たした窓について`PolicyEvaluator.selectManagement`を呼び、`Resolved`なら即座に`Classified`、`AwaitingEvidence`なら期限タイマー（`TimerId.Windows (ClassificationDeadline w)`）を張って待つ。期限超過（`onClassificationDeadline`）では`Classified (EffFloating, ByTimeout)`へ解決し（§5.4「既定は保守的にFloating」）、その後に届く最初の証拠で一度だけ再分類を許す（`LateReclassifyUsed`）——2回目以降の証拠の変化は`ByTimeout`からの再分類を発生させない。`ByPolicy`で確定済みの窓の一般的な増分再分類（タイトル変化等）と、それに対する7.3の振動検知・劣化ロジックは、具体的な振動の閾値が本書に数値として無いため、このラウンドでは実装しない（既知のギャップ、次のラウンドで扱う）。ユーザー指定（`ToggleFloating`/`SetManagement`/`ClearUserOverride`）は`WindowRecord.UserOverride`に反映され、信念の変化を理由に上書きされない（§4.4本文）。`Movability`の信念はPlacementドメインが無いため常に4.5の初期既定値（`Movable`、`Inferred []`）を返す射影を使い、`EffUnplaced`はPlacement着手まで到達しない。HWND再利用の検知（Sticky規則の`Process`不一致→`WindowUnmanaged`＋`ForgetWindow`、上記4.5のSticky規則の節）、`StyleChanged`/`MinimizedStateChanged`の配線も、このラウンドでは対象外のままである。
- **ユーザー（またはアプリ）による最小化は戻さない（v2.6、独立レビューR6-4）**：Tiled窓をユーザーがタイトルバーやタスクバーで最小化すると、観測は`ObservedPresence = HiddenOther`、`Desired`は`Shown rect`になる。これを差分として`SetPresence(shown=true)`で戻すと、ユーザーの最小化を即座に取り消してしまう。そこで、表示状態のReconcileは`Desired`と観測（`ObservedPresence`の信念）の**組で決める状態ベースの規則**にする（v2.6、R6-4／独立レビューR7-2で「`Desired`の変化のときだけ出す」から修正。変化は1 stepでしか見えないので、抑止区間中の切替やRetryの使い切りの後に取りこぼす）：`Desired = Shown`かつ観測が`HiddenByWmMethod`（WM自身が隠した状態）なら`SetPresence(shown=true)`、`Desired = HiddenByWm`かつ観測が`VisibleToUser`なら`SetPresence(shown=false)`、観測が`HiddenOther`ならどちらの場合も何もしない。これにより、ユーザーが最小化した窓は、そのWorkspaceを離れて戻っても最小化されたまま保たれ（R7-4）、抑止区間や失敗の後も確定後の最初のReconcileで自然に収束する。`HiddenOther`の間、その窓は`LayoutContext.TiledWindows`から除き、残りの窓で枠を詰める（i3/komorebiと同じく、最小化は一時的に配置から外れることを意味する）。`VisibleToUser`に戻ったら、再び配置に加える。枠は詰める（v2.6でプロジェクトオーナー確認済み、21章）。
- **ハングした窓の枠は残す（v2.6、R6-7）**：`Responsiveness = Hung`の窓には`SetRect`を出さないが、`TiledWindows`からは除かない（枠を残す）。ハングは数秒で回復することが多く、除くとハングと回復のたびに同じWorkspaceの全窓が2回動くからである。振動による劣化（7.3）はFloatingへの変更なので、これとは別の経路である。
- フォーカス移動（FocusNext/Previous）が実際にOSへ反映できるかはB-3（8.1参照）のSetForegroundWindow許可の検証に依存する。**実デスクトップ実機検証で、`mouse_event`自己プライミングを使えば無関係な第三者プロセスからでも奪取できることを確認済み**（failure-catalog.md F16）。したがってShellの`RequestForeground`実装がこのプライミングを既定で行う前提であれば機能する。

**マルチモニター状態モデルの欠落を修正（v2.5で追加、OSS調査[oss-wm-survey.md]対応／v2.6でTopologyドメインの型に置き換え）**：旧版（v2.1〜v2.4.5）の`WmState`は`ActiveWorkspace: WorkspaceId`という単一フィールドしか持たず、ワークスペース↔モニターの対応がどこにも定義されていなかった。これはADR-011が模範として引用するxmonadの`StackSet`（`current`/`visible`/`hidden`という複数のscreen概念を持つ）と整合しない構造的な欠落だった。v2.5は`Screens: Map<MonitorId, WorkspaceId>`と独立した`ActiveMonitor`で塞いだが、`Map`と独立したフィールドの組では「アクティブなモニターは表示中の画面の1つ」「同じWorkspaceは1画面だけ」「モニター0台」を表現できず、モニターを抜くと`Screens.[ActiveMonitor]`が`KeyNotFoundException`→`InternalFault`→安全停止になる経路と、`SwitchWorkspace`で同じWorkspaceが2画面に出る経路があった（R3-2）。v2.6の`Displays`はxmonadの`StackSet`と同じく`current`と`visible`で表すので、前者2つと0台が型で表現され、インデクサで引く操作自体が無くなる。Workspaceのうち`Displays`に現れないものがhidden（xmonadのhiddenに相当）である。モニターの識別は4.1の安定MonitorId（HMONITORではない）を前提にする。

**設定上の割当と切断時の復帰先を分ける（v2.6で追加、R3-1対応）**：v2.5の`WorkspaceState.HomeMonitor`は、i3の`Workspace_Assignment`（設定上の`workspace X output Y`割当）とHyprlandの`m_returnMonitors`（切断時に記録する復帰先）という2つの異なる意味を1フィールドに混ぜ、更新規則を「最後に表示されていたモニター」にしていた。これは、スリープ復帰でDisplayPortのモニターが一時的に切断され、Workspaceが別モニターへ退避した瞬間に復帰先を退避先で上書きしてしまう（Hyprlandのテスト`cascadedDisconnectsReturnEachWorkspaceToItsFirstOwner`が守っている性質と逆の動作）。v2.6では2つに分ける：
- **設定上の割当**は`PolicyIR.WorkspaceAssignments`（5.2）に置き、DTOタグの対象にする。モニターは`MonitorId`（int64、Host内部の採番）ではなく、ユーザーが読める`MonitorSelector`（`PrimaryMonitor`または4.1の安定キーの文字列表現`MonitorKey of string`）で指定する（R4-17）。Topologyは採用中のPolicyの写しを持ち、モニターが現れた時点で`MonitorSnapshot.StableKey`/`IsPrimary`と照合して解決する。割当が使われるのは、モニターが新たに現れたときにどのWorkspaceを表示するかを決める場面だけで、Policy切替（Deploy/Rollback）時に既に表示中のWorkspaceを動かすことはしない。
- **切断時の復帰先**はTopologyドメインの`Returns: Map<WorkspaceId, MonitorId>`に置く（v2.6第1版は`WorkspaceState.ReturnMonitor`としてWorkspacesに置き、TopologyがDomainEventで記録を依頼していたが、Workspacesは7.1の段⑤でTopologyより後に処理されるので依頼が同じstepで反映されず、カスケード切断で記録が落ちる経路があった。復帰先は「どのモニターにどのWSを戻すか」というTopologyだけの関心事なので、Topologyが所有する、R6-1）。以下では`Returns`の値を「`ReturnMonitor`」と呼ぶ。更新規則：(1) 表示中のモニター`m`が確定したトポロジ（7.3のScreenSettle後）から消えたとき、そのWorkspaceはhiddenになり、`ReturnMonitor`が`None`の場合に限り`Some m`を記録する（既に値があれば上書きしない。最初の所有者を保つ）。`m`がアクティブなモニターだった場合、新しい`current`は残ったモニターのうち`IsPrimary`のもの、無ければ最小の`MonitorId`、モニターが残っていなければ`NoMonitors`とする（R4-16）。(2) モニター`m`が現れたとき、`ReturnMonitor = Some m`のhiddenなWorkspaceがあればそれを`m`に表示して`ReturnMonitor`を`None`へ戻す。無ければ割当で`m`に割り当てられたhiddenなWorkspace、それも無ければ最小IDのhiddenなWorkspace（他のモニターへの`ReturnMonitor`を持つWorkspaceは候補から除く——v2.11、Opus批判的レビューdocs/review-phase-a-round1.md 3-3対応。除かないと、cascaded disconnect/reconnectの途中で無関係な新規モニターが他モニターの復帰待ちのWorkspaceを奪い、15.1 #18が破れる）、それも無ければ未使用の最小IDで空のWorkspaceを作って表示する。**この(2)で選ばれたWorkspaceが持っていた`ReturnMonitor`は、選ばれた経路（割当済みの復帰先／割当／最小ID）を問わず必ず解除する（v2.11、docs/review-phase-a-round2.md N-3対応）**：解除を`ReturnMonitor = Some m`に一致した場合だけに限ると、割当経由で別モニターに表示されたWorkspaceの古い復帰先が永久に残り、そのWorkspaceが元のモニターへ二度と戻れなくなる（「予約より明示的な表示を優先する」）。(3) **ユーザーのCoreCommand**（`SwitchWorkspace`）で`ReturnMonitor = Some m`のWorkspaceを`m`以外のモニターに明示的に表示したときは`None`へ戻す（明示的なユーザー操作は自動復帰より優先する）。下記の「hiddenなWorkspaceの窓がフォアグラウンドになった」ことによる表示はアプリの自己アクティブ化でも起きるので、ユーザーの明示操作とみなさず`ReturnMonitor`を保持する（R4-16）。
- 一時的な切断（数秒で再接続するスリープ復帰の典型例）では、7.3の`ScreenSettle`の安定待ちの間はトポロジを確定させないので、退避そのものが起きない。上記の規則は、安定待ちを超えて続いた切断のためのものである。
- この反応全体（モニター消失時のWorkspace退避、アクティブなモニターの付け替え、新規モニターへのWorkspace割当）は、Topologyドメインの純粋関数`MonitorLifecycle.reconcile : TopologyState -> MonitorSnapshot list -> TopologyState * DomainEvent list`として1つの`step`の中で原子的に行う（7.3のS8、Phase Aのシミュレーション対象。19章）。15.1 #18で、Hyprlandのcascadedテストと同型の性質（任意の切断列・再接続列の後、各Workspaceは最初に退避した時点のモニターへ戻る）を検査する。
- **未決：同じWorkspaceが`ReturnMonitor`（特定のモニターへの復帰待ち）と`WorkspaceAssignments`（別のモニターへの割当）を同時に持つ場合の優先順位は未確定（v2.11、docs/review-phase-a-round2.md N-3の付記）。** 上記(2)の判定は「このモニターに対して」1件ずつ行うため、「Returnsが別モニターを指しているなら、このモニターへのAssignmentsは保留する」という大域的な優先規則をまだ持たない。実害が出るのは稀な競合（Assignmentsの対象モニターのセレクタ（例：`PrimaryMonitor`）が複数の物理モニターに同時に一致し得る場合等）に限られるため、Windows/Placement着手前に決める必要はないが、決める際は21章の形式で記録すること。

モニター0台（RDP切断、全ディスプレイの電源断で列挙が空になる場合）は`Displays = NoMonitors`で表す。HyprlandのようにFALLBACKの仮想出力を作る方式は採らない（仮想出力に対するLayoutを計算しても、それを適用する実体が無いため）。この間、Layout計算とEffect発行は行わず、窓の所属は保持し、`Desired`も変えない（表示先が無い状態で窓を隠すと、再接続までユーザーが窓を取り戻せないため。(X3)の例外）。

**`SwitchWorkspace`の意味（v2.6で確定、R3-2対応）**：xmonadの`view`の意味論を採る。対象Workspace `w`が既に`visible`のモニター`n`に表示されていれば、`n`の画面を`current`にするだけ（フォーカスがそのモニターへ移る）。`w`がhiddenなら、`current`の画面の表示を`w`へ入れ替え、それまで表示していたWorkspaceはhiddenになる。`w`が未知なら空のWorkspaceとして作ってから同じ規則を適用する（R4-16）。`NoMonitors`なら何もしない。2つのモニターの表示を交換する`greedyView`は、窓の移動（Effect）が2画面分発生し、ユーザーが見ていた別モニターの内容まで入れ替わるため既定にしない。`view`の実装は「表示中なら`current`を入れ替えるだけ、hiddenなら`current`の中身を入れ替える」の2分岐しか持たないので、同じWorkspaceを2画面に出す状態は作りようがない（v2.5の`Map`への素朴な追加で起きた、窓が2つの目標矩形の間で振動する経路は、型の上で書けなくなる）。ユーザーの操作（`ShownBy = UserView`）でWorkspaceが表示されたとき、および`FocusMonitor`や表示中のWorkspaceへの`SwitchWorkspace`で`current`だけが変わったとき（`CurrentChanged`）、フォーカスを渡す窓はそのWorkspaceのzipperの`Focus`であり（Workspace内の「最後にフォーカスされた窓」を構造が持っている）、`Stack = None`なら`RequestForeground`を発行しない。アプリの自己アクティブ化による表示（`ForegroundFollow`）とモニターの再接続による表示（`Reconcile`）ではフォーカスを要求しない（前者はユーザーがタスクバーで選んだ窓から別の窓へフォーカスを奪わないため、後者はユーザーが作業中の窓からフォーカスを奪わないため。独立レビューR7-1）。

**Workspace切替は「待ち」を伴う（v2.6で追加、独立レビューR4-5、ADR-017）**：切替では、旧Workspaceの窓を隠す`SetPresence`と新Workspaceの窓への`RequestForeground`が非同期に適用される。その途中でOSは、隠されつつある旧Workspaceの窓へフォアグラウンドを一時的に移すことがある（最小化方式ならZ順で次の窓）。これを下記の「hiddenなWorkspaceの窓がフォアグラウンドになった」と解釈すると、切替が巻き戻る。そこで`SwitchWorkspace`の後、Focusドメインは`SwitchSettle = Some (フォーカスを渡す窓, deadline)`を状態に置き、`StartTimer (FocusTimer WorkspaceSwitchSettle, ms)`を返す。この待ちの間は、hiddenなWorkspaceの窓のフォアグラウンド化でWorkspaceを切り替えない（`Foreground`の信念は更新する）。期限が来た時点でフォアグラウンドがまだhiddenなWorkspaceの窓なら、フォーカスを渡す窓へ`RequestForeground`を1回だけ再発行する（Workspaceは戻さない）。

**フォーカスの単一情報源とFocusHistoryのスコープ（v2.6で確定、R3-3対応、独立レビューR4-4/R4-18/R5-1/R5-4で修正）**：v2.5は`WorkspaceState.FocusHistory`（Workspace単位）と`WmState.RecentFocus`（グローバル）の2つの履歴を持ち、「今フォーカスされている窓」がどちらの先頭なのか定義していなかった。両者は簡単に食い違い、`CloseFocused`がユーザーの見ているFloating窓ではなく前者の先頭のTiled窓を閉じる実装が規格上許されてしまっていた。v2.6では次のように決める：
- **Workspace内のフォーカスは、zipperの`Focus`という構造そのものが持つ。** xmonadの`Stack a = Stack { focus, up, down }`と同じく、「このWorkspaceで最後にフォーカスした、またはフォーカスを要求した窓」はzipperの`Focus`フィールドという1つの値であり、別の表現と食い違う余地が無い。zipperは`FocusNext`等のコマンドの時点で回し（意図）、観測（段⑥の`FocusObserved`）でも上書きする。`RequestForeground`が`ForegroundDenied`になった場合、zipperの`Focus`は実際のフォアグラウンドと一時的に食い違うが、次の`FocusNext`はユーザーの意図した位置から回るので、キーの連打に強い（観測でだけ回す方式は、観測が届く前の2回目のキーが同じ窓を起点に回ってしまう。コマンドで回す方式はv2.6でプロジェクトオーナー確認済み、21章、R6-6）。v2.5の`WorkspaceState.FocusHistory ⊆ Order`のような不変条件は、zipperでは型で保証される（`Focus`は並びの要素そのもの）ので、検査自体が不要になる。
- **「今フォーカスされている窓」は、フォアグラウンドの信念と各ドメインの状態から毎回導出する唯一の値`focused s`である。** `Foreground`は`ForegroundObserved`（観測）から更新するLatestWinsの信念であり、`RequestForeground`を発行しただけでは更新しない（ADR-003の受付と実現の区別）。`focused`を状態として持たず導出にしたのは、新しく開いた窓（OSが表示と同時にフォアグラウンドにするが、その時点では未分類）が数百ms後に分類されて管理下に入ったとき、`ForegroundObserved`は二度と来ないので、観測時にだけ更新する方式では`Elsewhere`のまま固着し、新しい窓での最初のキー操作が効かなくなるからである（R4-4）。`focused`は**表示中のWorkspaceの窓だけ**を`ManagedWindow`とし、hiddenなWorkspaceの窓がフォアグラウンドに残った場合（`SwitchSettle`中のOSの一時的なアクティブ化、`ForegroundDenied`、自己アクティブ化でWorkspaceを切り替えない設定）は`Elsewhere`になる。これにより`CloseFocused`/`PromoteToMaster`が見えない窓に作用することは無い（R5-4）。CoreCommandはすべて`focused`を基準に解釈する。
- **グローバルな履歴（`Focus.History`）は「直前にフォーカスしていた窓へ戻る」専用の、zipperとは別の小さな構造である。** 要件「直前にフォーカスしていた窓へ戻る」は、別のモニター・別のWorkspaceの窓へも戻れなければ意味が薄く、これにはbspwmの`--record-history`と同じグローバルなリストが必要である。Workspace内の「最後にフォーカスされた窓」はzipperが答えるので、履歴をWorkspace単位で持つ必要は無く、グローバル履歴がそれと重複することも無い。v2.5の引用「i3のRefocusLast」は誤りで、`RefocusLast`はxmonad-contribのもの（Workspaceごとに直近2件のみ）であり、i3の`focus_head`は全階層（root/output含む）にある。
- **更新規則（契機を2つに分ける、R5-1/R6-3）**：
  - **アクティブなモニターの追従は、`Foreground`の信念の値が変わったstepに限る。** 新しい値`w`が（step開始時点で）管理下の窓で、そのWorkspaceが`visible`のモニター`n`に表示されていれば`n`を`current`にする（マウスで別モニターの窓をクリックした直後の`SwitchWorkspace`が、ユーザーの見ていない側を切り替えないように。xmonad/i3と同じ、R4-18）。状態から毎stepに適用する（level-triggered）と、`FocusMonitor n`の直後、`ForegroundObserved`が届く前の同じstepで`current`が元へ戻され、空のモニターへは永久に移れない（R5-1）。
  - **zipperのフォーカスと`Focus.History`の更新は、導出値`focused`の値が変わったstepに行う**（step開始時と終了時の`focused`を比べる、7.1の段⑥）。`Foreground`の値の変化だけを契機にすると、新しく開いた窓（フォアグラウンドになったstepでは未分類、分類されたstepではフォアグラウンドの値が変わらない）や、hiddenだったWorkspaceが表示されて元からフォアグラウンドだった窓が見えるようになった場合に、zipperと履歴が二度と更新されない（R6-3）。`FocusMonitor`や別モニターへの`SwitchWorkspace`は`focused`の値を変えないので、この規則がそれらを打ち消すことは無い。
- **不変条件（15.1 #16）**：(F1) `Focus.History`は重複を持たず、Workspacesに属する窓だけを含む（(X3)）。(F2) どのstepの終了時点でも、`focused s = ManagedWindow w`なら、`w`のWorkspaceのzipperの`Focus = w`かつ`head Focus.History = w`（無条件の形、R6-3）。消滅・Excluded化した窓は同じ`step`で除去するので、`Focus.History`の長さは常に管理下の窓数以下であり、別途の長さ上限は要らない。
- **コマンドの意味（R4-18で補完）**：`FocusNext`/`FocusPrevious`は、アクティブなWorkspaceのzipperを回す（`Focus`を`After`の先頭／`Before`の先頭へ移し、端では反対側へ巡回する。TiledとFloatingの両方を巡回し、ユーザーが最小化した窓（`ObservedPresence = HiddenOther`）は飛ばす。`SetForegroundWindow`は最小化された窓を復元しないことがあり、最小化はLayoutからも外れる扱いなので、巡回からも外すのが一貫する、R7-5）。`focused`が`Elsewhere`のときも同じzipperを基準にし、`Focus`の次の窓へ移る（v2.6第2版の「最後にフォーカスしていた窓へ移る」から意図的に変えた：zipperの`Focus`がその「最後の窓」なので、そこへ戻るだけでは`FocusNext`を押しても何も進まないように見えるため、R6-6）。`FocusMostRecent`は、`focused = ManagedWindow _`なら`Focus.History`の2番目、`Elsewhere`なら先頭へ移る。行き先の窓のWorkspaceがhiddenなら、`SwitchWorkspace`と同じ遷移（view意味論、`ShownBy = UserView`）でアクティブなモニターに表示してから`RequestForeground`する。`MoveFocusedToWorkspace`で窓を他のWorkspaceへ送ると、元のWorkspaceのzipperは送った窓を除いた隣の窓へフォーカスを移し、そこへ`RequestForeground`する（xmonadの`delete`と同じ）。`CloseFocused`/`PromoteToMaster`/`SwapWithMaster`は、`focused = Elsewhere`なら何もしない。

**観測されたモニターと意図されたモニターが食い違うとき（v2.6で追加、R3-4対応、独立レビューR4-1/R4-7/R5-9で修正）**：v2.5で各窓の「あるべきモニター」が導出できるようになったが、観測（Placementの`Geometry`の`Monitor`）と食い違ったときの扱いが無かった。食い違いの**原因**で分ける。原因は7.1の処理順序の段②で、Topologyドメインが移動の観測に付ける原因ラベル（`MoveCause = OsInduced | UserDrag | Unattributed`、3.3）で表す。`MoveSize`系の入力はユーザー操作でしか発生しないので、`ScreenPhase`が`Settling`/`Grace`の間でも`UserDrag`を優先する（R5-9）：
- **`UserDrag`**：`MoveSizeEnded of WindowId * Observation<PixelRect> * Observation<MonitorId>`（v2.6でShellがドロップ時点の矩形とモニターを同時に取得して運ぶ形に変更、R4-7）による観測。`MoveSizeEnded`が位置を運ばないと、後から届く`WindowObserved`が「ドラッグの直後」と認識されず「原因不明」として元のモニターへ引き戻される（R3-4の失敗シナリオがそのまま残る）ため、ペイロードに含める。観測を採用する：窓が別のモニター`m`へ落とされたら、`m`に表示中のWorkspaceへその窓を移す（i3/komorebiと同じ）。同じモニター内のドラッグは7.4に従う。
- **`OsInduced`**（`ScreenPhase`が`Reconfiguring`/`Settling`/`Grace`の間の移動・最小化。7.3）：観測は段①で既に信念に反映されている。所属の変更は行わず、grace window終了後に意図した配置（`Desired`）を再適用する。
- **`Unattributed`**（MoveSizeイベントを伴わない移動。アプリ自身の位置変更や、`Win+Shift+←/→`のようにMoveSizeイベントを出さないキーボード操作）：Tiled窓は`Desired`の矩形へ戻し、7.3の振動検知に委ねる（繰り返し戻されるならFloatingへ劣化させる）。`Win+Shift+矢印`をユーザー操作として認識できないことは既知の制約としてPhase Bの日常利用で記録する。
- **hiddenなWorkspaceの窓がフォアグラウンドになった**（タスクバーからの選択、アプリ自身のアクティブ化。`SwitchSettle`中を除く）：OSが既にその窓をユーザーに見せているので、その窓のWorkspaceをアクティブなモニターに表示する（`SwitchWorkspace`と同じview遷移だが`ShownBy = ForegroundFollow`。フォーカスは要求せず、`ReturnMonitor`は保持する、上記(3)）。フォーカスを奪うアプリの抑止はOSのフォアグラウンドロックに委ねる。この方針はv2.6でプロジェクトオーナー確認済み（21章）。
### 4.5 観測・信念・決定の3層（v2.6で新設、ADR-016）
窓の内部状態の多くは直接観測できず、推定するしかない。推定は誤り得るし、証拠が増えれば修正されるべきである。v2.5までは、本来は推定であるもの（整合性レベル、ハングしているか、UWPの実アプリの特定、Per-Monitor-V2として振る舞うか）を、1回のOS呼出の結果である`Observation<'T>`として扱っていた。その結果、次の観測が来たときに前の値をどう扱うか（R3-6）、どれが「今のフォーカス」か（R3-3）、probeが失敗したら分類はどうなるか（R3-7）が、どれも決まっていなかった。本節はこれを3層に分ける。

| 層 | 型 | 性質 | 永続化 |
|---|---|---|---|
| 観測（Observation） | `Observation<'T>`（4.2）、`WindowSnapshot`、`EffectCallReturned`等の入力 | 1回のOS呼出の生の結果。いつ・どこから得たかを伴う事実。誤りではなく、古くなるだけ | Journalに保存する（ADR-004の「Coreの外から来た事実」） |
| 信念（Belief） | `Belief<'T>`、`BeliefSlot<'T>`を、ドメインごとに個別に持つ（下記） | 入力列から推定した窓の属性。確度と根拠を持ち、新しい証拠で修正される | 保存しない。入力列からReplayで再計算する |
| 決定（Decision） | Policy評価（5章）、Reconciler（7.2）、CoreCommandの解釈（4.4）、およびその結果（4.4の`WindowRecord`、`Desired`、`Pending`等） | 窓の**属性**については信念だけを読み、Management・Desired・Effectを決める。属性以外の入力（CoreCommand、`MoveSizeEnded`、タイマー）は直接読む | 保存しない（ADR-004） |

信念は「観測だけの関数」ではない（v2.6、独立レビューR4-9で表現を訂正）。`MinWidth`と`Movability`はPendingの解決（Coreが過去に要求した目標矩形との照合）を、`Movability`はユーザーのCoreCommandを証拠に使うので、信念は**入力列全体と、それに対するCoreの過去の決定**から決まる（フィードバックがある）。決定的であることに変わりはないのでReplayは成立するが、正確には「信念はCoreの決定的な状態の一部であり、入力列から（Pendingを含む他のCore状態と共に）再計算される」である。3層の分離が保証するのは、**窓の属性について決定が読むのは信念だけ**（生の観測を決定が直接読まない）という一点である。

```fsharp
// Wm.Contracts。Belief<'T>はObservation<'T>と同じく小さな汎用の「形」であり、ドメインごとに個別に具体化する
// v2.6（R6-9）：全てのケースが、その根拠を運んだ入力のSequenceを持つ（根拠リストを1つの全順序で並べるため）
type EvidenceRef =
    | Sample of sequence: int64                          // 観測を運んだ入力（WindowObserved等）
    | Outcome of sequence: int64 * RequestId             // Effectの結果（EffectCallReturned）を運んだ入力
    | ProbeEvidence of sequence: int64 * EvidenceId      // 11章のProbeの証拠を運んだ入力

type Confidence =
    | Confirmed                        // 直接の観測で裏付けられ、それ以降に矛盾する証拠が無い
    | Inferred of EvidenceRef list     // 間接的な証拠からの推定。空リストは「証拠の無い事前の仮定」（例：Movabilityの楽観的な初期値）
    | Stale of asOf: int64             // 裏付けの前提が変わった／期限が来た。値は最後に信じていたものを保つ

type Belief<'T> = { Value: 'T; Confidence: Confidence; LastRevisedAt: int64 }

type BeliefSlot<'T> =
    | Believed of Belief<'T>
    | Unformed of EvidenceGap list     // まだ信念を形成できていない（未観測、または観測がすべて失敗した）。11.1のEvidenceGapそのもの

// v2.11で追加（Phase A実装、Opus批判的レビューdocs/review-phase-a-round1.md 1-1／round2.md N-1・N-2対応）。
// LatestWins規則専用の水位つきスロット。LatestWinsの新旧比較には`Observation<'T>.SampleSequence`が必要だが、
// `Belief<'T>`にはその専用フィールドが無い。`LastRevisedAt`を代用すると、Accumulating規則（`now`＝
// EventEnvelopeのMonotonicTime、ms）と同じフィールドに異なる番号空間が混在し、5.4の`combineAll`/`combineAnyOf`の
// `max LastRevisedAt`が意味を失う（例：`All [IsDialog; MovableIs Movable]`でSampleSequenceとmsのmaxを取ってしまう）。
// 解決：`Belief<'T>.LastRevisedAt`は全ドメイン・全修正規則で常に`now`に固定し、LatestWinsの水位はこの型で別に持つ。
// 水位は1本ではなく`ValueSeq`/`ErrorSeq`の2本に分ける。1本の水位をOk/Errorどちらでも進める設計は、値が既に
// 確定している間はOk観測の採用判定に使ってよい（Errorより古いがより新しい値を誤って捨てないため、ValueSeqだけを
// 見る）が、値がまだ無い(Unformed)間は逆にErrorの到着を水位に残す必要がある（より新しい失敗を知っているのに
// 後から届いた古いOkをConfirmedにしてしまうことを防ぐため）——1本の水位に矛盾する2つの要件を同時に満たすことは
// できない。Ok観測の採用判定は常に`ValueSeq`だけで行い（Errorの有無に関わらず値の更新を妨げない）、採用した値の
// 確度は`ErrorSeq`がその観測より新しければ`Stale`、そうでなければ`Confirmed`にする。
type LatestWins<'T> = { Slot: BeliefSlot<'T>; ValueSeq: int64 option; ErrorSeq: int64 option }
```
LatestWins規則の修正関数`revise`はドメインごとに個別実装せず、Wm.Contractsに1つだけ置く（Windows/Placementの
`ClassInputBeliefs`/`GeometryBeliefs`が実装される際も、Focusの`Foreground`のようにコピーして再実装せず、この
関数を直接使うこと）。
`Inferred`の根拠には、指定案の`EvidenceId list`ではなく`EvidenceRef list`を使う。信念の根拠の大半はProbe（`EvidenceId`）ではなく通常の観測やEffect結果であり、それらはJournalのSequence/RequestIdで指せるからである。根拠のリストは集合として扱い、`sequence`の降順、同じ`sequence`ならケースの順（`Sample` < `Outcome` < `ProbeEvidence`）、次に値の順という全順序で並べて重複を除き、最大8件までに切り詰める（「全順序で並べた上位8件」なので、和集合をとってから切り詰める操作は結合的になり、15.1 #25の結合則が成り立つ。`Unknown`の`EvidenceGap`も同じく重複を除いた正準順序の集合として扱う。R6-9）（Checkpointの肥大を防ぐ。古い根拠はExplainがJournalを遡って示す。番号空間の違う3種類を1つの全順序で並べるための規定で、Replayで根拠リストが一致すること（15.1 #10）に必要、R4-24）。

**信念を1つの大きなレコードに集めない（v2.6、プロジェクトオーナーの指示）**：v2.6の第1版は、窓の全ての推定属性を`WindowBeliefs`という1つのレコードに集め、1つの`revise`関数で全てを修正していた。これをやめ、信念は**関心事（ドメイン）ごとに個別の型・個別のフィールド・個別の修正関数・個別の再probeの契機**を持つ形にする。`Observation<'T>`が`WindowSnapshot`の上で別々の`option`フィールドとして分かれているのと同じである。1つにまとめると、(a) ハング検知とAppIdの解決のように互いに無関係な推定が、同じ修正の契機・同じ周期を共有させられ、(b) あるコードがどの推定に依存しているのかが型から読めなくなり、(c) 本書が他所で退けてきた不要な統一（ADR-008、3.2の全面Deep DSL、6.1の`result {}`、3.3の汎用`Gated`/`EffectBatch`）を再び持ち込むことになる。共有するのは`Belief<'T>`という形（構造）だけで、意味（いつ・何によって修正されるか）は共有しない。

| 信念 | 置き場所（4.4） | 型 | 修正規則 | 修正の契機 |
|---|---|---|---|---|
| AppIdentity（4派生元）、Process | Windows `Identity` | `IdentityBeliefs = { Exe; HostExe; Aumid; CoreWindowExe: BeliefSlot<string>; Process: BeliefSlot<ProcessKey> }` | Sticky | 発見時と、まだ`Unformed`の派生元の観測 |
| 分類述語が読む属性 | Windows `ClassInputs` | `ClassInputBeliefs = { IsDialog: LatestWins<bool>; Owner: LatestWins<WindowId option>; IsVisible: LatestWins<bool>; Cloak: LatestWins<CloakState>; TitleMatches: LatestWins<TitleMatchVector> }`（v2.11で`BeliefSlot`から`LatestWins`へ訂正。上記参照） | LatestWins | 通常のWinEventと定期的な全体Snapshot |
| 矩形・モニター・最小化・表示状態 | Placement `Geometry` | `GeometryBeliefs = { NativeRect; VisibleFrameRect: LatestWins<PixelRect>; Monitor: LatestWins<MonitorId>; IsMinimized: LatestWins<bool>; Presence: LatestWins<ObservedPresence> }`（v2.11で`BeliefSlot`から`LatestWins`へ訂正） | LatestWins | 同上。7.2の「Observed」は`VisibleFrameRect`を指す |
| 最小幅 | Placement `MinWidth` | `Belief<int>`（DIP） | Accumulating | `SetRect`のPendingの`Mismatch` |
| DPIの振る舞い | Placement `DpiBehavior` | `Belief<DpiBehavior>` | Accumulating | 申告の観測、DPIをまたいだ後の2段目の有無 |
| 動かせるか | Placement `Movability` | `Belief<Movability>` | Accumulating | 整合性レベルの観測イベント、`SetRect`の結果、ユーザーの管理操作 |
| 整合性レベル | Placement `TargetIntegrity` | `BeliefSlot<IntegrityRelation>` | ProbeOnce | 発見時のprobe、失敗時の上限付き再probe |
| 応答性（ハング） | Health `Responsiveness` | `Belief<Responsiveness>`（`Map`に無ければ`Responding`。下記） | Accumulating | 事前確認・`Reprobe`の結果、`TargetHung` |
| フォアグラウンド | Focus `Foreground` | `LatestWins<WindowId option>`（v2.11で`BeliefSlot`から訂正） | LatestWins | `ForegroundObserved` |

同じ行（同じ修正規則・同じ契機を持つ同じドメインの属性）だけを小さなレコードにまとめ、行をまたいでまとめない。Accumulatingの信念（`MinWidth`/`DpiBehavior`/`Movability`/`Responsiveness`）は`BeliefSlot`ではなく`Map<WindowId, Belief<'T>>`で持ち、「`Map`に無い」ことを「まだ証拠が無い」の意味に使う（それぞれ既定の扱いが決まっているので、`Unformed`のEvidenceGapを区別する必要が無いため。R6-12）。各ドメインは自分の行の信念を修正する純粋関数を自分で持つ（例：`Health.reviseResponsiveness : now -> Belief<Responsiveness> option -> ResponsivenessEvidence -> Belief<Responsiveness> option`——戻り値が`option`なのは、証拠が決定的でなく既存の信念も無い場合に`None`（Mapへ書かない）を返し、「Mapに無い」＝「まだ証拠が無い」の意味を保つため、v2.11、Opus批判的レビューdocs/review-phase-a-round1.md 5-5対応、`Placement.reviseMovability`、`Windows.reviseIdentity`）。どの関数も、その信念に関係する証拠の型だけを受け取る。修正は「前の信念と、1つの新しい証拠から、次の信念を作る」決定的な1段の関数であり、Journalの全順序（`SampleSequence`）に沿った左からの畳み込みで十分なので、モノイド則（結合則）のような代数的性質は要求しない。

**`ObservedPresence`の判定に使う属性は非表示方式ごとに異なる（v2.6で追加、CIスパイクS13対応、failure-catalog.md F25）**：`(IsVisible, IsMinimized, DWMWA_CLOAKED)`の3値だけでは非表示方式を区別しきれない。実機検証（S13）で、「画面外へ移動」と「通常表示」はこの3値が完全に同一（`IsVisible=true, IsMinimized=false, Cloak=0`）になることを確認した。したがってShellが21章の「画面外へ移動」方式を採る場合、`ObservedPresence`の判定にはこの3値に加えて矩形位置（モニター矩形の外にあるか）も見る必要がある。`SW_HIDE`（`IsVisible=false`）・`SW_MINIMIZE`（`IsMinimized=true`）・Cloak（`DWMWA_CLOAKED≠0`、ただしS13bでは誘発方法が不十分で未確認）はそれぞれ他と重複しない値を持つため、採用する非表示方式が1つに決まればその方式専用の判定式は作れる。Shellは自分が実際に使っている非表示方式を知っているので、この判定はShell内に閉じ、`ObservedPresence`型自体に手を入れる必要はない。

```fsharp
// v2.6で追加（R5-2）。ShellはWMとして自分が採った非表示方式を知っているので、方式に依存しない形で観測を報告できる。
// SetPresenceのPendingはこの観測と照合する（TargetRect対VisibleFrameRectでは照合できない）
type ObservedPresence = VisibleToUser | HiddenByWmMethod | HiddenOther

type Movability = Movable | NotMovable                // ADR-015。yabaiのWINDOW_MOVABLE相当
type Responsiveness = Responding | Hung
type DpiBehavior = RescalesItself | OsBitmapScaled    // S11。WM_DPICHANGEDで自ら再配置するか、OSのビットマップ拡大縮小に任せるか

// 再probeの対象を指す名前、5.5の読み取り集合、Explainで使う（v2.6でケースを列挙、R4-22）
type BeliefKey =
    | KProcess | KAppExe | KAppHostExe | KAppAumid | KAppCoreWindowExe
    | KOwner | KIsDialog | KNativeRect | KVisibleFrameRect | KIsVisible | KIsMinimized | KCloak | KMonitor | KPresence
    | KMinWidth | KMovability | KTargetIntegrity | KResponsiveness | KDpiBehavior | KTitleMatches
```

**Policyの評価とLayoutが読む「ビュー」**：Policyの述語は、複数のドメインの信念を読む（`AppIs`はWindowsの`Identity`、`MinWidthAtLeast`はPlacementの`MinWidth`、`MovableIs`はPlacementの`Movability`）。そのために、7.1の段⑤で窓ごとに`ClassificationView`（述語が読む信念だけを各ドメインから集めた読み取り専用の値）を**その場で組み立てて**評価器に渡す。これは評価のための一時的な投影であり、状態として保存せず、修正もしない（したがって「全てを集めた信念オブジェクト」には当たらない）。Layoutに渡す`LayoutContext.TiledWindows`も同様に、Layoutが読む信念（`MinWidth`）だけを集めた一時的な投影である（5.6）。

**5.4の三値`Truth`はブール値の信念である。** 述語を信念の上で評価した結果は`BeliefSlot<bool>`であり、5.4の`Truth = Yes | No | Unknown of EvidenceGap list`は、これを確度を読まずに見た形にすぎない。v2.6では`Truth`を独立したDUとしてではなく、次のように`BeliefSlot<bool>`の別名とアクティブパターンとして定義し直す。5.4の三値論理の規則（`Not Unknown = Unknown`、`All`/`AnyOf`の伝搬、`Unknown`を`No`へ潰さない）はそのまま有効である：
```fsharp
type Truth = BeliefSlot<bool>
let (|Yes|No|Unknown|) (t: Truth) =
    match t with
    | Believed b when b.Value -> Yes b.Confidence
    | Believed b -> No b.Confidence
    | Unformed gaps -> Unknown gaps

// 5.4のAll/AnyOfを、単位元を持つ2つの結合演算として明示する（v2.6）。汎用のMonoidインターフェースは作らない
val combineAll   : Truth -> Truth -> Truth   // 単位元 allIdentity   = Believed { Value = true;  Confidence = Confirmed; LastRevisedAt = Int64.MinValue }
val combineAnyOf : Truth -> Truth -> Truth   // 単位元 anyOfIdentity = Believed { Value = false; Confidence = Confirmed; LastRevisedAt = Int64.MinValue }
// All [p1; …; pn] = List.fold combineAll allIdentity [p1; …; pn]
```
`combineAll`は、どちらかが`No`なら`No`（確度は`No`である側の確度の弱い方）、そうでなくどちらかが`Unknown`なら`Unknown`（`EvidenceGap`は和集合）、両方`Yes`なら`Yes`（確度は弱い方）。`combineAnyOf`は`Yes`と`No`を入れ替えた双対である。確度の「弱い方」は`Confirmed` > `Inferred` > `Stale`の順で、`Inferred`同士は根拠の和集合を上記の順序で並べて8件に切り詰め、`LastRevisedAt`は大きい方をとる。この定義は結合則と単位元の法則を満たすように作ってあり、15.1 #25でFsCheckにより検査する（三値論理の実装のずれ、例えば確度の合成の順序依存を捕まえるため）。確度は分類の**結論**を変えない（`Stale`の信念も最後に信じていた値で`Yes`/`No`になる）。確度が使われるのは、Explainでの説明と、再probeの契機の2つだけである。古くなった信念を`Unknown`に戻すと、R3-6が指摘した「再観測のたびに高優先ルールが`Unknown`になり、全窓の分類が保留へ戻る」現象が起きるため、この選択は意図的である。

**修正規則の4種類**（任意の関数ではなく閉じた種類にすることで、Explainが「なぜこの値か」を規則の名前で説明できる）：

| 規則 | 修正のしかた |
|---|---|
| LatestWins | スナップショット由来かイベント由来かを問わず、より新しい`SampleSequence`（4.2：Shellが全観測に付ける単一の単調番号）の`Ok`観測で置き換える（`Confirmed`）。`Error`の観測は値を保ったまま`Stale`にする。`None`（今回問い合わせなかった）は何もしない。古い`SampleSequence`の観測は新しい信念を上書きしない（7.2「古い取得結果が新しいSnapshotを上書きしない」の具体化、R3-6/R4-10） |
| Sticky | 最初の**妥当な**`Ok`観測で固定する（`Confirmed`）。既知の不定値は証拠にしない：`CoreWindowExe`が`ApplicationFrameHost.exe`を返した場合（フルスクリーン時に親子とも同じPIDを返す既知の例外、5.3）は`Ok`として扱わず`Unformed`のままにする（R4-13）。固定後に異なる妥当な値が観測されたら、値は変えずに確度を`Inferred [矛盾した観測…]`へ下げる（確度は分類の結論を変えないので安全）。`Process`が異なる値で観測されたら、それはHWNDの再利用を見逃した証拠である。Windowsドメインは同じ`step`で旧`WindowId`を消滅と同じに扱い（`WindowUnmanaged`を後段へ）、`ForgetWindow`（7.1）を返してShellに対応表から外させる。次の列挙でShellがそのHWNDを新しい`WindowId`として再発見する（R4-14：CoreはWindowIdを採番しない）。`ForgetWindow`が処理されるまでの間に旧`WindowId`で届いた観測は、`Records`に無いIDとして捨てる（R5-11） |
| ProbeOnce | 窓の発見時に1回probeし、`Ok`なら`Confirmed`。`Error`なら`Unformed [MissingTargetIntegrity]`のまま`RetryState`（3.3）で上限付き（既定3回、指数バックオフ）に再probeし、上限に達したら打ち切る。整合性レベルはプロセスの生存中に上がることがない（下げることはできる）ので、同じ`ProcessKey`の間は再probeしない。`Process`が`Unformed`の間は同一性が未確認なので、この抑止を適用しない（R4-14） |
| Accumulating | 複数種類の証拠から推定する。各信念の規則は下記 |

- **Movability**（ADR-015）：窓の発見時に`{ Value = Movable; Confidence = Inferred [] }`（証拠の無い楽観的な事前仮定）で始める。Windowsにはyabaiの`AXUIElementIsAttributeSettable`に相当する事前の能力問い合わせが無いため、動かせると仮定して試し、失敗から学ぶ。`TargetIntegrity`の観測イベントで`Higher`が得られた**その時に限り**`NotMovable`（`Inferred [Sample …]`）に修正する（最初の失敗を省く最適化。状態からの導出ではなくイベント駆動の規則なので、下記のユーザー操作による試し直しを即座に打ち消さない、R4-11）。`SetRect`が`Denied OtherDenial`で失敗したら`NotMovable`（`Inferred [Outcome …]`）。`SetRect`の目標矩形が観測されたら（7.2の`Satisfied`）`Movable`（`Confirmed`）。`NotMovable`の窓は`Unplaced`としてどのWorkspaceにも属さず（4.4）、Placementはその窓へ`SetRect`を発行しないので、その後この信念を修正する証拠は自然には来ない。ユーザーがその窓に`SetManagement`/`ToggleFloating`を明示的に行った場合に限り、`Movable`（`Inferred []`）へ戻して1回だけ試し直す（ユーザーの明示操作は、古い推定を捨てる根拠になる）。自動の再試行（長いバックオフでの再挑戦）は行わない：`Denied OtherDenial`は`classify`が一過性の失敗（`Transient`）と区別したうえでの分類であり、誤分類の危険は`classify`の対応表の側（23章で既存キーの付け替えを独立レビュー対象にしている）で管理するのが筋だからである。**整合性レベルのprobeが失敗しても（ADR-015の未検証APIが使えなくても）、この信念は`Denied`の実績だけから正しく形成される。**
- **Responsiveness**（ADR-012）：`PumpTimedOut`で`ElapsedUs`が0に近い（OSがハング判定済み、7.1）か、Effectの結果が`TargetHung`なら`Hung`（`Confirmed`）。`IsHungAppWindowTrue`だけなら`Hung`（`Inferred`）に留め、決して`Confirmed`にしない（ゴーストウィンドウでは常にTRUEになる、feasibility.md §5）。`PumpResponded`が観測されたら`Responding`（`Confirmed`）へ修正する。`Hung`の間、Healthドメインは`HealthTimer.ResponsivenessReprobe w`のタイマーを`RetryState`の指数バックオフ（1s、2s、4s…上限30s）で張り（最大20回。上限に達したら再probeを止め、その窓の`ForegroundObserved`かユーザー操作で再開する。ゴーストウィンドウや恒久的にハングした窓に対してJournalへ無期限に観測を積まないため、R4-25）、`Reprobe`（7.1）で`SendMessageTimeout(WM_NULL)`の再確認を要求する。**`Responding`へ修正されたら、その窓は再び配置の対象になる**（v2.5までは`TargetHung`で自動管理を停止した後、それを解除する経路が無かった）。「ハングによる停止」はHealthの信念そのものであり、Placementは`Hung`の窓を配置対象から外すだけで、停止理由を別の状態として複製しない（振動による停止はPlacementの`Oscillation`が別に持つので、互いを解除しない）。信念が無い窓は`Responding`として扱う（事前確認がEffectごとに行われるので、楽観側に倒しても安全、R4-24）。
- **MinWidth**：`SetRect`で幅`r`を要求して幅`o > r`が観測された（`Mismatch`）ことを証拠に、`o`を`Inferred`の最小幅とする。**値は観測時に窓がいたモニターのDPIでDIPへ正規化して持つ**（`o × 96 / dpi`。v2.6、独立レビューR4-12）。アプリの最小幅は通常DIPで決まっており（400DIPなら100%で400px、150%で600px）、物理ピクセルのまま蓄積すると、150%のモニターで観測した600pxが100%のモニターでも最小幅として残り、分類が移動経路に依存する（R3-5の失敗シナリオが形を変えて残る）。DPI非対応のアプリ（`DpiBehavior = OsBitmapScaled`）もOSのビットマップ拡大で物理幅がDPIに比例するので、同じ正規化でよい（System DPI Awareのアプリはシステムの DPIとモニターのDPIの比で拡大されるので近似になる。誤差はPhase Bの実機Journalで確認する）。`DpiBehavior = RescalesItself`の窓が異なるDPIのモニターへ移った直後、2段目の自己リサイズを待っている間に観測した`Mismatch`は証拠にしない（1段目のOSの拡大と2段目の間の値を誤って正規化しないため、R5-11）。Layoutが使うときは対象モニターのDPIで物理ピクセルへ戻す。同じ結果が繰り返されれば根拠が増えるが、`Confirmed`にはしない（4.2「単発の観測を確定事実に昇格させない」）。より小さい幅が観測されたら値を下げる。具体的な閾値（何回で配置に反映するか）はPhase Aで13章のシナリオを使って決める。
- **DpiBehavior**（S11）：`DpiAwareness`の申告が`PerMonitorV2`なら`RescalesItself`、それ以外なら`OsBitmapScaled`を`Inferred [Sample …]`の初期値とする。異なるDPIのモニター間を移動した後、`WM_DPICHANGED`に応じた2段目の自己リサイズが観測されたかどうかで修正する（申告と実際の挙動が違うアプリがあり得るので、申告は事前証拠にすぎない）。Placementは8.3のB-6で、この信念が`RescalesItself`の窓に対してだけ2段目の観測を待つ。信念が無ければ待たない（R4-24）。

**信念の計算は入力列の決定的な純粋関数である（ADR-004との整合の核心）**：各ドメインの信念は、直近のCheckpointの信念に、その後の入力を各ドメインの修正関数で順に畳み込んだ結果と常に等しい。修正関数はCoreの一部であり、OS時計を読まず（`now`は`EventEnvelope`の時刻）、`Map`/`Set`の順序付きコレクションだけを使う（7.1の決定性の規則、3.4）。したがって信念はJournalに保存せず、Replayで再計算する。信念の修正の履歴を独自のログとして永続化することもしない（それは旧ADR-004が試みてv2.1で撤回した「判断ログの全量永続化」と同じ誤りである、3.4のDeciderパターンについての注意参照）。この性質から次の2つが自然に従う：
- **信念が修正されれば、決定も再計算される。** 決定は信念だけを読むので、後から証拠が届いて信念が修正されれば、次の`step`の分類・Reconcileはそれに基づいてやり直される。Fixed-log Replay（12.2）は、ピン留めした当時の修正関数で当時の信念と決定を再現する。修正関数の改良をCounterfactual Simulationで評価する場合は、**セッションの先頭から**畳み込み直さなければならない（R4-9）。Checkpointの信念は旧規則で畳み込まれた結果であり（StickyやAccumulatingは過去に依存する）、Checkpoint起点では改良後の信念にならない。また、Policy/Layoutも変えた場合は要求矩形が変わり、`Mismatch`という証拠自体が変わるので、「同じ観測から別の信念」ではなく「別の入力・別の信念」の比較になる。
- **6.1のパイプラインの「Classify」は、信念の形成と、信念に基づく分類の2段に分解される。** v2.5までの「Classify」は、生の観測を読んで一度にTiled/Floating/Excludedを決めていた。v2.6では、観測→各ドメインの信念の修正→Policy評価（`ClassificationView`の上の三値論理）→Management、の順になる。前者は分類以外（Placement、フォーカス、ハング、DPI）にも使われる一般的な段である。

**Checkpointは稼働中の再起動には使わない（v2.6、独立レビューR4-3）**：当初は「稼働中の再起動ではCheckpointの信念を全て`Stale`にして使う」としていたが、それは(a) Journal上の入力として表現されないのでReplayと元の実行で信念の確度が食い違い（確度は再probeの契機を変えるので、Effect列が変わる）、(b) ランタイムのタイマーはプロセスとともに消えるので、Checkpoint内の待ちの`Deadline`に対する`TimerElapsed`が二度と来ない（分類待ちの窓が永久にUnknown、Pendingが永久に残る）、(c) `HWND → WindowId`の対応表はShell側にあって失われるので、Checkpointの各ドメインの状態が誰とも対応しない孤児になる、という3つの破綻を持っていた。そこで、Checkpoint（9.5）は**Replayの開始点専用**とし、稼働中の再起動では16.2の「保存状態をOSの真実とみなさず、全体再観測で再同期」をそのまま適用して、空の状態から始める。Workspaceの所属だけは引き継がないと使い勝手が大きく損なわれるので、Hostが`(HWND, ProcessKey) → (WorkspaceId, UserOverride)`という小さな**所属ヒント**を永続化する（Coreの所属・`UserOverride`の変化を受けて更新する）。起動時、Shellは最初の全体列挙で`WindowId`を割り当てた後、`(HWND, ProcessKey)`が一致した窓についてだけヒントを`WindowId`へ解決し、**その列挙の`WindowObserved`群より前に**`MembershipHintsLoaded of (WindowId * WorkspaceId * Management option) list`入力（7.1）としてJournal経由でCoreに渡す（HWNDはCoreに入らない、4.1。到着順をShellの契約にするのは、ヒントより先に分類まで進んだ窓にヒントが適用されない穴を塞ぐため、R5-8）。Coreは、その窓が分類されたときにヒントのWorkspaceへ入れ、`UserOverride`を復元する（ヒントの無い窓は通常どおりの規則）。ヒントは入力なのでReplayで再現でき、`ProcessKey`の照合によりHWND再利用で別の窓に誤適用されない。

**ADR-007（Active Diagnosis）との関係**：11章のProbeで得た結果は、観測として該当するドメインの修正関数に入り、信念を修正する。11.1の`EvidenceGap`は`Unformed`の中身そのものであり、11.3の手順6「支持・反証された仮説を更新する」は、窓の属性の単位では信念の修正である。ADR-007が方向性として述べていた信念の修正は、本節によって操作対象となる具体的な型を得た。一方、12.3の「アプリの振る舞いモデル」（`MinimumWidth(700)`のような疑似窓のモデル候補）は、個々の窓の属性ではなくアプリの振る舞い全体についての仮説であり、本節の`Belief<'T>`の対象ではない（同じ確度・根拠の考え方を使ってよいが、型を統一はしない）。
## 5. F# + 宣言的Policy IR
### 5.1 表現の選択
| 対象 | 表現 | 理由 |
|---|---|---|
| ウィンドウ分類・優先順位 | 宣言的Policy IR | 衝突・到達性・選択理由を説明できる |
| レイアウト選択・Fallback | 宣言的Policy IR | 保存・比較・事前検査できる |
| 観測条件・Probe方針 | 宣言的Policy IR | 読む情報を明確化できる |
| Master–Stack等の幾何計算 | F#純粋関数 | アルゴリズムを自由に記述できる |
| Reconciliation、Timer、要求追跡 | F#純粋関数 | 時系列のイベント状態遷移を表現できる |
| 外部操作 | 型付きEffect IR | OS APIの呼び出しと結果を分離できる |
| 疑似窓の振る舞い設定 | Scenario/Behavior IR | 証拠に対応付けた再現モデルにする |
| 診断手順 | 型付きCommand、必要に応じCE | 複数手順の実験を記述できる |


### 5.2 IRの基本型（初期案）
type WindowPredicate =
    | AnyWindow
    | AppIs of AppIdSource * string // v2.5でAppIs of stringから変更。komorebiのkind: Exe|Class|Title|Pathと同じ発想で、どの導出元を指すかを明示する（4.2のAppIdentity参照）
    | TitleContains of string
    | DialogIs of bool
    | MinWidthAtLeast of int // DIP。比較相手のMinWidthの信念もDIPに正規化して持つので（4.5）、分類はDPIに依存しない（v2.6、R3-5/R4-12。8.3参照）
    | MovableIs of bool // v2.6で追加（ADR-015/R3-7）。4.5のMovabilityの信念を読む。v2.5のTargetIntegrityIsを置き換え（タグ"target-integrity-is"は予約済み・再利用禁止、9.2）
    | All of WindowPredicate list
    | AnyOf of WindowPredicate list
    | Not of WindowPredicate

type Management = Tiled | Floating | Excluded

type WindowRule = {
    Id: PolicyRuleId
    Priority: int
    When: WindowPredicate
    Apply: Management
}

type LayoutPredicate =
    | Always
    | TiledCountAtLeast of int
    | MonitorWidthAtLeast of int // DIP（v2.6で単位を明記、R3-5）。レイアウト選択はモニター単位で行うので、そのモニターのDPIで変換する
    | Both of LayoutPredicate list

type LayoutArgs =
    | NoArgs
    | MasterRatio of decimal
    | ColumnCount of int

type LayoutRef = {
    Id: LayoutId
    ApiVersion: int
    Args: LayoutArgs
}

type LayoutRule = {
    Id: PolicyRuleId
    Priority: int
    When: LayoutPredicate
    Use: LayoutRef
}

type PolicyIR = {
    SchemaVersion: int
    WindowRules: WindowRule list
    DefaultManagement: Management
    LayoutRules: LayoutRule list
    DefaultLayout: LayoutRef
    WorkspaceAssignments: (WorkspaceId * MonitorSelector) list // v2.6で追加（R3-1）。i3の`workspace X output Y`相当。4.4参照
}

// v2.6で追加（R4-17）。PolicyではHost内部採番のMonitorIdではなく、ユーザーが読める識別で指定する
type MonitorSelector =
    | PrimaryMonitor
    | MonitorKey of string // 4.1のMonitorSnapshot.StableKey（REPLのInspectで確認できる）
これは互換契約の第一案。実際の永続フォーマットはF#のDUシリアライザーに暗黙依存せず、SchemaVersionを明示したDTOから変換する。`HWND`、任意の関数オブジェクト、任意のクロージャ、現在時刻、外部サービス参照はIRに埋め込まない。
### 5.3 設定サンプル
let policy = {
    SchemaVersion = 1
    WindowRules = [
        { Id = PolicyRuleId "dialog"; Priority = 100
          When = DialogIs true; Apply = Floating }
        { Id = PolicyRuleId "terminal"; Priority = 50
          When = AppIs (Exe, "WindowsTerminal"); Apply = Tiled }
        { Id = PolicyRuleId "unmovable"; Priority = 90 // v2.6でv2.5の"elevated"（TargetIntegrityIs Higher）を置き換え（ADR-015/R3-7）。
          When = MovableIs false; Apply = Excluded }    // 動かせない窓の既定はCoreの組込み規則で`Unplaced`（どのWorkspaceにも属さない、4.4）。これは「分類上もExcludedとして扱いたい」場合の例
    ]
    DefaultManagement = Tiled
    LayoutRules = [
        { Id = PolicyRuleId "columns-many"; Priority = 20
          When = TiledCountAtLeast 4
          Use = { Id = LayoutId "columns"; ApiVersion = 1
                  Args = ColumnCount 3 } }
        { Id = PolicyRuleId "coding"; Priority = 10
          When = TiledCountAtLeast 2
          Use = { Id = LayoutId "master-stack"; ApiVersion = 1
                  Args = MasterRatio 0.62m } }
    ]
    DefaultLayout = { Id = LayoutId "monocle"; ApiVersion = 1
                      Args = NoArgs }
    WorkspaceAssignments = []
}
（`SchemaVersion = 1`のままにしているのは、v2.6時点で永続化された互換対象のPolicyが存在しないためである。最初に永続化するスキーマをPhase AのofDto実装時に`1`として確定し、それ以降の形状変更は9.2の規則に従う。v2.6、R3-9対応）
設定用の`rule "name" |> whenApp ...`等のコンビネーターは提供してよいが、結果は必ずこの保存可能なPolicy IRになる。QuotationsやComputation Expressionsは必要な部分だけの構文糖衣とし、初期必須基盤にしない。

**述語の前提の明確化（v2.1で追加、A-6対応／v2.5でOSS調査[oss-wm-survey.md]対応として再構成）**：
- `AppIs`は4.2で定義した`AppIdentity`の4つの派生元（`Exe`/`HostExe`/`Aumid`/`CoreWindowExe`）のうち、どれを指すかを第1引数で明示する（例：`AppIs (Exe, "WindowsTerminal")`）。UWP/WinUIアプリの最上位HWNDは`ApplicationFrameHost.exe`が所有するため、`Exe`だけで判定するとアプリの種類によって機能しない場合がある（v2.2でfeasibility.md §9により具体化）。実アプリの特定には、クラス名`Windows.UI.Core.CoreWindow`を持つ子ウィンドウを探しそのプロセスIDを読む、という実装パターンがChromiumプロジェクトで使われている（`CoreWindowExe`の由来）。ただし、この手法には既知の例外がある：フルスクリーン表示中のUWPアプリでは親・子の両方が`ApplicationFrameHost.exe`のPIDを返す場合があり、また子ウィンドウからの取得はアプリがアクティブな間だけ機能するという制約がある。**GitHub Actions windows-latestランナーでのCI実機検証では、`calc.exe`は（モダンUWP版ではなく）クラシックな`win32calc`として起動され、トップレベル窓を一定時間内に取得できなかった（failure-catalog.md F02）。このランナーイメージにはUWP版Calculatorが無いため、ApplicationFrameHost仮説自体をCI上では検証できない**。この検証は実際のWindows 10/11デスクトップでのPhase 0〜Bでの再確認が必要。**S5の位置づけの変更（v2.5）**：調査した既存OSS WM（komorebi、GlazeWM）はいずれも`ApplicationFrameHost.exe`の実アプリを解決していない（[oss-wm-survey.md]）。したがってS5は「3手法（実行ファイル名／CoreWindow子窓探索／AUMID）から1つを選ぶ」問題ではなく、`AppIdentity`の各フィールドを独立に観測し続け「アプリごとにどのフィールドが信頼できるか」を実測データで判断する問題として扱う。単一の決定手法が確定しないこと自体はPhase Aをブロックしない。Analyzerは`Exe`が`ApplicationFrameHost.exe`そのものに直接マッチするルールをWarningとして検出する（5.5参照。ほぼ常にモデリングの誤りである）。
- `TitleContains`を使うルールを1つでも有効にすると、常にウィンドウタイトルを取得することになり、9.6の「必要時のみ取得する機密フィールド」という方針と直接競合する。`TitleContains`を使う設定では、取得したタイトルをJournalへ記録しない（分類判定にのみ使い、保存しない）ことを既定動作とする。**v2.6でこれを型で保証する（R4-8）**：Shellはタイトルを取得したら、その場で採用中Policyの全`TitleContains`パターンに対する一致ビット列（`TitleMatchVector`、4.2）に変換し、生の文字列はShellの外へ出さない。Journal・信念・Checkpoint・Incident Bundleに残るのは一致ビット列とパターン集合のハッシュだけである。これにより「タイトルをJournalに残さない」と「信念はJournalから再計算できる」（ADR-016）が両立する。**パターン集合のハッシュの照合は、信念の修正ではなく述語の評価器で行う**（v2.6、独立レビューR5-6）：`TitleContains`の評価時に、信念の`PatternSetHash`が採用中Policyのパターン集合のハッシュと異なれば`Unknown [MissingTitleMatch w]`を返す。信念の修正関数はPolicyを知らないまま保て、Policy切替の前に旧パターン集合で評価された観測が切替後に届いて（`SampleSequence`が新しいので採用され）旧集合の添字を新集合の添字として読む、という誤りも起きない。Policyを切り替えたら、Shellは新しいパターン集合でタイトルを評価し直して送る。
- ウィンドウタイトルはタブ切替等で頻繁に変わるため、分類の再評価タイミング（初回確定時に固定するか、変化ごとに再評価するか）を決める。再評価する場合はTiled⇔Floatingの往復を7.3の振動防止対象に含める。
### 5.4 三値評価と優先順位
`AppIs`が参照する`AppIdentity`の該当フィールドが未観測（v2.5でAppId=NoneからAppIdentityの各フィールド未観測へ変更）、Dialog判定不明、計測していないMinWidthはFalseではなくUnknownとして扱う。v2.6以降、述語は生の`WindowSnapshot`ではなく、4.5の各ドメインの信念から組み立てた`ClassificationView`の上で評価し、「未観測」は信念が`Unformed`であることを指す。
```fsharp
// v2.6でBeliefSlot<bool>の別名に再定義（ADR-016、4.5）。Yes/No/Unknownはアクティブパターン
type Truth = BeliefSlot<bool>
```
Not Unknown = Unknown。`All`は確定したNoがあればNo、なければUnknownを伝搬し、全てYesならYes。`AnyOf`は確定したYesがあればYes、なければUnknownを伝搬し、全てNoならNo。優先順位は`Priority`降順、同値は`PolicyRuleId`の安定昇順で評価する。`Unknown`の高優先ルールがある場合、低優先のTiledをすぐ適用すると危険なら保留 + 追加観測 + 安全な暫定方針へ進む。`Unknown`を暗黙にNoへ潰さない。

**暫定方針の具体化（v2.1で追加、A-5対応）**：分類がUnknownの間、窓はOSが表示した既定位置・既定サイズのまま維持し、4.4のWorkspaceのStack（zipper）には挿入しない。`LayoutPredicate.TiledCountAtLeast`等の窓数カウントにもUnknownの窓を含めない。分類が確定した時点で初めてStackへ挿入し配置を発行する。これにより新規窓は「既定位置で表示→分類確定→タイルへ移動」の2段階になることを許容し、2段階目を1回のバッチEffect（6章PlanValidator・7章Effect参照）にまとめてちらつきを抑える。分類確定後に再分類（タイトル変化等、A-6参照）で結果が変わる場合は、7.3の振動防止の対象に含める。

**Unknownには期限を設ける（v2.5で追加、OSS調査[oss-wm-survey.md]対応）**：分類確定に使う追加観測（Probe等）は無期限に待ってはならない。bspwmの外部ルールフック（`external_rules_command`、本体の`select()`にタイムアウトが無い実装）は、ハングした外部スクリプトが窓を**永久に**未管理のまま残してしまう反面教師である。分類待ちの観測に期限を設け、期限超過時はUnknownのまま配置をブロックし続けるのではなく、安全な既定分類（6.3のフォールバックと同じ考え方で、既定は保守的にFloating）へ解決する。この期限はADR-012のEffect適用タイムアウト（Shellが対象窓へ呼出す際の期限）とは別物（分類の入力待ちであり、Effect呼出ではない）だが、「無期限に待たない」という方針を共有する。

**期限の実装位置と、期限後に届いた証拠の扱い（v2.6で追加、R3-12対応）**：期限はShell側で時間切れにするのではなく、Windowsドメインが3.3の`StartTimer (Windows (ClassificationDeadline w), ms)`を返し、`TimerElapsed`入力で判定する（Shell側で打ち切ると、期限の判定がJournalに残らずReplayで再現できない）。窓が最初に`Unknown`と評価された`step`でタイマーを開始し（`WindowRecord.Classification = AwaitingEvidence (deadline, その時点のアクティブWorkspace)`、4.4）、分類が確定したら`CancelTimer`する。期限超過で解決した窓の所属先は、解決時ではなく最初に`Unknown`と評価された時点のアクティブなWorkspaceとする（期限の数秒間にユーザーがWorkspaceを切り替えても、窓が出現した側に入るように、R4-19）。**ただし分類が確定する時点でその窓がフォアグラウンドなら、所属はその時点のアクティブなWorkspaceとする**（ユーザーが新しい窓に入力しながらWorkspaceを切り替えていた場合に、使用中の窓がhiddenなWorkspaceへ入って消え、キー入力が見えない窓へ流れることを防ぐ。v2.6、独立レビューR5-4。この優先順位はv2.6でプロジェクトオーナー確認済み、21章）。**分類と期限の対象は、Normalizerの候補条件（可視、非ツール窓、非Cloak等、12.1のRaw層からの駆動と同じ判定）を満たす窓に限る**（`EnumWindows`が返す数百の不可視トップレベル窓に期限を張り、期限後に一斉にFloatingとしてフォーカス巡回へ入れてしまわないように、R4-19。候補条件を満たさない窓は`NotCandidate`のまま信念だけを持つ）。期限超過で既定分類（Floating）に解決した後に証拠が届き、信念の修正（4.5）によって分類が変わる場合は、**再分類を1回だけ許可する**（ユーザーからは「Floatingで表示→数秒後にタイルへ移動」に見えるが、遅いprobeの窓が恒久的にFloatingになるよりは良い）。この再分類は7.3の振動カウンタに算入し、2回目以降の分類変化は振動として扱う（「1回使ったか」は`WindowRecord.LateReclassifyUsed`に記録する、4.4）。期限の既定分類をFloatingにするか、管理しない（Discoveredのまま）にするかはトレードオフがあり、v2.6ではv2.5のFloatingを維持する（v2.6でプロジェクトオーナー確認済み、21章）。

**`MovableIs`/整合性レベルと期限（v2.6、R3-7対応）**：`NotMovable`の窓の既定の扱い（`Unplaced`：どのWorkspaceにも属さない）はPolicyのルールではなくCoreの組込み規則であり（4.4、R4-11/R5-3）、`MovableIs`は「さらにExcludedにしたいか」を表すためだけの述語である。`MovableIs`が読むMovabilityの信念は、窓の発見時に楽観的な事前仮定（`Movable`、4.5）で必ず`Believed`になるので、`Unknown`にならず分類を保留させない。v2.5の`TargetIntegrityIs`は、整合性レベルのprobeが失敗する（ADR-015の未検証APIが使えない）と常に`Unknown`になり、Priority 90の`Unknown`が全ての窓の分類を期限まで保留させたうえで、非昇格の窓まで期限超過でFloatingにしてしまうという欠陥があった（R3-7の失敗シナリオ）。v2.6でこの述語を`MovableIs`に置き換えたのはこのためである。

全候補の判定結果は説明可能にし、`whyNot(ruleId)`で非選択理由を返せるようにする。

**PolicyEvaluator.fsの実装（v2.14追加、ADR-019実装順8）**：`ClassificationView`は、6種の`WindowPredicate`が
読む信念だけを集めた最小限の形として`Wm.Policy`自身がPolicyEvaluator.fsで定義する
（`Identity: IdentityBeliefs`、`IsDialog`/`TitleMatches`/`MinWidthDip`/`Movability`の各
`BeliefSlot`）——`LayoutContext`/`ValidationInput`（§5.6/§6.2）がそれぞれLayouts/Validatorの
消費者側の要求形として先に定義され、後から生産者（Core）がそれに合わせて値を組み立てる、という
既存の順序と同じ位置づけである。将来Core.Windows/Core.Placementが実装されるとき、7.1の段⑤は
自身の実際の信念レコードからこの形へ射影するだけでよく、`ClassificationView`自体の形は
この6述語の意味から機械的に決まるので、Windows/Placementドメインの内部設計を先取りする
ものではない。

`selectManagement`（`WindowRules`の評価順をPriority降順・PolicyRuleId昇順で辿り、最初に
Yesとなったルールの`Apply`を返す）の挙動のうち、**「Unknownに出会ったときに低優先のルールへ
進むか」は本書に具体的なアルゴリズムが無かったため、実装時に次のとおり確定する**：
最初にUnknownに出会った時点で即座に`AwaitingEvidence`として停止し、それより低優先のルールが
たとえ確定Yes/Noであっても評価しない。低優先のルールを先に確定させてしまうと、高優先のルールが
後から確定したときに結果が変わる余地が残り、§7.3の振動防止・§5.4「暫定方針の具体化」の
「分類が確定するまでは既定位置を維持する」という設計意図と矛盾するためである（優先度の低い
ルールの結果が「高優先ルールの不確定性に依存しない」という直感に反する振る舞いを許してしまう）。

**`ClassificationResult.Resolved`にマッチしたルールのIdを追加（v2.15追加、ADR-019実装順14、
Core.Windows着手時に発見）**：`Resolved of Management`はどのルールが確定させたかを運ばなかったため、
Core.Windowsの`WindowRecord.Classification`が`ClassifiedBy = ByPolicy of PolicyRuleId option`
（Explainが「なぜこの分類か」を示すために§4.4で既に定義されていた型）を実際に埋められなかった。
`Resolved of Management * matchedRule: PolicyRuleId option`へ拡張し、`DefaultManagement`への
フォールバックは`None`、ルールが一致した場合はそのルールの`Id`を`Some`で運ぶ。

### 5.5 静的Analyzer
| 対象 | 初期の振る舞い |
|---|---|
| 重複RuleId / 未登録LayoutId / ApiVersion不一致 | Error |
| 比率が0未満または1以上、列数0以下 | Error |
| 既知の述語矛盾、明確な重複 | ErrorまたはWarning |
| 同優先順位の潜在的な競合 | Warning / PotentialOverlap |
| 不到達ルール・Alwaysによる遮蔽 | Warning |
| 利用する属性が実機で取得できない可能性 | EvidenceRequirement |
| 任意F#関数の停止性・副作用 | NotProven |
| `AppIs (Exe, "ApplicationFrameHost.exe")`への直接マッチ（v2.5で追加、OSS調査対応） | Warning（ほぼ常にモデリングの誤り。5.3のS5再構成参照） |
| `WorkspaceAssignments`に同じWorkspaceIdが2回以上現れる（v2.6で追加、R3-1） | Error |


Analyzerは`Error / Warning / Unknown / EvidenceRequirement`を返す。「検査に成功したので全実行が安全」とは扱わない。将来は限定ドメインのSAT/SMT、JournalのSnapshot集合による新旧Policyの差分評価を追加できるが、初期には義務化しない。

**初期スコープの縮小（v2.1で追加、A-7対応）**：個人設定が数十ルール規模である前提では、矛盾検出・到達性検査・SAT/SMTは費用に対して価値が小さい。初期必須はこの表の上2行（重複RuleId/未登録LayoutId/ApiVersion不一致のError、比率・列数のError）のみとし、PotentialOverlap検出・不到達ルール検出・EvidenceRequirement推論は有用なら後から追加する「できればやる」項目に位置づける。

**ルールの静的読み取り集合とincremental再分類（v2.5で追加、OSS調査[oss-wm-survey.md]対応。できればやる項目）**：Hyprlandは各ルールが読む`WindowSnapshot`相当のプロパティをビットマスク（`RULE_PROP_CLASS`/`TITLE`等）として持ち、変化したプロパティとマスクが交差するルールだけを再評価する。Analyzerは同様に、`WindowPredicate`の構造から各`WindowRule`が読む信念の集合（`BeliefKey`の集合、v2.6で`WindowSnapshot`のフィールドから変更。7.1の段⑤で組み立てる`ClassificationView`の中身もこれで決まる、4.5）を静的に計算できる。`MinWidthAtLeast`はDIP同士で比較する（v2.6、R3-5/R4-12。信念がDIPに正規化済み）ので、読み取り集合は`MinWidth`だけで`Monitor`を含まない。`TitleContains`の読み取り集合は`TitleMatches`である。これは2つの実利を持つ：(1) 5.3の「`TitleContains`は常にタイトル取得を強制する」と9.6の「機密フィールドは必要時のみ取得する」という緊張を解消する——有効なルールの読み取り集合に`TitleMatches`が含まれる場合のみShellがタイトルを取得すればよい。(2) A-6の再分類タイミングに原理に基づくトリガーを与える——観測が変化させたフィールドを、実際にどのルールかが読んでいる場合にのみ再分類を行う（変化ごとに全ルールを再評価する、または初回確定時に固定する、という二択を避けられる）。A-7のスコープ縮小方針と同じく、初期必須ではなく「できればやる」に位置づける。
### 5.6 Layout Registryと純粋関数
type LayoutContext = {
    WorkArea: PixelRect
    Dpi: int // v2.6で追加。DIPで指定されたギャップ等をこのモニターのDPIで物理ピクセルへ変換するため（8.3）
    TiledWindows: LayoutWindow list // v2.6でWindowSnapshot listから変更（ADR-016：決定は信念を読む）。順序はWorkspaceのzipperの順（Floatingを除く）
    Focused: WindowId option // v2.6で導出元を明記：このWorkspaceのzipperのFocusがTiledの窓ならSome（R3-3）
}

// v2.6で追加（4.5）。Layoutが読む信念だけを集めた、その場で組み立てる読み取り専用の投影（状態として保存しない）
type LayoutWindow = { Id: WindowId; MinWidthDip: Belief<int> option }

type Placement = { Window: WindowId; Target: PixelRect }

type LayoutError =
    | InvalidInput of string
    | Unsatisfiable of string
    | LayoutCrashed of exnType: string // v2.4で追加（ADR-014/4-2対応）。メッセージは非決定的なので型名のみ記録

type LayoutFunction =
    LayoutArgs -> LayoutContext -> Result<Placement list, LayoutError>

type LayoutImplementation = {
    Id: LayoutId
    ApiVersion: int
    ImplementationDigest: string
    Calculate: LayoutFunction
}
PolicyIRは`LayoutId + ApiVersion + Args`のみを保存し、RegistryがF#関数を解決する。`ImplementationDigest`はビルド成果物と対応し、再現時に同じ実装であるか検証する。異なるアセンブリを同一バージョンとみなさない。

F#の型だけでは`Calculate`の純粋性を証明できない。CoreからI/O依存を排除し、コードレビュー、依存ルール、繰り返し評価テスト、入力固定テストで管理する。自由なユーザーF#コードをHostへ無条件にロードすることは避け、初期は信頼済み拡張のみとする。

**Layout呼出点での例外捕捉（v2.4で追加、ADR-014/4-2対応）**：`Calculate`は「自由なF#製レイアウトアルゴリズム」（1.2）であり、`Result`を返す型であっても内部で0除算や範囲外アクセス等の例外を投げ得る。Registryが`Calculate`を呼ぶ唯一の地点で捕捉し、`LayoutCrashed`へ変換する。
```fsharp
let invokeLayout (impl: LayoutImplementation) (args: LayoutArgs) (ctx: LayoutContext) : Result<Placement list, LayoutError> =
    try impl.Calculate args ctx
    with
    | :? OutOfMemoryException | :? InsufficientExecutionStackException -> reraise () // v2.4追加、R2-26対応。実行環境依存で非決定的なため第3層(InternalFault)へ回す
    | ex -> Error (LayoutCrashed (ex.GetType().FullName))
```
これにより、1つのレイアウト実装のバグで6.3の「前回のDesiredへ」フォールバックに正しく入り、Reducerループ（16.2の「安全な停止」）まで例外が伝播してWM全体が止まることを避ける。同じ入力からは同じ`LayoutCrashed`が再現されるため、決定性（2.2）も保たれる。ただし`OutOfMemoryException`/`InsufficientExecutionStackException`は入力ではなく実行環境に依存し、Replayで同じ結果が再現されるとは限らないため`LayoutCrashed`の対象から除外し、第3層（`InternalFault`、7.1の例外境界原則）へ回す（v2.4追加、R2-26対応）。`StackOverflowException`や無限ループはこの捕捉で防げず、20章の既存リスク「任意F#拡張がHostを止める」に残る。
## 6. 配置計画、検査、制約解決
### 6.1 処理パイプライン
Observation（Window Snapshot等、Journalに残る事実）→ **Belief Revision（各ドメインの修正関数、4.5、v2.6で明示）**
    → Classify（信念の上のPolicy評価）→ Select LayoutRef → Resolve Registry
    → F# LayoutFunction → Placement Proposal → PlanValidator
    → Desired Update → Reconciler（信念を読む）→ Effect IR → **GatedEffect（Shell直前の最終ゲート、6.2参照）** → Windows/Sim Shell
    → Observation → Belief Revision → Reconciliation (again)
（v2.6：v2.5までの「Classify」は生の観測を直接読んでいた。ADR-016により、観測→信念の段と、信念に基づく決定の段に分けた。前者はClassifyだけでなく、Reconciler・フォーカス・ハング・DPIの判断にも共通する段である。4.5参照）
- LayoutFunctionは希望配置の提案を返すだけでOSへは触れない。
- PlanValidatorは配置計算のコードと独立した共通経路に置き、全レイアウト出力を検証する。
- Reconcilerは現在のObservedとPendingを参照し、必要な差分Effectだけを生成する。
- ShellはEffectをOSに適用するが、レイアウトの妥当性判断は担当しない。
- **GatedEffect**（v2.4で追加）は、PlanValidatorとは別の、Shell直前に置く最終防衛線である。6.2末尾を参照。

**`result { }` computation expressionは今は導入しない（v2.4で追加、ADR-014/3-1対応）**：本パイプラインの各段は異なるエラー型（`LayoutError`、`ValidationError list`等）の`Result`を返すため、`FSharp.Core`標準の`Result.bind`/`Result.mapError`の連結で素直に書ける。CEを導入しない理由は「ADR-008/3.2の最小主義に合わせて」ではなく、**CEがこのパイプラインの本当の課題（失敗時のTrace欠落）を解決しないため**である：`Result.bind`は失敗した時点でそれまでの段のTrace（どのRuleを選び、どのLayoutを呼んだか）を捨ててしまい、Explain（9.1）が最も必要とする「失敗時の経路」が残らない。この課題は`result { }`を導入しても自動では解決せず、「各段が`(結果, Trace断片)`を返し、`step`が明示的にTraceを連結する」という小さなヘルパー（Writer相当、10行程度）で対処する。また5.4のClassify（三値`Truth`）はResultに乗らない概念（Unknownはエラーではない正常系の分岐）なので、パイプライン全体を`result { }`で統一することもできない。
### 6.2 PlanValidatorの契約
以下を必須条件として検査する。
1. 配置先WindowIdが現在管理対象かつ生存している。
2. 同一Planに同一WindowIdが重複しない（同じ対象への複数命令を意図的に組み立てる場合は別Effectへ分離）。
3. Width/Height > 0、座標演算で整数オーバーフローがない。
4. モニター/WorkArea参照の整合性がある。
5. 管理除外窓、保護UI、無効になったWindowIdへ移動命令が出ない。
6. そのレイアウトに必要な対象の未配置を検出する（仕様で許す部分配置は別の明示契約を使用）。
重なりを一律に禁止しない：Monocleは意図的に同位置へ重ねる。ギャップ、比率、推定最小サイズ等はソフト制約として扱い、要求を満たせない場合は調整・Floating・Suspended等へ劣化させる。OSの実際の反応は保証しない。

**全違反の蓄積とsmart constructor（v2.4で追加、ADR-014/2-1・2-2対応）**：PlanValidatorはfail-fastで最初の違反だけを返すのではなく、**全ての違反を一度に収集する**。Explain/Journalで「なぜ拒否されたか」を1回の検証で全部提示できることを優先する。

```fsharp
type ValidationInput = {
    Context: LayoutContext                      // WorkAreaを含む（6.2 #4対応）
    Management: Map<WindowId, EffectiveManagement> // #1/#5対応（v2.6でEffectiveManagementに。Unplacedの窓はTiledWindowsに入らない、4.4）
    Alive: Set<WindowId>                          // #1対応
}

type ValidationError =
    | UnmanagedOrDeadWindow of WindowId
    | DuplicateInPlan of WindowId
    | NonPositiveSize of WindowId * width: int * height: int
    | ArithmeticOverflow of WindowId              // #3対応
    | OutsideWorkArea of WindowId * PixelRect     // #4対応（UnknownMonitorより具体的）
    | ExcludedTarget of WindowId                  // #5対応
    | MissingPlacement of WindowId                // #6対応

// private constructor：PlanValidatorの外からは構築できない
type ValidatedPlan = private { Placements: Placement list }
module ValidatedPlan =
    let placements (p: ValidatedPlan) = p.Placements

let validate (input: ValidationInput) (placements: Placement list) : Result<ValidatedPlan, ValidationError list> =
    let errors =
        [ yield! checkManagedAndAlive input placements
          yield! checkNoDuplicates placements
          yield! checkPositiveSize placements
          yield! checkNoOverflow placements
          yield! checkWithinWorkArea input placements
          yield! checkNotExcluded input placements
          yield! checkNoMissingPlacement input placements ]
    match errors with
    | [] -> Ok { Placements = placements }
    | es -> Error es
```

`ValidatedPlan`のprivateコンストラクタが保証するのは「このDesiredはvalidateを1回通過した」という事実**だけ**であり、22章の受け入れ条件4番（「任意のLayout関数出力もPlanValidatorを通過しなければEffectを発行できない」）そのものはこれだけでは成立しない。理由：(a) `Effect`（7.1）は公開DUなので誰でも構築できる、(b) 検証からEffect発行までの間に対象が消滅・Excluded化する鮮度の問題がある、(c) `RestoreBeforeMove`/`RequestForeground`やREPLのIntervene由来のEffectはLayoutを経由しない。受け入れ条件4番を実際に満たす仕組みは、Shell直前の`GatedEffect`（本節末尾）である。`ValidatedPlan`はあくまで「Layout出力からDesiredへ渡す経路」の整合性を保証するものと位置づける。

**「集める」診断と「決める」判断を分離する（v2.4で追加、ADR-014/2-3対応）**：`ValidationError list`は診断のための全量報告であり、どの単位を拒否するかの判断は別の純粋関数`decide`が行う。6.3の「明示された安全な単位」は、**Planから違反窓を引くこと**ではなく、**LayoutContextから違反窓を引いてLayoutを1回だけ再計算すること**と定義する（Master–Stack等の幾何は全窓を前提に計算されるため、Planから1窓だけ引くと隙間の空いた配置になり、#6が防ぐ未配置状態そのものを作ってしまう）。

```fsharp
type Disposition =
    | RejectWholePlan
    | RelayoutWithout of Set<WindowId>  // 検証時点で死亡/Excluded化していた窓を除いて1回だけ再計算する

let decide (errors: ValidationError list) : Disposition =
    let removable =
        errors
        |> List.forall (function UnmanagedOrDeadWindow _ | ExcludedTarget _ -> true | _ -> false)
    let targets =
        errors
        |> List.choose (function UnmanagedOrDeadWindow w | ExcludedTarget w -> Some w | _ -> None)
        |> Set.ofList
    if removable && not (Set.isEmpty targets) then RelayoutWithout targets
    else RejectWholePlan
```

呼び出し元（6.1パイプライン）は`RelayoutWithout targets`を受け取ったら、`LayoutContext.TiledWindows`から`targets`を除いてLayoutFunctionを**1回だけ**再実行し、再度`validate`する。2回目も失敗したら`RejectWholePlan`へ進む（15.1 #7と同系統の「要求の無制限増殖防止」を、再計算1回という形で保証する）。`ValidationError`の中に`NonPositiveSize`/`ArithmeticOverflow`/`OutsideWorkArea`/`MissingPlacement`等（窓の生死に関係しない構造的な問題）が混ざっている場合は再計算の対象にならないため`RejectWholePlan`になる。

**`RelayoutWithout`が発火する状況の位置づけ（v2.4で追加、ラウンド2レビューR2-21で訂正）**：`validate`の入力`ValidationInput`（`Context`/`Management`/`Alive`）は、同じ`step`呼び出しの中で同じ`WmState`から導出される。したがって「`LayoutContext.TiledWindows`に入っているのに死んでいる／Excluded化している窓」は、通常は**導出の不整合（Coreのバグ）でしか起きない**。死亡窓を扱う本来の経路は、OSの`1400`（`TargetGone`→`Cancelled`、7.2）と`WindowRemoved`入力によるWorkspacesのzipperからの除去である。`RelayoutWithout`はユーザー提供のLayout関数が存在しないWindowIdを誤って出力した場合等の防御として残すが、発火したら`DecisionTrace`に「導出不整合の疑い」を記録する。性質ベーステストに「`TiledWindows ⊆ Alive ∩ Managed(Tiled)`が常に成り立つ」を追加する（15.1参照）。

**レイアウト切替の一括適用（v2.1で追加、A-4対応／v2.2でfeasibility.md §4により方針変更／v2.3でGitHub Actions windows-latest実機検証により確定：failure-catalog.md F09-F10。ハング窓を含む3窓を`DeferWindowPos`系で一括適用すると4000ms超ブロックしたが、個別`SetWindowPos`+`SWP_ASYNCWINDOWPOS`では3窓全てが1ms以内にリターンした。以下の既定方針は提案ではなく実機で実証済みの確定事項とする）**：複数窓を個別に`SetWindowPos`すると、適用途中の状態が見える（窓の重なり・ちらつき）。当初`BeginDeferWindowPos`/`DeferWindowPos`/`EndDeferWindowPos`を第一候補としていたが、調査の結果、既存のWindows用タイル型WM（komorebi、GlazeWM）は**いずれも`DeferWindowPos`系を使わず**、各ウィンドウへ個別に`SetWindowPos`を`SWP_ASYNCWINDOWPOS`付きで呼ぶ方式を採用していることが判明した。`SWP_ASYNCWINDOWPOS`は「呼び出し元スレッドと対象ウィンドウ所有スレッドが異なる入力キューに属する場合、要求を対象スレッドへポストし、呼び出し元スレッドをブロックしない」という公式の効果を持ち、ADR-012（ハング耐性）と直接合致する。一方`DeferWindowPos`は公式ドキュメントで「失敗時はバッチ全体を中止すべき」と明記されており、ハング耐性に関する公式な言及がない。**本WMはちらつき抑制よりハング耐性を優先し、Shell側の`ApplyBatch`実装は個別`SetWindowPos` + `SWP_ASYNCWINDOWPOS`を既定とする**（`DeferWindowPos`系はPhase 0のスパイクでちらつきの差を比較した上で、必要なら代替として採用を検討する）。7.1のEffectにこのバッチを表す型（`ApplyBatch`）を用意する。最大化・Aero Snap状態の窓は`SetWindowPos`だけでは意図した通りに動かないため、先に`ShowWindowAsync(SW_RESTORE)`相当のEffect（`RestoreBeforeMove`。v2.4で同期版ではなく非同期版に確定、R2-4対応）を発行してから配置する契約とする。バッチの一部だけが失敗した場合の扱い（残りを適用するか全体を中止するか）はPending追跡（7.2）と合わせて定義する（v2.4で確定：7.1の`EffectBatchReturned`で窓ごとに結果を返すため、「全体を中止」ではなく生きている窓だけ成立させる）。**v2.6でこの方針を`BestEffort`として明記する**：発行段階では、1窓の失敗（ゲートでの拒否、事前確認でのハング検出、OSの拒否）は他の窓の発行を止めない。komorebiは窓ループ内の`?`で最初の失敗以降の窓の配置を中止し、GlazeWMはログして続行する（[oss-wm-survey.md]）が、本WMは後者を採る。前者（発行段階の`AllOrNothing`）はWin32上で実現できない：`SetWindowPos`には取消しが無く、途中で止めても既に発行した窓は戻らないので、「半分だけ適用された」状態を作るだけである。「Planが成立しないなら1つも発行しない」という意味での全体性は、発行前の`validate`/`decide`（本節）が担う。バッチ方針を選ぶ型（`EffectBatch`/`BatchPolicy`）を導入しない理由は3.3参照。

**Shell直前の最終ゲート：GatedEffect（v2.4で追加、ADR-014/2-1対応／ラウンド2レビューR2-5・R2-6・R2-7・R2-8・R2-20で型・状態・例外を具体化）**：`ValidatedPlan`が保証する範囲は「Desiredを作る経路」だけであり、Reconcilerの再送（7.2）、`RestoreBeforeMove`、`RequestForeground`（CoreCommand由来）、REPLのIntervene由来のEffectはPlanValidatorを経由しない。22章の受け入れ条件4番と16.1「全てのOS変更EffectはPlanValidatorや権限ゲートを通る」を実際に満たすのは、**Effectの発行元を問わず、Shellへ渡す直前に通る唯一の安価な最終ゲート**である。gateが保証するのは「Shellへ渡す瞬間の実世界の鮮度」ではなく「**Coreの現在状態（同じstep呼び出し内のWmState）との整合**」であり、実世界側のハング・消滅はShellの事前確認（7.1）とOSの戻り値（`1400`等）が受け持つ。

```fsharp
// Wm.Contracts。コンストラクタはinternalにし、InternalsVisibleToでWm.Coreだけに構築を許す（R2-20対応、option(b)を採用）。
type GateInput = {
    Alive: Set<WindowId>
    Management: Map<WindowId, EffectiveManagement> // v2.6でEffectiveManagementに（Unplacedへの配置のEffectはTargetNotMovableで弾く）
    Monitors: MonitorSnapshot list
    NotMovable: Set<WindowId> // v2.6で追加（ADR-015）。Movabilityの信念がNotMovableの窓（確度を問わない）
    Hung: Set<WindowId>       // v2.6で追加（R4-23）。Responsivenessの信念がHungの窓
    HiddenByWm: Set<WindowId> // v2.6で追加（R4-6）。Desired = HiddenByWm の窓
    NeedsRestore: Set<WindowId> // v2.11で追加（Phase A実装、Opus批判的レビューdocs/review-phase-a-round1.md 2-6対応）。
                                // 最大化/Snap状態でRestoreBeforeMoveが必要な窓（Placementの信念から得る想定。
                                // 生産元のドメインがまだ無いため、Phase Aの最初の縦切りでは常に空集合）
}
// v2.11でRestoreIssuedInBatchフィールドを削除した（Opus批判的レビューdocs/review-phase-a-round1.md 2-7対応）。
// 「同一ApplyBatch内で先行するRestoreBeforeMoveの対象集合」は呼び出し元が渡す入力ではなく、gate自身が
// ApplyBatchごとに空集合から計算し直す値である。外から与えると、全バッチに同じ集合が効いてしまう。

type GateRejection =
    | TargetNotAlive of WindowId
    | TargetExcluded of WindowId
    | NonPositiveRect of WindowId
    | OutsideAnyMonitor of WindowId // R2-7対応：目標矩形がどのモニター矩形とも交差しない
    | MissingRestoreBeforeMove of WindowId // 最大化/Snap状態の窓へSetRectする前にRestoreBeforeMoveが同バッチに無い（R2-8対応。19章のPlanValidator記載はここに統合し削除）
    | TargetNotMovable of WindowId // v2.6で追加（ADR-015）。NotMovableと信じている窓へのSetRect/RestoreBeforeMove。Reconcilerは本来これを生成しない（最終防衛線）
    | TargetBelievedHung of WindowId // v2.6で追加（R4-23）。Hungと信じている窓へのSetRect/RestoreBeforeMove/RequestForeground
    | TargetHiddenByWm of WindowId   // v2.6で追加（R4-6）。WMが隠している窓へのSetRect/RequestForeground

type GatedEffect = internal GatedEffect of Effect
module GatedEffect =
    let effect (g: GatedEffect) = match g with GatedEffect e -> e
    // v2.11でgate自体もinternalにした（Phase A実装、Opus批判的レビューdocs/review-phase-a-round1.md 2-9対応）。
    // gateが公開関数のままだと、internalコンストラクタは実質何も守らない：誰でも偽のGateInput
    // （例：Aliveに全窓を含める）を渡してgateを呼べば、任意のEffectをGatedEffectにできてしまう。
    // 「GateInputを作れるのがCoreだけである」ことに、本節冒頭の「Coreの現在状態との整合」という
    // 約束そのものが依存している。表駆動の実装（§6.2の表そのものをデータとして持つ）の単体テストは、
    // InternalsVisibleToの対象にテストアセンブリも加えて行う。
    let internal gate (input: GateInput) (effects: Effect list) : GatedEffect list * (RequestId option * GateRejection) list =
        ...
```

**Effect種別ごとのゲートの適用範囲（v2.6で追加、独立レビューR4-23）**：

| 拒否理由 | `SetRect`/`RestoreBeforeMove` | `RequestForeground` | `SetPresence` | `ObserveWindow`/`Reprobe`/`ForgetWindow` |
|---|---|---|---|---|
| `TargetNotAlive` | 適用 | 適用 | 適用 | 適用（`ForgetWindow`は除く） |
| `TargetExcluded` | 適用 | 適用 | 適用 | 適用しない（Excludedの窓も信念を持ち続け、再probeする、4.4） |
| `NonPositiveRect`/`OutsideAnyMonitor`/`MissingRestoreBeforeMove` | 適用 | — | — | — |
| `TargetNotMovable` | 適用 | 適用しない | 適用しない | 適用しない |
| `TargetBelievedHung` | 適用 | 適用 | 適用しない（ハング窓も隠す必要があり、`ShowWindowAsync`系で非同期に出す） | 適用しない（`Reprobe`はハング解除の唯一の経路） |
| `TargetHiddenByWm` | 適用 | 適用 | 適用しない | 適用しない |

`RequestClose`（7.1）は`RequestForeground`の列と同じ扱いにする（ただし`TargetBelievedHung`は適用しない。ハングした窓を閉じたいのはユーザーの正当な要求であり、`WM_CLOSE`の送信は非同期で呼出側をブロックしない）。観測系のEffectにほとんどの拒否理由を適用しないのは、ExcludedやSuspendedの窓の再probeを止めると「属性が変われば再分類するために信念を持ち続ける」（4.4）が死文化するからである。

`step`（7.1）は`StepResult.Effects`（`GatedEffect list`）を構築する最後の一手として必ず`GatedEffect.gate`を呼ぶ。**弾かれたRequestIdに対応するPendingは、同じ`step`の中で`Cancelled(GateRejected)`へ遷移させてから`Next`を確定する**（gateは純粋関数なので、この遷移も決定的でReplay可能）。これを明記しない場合、弾かれた`SetRect`のPendingが`Deadline`まで残り、`Timeout`→Retry→同じゲートでの再拒否を`Retry`（`RetryState`）の上限まで繰り返し、7.3の重複抑止により当該窓への新しい意図までブロックされてしまう。15.1 #1/#6の性質ベーステストは、型があっても省略しない（型が保証するのは「どこかでPlanValidatorを通った」ことだけで、「直前に再確認した」ことではないため）。

**例外：8.4の安全停止・復旧経路（v2.4で追加、R2-6対応）**：安全停止時のベストエフォート復旧（8.4）は、Core自体が止まっている／ハングしている可能性を前提に、Hostの監視コンポーネントが`Native`モジュール（8.1）を直接呼ぶ。これは意図的にgateを経由しない（Coreへの依存を持たないことが安全停止の要件だから、22章受け入れ条件4番の例外として明記する）。この経路が呼べる操作は「表示の復元」と「画面内への移動」に限定し、Journalに`SafetyRecoveryAction`として記録する。構成上は、`Native`モジュールをWm.Windows内部（internal）にし、外部から呼べる経路を「`GatedEffect`を受け取る通常のShell」と「8.4の限定された安全停止用API」の2つだけに絞ることで、実装レベルでも迂回が広がらないようにする。
### 6.3 例外とフォールバック
- レイアウトの計算エラー：`LayoutCrashed`等を含め、前回の有効なDesired、または既定の安全レイアウトへ移行（5.6参照）。
- Validatorの失敗（v2.4で具体化）：6.2の`decide`が`RelayoutWithout`を返せば、違反窓を除いて1回だけLayoutを再計算する。それでも失敗、または`RejectWholePlan`であれば前回の有効なDesiredを維持し、診断Journalを残す。
- 属性不明：原則、危険な移動を保留して追加観測を提案。
- 無限に要求が収束しない：同種要求の再送を抑制、Backoff、Floating/Suspendedを選択。
## 7. Functional Coreと実行意味論
### 7.1 Reducer
type Effect =
    | SetRect of RequestId * WindowId * PixelRect
    | RestoreBeforeMove of RequestId * WindowId // v2.4でShowWindowAsync(SW_RESTORE)相当に確定（ADR-014/R2-4対応）。同期版ShowWindowはハング窓でブロックするため使わない。GitHub Actions windows-latest実機検証で実証済み：最大化状態のまま応答停止した窓への同期ShowWindow(SW_RESTORE)は4000ms超ブロックし、ShowWindowAsyncは0msで返った（failure-catalog.md F13）
    | ApplyBatch of BatchId * Effect list // v2.4でRequestId→BatchIdに変更（ADR-014/1-5対応）。子Effectは自分のRequestIdを保ち、結果は窓ごとに返る
    | RequestForeground of RequestId * WindowId
    | ObserveWindow of WindowId
    | ObserveAll
    | SetPresence of RequestId * WindowId * shown: bool // v2.6で追加（R4-6）。WMによる窓の非表示／再表示。非表示の実装方式（隠す／最小化／画面外／Cloak、21章）はShellが選ぶ。Coreは方式に依存しない
    | RequestClose of RequestId * WindowId // v2.6で明記（R6-2）。CloseFocusedの実体。WM_CLOSEの送信であり、アプリが拒否できる丁寧な要求（8.4の「破壊・終了するEffectを提供しない」の範囲内）
    | ForgetWindow of WindowId // v2.6で追加（R4-14）。ShellのHWND→WindowId対応表から外させる（HWND再利用を検知したとき、次の列挙で新しいWindowIdとして再発見させる）
    | Reprobe of WindowId * BeliefKey // v2.6で追加（ADR-016）。特定の信念の根拠となる属性だけを問い合わせ直す（Responsivenessなら SendMessageTimeout(WM_NULL)、TargetIntegrityならプロセストークン）。結果は部分的なWindowObservedとして戻る
    // v2.6でArmTimer/CancelTimerを削除（ADR-017）。タイマーはOSの窓に触れないのでEffect（GatedEffectの対象）ではなく、
    // StepResult.Timersで返すTimerCommand<TimerId>（3.3）になった。タグ"arm-timer"/"cancel-timer"は予約済み・再利用禁止（9.2）

// ===== エラー表現（v2.4で追加、ADR-014対応） =====
// Wm.Contracts：OS非依存。Wm.Simulationも同じ形を作れる（ADR-002：同じCoreをShell/Simulationが実行）。
type ErrorSpace = Win32LastError | HResult | BoolFalseNoCode
type NativeCallRecord = { Api: string; Space: ErrorSpace; Code: int; ElapsedUs: int64 } // ElapsedUsはShellが実測する。常に0を入れない（v2.4追加、R2-22対応）

// Coreが分岐する反応の軸で切る（1-8対応：原因ベースの早すぎる確定を避ける）。
// 原因の多様性はNativeCallRecord（生の事実）側で持つ。
type DenialKind = ForegroundDenied | OtherDenial // v2.5でTargetIntegrityHigherを削除（ADR-015対応、タグ"target-integrity-higher"は予約済み・再利用禁止、9.2）。整合性レベルは原因分類ではなく、4.5のMovabilityの信念の事前証拠にする。残り2種で開始し、Phase 0の失敗カタログで増える分だけclassifyの対応表に追加する
type EffectFailure =
    | TargetGone           // → Cancelled
    | TargetHung           // → Cancelled + Responsivenessの信念をHungへ（→配置対象外。Respondingへ修正されたら復帰、v2.6）
    | Denied of DenialKind // OtherDenial → Cancelled + Movabilityの信念をNotMovableへ（→Coreの組込み規則でUnplaced、4.4）／ForegroundDenied → Cancelled のみ（v2.6で分離、7.2）
    | Transient            // → Retry/Backoff
    | Unclassified         // → Retry上限付き、診断で要調査フラグ（分類漏れを数えられるようにする。11.1のEvidenceGapと同じ考え方）

// v2.4で版管理の置き場所を修正（ADR-014/R2-15対応）：classifyはCoreのstep内で呼ぶ（入力のNativeCallRecordは生の記録のままInputEventを運ぶため）。
// ADR-013のNormalizer（Shell側でRaw→InputEventに変換する層）とは呼び出し位置が異なり、版はCoreBuildIdでピン留めされる（normalizerSchemaVersionとは無関係）。
// Journalには結果（Result<unit, NativeCallRecord>）だけを永続化し、EffectFailureはReplay時にclassifyで再計算する
// （ADR-004「再計算できるものは永続化しない」と整合。X-3参照）。対応表は(Api, Space, Code)の3つ組をキーにし（8.1参照、R2-1対応）、
// 一部のエントリはElapsedUsも読む。守るべき不変条件は「NativeCallRecordだけを読み、窓の状態・信念を読まない純粋関数」である（v2.6でADR-015の誤った言い換えを訂正、R3-7）。
// 窓の状態を加味した解釈（整合性レベルが高いから拒否された可能性が高い、等）はclassifyではなく4.5の信念とExplainが行う。
val classify: NativeCallRecord -> EffectFailure

type InputEvent =
    | WindowObserved of WindowSnapshot
    | WindowRemoved of WindowId
    | ForegroundObserved of Observation<WindowId option> // v2.6でObservationに変更（R4-10）。Focusドメインの`Foreground`の信念を更新する
    | UserCommand of CoreCommand
    | EffectCallReturned of RequestId * Result<unit, NativeCallRecord> // v2.4でEffectRejectedを置き換え（1-4対応）。Okは「受付成功」(ADR-003の受付)を意味し「実現」ではない。EffectSubmittedは廃止（R2-17対応、同じ事実の二重表現だった）
    | EffectBatchReturned of BatchId * (RequestId * Result<unit, NativeCallRecord>) list // v2.4で追加（1-5対応）。ApplyBatchは窓ごとに結果を返す
    | InternalFault of component: string // v2.4で追加（ADR-014/4-3対応）。想定外の例外（プログラムのバグ）をスレッド単位の最上位ハンドラが捕捉した
    | JournalDegraded of since: int64 // v2.4で追加（ADR-014/3-4・R2-16対応）。Sequence。この入力自体はJournalへ書けない状況で発生するため、Replayはこの区間をまたげない（9.5参照）
    | JournalRecovered of gapStart: int64 * gapEnd: int64 // v2.4で追加（R2-16対応）
    | MoveSizeStarted of WindowId // ユーザーのドラッグ/リサイズ開始。自動整列を抑止する
    | MoveSizeEnded of WindowId * Observation<PixelRect> * Observation<MonitorId> // v2.6でドロップ時点の矩形とモニターを運ぶ形に変更（R4-7、4.4）
    | VisibilityChanged of WindowId * Observation<bool> // v2.6でboolからObservationに変更（R4-10）。信念の新旧比較にSampleSequenceを使う。以下のイベント型入力も同じ
    | MinimizedStateChanged of WindowId * Observation<bool>
    | CloakChanged of WindowId * Observation<CloakState> // v2.6でboolからCloakStateの観測に変更（R3-8/R4-10）
    | StyleChanged of WindowId
    | TimerElapsed of TimerId // v2.6でint64からTimerId（3.3）に変更。古い発火かどうかはCore側の待ち状態のDeadlineで判定する
    | MonitorTopologyObserved of MonitorSnapshot list
    | ScreenReconfigurationInProgress of bool // スリープ復帰/モニタ抜き差し/RDP接続中は再配置を抑止する（20章E-3）
    | PolicyActivated of string
    | MembershipHintsLoaded of (WindowId * WorkspaceId * Management option) list // v2.6で追加（R4-3）。稼働中の再起動時、Shellが解決した所属ヒント（4.5）

type StepResult = {
    Next: WmState
    Effects: GatedEffect list // v2.4でEffect list→GatedEffect listに変更（ADR-014/2-1・R2-5対応）。6.2参照
    Timers: TimerCommand<TimerId> list // v2.6で追加（ADR-017、3.3）。ゲートを通さない。Replayで再計算し永続化しない
    Trace: DecisionTrace list
}

// 概念上の契約。WmState/CoreCommand等は対応モジュールで定義する。
val step: RuntimePolicy -> WmState -> EventEnvelope<InputEvent> -> StepResult
**実装状況（v2.11で追記、Opus批判的レビューdocs/review-phase-a-round2.md 5-3対応／v2.15で更新）**：Phase Aの最初の4ドメイン（Topology/Workspaces/Health/Focus）の時点では、Windows/Placementの段③〜⑤⑦に配線先が無いため、`WmState.step`はこの表のとおりの8段パイプラインとして組んでいなかった（各ドメインの入力分岐を素直に並べた形）。Core.Windows着手時（v2.15）に、段①⑤相当（`WindowObserved`/`VisibilityChanged`/`CloakChanged`からの信念反映と分類の確定、`ClassificationDeadline`タイマー、`ToggleFloating`/`SetManagement`/`ClearUserOverride`）を同じ入力について続けて処理し、出た`WindowClassified`/`WindowUnmanaged`をその場でWorkspaces/Focusへ反映する形で配線したが、これは「今実装済みのドメインが処理できる入力の分だけ素直に並べる」という既存の方針の延長であり、表どおりの汎用8段パイプライン（各段が`DomainEvent`を累積して次段へ渡す構造）への完全な組み直しではない。Placementが無い間は段③④⑦の配線先が依然として無く（`NotMovable`/`HiddenByWm`/`NeedsRestore`は空集合のまま）、`MinimizedStateChanged`（Placementの`GeometryBeliefs.IsMinimized`が所轄）と`StyleChanged`（信念を運ばない通知のみで、どの段がどう使うか本書に明記が無い）も未配線である。表どおりの8段パイプラインへの完全な組み直しは、Placement着手時まで持ち越す。

**1 `step`の処理順序とドメイン間のデータの流れ（v2.6で規範として固定、独立レビューR4-1／R6-1・R6-2で全面改訂）**：1つの入力に対し、`step`は次の段を順に実行する。1つのドメインが複数の段で働くことはある（Placementは③④⑦）が、**どのドメインも自分の状態だけを返し、他ドメインの状態を書かない**（4.4の`update`の型。17.1のアーキテクチャテストで検査）。ドメインが読めるのは、(a) この段より前に処理されたドメインの**今回の**状態、(b) 任意のドメインの**step開始時点の**状態、(c) 前段までに出された`DomainEvent`、の3つだけである。これにより、データの流れは段の順に一方向になり、循環は型の上で書けない。各段の`Response`（3.3：Effectとタイマー指令のデータ）はこの順で連結する。

| 段 | ドメイン | 読むもの（Ctx） | 受けるDomainEvent／入力 | 自分の状態に行うこと | 出すDomainEvent |
|---|---|---|---|---|---|
| ① 観測の信念反映 | Windows→Placement→Health→Focus | なし | 観測を運ぶ入力（`WindowObserved`、属性変化イベント、`ForegroundObserved`、`MoveSizeEnded`のペイロード） | 各ドメインの修正関数で自分の信念を修正（4.5）。**全入力について無条件に最初に行う**（抑止区間・grace中の観測も信念には届く） | Focus：`Foreground`の値が変わったら`ForegroundChanged w`。Windows：Stickyの`Process`の不一致を検知したら`WindowUnmanaged w`（とEffect`ForgetWindow w`、4.5） |
| ② 原因・コマンドの受付 | Topology→Focus | step開始時のWorkspaces（窓の所属） | 移動・最小化の観測、`ScreenReconfigurationInProgress`、`MonitorTopologyObserved`、Topologyのタイマー、任意の`UserCommand`（`Reconfiguring`の解除条件(b)、7.3）、`FocusMostRecent`、Focusのタイマー（`WorkspaceSwitchSettle`） | Topology：`ScreenPhase`の遷移と、移動の観測への原因ラベル（`MoveCause`）付け。Focus：`FocusMostRecent`の行き先を`History`から解決。`SwitchSettle`の期限満了時、フォアグラウンドがまだhiddenなWSの窓なら、保持していたフォーカスを渡す窓を1回だけ要求し直す（4.4） | `MoveAttributed (w, cause)`、`TopologySettled snapshots`、`FocusTargetRequested w`（`FocusMostRecent`由来、`SwitchSettle`の満了由来） |
| ③ Pendingの照合 | Placement | ①の信念 | `EffectCallReturned`/`EffectBatchReturned`（`classify`）、`PendingDeadline`のタイマー、`TopologySettled` | `VisibleFrameRect`の信念と目標矩形（`SetPresence`なら`ObservedPresence`）を比べて`Satisfied`/`Mismatch`/失敗を決める。`TopologySettled`なら全Pendingを`Superseded`にする（7.2） | `PendingResolved (rid, outcome)` |
| ④ 照合結果・ユーザー操作の信念反映 | Placement→Health | ③の結果 | `PendingResolved`、`ToggleFloating`/`SetManagement`（`Movability`の試し直し、R6-2）、Health/Placementの再probeのタイマー | Placement：`MinWidth`/`Movability`を修正。Health：`Responsiveness`を修正 | — |
| ⑤ 管理状態と表示の決定 | Windows→Topology→Workspaces | ①④の信念（`ClassificationView`）、②の`ScreenPhase` | `ToggleFloating`/`SetManagement`/`ClearUserOverride`、`Classification`のタイマー、`SwitchWorkspace`/`FocusMonitor`、`ForegroundChanged`、`FocusTargetRequested`、`MoveAttributed`、`TopologySettled`、`MoveFocusedToWorkspace`/`FocusNext`/`FocusPrevious`/`SwapWithMaster`/`PromoteToMaster`/`ChangeLayout` | Windows：分類（5.4）、ユーザー指定、`Unplaced`への読み替え。Topology：`view`（`SwitchWorkspace`、hiddenなWSの窓の`ForegroundChanged`、行き先がhiddenな`FocusTargetRequested`）、`FocusMonitor`、`ForegroundChanged`による`current`の追従（R5-1）、`MonitorLifecycle.reconcile`、`ReturnMonitor`の記録・解除（R6-1）。Workspaces：所属の追加・除去・移動（分類、`UserDrag`）、zipperの回転・並べ替え（コマンド）、`ChangeLayout`の記録 | Windows：`WindowClassified (w, mgmt, home)`／`WindowUnmanaged w`。Topology：`WorkspaceShown (ws, ShownBy)`／`WorkspaceHidden ws`、`CurrentChanged ws`（`FocusMonitor`や表示中のWSへの`SwitchWorkspace`で`current`だけが変わったとき）。Workspaces：`FocusTargetRequested w`（zipperの回転、`ShownBy = UserView`で表示されたWSのzipperの`Focus`（同じstepで②から明示的な行き先`FocusTargetRequested w`がそのWSについて届いていれば、先にzipperの`Focus`を`w`へ移すので、要求は`w`になる）、`CurrentChanged`のWSのzipperの`Focus`、移動・除去後の隣の窓）、`CloseRequested w` |
| ⑥ フォーカスの更新 | Focus→Workspaces | 今回のWindows/Topology/Workspaces（`focused`の導出）、step開始時の`focused` | ⑤までの全DomainEvent | Focus：`WindowUnmanaged w`の`w`を`History`から除く（(X3)）。`focused`の値がstep開始時から変わり`ManagedWindow w`になったら`History`の先頭へ`w`（R6-3）。`ShownBy = UserView`の`WorkspaceShown`なら`SwitchSettle`を開始。Workspaces：`FocusObserved w`を受けてzipperの`Focus`を`w`へ | Focus：`FocusObserved w` |
| ⑦ Layout・Reconcile | Placement | 今回の全ドメイン、`ScreenPhase` | `WorkspaceShown`/`WorkspaceHidden`、`WindowClassified`/`WindowUnmanaged`、`FocusTargetRequested`、`CloseRequested` | 可視WSごとにLayout→`validate`/`decide`（6.2）→`Desired`の更新、差分Effectの生成（表示状態は`Desired`と`ObservedPresence`の組で決める状態ベースの規則、4.4、R7-2）。`ScreenPhase`が`Reconfiguring`/`Settling`の間は差分を作らない（R6-8）。同じstepに`TopologySettled`があれば、`HeldFocusTarget`をここで1回だけ`RequestForeground`として出す（R7-3）。`RequestForeground`は全経路の`FocusTargetRequested`のうち最後の1件だけから、ここで一元的に作る（R6-2）。同じ窓の`SetPresence(shown=true)`を`RequestForeground`より先に並べる（R5-7） | — |
| ⑧ ゲート | （親） | ⑦までで確定した次の状態 | ⑦のEffect | `GateInput`を次の状態から作り（R5-7）、`GatedEffect.gate`（6.2）を通す。弾かれたPendingを`Cancelled(GateRejected)`に、抑止区間・モニター0台の間の配置のEffectを保留して`Superseded`にする（最終防衛線、R4-15/R5-5） | — |

補足：
- ⑤の内側を「Windows→Topology→Workspaces」の順にするのは、Topologyの`view`がWindowsの分類結果を、Workspacesの所属の更新がTopologyの表示結果（`UserDrag`で落とされたモニターに表示中のWS）を読むからである。Topologyが窓の所属を知る必要がある場面（`ForegroundChanged w`の`w`がどのWSか）では、step開始時のWorkspacesを読む。そのstepで初めて分類された窓はstep開始時には所属を持たないので、その窓へのフォアグラウンドの変化ではモニターを追従しない（追従が必要なのは既に管理下にある別モニターの窓をクリックした場合であり、新しい窓は常にアクティブなWSに入る）。
- ⑥で`focused`の**導出値の変化**を見るのは、新しく開いた窓のように「フォアグラウンドになったstepでは未分類で、分類されたstepではフォアグラウンドの値が変わらない」窓でも、zipperと`History`を正しく更新するためである（R6-3）。`current`の追従だけは⑤で`ForegroundChanged`（フォアグラウンドの値の変化）に限るので、`FocusMonitor`や別モニターへの`SwitchWorkspace`が同じstepで打ち消されることは無い（R5-1）。
- 抑止区間（`Reconfiguring`/`Settling`）の間に保留した`FocusTargetRequested`は、Placementが最後の1件だけを`HeldFocusTarget`に保持し、`TopologySettled`が出た同じstepの段⑦で1回だけ`RequestForeground`として出し直す（後続の入力を待たない、R7-3）（settle中に`SwitchWorkspace`したユーザーにフォーカスが渡らない、という穴を塞ぐ、R6-8）。
- `CloseFocused`はWorkspacesが`CloseRequested w`を出し、Placementが`RequestClose`（`WM_CLOSE`の送信。アプリが拒否できる丁寧な要求であり、8.4の「窓を破壊・終了するEffectを提供しない」には反しない）として出す。

この順序が守られないと、例えば③を①より先に行うと、今回届いた観測を反映する前の古い矩形で照合して誤った`Mismatch`を出し、それが④で`MinWidth`の誤った証拠になる。15.1 #22で検査する。

**Core内では例外を制御フローに使わない（v2.6で明記）**：各ドメインの更新関数・信念の修正関数・`MonitorLifecycle.reconcile`・タイマーの照合はすべて全域関数として書く。起こり得ない組合せ（例：Workspacesに無い窓への`DomainEvent`）に出会ったら、例外を投げずに入力を捨て、`DecisionTrace`に「導出不整合の疑い」を記録する（6.2の`RelayoutWithout`と同じ扱い）。失敗が意味を持つ箇所（`validate`、`PinnedPolicy.ofStored`等）は`Result`を返す。ADR-014の第3層（想定外の例外は最上位ハンドラで捕捉し安全停止）は、Coreのバグに対する最後の網であって、Coreの中で`try`/`with`を分岐に使うことを許すものではない。v2.5の`Screens.[ActiveMonitor]`（インデクサが`KeyNotFoundException`を投げる）は、まさにこの規則に反する書き方だった（R3-2）。

EventEnvelopeにはReducerへ投入したSequence、論理/単調時刻、入力の発生元、観測元時刻、SessionId等を含める。時刻、乱数、ID採番は状態やイベントの入力として明示し、Core内からOS時計を呼ばない。コレクションの暗黙の列挙順やバックグラウンドスレッドの状態に依存しない。.NET Core以降`string.GetHashCode`はプロセスごとにランダム化されるため、`HashSet<string>`/`Dictionary<string,_>`の列挙順にCoreの判断が依存するとReplayでプロセス間の再現性が崩れる。F#の`Map`/`Set`（順序付き）を用いるか、明示的にソートする。

**実行スレッドモデルと耐ハング性（v2.1で追加、ADR-012対応、E-1対応／v2.2でfeasibility.md §5により補強／v2.3でGitHub Actions windows-latest実機検証により実証：failure-catalog.md F05-F08。メッセージポンプを止めた窓への`SetWindowPos`は`SWP_ASYNCWINDOWPOS`なしで4000ms超ブロックし、付きでは0msで返った。`IsHungAppWindow`は6秒後に正しく`true`を返し、`SendMessageTimeout(SMTO_ABORTIFHUNG)`はハング窓に対し即時[0ms]に中断した／v2.4でADR-014により実測値に厳密に整合させた）**：`SetWindowPos`等は対象窓を所有するスレッドへメッセージを送るため、対象アプリがハングしていると呼び出し側がブロックされる（公式ドキュメントに「`SetWindowPos`が内部で同期送信する」という直接の一文はないが、`SWP_ASYNCWINDOWPOS`フラグの説明「異なる入力キューに属する場合は要求をポストしブロックしない」が既定動作でのブロックを前提にしており、これを根拠とする）。Effect適用（Windows Shell側の実装）はWinEventコールバックや安全停止の制御経路（8.4）と同じスレッドで行わない。専用のEffect適用スレッドを設け、既定で各`SetWindowPos`に`SWP_ASYNCWINDOWPOS`を付ける（6.2参照）。

**ハング事前確認は毎回行うが、タイムアウト値はレイテンシ予算から決める（v2.4で確定、ラウンド2レビューR2-1/R2-2対応で修正／実機検証でタイムアウト値を確定：failure-catalog.md F12）**：`SWP_ASYNCWINDOWPOS`が既定である以上、`SetRect`の呼出自体はハング窓に対しても即時に返る（F08/F10実証）ため、**「タイムアウトした」という事象はSetRectからは発生しない**。代わりにShellは`SetRect`を発行する**前**に毎回`SendMessageTimeout(WM_NULL, SMTO_ABORTIFHUNG, timeoutMs)`で対象を事前確認する。F06が実証したのは「OSが既にハング判定済み（メッセージポンプ停止から5秒以上）の窓」への即時中断（0ms）だけであり、**判定前（停止から5秒未満）の窓は指定したタイムアウトの満了まで待つ**。GitHub Actions windows-latest実機検証（F12）で、ハング判定前の窓は`timeoutMs`に10/50/100/500msを指定したいずれでも実測`ElapsedUs`がほぼ要求値どおり（9/57/99/499ms）だったのに対し、判定後の窓は全て`0ms`で返った。「毎回確認してもコスト無視できる」はハング判定済みの窓にしか成り立たないため、`timeoutMs`は**10ms**に確定する（2.2のレイテンシ予算「数十ms台」に対し、判定前の窓1つあたりの最悪コストを10msに抑えられる。応答が遅いだけで実際にはハングしていない窓は、10msで打ち切られても`Transient`としてRetryに回るだけであり、誤ってSuspendされるわけではないので安全側に働く）。戻り値が0で`ElapsedUs`が`timeoutMs`（10ms）に近ければ「判定前」（`classify`で`Transient`、次の操作列でRetryに任せる）、`ElapsedUs`が0に近ければ「判定済み」（`TargetHung`）と区別する。`classify`の対応表（8.1）は`(Api, Space, Code)`の3つ組をキーにし、`(SendMessageTimeout, Win32LastError, 1460/* ERROR_TIMEOUT */)`を`TargetHung`または`ElapsedUs`に応じて`Transient`に分類する初期エントリを持つ。事前確認はバッチ内の窓ごとに並列に行い、1窓の確認待ちが他の生きている窓の`SetRect`発行を遅らせないこと（15.1 #13の具体例）。事前確認後にハングし始めたウィンドウ（TOCTOU）は、1回のRetryを経て次回の事前確認で`TargetHung`に収束することを許容する（7.2に注記）。`IsHungAppWindow`は公式ドキュメントが「一般利用を意図していない。将来のWindowsバージョンで変更・廃止される可能性がある」と明記しているため、補助的な事前チェックとしてのみ使い、主たるハング検知手段にはしない。**v2.6で、事前確認と`IsHungAppWindow`の結果は「ハングしているか」の判断そのものではなく、4.5の`Responsiveness`の信念を修正する証拠（`ResponsivenessSample`）として扱う（ADR-016）**。これにより、一度`TargetHung`で止めた窓も、後の再確認で応答すれば`Responding`へ修正され、Suspendが解除される。

**Shell/Simulationで同じ手順を共有する（v2.4で追加、R2-3対応）**：「事前確認→ASYNC呼出」という手順そのものをContractsの純粋関数として書き、Windows版とSimulation版は確認手段（`probe`）だけを差し替える。（v2.6、3.4：差し替えの具体的な手段はShell側の環境インターフェース`IWin32Ops`であり、Windows版は実装クラス、Simulation版はオブジェクト式で作る。下の関数引数の形は、その最小の例である。）
```fsharp
type ProbeOutcome = Responsive | HungConfirmed | HungPending of waitedMs: int
val executeEffectProtocol: (probe: WindowId -> ProbeOutcome) -> (dispatch: unit -> Result<unit, NativeCallRecord>) -> WindowId -> Result<unit, NativeCallRecord>
```
これによりADR-002（同じCore）に加えて「同じShell手順」も保証され、実装者がSimulation側で独自の簡略版を書いて実機と再び乖離する（前回ラウンドの1-3の根本原因）ことを防ぐ。Simulationは12.1の`WindowBehavior`に`HungAfterMs of startMs: int`（startMs以降はメッセージを処理せず、start+5000ms以降はOSのハング判定が成立する）を追加し、`probe`の実装をこのモデルから導出する。

**`SWP_ASYNCWINDOWPOS`が使えない呼出（v2.4で見直し、R2-4対応）**：事前確認（`SendMessageTimeout`）自体がタイムアウト付きで必ず戻るAPIであるため、初期スコープで「専用スレッド上でハード期限を越えて戻ってこない呼出」のリストは**空である**（`RestoreBeforeMove`は`ShowWindowAsync`化済みで`callBool`に乗る。観測系の失敗は4.2の`Observation<'T>.Value = Error`で表し、`ObserveWindow`にRequestIdが無い以上、Effect結果としての表現は作らない）。将来、本当に同期APIを避けられないEffectが見つかった場合は、そのEffectを明示的にこのリストへ追加した上で、「ハード期限を越えたらそのEffect適用スレッドを捨てて新しく立て直す（古いスレッドのリークを許容する）」手順を適用する。戻ってこないスレッドは中断できないため、タイムアウトを表す新しいInputEvent型は追加しない（当該Pendingは通常の`Timeout`で扱う）。WinEventフックのコールバック自体は、登録したスレッドのメッセージポンプ中に同期的に再入され得るため（24章「Guarding Against Reentrancy」参照）、コールバックからOS呼出APIを直接呼ばず、最小限の情報をキューへ渡すだけに留める（8.1と同じ方針をCore〜Shell間のスレッド境界として明文化する）。

**例外とResultの境界原則（v2.4で追加、ADR-014/4-3対応）**：3層に分ける。
1. **予期された外部の失敗**（エラーコード・拒否・消滅・ハング）→ 境界で`Result`/`NativeCallRecord`に変換する（8.1の`Native`モジュール参照）。
2. **ユーザー提供コードの失敗**（Layout関数、5.6）→ 呼出点で捕捉し`LayoutError`へ変換する。
3. **それ以外の例外**（自分たちのバグ・不変条件違反）→ 変換せず、**スレッド単位の最上位ハンドラ**（Reducerループ、Effect適用スレッド、JournalWriter、IPCハンドラ、8.4の監視コンポーネント）で捕捉し、`InternalFault of component`として安全停止経路（8.4）へ渡す。Effectスレッドが落ちた場合はPendingを即座に全件`Cancelled`扱いにする。

ここで原則を誤ると、2方向のまずさが起きる：境界で捕まえすぎると（`NullReferenceException`等まで`Result`に変換すると）バグがEffect失敗として7.2の`Retry/Backoff`に吸収され、Journal上は「アプリが拒否した」ように見えて13章のような調査を誤らせる。逆に何も捕まえないと、Effect適用スレッドが死んだままCoreがそれに気づけない。

Reducerループは「**入力をJournalに書いてから`step`を呼ぶ**」順序を固定する。これにより`step`実行中に例外を起こした入力（poison input）が必ずJournalに残り、Replayで再現できる（9.4「Input accepted → Core decision」の順序の具体化）。
### 7.2 Desired / Observed / Pending
| 状態 | 意味 | 注意 |
|---|---|---|
| Desired | 最新Policyと入力から意図した値 | まだ実現していない可能性 |
| Observed | 取得時刻・取得元を伴う観測値 | 古い可能性がある。観測の欠落もある |
| Pending | 発行済み要求と期限・試行回数・IntentEpoch | OS APIが戻っただけでは完了しない |


PendingRequestは最低限、RequestId/WindowId/IntentEpoch/TargetRect/IssuedAt/Deadline/Retry（3.3の`RetryState`。v2.6でAttemptsを置き換え）/Statusを持つ。PendingRequestは3.3の「待ちをデータで表す」規約の代表例であり、`Deadline`を書いたときに`StartTimer (PendingDeadline rid, …)`を返し、`Satisfied`/`Cancelled`/`Superseded`へ遷移したときに`CancelTimer`を返す（v2.6、ADR-017）。「Observed」は4.5の`VisibleFrameRect`の信念を指す。対象が消えた、Policyが変更された、同じ窓へ新しい意図が生じた場合、古い要求を失効させる。
Idle → Planned → Dispatched → AwaitingObservation
                             ├─ Observed matches        → Satisfied
                             ├─ Mismatch                 → Reevaluate
                             ├─ Timeout                  → Retry/Backoff、上限後は配置対象外（7.3の振動・収束不能の劣化。観測が来ないまま期限切れ。呼出自体は成功している。事前確認後にハングし始めた窓のTOCTOUもここを1回だけ通る、v2.4で明記）
                             ├─ EffectCallReturned(Error) → classify の結果で分岐（v2.4でEffectRejectedを置き換え）
                             │     ├─ TargetGone          → Cancelled
                             │     ├─ TargetHung          → Cancelled ＋ Responsiveness:=Hung → 配置対象外（枠は残す。再試行しない。Respondingへ修正されたら復帰、v2.6）
                             │     ├─ Denied OtherDenial  → Cancelled ＋ Movability:=NotMovable → Coreの組込み規則でUnplacedへ（どのWorkspaceからも外す、v2.6）
                             │     ├─ Denied ForegroundDenied → Cancelled のみ（管理状態は変えない、v2.6）
                             │     ├─ Transient           → Retry/Backoff（事前確認がハング判定前でタイムアウト満了した場合もここに入る、v2.4で追加）
                             │     └─ Unclassified        → Retry上限付き、要調査フラグ
                             ├─ トポロジ確定（ScreenSettle満了）→ Superseded（モニター構成が変化した場合は古いPendingの目標矩形が無効になる可能性があるため、全体Snapshotを取り直すまで全件を失効させる。v2.4で追加、R2-7対応。v2.6で契機を生の`MonitorTopologyObserved`から確定時点へ変更、R4-15/R5-5：抑止区間中は段⑦が差分を作らず、それ以外の経路で生まれた配置のEffectも段⑧のゲートで保留され`Superseded`になる）
                             ├─ GateRejected              → Cancelled（6.2のGatedEffect.gateに弾かれた。v2.4で追加、R2-5対応。Deadlineまで待たず同じstep内で即時に遷移する）
                             ├─ Window destroyed         → Cancelled
                             └─ Superseded intent        → Superseded
（この図はPendingの遷移である。窓の管理状態の遷移——ハング・振動による配置対象からの除外と解除、`NotMovable`による`Unplaced`への読み替え、ユーザー操作による`Movability`の試し直し——は4.4の各ドメインの状態と4.5の規則で決まり、7.1の処理順序の段⑤で行う。v2.6、R4-25。`SetPresence`のPendingは`TargetRect`ではなく、Shellが報告する`ObservedPresence`の信念（4.5）と照合する：`shown = false`なら`HiddenByWmMethod`、`true`なら`VisibleToUser`の観測で`Satisfied`。R5-2）

SetRectのネイティブAPI呼び出しが成功しても、目標矩形を観測するまでは`Satisfied`にしない。WinEvent通知そのものを特定Requestへの応答と断定せず、窓ID・epoch・観測の鮮度・矩形条件を照合する。最新Observedの選択では古い取得結果が新しいSnapshotを上書きしないよう、ソース・取得順・再観測の品質を考慮する。`EffectCallAbandoned`は7.1で見送った（初期スコープで対象が存在しないため）。

`IntentEpoch`は窓単位で発行する（グローバル単一値にはしない）。全体で単一のEpochにすると、1窓に新しい意図が生じただけで他の全窓のPendingが失効してしまう。

`RequestForeground`はAPI呼出としては概ね冪等だが、副作用（フォーカスを奪う）は大きい。9.4のクラッシュ後再送方針（冪等性が低いEffectは再送しない）の対象に、副作用の大きいEffectとして`RequestForeground`も明示的に含める。

**失敗への反応は信念の修正を経由する（v2.6で変更、ADR-016）**：v2.5までは`Denied`をDenialKindに関わらず「Suspend/Floating」へ直結していたため、`RequestForeground`が`ForegroundDenied`で拒否されただけで、その窓の配置管理まで止まってしまう経路があった。v2.6では、`EffectFailure`はPendingを終わらせる（`Cancelled`）ことと、該当する信念を修正すること（`TargetHung`→`Responsiveness`、`Denied OtherDenial`→`Movability`）だけを行い、管理状態の変化（Suspend、Floating）はその信念から決定の段が導く。`ForegroundDenied`はどの信念も修正しない（フォーカスの拒否は窓の性質ではなく、OSのフォアグラウンド制御の都合だからである）。Placementドメインは、`Movability = NotMovable`の窓には`SetRect`/`SetPresence`を出さない（その窓は`Unplaced`としてどのWorkspaceにも属さない、4.4）、`Responsiveness = Hung`の窓には`SetRect`を出さない（Healthの信念を読む）、という2つの規則を信念から読む。ゲート（6.2）は同じ2つを最終防衛線として再確認する。原因の多様性を`NativeCallRecord`に、Coreの反応を`EffectFailure`に、窓についての持続的な推定を信念に、という3つの置き場所が分かれたことで、ADR-014の「反応の軸で切る」方針がより明確になる。
### 7.3 収束と振動防止
- 未完了の同一Targetへの重複要求を発行しない。
- 期限切れと再試行を、Core上の明示された状態遷移とTimerイベントで表現する（v2.6で3.3の`TimerCommand`/`TimerId`に統一。ADR-017）。
- 最大試行数、Backoff、安定判定の許容差、ユーザー操作中の抑止をPolicyまたは既定設定として持つ。
- 操作直後に窓が元へ戻るような往復運動は振動として検知し、制御を弱める。振動の検知（一定時間内の往復回数）は、入力の時刻を状態に記録して比較すれば足りるので、タイマーを使わない（v2.6、3.3「何でもタイマーにしない」）。
- 定期的な全体Snapshotでイベント欠落やジャーナル復旧後の状態差を解消する。
収束保証は「対象アプリが移動を受け入れる」「外乱が収まる」等の前提付き。外界が無制限に再配置する場合は、成功ではなく安全な諦め方を定義する。

**画面構成変化中の抑止（v2.1で追加、E-3対応）**：スリープ復帰、モニター抜き差し、RDP接続/切断時はOS自身が複数の窓を一斉に移動させ、WorkAreaとDPIも変わる。この間に自動整列を行うと外乱と衝突し、振動検知が全窓に発動して収束不能になる。`ScreenReconfigurationInProgress`イベント（7.1）がtrueの間は新規Effectの発行を抑止し、安定（一定時間イベントが来ない）を検知したあとに全体Snapshotを再観測してから再開する。Shellは画面構成変化そのもの（モニタートポロジ変化）だけでなく、スリープ移行・セッションロック（komorebiの`EnteringSuspendedState`/`SessionLocked`相当）も`ScreenReconfigurationInProgress`のトリガー源として扱い、対応するOS通知（`WM_POWERBROADCAST`、セッションロック通知等）を監視する。

**抑止区間・安定待ち・grace windowのデータ表現（v2.6で追加、ADR-017／独立レビューR4-1・R4-15・R5-5で修正）**：状態はTopologyドメインの`ScreenPhase`（4.4）に置き、次の3つの区間を区別する。
- **`Reconfiguring`（明示的な抑止区間）**：`ScreenReconfigurationInProgress true`（スリープ移行、セッションロック、RDP切断等）で入る。v2.1の意味どおり、`false`が来るまで配置のEffectを抑止し続ける（RDP切断中の仮想ディスプレイに全窓を詰め直し、再接続時にもう一度詰め直す、という往復を避ける）。`false`の取りこぼしに備え、解除条件を3つ持つ：(a) `ScreenReconfigurationInProgress false`、(b) ユーザーの`UserCommand`（ホットキーが届いたこと自体が、セッションが対話可能に戻った証拠である）、(c) `StartTimer (Topology ReconfigurationCap, 既定10分)`の満了。いずれかで`Settling`へ進む。
  **`Reconfiguring`の間も`MonitorTopologyObserved`は段①相当で無条件に`TopologyState.LastObserved`へ記録する（v2.11、Opus批判的レビューdocs/review-phase-a-round1.md 3-1対応）。** (a)(b)(c)のいずれの解除経路も、それ自身はモニター構成を運ばないので、`Settling`へ入るときの`latest`には`LastObserved`（無ければ解除前の`Displays`のモニター集合、つまり構成が変わっていないとみなす）を使う。これが無いと、トポロジが何も変化していないロック→アンロックだけで`latest`が空になり、`MonitorLifecycle.reconcile`が全モニターを「消えた」と解釈して`Displays = NoMonitors`に確定してしまう。
- **`Settling`（安定待ち、debounce）**：トポロジ変化（`MonitorTopologyObserved`）と`Reconfiguring`の解除で入り、入力のたびに`StartTimer (Topology ScreenSettle, settleMs)`を返して再始動する（同じIDの`StartTimer`は再始動、3.3）。**`hardDeadline`（最初に`Settling`に入った時刻＋上限、既定30秒）を過ぎたら、イベントが続いていても強制的に確定する**（swayの`txn_timeout_ms`と同じ考え方、5.4の「無期限に待たない」）。確定時点の最新トポロジで4.4の`MonitorLifecycle.reconcile`を同じ`step`で実行し、全Pendingを`Superseded`にし、全体再観測（`ObserveAll`）を発行し、`Grace`へ進んで`StartTimer (Topology ScreenGrace, graceMs)`を返す。
  **`Stable`/`Grace`で、現在の`Displays`と内容が同じ（Id/Bounds/WorkArea/Dpi/IsPrimaryが全て一致する集合として）`MonitorTopologyObserved`を受けても`Settling`へ入らない（v2.11、同round1レビュー3-2対応）。** 確定時に発行する`ObserveAll`への応答が同じ構成のトポロジを含む場合、この無変化ガードが無いと`Settling`→`Grace`→`Settling`…を無限に繰り返し、配置のEffectが永久に抑止される。
- **`Grace`**：再同期直後、OS起因の移動を原因ラベル`OsInduced`として扱う区間（下記）。

`Reconfiguring`/`Settling`の間、ユーザーのCoreCommandによる状態遷移は行うが、**配置のEffect（`SetRect`/`RestoreBeforeMove`/`SetPresence`/`RequestForeground`）は、どの段で生まれたものであってもゲート（7.1の段⑧、6.2）で保留し、対応するPendingを`Superseded`にする**（v2.6第1版は段⑦だけを抑止していたため、抑止区間の前に発行済みのPendingの再送（段③の`Transient`→Retry）が、既に存在しないモニターの座標へ窓を送る経路が残っていた、R5-5）。`Settling`/`Grace`の間の窓の移動・最小化の観測は、7.1の段1で信念に反映したうえで、段2で原因ラベル`OsInduced`を付けられ、4.4の「ユーザーのドラッグ」の解釈へは流れない。grace満了後のReconcileは、段1で更新済みの信念（OSが寄せた後の実際の位置）と`Desired`を比べるので、寄せられた窓を正しく元へ戻す。`settleMs`/`graceMs`の値はPhase 0〜Bの実機Journalから決める。一時的な切断（スリープ復帰で数秒以内に再接続する）は`ScreenSettle`の待ちの中で吸収され、トポロジが確定しないので、Workspaceの退避そのものが起きない（4.4、R3-1）。

**S8（スリープ/ホットプラグ/RDP）の決定ロジックはPhase Aのシミュレーション対象にできる（v2.5で追加、OSS調査対応）**：OS自身が窓を動かすという物理的な事象は実機依存のまま残るが、「その変化にどう反応するか」という決定自体はモニタートポロジのスナップショットに対する純粋関数として表現でき、実機なしでユニットテストできる。Hyprlandの`CMonitorLifecyclePolicy`（`(SMonitorSnapshot, SWorkspaceSnapshot)`を入力に取り`IPolicyContext`というポートを通じて作用する、本WMのADR-002と同じFunctional Core/Imperative Shell構造）がcascaded disconnectを含むユニットテストを持つことがこれを裏付ける。採用する具体項目：(1) モニターは安定ID（4.1参照）で識別する、(2) ワークスペースごとのreturn monitor（4.4の`ReturnMonitor`。v2.6で設定上の割当`WorkspaceAssignments`と分離し、更新規則を確定、R3-1）、(3) サスペンド/ロックを明示的な再構成中状態として扱う（上記）、(4) 画面変化イベント後にgrace windowを設け、その間の最小化/移動をユーザー操作ではなくOS起因として扱う（komorebiの`display_change_in_progress(grace_period)`）——これは本節の振動防止ロジックに直接関わる（v2.6で`ScreenGrace`タイマーと原因ラベル`MoveCause`として具体化、上記）。OSが実際に窓を動かす部分の証拠源は、自動化されたハードウェアテストではなく実機日常利用時のJournal採取のままとする。
### 7.4 手動操作
ドラッグ中、サイズ変更中、明示的なユーザー操作の直後は自動整列を抑制できる。操作終了後は再整列、現配置の採用、Floating化等の方針をCoreが決定し、Shellでアドホックな再試行規則を実装しない。

**起動・終了時の振る舞い（v2.1で追加、G-4対応）**：WM起動時に既存の全窓を即座にタイルし直すか、終了時に起動時の矩形へ戻すかは、Phase Aで明示的に決める（4.4のワークスペース実装方式の決定と合わせて検討する）。終了時に元の位置へ戻す方針を取る場合、起動時点の矩形をWmStateの一部として保持する。

**窓を閉じた後のフォーカス（v2.1で追加、G-5対応）**：窓が閉じられた後、OSは何らかの窓へ任意にフォーカスを移す。WMが「同じWorkspaceの次の窓」（`FocusNext`相当）へフォーカスを移そうとする場合、8.1のB-3（非同期経路後の`SetForegroundWindow`許可）の検証結果に依存する。この検証が通らない限り、閉じた直後のフォーカス移動は保証できない前提で扱う（v2.4.4でF16により`mouse_event`自己プライミング付きなら成立することを確認済み）。**移動先（v2.6で確定、R3-3）**：閉じた窓をWorkspaceのzipperから除くと、zipperのフォーカスは隣の窓（`After`の先頭、無ければ`Before`の先頭。xmonadの`delete`と同じ）へ移るので、移動先はその窓になる。Workspaceが空になれば（`Stack = None`）`RequestForeground`を出さない。直前に閉じた窓のWindowIdへ`RequestForeground`が出てゲートの`TargetNotAlive`で弾かれ何も起きない、という経路はこれで無くなる。
## 8. Windows Shell・ネイティブ境界
### 8.1 初期方式
- EnumWindows等で対象とする最上位ウィンドウを取得する。
- SetWinEventHook(WINEVENT_OUTOFCONTEXT)等で変化を観測する。
- WinEventを受けたコールバックは最小限の情報をキューにコピーして返る。そこでCoreを更新したり、重いCOM/UIA呼び出しをしたりしない。
- メッセージループ、GC対象デリゲートの寿命、フックの解除、ネイティブ資源の解放をShellが管理する。
- SetWindowPosなどで位置を要求し、必要に応じ`GetWindowRect`・DWM境界を再取得する。
- SetForegroundWindowはOSの制限で拒否され得る。自動レイアウトで不要にフォーカスを奪わない。
- RegisterHotKey等の標準経路から操作を入力し、グローバル低レベルキーボードフックは初期必須としない。
初期対象は通常権限・同一ユーザーの対話Desktopで管理可能な窓。異なるIntegrity Level、保護されたUAC/ロック画面、Windows Shell特殊窓等には操作できない場合があり、そこを管理者権限で強行突破する設計にはしない。

**ホットキー登録の実現性（v2.1で追加、B-2対応／v2.2でfeasibility.md §1・§3により補強／v2.3でGitHub Actions windows-latest実機検証により確定：`Alt+J`/`Alt+Shift+Return`/`Alt+Shift+1`は`RegisterHotKey`で確保でき、`Win+Return`/`Win+1`/`Win+L`は全て`GetLastError=1409`(ERROR_HOTKEY_ALREADY_REGISTERED)で失敗した。failure-catalog.md F11。既定のCoreCommandキーバインドはAlt系を基本とし、Win系は避ける）**：`Win+1..9`等の多くの組み合わせはExplorerが既に登録しており、`RegisterHotKey`は`ERROR_HOTKEY_ALREADY_REGISTERED`で失敗する（`Win+L`等は低レベルフックでも介入困難で原理的に奪えない）。`MOD_WIN`で`Win`キーを指定する場合、既存のWindows用タイル型WM（GlazeWM）のDiscussionで、`win`ではなく`lwin`/`rwin`（左右Winキー個別）指定が必要だったという報告がある。キーボード中心の操作体系（1.2）に必要なキーバインドが`RegisterHotKey`で確保できるかはPhase 0で実際に検証し、確保できない場合は低レベルキーボードフック（`WH_KEYBOARD_LL`）を使う方針へ切り替える。`WH_KEYBOARD_LL`を使う場合、フックスレッドがGCやI/Oで停止すると`LowLevelHooksTimeout`（Windows 10 1709以降は最大1000msにクランプされる）を超過してOSがフックを**黙って外し**（アプリケーションに通知する手段は無い）、フック処理はEffect適用スレッドやJournal I/Oと分離する。フックが無言で外れうる以上、WM側で自分が登録したはずのキー入力が一定時間観測できない場合に再登録する自己診断の仕組みをPhase Aで検討する。

**フォーカス移動の実現性（v2.1で追加、B-3対応／v2.2でfeasibility.md §2により具体化／v2.3でGitHub Actions windows-latest実機検証を実施したが結論は保留：failure-catalog.md F04／v2.4.4で実デスクトップ（dragonflyg4）実機検証により確定：failure-catalog.md F16）**：CI実機検証では、別プロセス（検証ハーネス自身が起動した子プロセス）が直前にフォアグラウンドだった状態から、`mouse_event`自己プライミングの有無にかかわらず`SetForegroundWindow`が成功してしまい（F04）、公式ドキュメントの「フォアグラウンドプロセスが呼び出し元プロセスによって起動された」という許可条件（親子関係）の影響を排除できなかった。**実デスクトップでの再検証（F16）で、自分が起動した子プロセス・自分が起動していない無関係な第三者プロセスの両方に対し、plain `SetForegroundWindow`は明確に失敗し、`mouse_event`自己プライミング版は明確に成功することを確認した**。自己プライミングが実際に必要であることは、推測ではなく実機実証済みの確定事項である。`SetForegroundWindow`は「直前に入力イベントを受け取ったプロセスか」等のOS側の判定で拒否され得る。公式ドキュメントは条件を満たしても拒否され得ると明記しており（「It is possible for a process to be denied the right to set the foreground window even if it meets these conditions」）、ホットキー受信から「キュー→Reducer→Journal→Effect」という非同期経路を経た後は許可が落ちることを前提にする。既存のWindows用タイル型WM（komorebi、GlazeWM）はいずれも、`RequestForeground`相当のEffect適用時に**自分自身のプロセスへ`SendInput`でダミー入力（マウスイベント）を送出してから`SetForegroundWindow`を呼ぶ**という同一のパターンで対処している（`AttachThreadInput`はデッドロック報告があり両WMとも採用していない）。Shellの`RequestForeground` Effect実装はこのパターンを既定とする。komorebiはさらに起動時に`SPI_SETFOREGROUNDLOCKTIMEOUT`でシステム全体の`ForegroundLockTimeout`を0に変更しているが、これはユーザー環境のシステム設定を書き換える副作用が大きい回避策であり、本WMでは既定で採用せず、Phase 0での実測で自己`SendInput`方式だけでは不十分と判明した場合にのみ設定可能なオプションとして検討する。

**フルスクリーンアプリの除外（v2.1で追加、E-4対応／v2.3でGitHub Actions windows-latest実機検証により修正：failure-catalog.md F03）**：枠なし全画面のゲーム・プレゼン・動画プレイヤーを通常の窓として誤ってタイル化しないよう検出する。実機検証で、ボーダレスかつモニター全体と同じ矩形の窓に対し`SHQueryUserNotificationState`は`QUNS_QUIET_TIME`を返し**検出に失敗**した（このAPIは主にD3D排他的フルスクリーンや集中モードの検出が目的で、汎用のボーダレス全画面窓には反応しない）。一方「モニター全体と同じ矩形かつ枠なし」のジオメトリヒューリスティックは正しく検出できた。したがって検出はジオメトリヒューリスティックを主手段とし、`SHQueryUserNotificationState`は補助情報としてのみ扱う。

**P/Invoke呼出しの変換点（v2.4で追加、ADR-014/4-1対応）**：生のWin32 API（`[<DllImport(..., SetLastError = true)>]`で宣言したもの）は失敗しても例外を投げず、呼出し規約ごとに戻り値で知らせる。規約ごとに変換点を分け、いずれも7.1の`NativeCallRecord`を作る。

**前提条件（v2.4で追加、R2-23対応／v2.4.5で実機バグとして実証：failure-catalog.md F24）**：以下の3関数が成り立つのは、対象のDllImportが全て`SetLastError = true`で宣言されている場合のみである。戻り値の`0`/`false`が正当な結果になり得るAPI（例：`GetWindowTextLength`）では、呼出前に`Marshal.SetLastPInvokeError(0)`でクリアしてから呼ぶ専用ラッパーを使う。**これは理論上の注意点ではなく実際に踏んだ罠である**：Phase 0スパイクハーネスで`SetWindowPos`のDllImport宣言に`SetLastError = true`を付け忘れた結果、昇格窓への呼出が`returned=false`なのに`GetLastError()`が`0`を返す（＝クリアされていない古い値を読んでいる）という偽の挙動を実機で観測した。属性を追加した直後に`GetLastError=5`という正しい値が得られたことで、この前提条件が単なる注記では済まない実害のある落とし穴だと確認された。レビュー・実装時は全DllImport宣言に`SetLastError = true`の有無を機械的にチェックすること。なお、同一ユーザーの上位整合性レベルプロセスを`OpenProcess`で照会する場合は`PROCESS_QUERY_INFORMATION`では拒否されるため`PROCESS_QUERY_LIMITED_INFORMATION`を使うこと（同じくF24で実証。Task Managerが非昇格のまま昇格プロセス情報を読めるのと同じ権限）。

```fsharp
module Native = // Wm.Windows内部（internal）。外部から呼べるのはGatedEffectを受け取るShellと8.4の限定APIのみ（R2-6対応）
    // GetLastPInvokeErrorは呼出直後・同スレッド・他のP/Invokeを挟まず読む契約。順序を守るためラッパーに閉じ込める
    // ElapsedUsは実測する（常に0を入れない。v2.4追加、R2-22対応。ハング判定前のタイムアウト満了と即時中断の区別に使う、7.1参照）
    let private timed (f: unit -> 'a) : 'a * int64 =
        let sw = System.Diagnostics.Stopwatch.StartNew()
        let r = f ()
        (r, sw.Elapsed.Ticks / 10L) // マイクロ秒

    let callBool (api: string) (f: unit -> bool) : Result<unit, NativeCallRecord> =
        let (ok, us) = timed f
        if ok then Ok ()
        else Error { Api = api; Space = Win32LastError; Code = Marshal.GetLastPInvokeError(); ElapsedUs = us }

    let callBoolNoCode (api: string) (f: unit -> bool) : Result<unit, NativeCallRecord> =
        // SetForegroundWindow等、falseを返してもGetLastErrorを設定しないAPI用
        let (ok, us) = timed f
        if ok then Ok () else Error { Api = api; Space = BoolFalseNoCode; Code = 0; ElapsedUs = us }

    let callHr (api: string) (f: unit -> int) : Result<unit, NativeCallRecord> =
        let (hr, us) = timed f
        if hr >= 0 then Ok () else Error { Api = api; Space = HResult; Code = hr; ElapsedUs = us }
```

`SetWindowPos`/`RegisterHotKey`/`SendMessageTimeout`は`callBool`、`DwmGetWindowAttribute`は`callHr`、`SetForegroundWindow`/`ShowWindowAsync`は`callBoolNoCode`を使う。`RestoreBeforeMove`（7.1）の実装は`ShowWindowAsync(SW_RESTORE)`に固定する（`ShowWindow`の戻り値は「以前に可視だったか」であり成否ではないため、同期版を`callBool`に通すと誤判定する。v2.4追加、R2-4対応。実機検証でも同期版はハング窓で4000ms超ブロックし`ShowWindowAsync`は0msで返ることを確認済み、failure-catalog.md F13）。Wm.Windows内の全P/Invoke呼出しはこの3関数のいずれかを経由し、生のBOOL/HRESULTをCore/Policy/Layoutsへ流さない。

`classify`（7.1）の対応表は、**`(Api, Space, Code)`の3つ組をキー**にする（v2.4で訂正、R2-1対応。Codeだけで分岐すると、`SetWindowPos`が偶然返した`1460`まで`TargetHung`になってしまう）。Phase 0の失敗カタログ（18章）で埋める。実測・文献から判明している初期エントリの例：

| (Api, Space, Code) | EffectFailure |
|---|---|
| `(SetWindowPos, Win32LastError, 1400)`（ERROR_INVALID_WINDOW_HANDLE） | `TargetGone` |
| `(SendMessageTimeout, Win32LastError, 1460)`（ERROR_TIMEOUT）、`ElapsedUs`が`timeoutMs`に近い | `Transient`（ハング判定前。v2.4追加、R2-1/R2-2対応） |
| `(SendMessageTimeout, Win32LastError, 1460)`、`ElapsedUs`が0に近い | `TargetHung`（既にハング判定済み。v2.4追加、R2-1対応） |
| `(SetWindowPos, Win32LastError, 5)`（ERROR_ACCESS_DENIED。昇格窓等） | `Denied OtherDenial`（実デスクトップ`dragonflyg4`でMedium→High昇格窓への`SetWindowPos`が実際に`returned=false, GetLastError=5`で失敗することを実機確認済み：failure-catalog.md F23。v2.4追加、R2-18対応／v2.4.5でF23により実測確定。**v2.5でADR-015として確定：専用`DenialKind`は増設しない**。v2.6では、この失敗が4.5の`Movability`の信念を`NotMovable`へ修正する証拠になる。Explain/whyNotは、`TargetIntegrity`の信念が`Higher`として形成されていれば「整合性レベル起因の可能性が高い」と示し、`Unformed`（probeできなかった）なら原因を断定せず「アクセス拒否（原因不明）」とだけ示す（v2.6、R3-7：probeの失敗を「おそらく昇格窓」と読み替えない）。コード5単体では原因が整合性レベルだと断定できない点はkomorebiの`DWMWA_CLOAK`の事例（外部HWNDであれば整合性レベルに関係なく`E_ACCESSDENIED`になる）が裏付ける） |
| `(RegisterHotKey, Win32LastError, 1409)`（ERROR_HOTKEY_ALREADY_REGISTERED。failure-catalog.md F11） | ※`RegisterHotKey`はEffectではなく起動時の登録。`EffectFailure`には含めない（1-8注記） |
| `(SetForegroundWindow, BoolFalseNoCode, 0)` | `Denied ForegroundDenied` |
| 上記以外 | `Unclassified` |
### 8.2 ウィンドウ分類の補助属性
プロセスID/実行ファイル識別、トップレベル/所有関係、スタイル、可視性、最小化、DWM Cloak、モニター/WorkArea、フォーカス、DPIを検討する。Win32の通知がすべて揃うとは想定せず、時折全体再観測する。
### 8.3 DPI・矩形
- 初期からDPI Awarenessを明確化し、座標系を混在させない。
- GetWindowRectはDPI仮想化や不可視リサイズ枠を含み得る。
- DWMWA_EXTENDED_FRAME_BOUNDSは見えているフレームの境界として取得可能だが、GetWindowRectと単純に同じ座標/意味ではない。
- 負のX/Y、モニター境界をまたぐウィンドウ、複数DPI、WorkArea変更、タスクバー位置変更を後期テストに含める。
- F# Coreが扱う矩形の意味（初期は一貫した物理ピクセル座標。4.2の座標系契約も参照）を固定し、Shellの変換前後を診断可能にする。

**Coreは物理ピクセル継続、ユーザー向け設定値はDIPでモニター単位に変換する（v2.5で追加、OSS調査[oss-wm-survey.md]対応、S11関連）**：調査した既存WMは、プラットフォームの実態に応じて2つの流派に分かれる——sway/Hyprland/yabai（macOSを通じて）は論理座標でレイアウトし、komorebi/GlazeWM（Windowsの2例）はいずれも物理ピクセルを使う。これはPer-Monitor-V2の実際の挙動（物理ピクセル前提）と一致するため、本WMもCoreは物理ピクセル継続の既定方針を維持する。一方で、ユーザーがPolicy IRで指定する「視覚的な一貫性を意図したレイアウト上の長さ」（ギャップ等）と、レイアウト選択の`MonitorWidthAtLeast`はDIP単位で指定させ、`LayoutContext`構築時（ギャップ）・レイアウト選択時（`MonitorWidthAtLeast`）に対象モニターのDPIで物理ピクセルへ変換する（GlazeWMの`scale_with_dpi`が先例。GlazeWMがスケールするのもギャップだけである）。**分類述語の`MinWidthAtLeast`は、閾値ではなく比較相手の側をDIPに正規化する（v2.6で訂正、R3-5／独立レビューR4-12で再訂正）**：v2.5は`MinWidthAtLeast`を「DIPで指定し`LayoutContext`構築時に変換する」としていたが、これは分類（Classify）の述語であり、6.1のパイプラインで`LayoutContext`より前に評価されるので、時系列上その位置では変換できない。仮に分類時に窓のいるモニターのDPIで閾値を変換すると、同じ窓・同じ観測値でも、モニターを移るたびに分類が反転する。v2.6の最初の案は閾値を物理ピクセルに固定するものだったが、比較相手の`MinWidth`の信念が、どのDPIのモニターで`Mismatch`を観測したかに依存して物理ピクセルで蓄積されるため、分類が移動経路に依存する問題は形を変えて残っていた。最終的に、`MinWidth`の信念を観測時のモニターのDPIでDIPへ正規化して持ち（4.5）、閾値もDIPで指定させる。両辺がDIPなので、分類はモニターのDPIに依存しない。レイアウト選択はもともとモニター単位で行うので、`MonitorWidthAtLeast`がDPIに依存しても新たな反転は生まない。Hyprlandの座標変換関数が示す通り、論理↔物理の変換は「どのモニターを基準にするか」に依存するため、変換はモニター単位で行い、グローバルな単一係数にしない。これは実機要件を変えない：対象アプリ自身の`WM_DPICHANGED`への反応は依然シミュレーションできず、S11の実機検証は必須のまま残る。

- **異なるDPIモニター間移動時の自己リサイズ（v2.1で追加、B-6対応／v2.2でfeasibility.md §7により修正）**：対象アプリを異なるDPIのモニターへ移動すると、OSは`WM_DPICHANGED`（`wParam`に新DPI、`lParam`に推奨矩形）を送り、Per-Monitor-V2対応アプリはこれを受けて自分で`SetWindowPos`を呼ぶことが期待される。この自己リサイズは、Journal上では`SelfRepositionAfterMs`（12.1）による自発的な挙動と見分けがつかない。**「DPIをまたぐ移動は2段階のSetWindowPosが必要になることが多い」という記述はPer-Monitor-V2対応アプリに限定される**：Per-Monitor-V2非対応（System DPI Aware/DPI Unaware）アプリは`WM_DPICHANGED`を実質的に処理せず、OSがビットマップとして拡大縮小するだけで2段階目のSetWindowPosは発生しない（公式ドキュメントも「それらのアプリでは安全に無視できる」と明記）。Shellは対象アプリのDPI Awareness（`GetWindowDpiAwarenessContext`等）を取得し、PMv2対応アプリのみ2段階目のObservedを待つ、非対応アプリは1段階目のObservedで完結すると判断する。**v2.6で、この判断の主体をShellからCoreの信念へ移す（ADR-016、S11）**：申告値は`WindowSnapshot.DpiAwareness`として観測し、4.5の`DpiBehavior`の信念の事前証拠にする。実際にDPIをまたいだ後に2段目の自己リサイズが観測されたかで信念を修正し、Reconcilerがこの信念を読んで2段目を待つかを決める。申告と実際の振る舞いが食い違うアプリ（PMv2を申告しながら`WM_DPICHANGED`を処理しない等）があっても、2回目以降の移動では正しく振る舞える。「DPI変更起因の自己変化」を疑似OSの振る舞い部品として区別できるようにする。
### 8.4 安全停止
通常の設定・Rule評価・REPLとは独立した制御経路により、WMの移動・フォーカス操作を停止できるようにする。常駐状態の視認/停止に使うUI、OS標準の操作への回帰、異常終了検知、直前に隠した窓がある場合のベストエフォート復旧を実装する。障害時にはWindowsそのものを操作不能にしないことを優先する。

初期版では窓を破壊・終了するEffectを提供しない。安全停止が発動したら新規Effectを遮断し、Pendingをキャンセル扱いにし、全体再観測の後にのみ再開する。

**復旧操作はGatedEffectを経由しない例外経路（v2.4で追加、ADR-014/R2-6対応）**：直前に隠した窓のベストエフォート復旧は、Coreが止まっている／ハングしている可能性を前提にHostの監視コンポーネントが直接`Native`モジュール（8.1）を呼ぶ。6.2・22章受け入れ条件4番の「全EffectはGatedEffectを経由する」の明示的な例外とし、呼べる操作を「表示の復元」と「画面内への移動」に限定する。実行したらJournalに`SafetyRecoveryAction`として記録する（9.2のKind一覧に追加）。ワークスペースの非表示方式にCloakを採用する場合（21章）、「表示の復元」はCloakの解除を含まなければならず、それには未文書化のCOM（`IApplicationView::SetCloak`）を監視コンポーネントから呼べる必要がある。これが満たせないならCloakは採用しない（v2.6で追加、R3-8）。復旧の対象（「直前に隠した窓」）は、Coreが止まっていても分かる必要があるので、Coreの`Desired`ではなく、Effect適用スレッドが`SetPresence(shown=false)`を適用するたびに監視コンポーネントへ通知するHWNDの一覧として監視側が自前で持つ（GlazeWMの`wm-watcher`が自前のHWNDリストを持つのと同じ。v2.6、R4-6）。`Native`モジュール自体はWm.Windows内部（internal）に留め、外部から呼べるのは「GatedEffectを受け取る通常のShell」とこの限定APIの2つだけにする。

**制御経路の実体（v2.1で追加、E-1/E-2対応／v2.5でGlazeWMの`wm-watcher`を先行実装例として追記、OSS調査[oss-wm-survey.md]対応）**：安全停止の「独立した制御経路」は、Effect適用スレッド（7.1のADR-012）やメインのメッセージループとは別のスレッド、できれば別プロセス（Wm.Host内の監視用の軽量コンポーネント）で動かす。**クラッシュ検知の経路**についての先行実装がGlazeWMの`wm-watcher`（独立プロセス。IPCで`window_managed`/`window_unmanaged`/`application_exiting`を購読し自前のHWNDリストを保持、`application_exiting`を受け取らずに購読が終わった場合に全窓表示・タスクバー可視性復元・ボーダー状態リセットを行う）にある。ただし`wm-watcher`の発動条件は「購読が終わったこと」であり、WMが**ハングした**場合（プロセスもパイプも生きている）には何もしない（v2.6で訂正、R3-13：v2.5は「同型」「裏付ける」と書いたが言い過ぎだった）。本節の主要件は「ハング時でも停止経路が生きていること」なので、ハング検知（Reducerループからのハートビートが一定時間途絶えたら監視側が発動する等）は本WM独自に必要であり、`wm-watcher`の発動条件をそのまま写してはならない。ハングした対象アプリへの呼出でメインのメッセージループや通知領域アイコンが応答しなくなっても、停止用のホットキー・経路だけは生き続けることを要件とする。通知領域アイコンはExplorer再起動時に消えるため、`TaskbarCreated`メッセージを処理してアイコンを再登録する。**GitHub Actions windows-latest実機検証で、`explorer.exe`を強制終了すると504ms後に自動再起動することを確認した（failure-catalog.md F15）**。再起動自体が高速（1秒未満）なので、`TaskbarCreated`処理の実装が多少遅れても、トレイアイコンの空白期間は短く収まる見込みである。**さらに実デスクトップ（`dragonflyg4`）実機検証で、`RegisterWindowMessage("TaskbarCreated")`→`Shell_NotifyIcon(NIM_ADD)`→Explorer強制終了→381ms後の自動再起動→`TaskbarCreated`ブロードキャストの実際の受信→トレイアイコンの再追加成功、という一連の流れがエンドツーエンドで成立することを確認した（failure-catalog.md F19）。本節の復旧ロジックはここまで実証済みの確定事項である。**

**再開の条件（v2.1で追加、F-7対応）**：安全停止からの再開は既定でユーザー操作による手動再開のみとする。自動再開にすると、停止原因（例：ハングしたアプリ）が解消していない場合に停止と再開を繰り返すことになる。
## 9. Unified Journalとログの統合
### 9.1 目的
Journalは後から見せる文字列ログの寄せ集めではない。状態遷移の入力、命令、呼出結果、外界の観測、診断操作、実験証拠を結ぶ永続的な因果記録である。

**永続化する対象の方針（v2.1で改訂、ADR-004/B-1対応）**：Coreは決定的である（2.2, 7.1）。したがって「入力列 + EffectCallReturned等のOS呼出結果 + Artifact識別子 + Checkpoint」を永続化すれば、判断（どのRuleが選ばれたか、Predicateがどう評価されたか等のDecisionTrace）はCoreをReplayして再計算すれば常に再構成できる。判断そのものを都度永続化する必要はない。これにより、LOCATIONCHANGEが1秒間に数百回届くドラッグ中でも、永続化のI/O量は入力イベント数に比例するだけで、全窓×全ルールの述語評価量には比例しない。

- Input Journal：Reducerへ投入した入力列とOS呼出結果（永続化の中心）。
- DecisionTrace：直近の判断は実行中プロセス内のリングバッファ（直近N秒、揮発性）として保持し、即時のExplainに使う。それより古い時点の判断はReplayで再計算する。
- Log View：人間向けのレベル別テキスト表示（Input Journal + 再計算したDecisionTraceの投影）。
- Timeline View：順序・相関・遅延を視覚化。
- Explain View：WindowIdからRule→Layout→Effect→Observationを追う（Replay再計算を利用する）。
- Replay Input：Coreへ投入した入力列とSnapshot。
- Incident Bundle：再現に必要な最小の証拠、モデル、バージョン情報。

Log View・Timeline View・Explain Viewは独立の収集経路ではなく、Input Journal（+ 必要に応じたReplay再計算）を起点とした用途別の投影とする。
### 9.2 Journal Envelope
type JournalEntry = {
    SchemaVersion: int
    SessionId: string
    Sequence: int64
    MonotonicTicks: int64
    WallTimeUtc: System.DateTimeOffset
    CorrelationId: string option
    CausationId: string option
    PolicyArtifactHash: string
    CoreBuildId: string
    Kind: JournalKind
    Payload: JournalPayload
}
型付きKind（抜粋）。**永続化するもの**（9.1参照）：`InputReceived`、`EffectDispatchStarted`、`EffectCallReturned`、`EffectBatchReturned`（v2.4で追加、1-5対応）、`InternalFault`（v2.4で追加、4-3対応）、`JournalDegraded`/`JournalRecovered`（v2.4で追加、3-4・R2-16対応。後述の通りこの2つはJournal自体に書けない状況で発生するため、Reader/Replayは欠落区間として特別扱いする）、`SafetyRecoveryAction`（v2.4で追加、R2-6対応。8.4のGatedEffect迂回経路の記録）、`PolicyStaleAnalysis`（v2.4で追加、R2-11対応。起動時・Rollback時の報告のみAnalyzer実行結果）、`ObservationReceived`、`PendingSatisfied`/`PendingTimedOut`、`ReplIntervention`、`ProbeRequested`、`EvidenceCaptured`。**揮発性リングバッファ＋Replay再構成で足りるもの**（都度永続化しない）：`PolicyPredicateEvaluated`、`RuleSelected`、`LayoutInvoked`、`PlanAccepted`/`PlanRejected`、`DesiredUpdated`、`EffectPlanned`、`ModelCandidateCreated`、`SimulationResult`、`BeliefRevised`（v2.6で追加、ADR-016。信念は観測履歴の純粋関数なので再計算できる）、`TimerCommanded`（v2.6で追加、ADR-017。タイマー指令は判断であり、発火の`TimerElapsed`だけが入力として永続化される）。

**`EffectCallReturned`/`EffectBatchReturned`のPayloadは`NativeCallRecord`ベースの`Result<unit, NativeCallRecord>`そのもので、`EffectFailure`は含めない**（v2.4で追加、ADR-014/1-2・X-3対応）。`EffectFailure`は`classify`（7.1）がRaw記録から再計算する派生物であり、ADR-004「再計算できるものは再生成する」の対象である。`classify`はCoreの`step`内で呼ばれるため、版は`CoreBuildId`でピン留めされる（`normalizerSchemaVersion`はShell側のADR-013 Normalizerの版であり、`classify`とは無関係。v2.4でR2-15対応として訂正）。

**`JournalDegraded`/`JournalRecovered`とReplayの欠落区間（v2.4で追加、R2-16対応）**：JournalWriterの書き込み失敗自体はJournalへ書けない状況で発生するため、`JournalDegraded`は**Journalに残らないままCoreのstepへ入る**唯一の入力である。復旧時（`JournalRecovered`）に、欠落区間とその間に投入された入力をメモリ上のバッファから書き戻せるなら書き戻すが、できない場合はReplayがこの区間をまたげない：Replayは欠落区間の直後の最初のCheckpoint（9.5）から再開する、と明記する。

**DTOのタグ付け規約（v2.4で追加、ADR-014/X-1対応、R2-12・R2-13・R2-14で範囲と扱いを訂正）**：対象は`EffectFailure`/`ValidationError`のような派生表示専用の型だけでなく、**Journal・Checkpoint・PolicyArtifact・Incident Bundle・IPCに出る全てのDU**（`InputEvent`の各ケース、`Effect`、`CoreCommand`、`ErrorSpace`、`Management`、`LayoutArgs`、`WindowPredicate`、`StackSlot`等）である。対象型の一覧はdocs/contracts.mdに置き、「全DUケースに安定タグが割り当てられ、過去のフィクスチャを読める」ことを17.1の共通ゲート「Schema互換性」で検査する。各DUのタグはF#のケース名をそのまま使わず、**明示的な安定文字列コード**（例：`"target-gone"`）を対応表で管理する。F#側でケース名をリネームしても過去のJournalとの互換性を壊さない。`Result<'T,'E>`自体はDTOの型にせず、`{ "outcome": "ok" | "failed", "failure": {...} }`の形に固定する（5.2の「DUシリアライザーに暗黙依存しない」を全DTOに適用する）。

**既知タグの形状変更と、廃止タグの予約（v2.6で追加、R3-9対応）**：上の規約は「未知のタグ」しか扱っておらず、v2.5では次の2つが規約の穴を通っていた：`AppIs`がタグを保ったままペイロードを`string`から`AppIdSource * string`へ変え、`DenialKind.TargetIntegrityHigher`を削除した。規則を追加する：
- **既知タグのペイロードの形を変えない。** 形を変える必要があるときは、新しいタグを作り、旧タグは読み取り専用で残す（`ofDto`は旧タグを読めるが、新規には書かない）か、そのDTOを含むスキーマ（Policyなら`PolicyIR.SchemaVersion`）を上げ、9.5の方針どおり旧形式からの明示的な変換結果を新しいArtifactとして保存する。寛容なデコーダが旧形状を黙って新形状へ読み替えることは、`PinnedPolicy`の「当時のまま」（14章）に反するので禁止する。
- **廃止したタグは予約済みとし、別の意味で再利用しない。** 予約済みタグの一覧をdocs/contracts.mdに置く。v2.6時点の予約：`"target-integrity-higher"`（`DenialKind`、v2.5で削除）、`"target-integrity-is"`（`WindowPredicate`、v2.6で`MovableIs`に置き換え）、`"arm-timer"`/`"cancel-timer"`（`Effect`、v2.6でStepResult.Timersへ移動）、`"home-monitor"`（`WorkspaceState`、v2.6で`ReturnMonitor`と`WorkspaceAssignments`に分離）、`"title"`（`WindowSnapshot`、v2.6で`TitleMatches`に置き換え）、`"is-cloaked"`（`WindowSnapshot`、v2.6で`Cloak`に置き換え）、`"recent-focus"`（`WmState`、v2.6で`FocusHistory`に統合）。
- **レコードのフィールド名も、DUのタグと同じく安定文字列で管理する**（v2.6で明記、R4-25）。上の予約一覧の`"home-monitor"`等はレコードのフィールド名である。フィールドの追加は、旧データでの既定値（`None`/空）を対応表に書けば互換を保てる。フィールドの削除・型の変更は、DUタグの形状変更と同じ規則（新しい名前を作るか、スキーマの版を上げて変換Artifactを作る）に従い、旧フィールド名は予約する。
- `EffectFailure`自体はJournalに永続化されない（Replay時に`classify`で再計算する）が、Explain出力とIncident Bundleには表示用DTOとして出るので、ペイロードのDU（`DenialKind`）も含めて本規約の対象である。
- v2.6で新設したDU（`AppIdSource`、`IntegrityRelation`、`CloakState`、`ResponsivenessSample`、`DpiAwarenessDeclared`、`Confidence`、`EvidenceRef`、`BeliefSlot`、`Movability`、`Responsiveness`、`DpiBehavior`、`BeliefKey`、`ObservedPresence`、`FocusTarget`、`TimerId`とドメインごとのタイマーDU、`MonitorSelector`、`ClassificationState`、`ClassifiedBy`、`EffectiveManagement`、`DesiredPresence`、`ScreenPhase`、`Displays`、`MoveCause`、および各ドメインの状態レコード（Checkpointに出る）。`DomainEvent`はCoreの内部でだけ使い、Journal・Checkpointに出ないので対象外）も対象に含む。信念はJournalには出ないがCheckpoint・Explain・Incident Bundleに出る。docs/contracts.mdはまだ存在しない（17章で参照のみ）。Phase AでofDtoを書き始める時点で作成し、それまでは本書の9.2が対象型の一覧を兼ねる。
- v2.6時点では永続化された互換対象のデータは存在しない（Phase 0は捨てる前提のスパイクで、Journal/Policyのスキーマを持たない）。したがって上記の変更による実害は無いが、規則はPhase Aの最初のofDtoから適用する。

未知のタグへの対応は、**用途でモードを分ける**：
- 表示/Explain/Bundle閲覧モード：ReaderのDTO型（`NativeCallRecordDto`等、ドメイン型自体ではない）が`Unrecognized of tag: string * raw: JsonElement`として保持し、そのまま表示する。`classify`ではなくDTO→ドメインの変換（`ofDto`）がLive運用時に限り`Unclassified`へ畳み込む。
- **Replayモード：`Unrecognized`を1つでも含むJournalはFail-fastで拒否する**（「このJournalはより新しいスキーマで書かれている」と報告する）。12.2の「ピン留めした当時のCore」を使えば未知のタグには原理的に出会わないはずであり、出会うこと自体がピン留めの失敗を意味する。未知のタグを黙って`Unclassified`に変換すると、元の実行と異なる決定（例：`TargetGone`→`Cancelled`だったものが`Unclassified`→`Retry`になる）がReplayの不一致として誤報告される。
- **不変条件：`step`が分岐に使うInputEventのケース ⊆ Journalが往復できる（符号化・復号化できる）ケース（v2.11で追加、Opus批判的レビューdocs/review-phase-a-round1.md 5-4対応）。** `step`がまだ対応していない入力のケースを後から追加するたびに取りこぼしやすいため、17.1の共通ゲートにアーキテクチャテストとして固定する（あるいは全InputEventケースを列挙し`Unrecognized`にならないことを検査するテストでもよい、round2レビューT-5）。Phase AのJournalWriterの実装はPhase Aが実際に分岐へ使う範囲（`MonitorTopologyObserved`/`ScreenReconfigurationInProgress`/`TimerElapsed`/`ForegroundObserved`/`WindowRemoved`/`UserCommand`）だけに対応しており、他のケースは表示専用の`"unsupported-input-event"`タグで書く（Windows/Placementが新しい入力ケースをstepの分岐に加えるたびに、対応するJournalの符号化も追加すること）。

関連キーは`WindowId / RequestId / BatchId / IntentEpoch / RuleId / LayoutId / EvidenceId / ScenarioId`。ログ文字列を唯一の情報源にしない。自由なF# Layout関数内部の全分岐は自動観測できないため、必要な算法のみ明示的な`LayoutDecisionTrace`を返す。
### 9.3 因果関係と時刻
- SequenceはReducerへ投入した順序（Session内で単調）であり、OSでの物理的な発生順序ではない。
- MonotonicTicksは同一Session中の相対時間計測に用い、`WallTimeUtc`は表示・外部相関に利用する。
- CorrelationIdは同一の操作/Incidentに関係する記録を束ね、`CausationId`は確かなアプリ内因果関係のみを明示する。
- WinEventの移動通知を、単に直前のSetRect要求への応答と断定しない。OS側の因果は推定として別扱い。**この照合がヒューリスティックになるのは実装の手抜きではない（v2.5で追加、OSS調査[oss-wm-survey.md]対応）**：X11（シーケンス番号）とWayland（swayのアトミックトランザクションが使うconfigureシリアル/Ack）はいずれもリクエスト↔応答を一次的に対応付けるプリミティブをプラットフォーム側で持つが、Win32にはこれに相当するものがない。他のWMから借用できる、よりきれいな対応付け手段は存在しない。v2.6では、この照合を3.3の「待ちをデータで表す」規約の上に置く：Pendingは`PendingDeadline`タイマー付きの待ちであり、swayのトランザクション（確認応答が来なければ`txn_timeout_ms`で強制適用）と同じ「期限付きの推測的な待ち」である。照合の結果（`Satisfied`/`Mismatch`）は、4.5の`VisibleFrameRect`の信念（観測そのものではない）と目標矩形を比べて決める。
- 外部ログ/ETW/画像等は主Journalに無制限格納せず、`EvidenceRef`と取得条件で参照する。
### 9.4 書き込み・障害と再起動
Coreは純粋に`NextState + Effect + DecisionTrace`を返し、JournalWriterが永続化する。OSの操作とファイルの書き込みを一つのトランザクションにすることはできない。次の事実を区別する。
Input accepted → Core decision → Effect planned
  → Dispatch attempt → Win32 call returned
  → Later observed state → Satisfied / Mismatch / Timeout
クラッシュ時にDispatchされたか不明な操作は、再送を無条件に行わず、再起動後の全体Snapshot→再Reconciliationで扱う。冪等性が低いEffectは初期対象に含めない。記録の耐久性、バッファ、バックプレッシャー、クラッシュ時の最後の数イベントの欠落許容度はPhase Bで測定して決める。

**JournalWriter異常はResultで呼び出し元に返さず、入力イベントにする（v2.4で追加、ADR-014/3-4対応）**：書き込みI/O失敗を`Result`で返しても、Reducerループにできることは「劣化状態へ遷移する」ことだけである。JournalWriterは内部で例外を捕捉し、`JournalDegraded`という`InputEvent`（7.1）としてReducerへ渡す。これは3.1の「すべて入力キューに送る」と整合し、16.2の「記録劣化を可視化し、安全性上不可欠な操作は停止する方針を選択可能にする」をCoreの通常の状態遷移として扱える。
### 9.5 Snapshotと永続互換
初期状態または低頻度Checkpoint + 増分Journalにより再生を可能にする。Snapshotには`SchemaVersion`、`LastSequence`、Core/Policy/Registry/Normalizerの成果物識別子を持たせる。フォーマット移行は古い記録を上書きせず、変換結果を新規Artifactに保存する。ログの編集を事実改ざんと混同しないよう、派生ビューと原記録を区別する。

**Checkpointは`Desired`を信用しない（v2.4で追加、ADR-014/2-4対応。v2.6で稼働中の再起動に使わなくなったため、以下はReplayの開始点として読む場合の規則）**：`ValidatedPlan`（6.2）等のsmart constructor型をそのままCheckpointへシリアライズすると、Validatorの規則を後から強化したバージョンで古いCheckpointを復元できなくなる。CheckpointにはDesiredを「生の`Placement list` DTO」として保存し、**復元時にはDesiredを信用せず**、16.2「保存状態をOSの真実とみなさず、全体再観測で再同期してから再開」と同じ方針で全体再観測の後に再Plan・再validateする。**v2.6でCheckpointの用途をReplayの開始点に限定した（独立レビューR4-3、4.5）**：稼働中の再起動ではCheckpointから復元せず、空の状態から全体再観測で始め、Workspaceの所属だけをHostの所属ヒント（`MembershipHintsLoaded`）で引き継ぐ。Checkpointから稼働を再開すると、ランタイムのタイマーが失われて待ちが永久に終わらない、`HWND → WindowId`の対応が失われて状態が孤児になる、復元時の信念の扱いがJournalに残らずReplayと食い違う、という問題を避けられないためである。Fixed-log Replayのために正確な復元が必要な場合に限り、ピン留めしたCoreBuildIdと同じValidatorで再検証する（14章のPinnedPolicyと同じ「当時の版を固定」の原則）。13.1の`input-journal.ndjson`は判断を含まず、入力のみを持つ（v2.4で修正、R2-24対応。判断はReplayで再計算する派生物であり原記録ではない。9.1のADR-004改訂と整合）。

**smart constructor型を直接シリアライズしない（v2.4で追加、ADR-014/2-5対応）**：F#の`private`はアクセス制限であり、同一アセンブリ内の別ファイルから構築できてしまう可能性や、リフレクションベースのシリアライザー（F#拡張のJSONコンバーター等）が`allowAccessToPrivateRepresentation`等の設定次第で`ofDto`を経由せず直接構築してしまう経路を防げない。`ValidatedPlan`/`PinnedPolicy`/`DeployablePolicy`等のsmart constructor型は**必ずDTO経由でシリアライズする**（5.2の注意書きと同じ扱い）。17.1の共通ゲートに、これらの型がシリアライザーのコンバーター登録対象に入っていないことを検査するアーキテクチャテストを加える。
### 9.6 機密性と容量
通常収集はID、矩形、属性フラグ、イベント、Rule、Effect等のメタデータを中心とする。窓タイトル、プロセスのパス、UIテキスト、スクリーンショット、ETW高頻度イベントは必要時に限定する。`Incident Bundle`出力時はサニタイズ、含有項目確認、削除・保持期間の設定を提供する。高頻度ノイズは保管層を分けるが、再現に必要な入力イベントは安易にサンプリングしない。
## 10. Live REPLと実稼働デバッグ
### 10.1 基本方針
F# Interactive（dotnet fsiを想定）をWM Hostと別プロセスで起動し、同一ユーザーのローカルIPCで接続する。REPL側のF#式はクライアント側で実行され、Hostで任意コードを評価しない。生のHWND/ポインターやHost内部の可変オブジェクト参照を渡さない。

IPCはNamed Pipeを第一候補とし、セキュリティ記述子で利用ユーザーを制限し、可能ならリモート接続を拒否する。クライアントの接続先権限を、管轄ユーザー・セッション・実行インスタンス単位で検査する。ストリーム上のメッセージはスキーマとサイズ上限・タイムアウトを持つ。

**パイプ名の乗っ取り対策（v2.1で追加、F-3対応／v2.2でfeasibility.md §8により補強）**：Hostより先に悪意あるプロセスが同名のパイプを作成すると、クライアントが偽のHostに接続してしまう可能性がある。Hostは`FILE_FLAG_FIRST_PIPE_INSTANCE`を付けてパイプを作成する（公式ドキュメント：このフラグ付きで複数インスタンスを作ろうとすると2つ目以降は`ERROR_ACCESS_DENIED`で失敗する）。クライアントは接続後に`GetNamedPipeServerProcessId`で接続先プロセスを検証する。ただしPIDのみの検証はプロセス終了後のPID再利用に弱いため、取得したPIDについて`QueryFullProcessImageName`等でイメージパスも併せて確認する。パイプ名にはセッションIDとランダム値を含める。パイプ名の乗っ取り対策と同じくらい重要なのが**セキュリティ記述子によるアクセス制御**である：Windowsの既定のパイプセキュリティ記述子は匿名リモートユーザーを含むEveryoneにアクセスを許可するため、Hostは「同一ユーザー・同等以上のインテグリティレベルのプロセスのみ許可」する明示的なセキュリティ記述子をパイプ作成時に設定する（既存アプリの実装例として、メディアプレイヤーmpvのWindows IPC実装が同種の対策を行っている）。**負の対照例（v2.5で追加、OSS調査対応）**：GlazeWMのIPCは認証やOriginチェックの無い`127.0.0.1:6123`のWebSocketであり（コード上に該当チェックが見当たらないのみの確認で、実際にブラウザから到達可能かは未検証`[unverified]`）、本節のNamed Pipe + セキュリティ記述子 + `FILE_FLAG_FIRST_PIPE_INSTANCE`という設計を怠った場合に何が起こり得るかの反面教師として位置づけられる。

**同一ユーザー境界の限界（v2.1で追加、F-2対応）**：「同一ユーザーなら許可」（16.1）はセキュリティ境界として弱い。同一ユーザーで動く任意のプロセス（ブラウザ拡張のネイティブヘルパー、npmのpostinstallスクリプト等）がパイプに接続できる。`SetWindowPos`自体は同一ユーザーなら誰でも呼べるためInterveneによる権限昇格そのものは小さいが、問題はInspectによる情報集約（9.6のタイトル等）とDeployによる永続的な振る舞い変更である。接続時にクライアントの整合性レベルを検査し、AppContainerまたは低整合性レベルのプロセスからの接続を拒否する。
### 10.2 権限段階
| モード | 許されること | 禁止・条件 |
|---|---|---|
| Inspect | Snapshot、Policy、Journal、Explain、Analyzerを見る | ホスト状態は変更不可 |
| Fork | Snapshotを疑似OSにコピーしPolicyを比較する | 現実のOSには触れない |
| Probe | 許可された観測を対象/予算/期限付きで要求する | UI本文や破壊的操作はデフォルト禁止 |
| Intervene | ユーザー認可の上でCoreCommandをイベント投入する | Revisionチェック、Journal監査必須 |
| Deploy | 検証済みPolicyArtifactを切り替える | 既知良好版へのRollback経路が必要 |


読み取りAPIからHost内の任意コードを実行しない。変更操作は必ず型付きCoreCommandを経由してReducerのキューへ投入する。要求と結果をJournalに記録し、再現可能な介入とする。

**窓単位の前提検査（v2.1で追加、F-4対応）**：Interveneの認可を単一の全体Revisionで行うと、LOCATIONCHANGEやフォーカス変化のたびにRevisionが進むため、REPLで人が入力している数秒の間にほぼ必ず不一致となり、介入が実質的に常に拒否される（結果として`force`オプションが追加され楽観的排他が骨抜きになる懸念がある）。Interveneの前提検査は、その命令が要求する具体的な条件（例：対象WindowIdが依然Managedである、対象のIntentEpochが指定した値のままである）に対する検査とし、無関係な全体Revisionの変化では拒否しない。

**Deployが運べるものの限定（v2.1で追加、F-5対応）**：Deployは検証済みのPolicy IRと、既存Registry上のLayoutId参照のみを運ぶ。アセンブリやF#コードそのものをIPC経由でHostへ運ぶ経路としては使わない。21章のAssembly Load Context検討やPhase Eの Hot Reloadは、この制約を変更するものではない。

**Forkの実行場所（v2.1で追加、F-6対応）**：Fork（10.4）やCounterfactual Simulationの計算は、クライアント側（REPLプロセス）で行う。Host側はSnapshotのコピーを渡すだけで、重い候補ポリシーの評価や探索（12.4）によってHostのCPU/GCが圧迫され常用中のWM応答が悪化することを避ける。これは「REPL側のF#式はクライアント側で実行される」という10.1の方針と同じである。

**Probeの承認はHost側で判定する（v2.1で追加、F-1対応）**：11.1の`ProbeRequest.UserApproved`は、要求を送るクライアント自身が設定できるフィールドにしない。承認はHost側のUI、またはHostが期限付きで発行する能力トークンで行い、クライアントはそのトークンを提示する形にする（Intervene「ユーザー認可」も同じ構造にする）。

**Deployは受信時に必ずHost側で再分析する（v2.4で追加、ADR-014/5-2対応）**：「検証済みPolicyArtifactを切り替える」の「検証済み」は、14章の`DeployablePolicy`を指す。クライアント側で`DeployablePolicy`を構築していても、ワイヤ上はDTOであり型の保証はプロセス境界を越えない。Host側のDeployハンドラは受信したDTOへ常に`PinnedPolicy.ofStored`→`DeployablePolicy.analyze`をHost自身のAnalyzerで実行し、クライアントの自己申告（「もう分析済みです」）を信用しない。一方、**Rollbackは`artifactHash`の参照だけを受け付け、Host自身の採用記録（Journalの`PolicyActivated`）に載っているものだけをHostのストアから読む**（DTOそのものは受け付けない。v2.4でR2-9対応として訂正、14章参照）。再分析はしないが、`layoutRegistryManifest`とのリンク検査（`link`、14章）は必ず行う。

**REPLのエラー型は3層に分ける（v2.4で追加、ADR-014/X-2対応）**：`Result<_, string>`のような単一のエラー型にまとめると、「前提不成立（再試行に意味がある）」「権限不足（トークン再取得が必要）」「IPC切断（再接続が必要）」「受理されキュー投入済みだが成否はまだ不明」を区別できない。特に最後のケースを`Ok`にすると、「受理された」が「OSに反映された」と誤読され、ADR-003の受付/実現の区別がREPL APIで崩れる。
```fsharp
type TransportError = ConnectionLost | Timeout of waitedMs: int | PayloadTooLarge
type AuthorizationError = TokenExpired | TokenInvalid | IntegrityMismatch

type InterveneResult = Queued of Sequence: int64 // 受理された事実のみ。成否はこのSequence以降のJournalを購読して確認する
```
**前提検査はどこで評価するかを明記する（v2.4で追加、R2-25対応）**：F-4の「窓単位の前提検査」をHostの受信時点で評価すると、検査からReducerが処理するまでの間に状態が変わり得る（TOCTOU）。前提検査は決定的でJournalに残る`step`（7.1）の内側で評価する。したがって`PreconditionFailed`は同期の戻り値ではなく、`Queued`された後に`InterveneRejected`としてJournal上に届くドメインイベントである。REPL呼出しの同期の戻り値は`Result<InterveneResult, TransportError | AuthorizationError>`だけになり、ドメインの拒否は購読で確認する。
### 10.3 APIの利用イメージ（概念的な非実装コード）
task {
    let! wm = WmClient.connectLocalAsync ()
    let! snapshot = wm.SnapshotAsync()
    let! why = wm.ExplainAsync(windowId)
    let! rules = wm.PolicyAsync()
    let! diagnostics = wm.AnalyzeAsync(rules)

    // 実ウィンドウを変更しない
    let! fork = wm.ForkAsync(snapshot.Id)
    let! comparison = fork.TryPolicyAsync(candidatePolicyArtifact)

    // 必要な証拠を追加観測
    let! evidence = wm.ProbeAsync(windowId, probeRequest)

    // 実ウィンドウへの介入は明示的、かつ楽観的排他制御つき
    let! result = wm.InterveneAsync(
        snapshot.Revision,
        SuspendWindow windowId)
    return why, diagnostics, comparison, evidence, result
}
実際のF#構文・Task型・引数の署名は実装時に確定する。REPLが得るSnapshotはコピーであり、内部の現在状態へ書込みアクセスできない。
### 10.4 Time-Travel / Forkの意味
Forkは「過去のWindows全体を実際に復元する」機能ではない。特定SnapshotのCore状態と必要な観測属性、選択した疑似モデルをコピーして、別の時間軸で試行する。過去の外部アプリ内部状態を複製したとは主張しない。古いSnapshotを基に現実へ介入する場合はRevision不一致を検出して拒否する。
## 11. Active Diagnosisと情報収集
### 11.1 EvidenceGap
type EvidenceGap =
    | MissingAppIdentity of WindowId
    | MissingDialogRole of WindowId
    | MissingVisibleBounds of WindowId
    | MissingOwnershipGraph of WindowId
    | UnknownMinSize of WindowId
    | UnknownMoveDelay of WindowId
    | UnknownSelfRepositionRule of WindowId
    | MissingTargetIntegrity of WindowId // v2.6で追加（R3-7）。整合性レベルのprobeが失敗した／上限まで再probeしても得られなかった
    | MissingProcessIdentity of WindowId // v2.6で追加。ProcessKeyが得られない（4.5のSticky規則の前提）
    | MissingTitleMatch of WindowId // v2.6で追加（R5-6）。採用中Policyのパターン集合で評価したタイトル一致がまだ無い

type ProbeRequest = {
    Window: WindowId
    Fields: EvidenceGap list
    TimeoutMs: int
    MaxSamples: int
    ApprovalToken: ApprovalToken option // Host側のUIが発行する期限付き能力トークン。クライアントの自己申告ではない（v2.1で変更、F-1対応）
}
EvidenceGapは「何をまだ知らないか」を機械可読に表す。v2.6以降、これは4.5の`BeliefSlot.Unformed`の中身そのものであり、Probeの結果は観測として該当するドメインの修正関数に入り信念を修正する（ADR-016）。各Gapについて、回答に必要な観測手段、権限・機密性、時間/容量のコスト、結果の信頼性を定義する。
### 11.2 収集レベル
| Level | 観測手段 | 標準方針 |
|---|---|---|
| L0 | ウィンドウ発見・標準イベント・矩形 | 常時、最小限 |
| L1 | DPI、所有関係、DWM境界、追加Win32属性 | 対象窓に限定して要求 |
| L2 | UI Automationの必要な属性 | 明示的に限定。テキスト本文は原則禁止 |
| L3 | ETW等の高頻度計測 | 期間/容量/権限を制限し明示的に有効化 |
| L4 | 実際のウィンドウを動かす実験 | ユーザー承認のあるテスト環境に限定 |


Probeは既存Journalに新しい証拠を追加する操作であり、すでに発生した過去の未知属性をさかのぼって知る能力ではない。過去と同じ条件を再現できるようにするため、将来の監視条件変更も記録する。

**L4の境界の明確化（v2.1で追加、D-3対応）**：WMは常用中、常に実際のウィンドウを動かしている（Effectの発行そのものが「実際のウィンドウを動かす」操作である）。「L4=ユーザー承認のあるテスト環境に限定」という区別が機能するのは、通常の配置Effectではなく「診断目的で、通常のPolicy判断から外れた一時的な操作（意図的な負荷・境界値試験等）を行う」場合に限ると定義する。通常のSetRect/RestoreBeforeMove等はL4ではなくL0〜L1相当の通常動作として扱う。
### 11.3 Active Diagnosisの選択アルゴリズム
1. Journalから失敗箇所と既知の観測・仮説を抽出する。
2. 現象と整合する複数候補を列挙する（例：最小幅、DPI換算差、別処理の干渉、単純な遅延）。
3. 各候補の判別に役立つ追加観測を列挙する。
4. 観測コスト/機密性/安全性に基づきProbeを順位付けする。
5. 承認・権限を確認して実行し、結果をEvidenceIdに紐付けてJournalへ記録する。
6. 支持・反証された仮説を更新し、残る不確実性を再提示する。
実測データから因果関係を無条件に推定しない。「要求後700pxになった」という時系列だけで「アプリの最小幅が700px」と決めない。

**初期スコープの縮小（v2.1で追加、D-2対応）**：上記アルゴリズム（候補列挙→順位付け→自動実行）は費用（実装・検証コスト）が大きい一方、初期は候補の列挙と順位付けを人（REPL利用者）が行っても十分に価値が出る。Phase Dの初期実装では、1〜3の候補列挙・列挙された追加観測の提示までを機能とし、4の自動順位付けと5〜6の自動実行・仮説更新ループは、実際にProbeを何度も使う場面が出てから追加する。
## 12. 疑似窓・疑似OS・モデル合成
### 12.1 疑似OSの対象
疑似OSは本物のWindows APIやGUIを完全実装するものではない。WMが外部環境に対して観測する契約に限定する。必要な状態は窓、モニター、フォーカス、仮想時計、予定イベント、疑似振る舞い、乱数Seedである。
type WindowBehavior =
    | Immediate
    | DelayedByMs of int
    | MinimumWidth of int
    | IgnoreMove
    | SelfRepositionAfterMs of int * PixelRect
    | DisappearOnMove
    | HungAfterMs of startMs: int // v2.4で追加（R2-3対応）。startMs以降メッセージを処理せず、start+5000ms以降はOSのハング判定が成立する（feasibility.md §5のOS内部基準と一致させる）
    | Compose of WindowBehavior list

type VirtualWorld = {
    Windows: Map<WindowId, VirtualWindow>
    ClockMs: int64
    Scheduled: ScheduledSimEvent list
    Seed: int
}

// 具体的なVirtualWindow、ScheduledSimEvent、SimEventはSimulation側で定義。
// applyEffectはSimEvent（Normalizer経由でInputEventになる観測）とCallReturn（Effect適用スレッドの戻り値、EffectCallReturned/EffectBatchReturnedの元）を別に返す（v2.4追加、R2-3対応）。
val applyEffect: VirtualWorld -> GatedEffect -> VirtualWorld * SimEvent list * CallReturn list
シミュレーションは決定的なイベントスケジューラーを持ち、同時刻イベントの順序も規約化する。`NoisyObservation`、通知重複・欠落、所有ダイアログ、無効ハンドル等はモデル部品として段階的に追加する。

**Shellと同じEffect実行手順を共有する（v2.4で追加、R2-3対応）**：「事前確認→ASYNC呼出」という手順（7.1）はShellの手続きとして独自に書くのではなく、Contractsの純粋関数`executeEffectProtocol`（7.1参照）としてWindows版・Simulation版で共有し、確認手段（`probe: WindowId -> ProbeOutcome`）だけを差し替える。Simulation側の`probe`は`HungAfterMs`から`ProbeOutcome`（`Responsive`/`HungConfirmed`/`HungPending`）を導出する。手順そのものを共有することで、実装者がSimulation側に簡略版を書いて実機と再び乖離する（C-2/1-3の根本原因）ことを防ぐ。

**Raw層からの駆動（v2.1で追加、ADR-013/C-2対応）**：`applyEffect`が生成する`SimEvent`は、Windows Shellと同じNormalizer（純粋関数）を通してから`InputEvent`になる。これにより、「どの窓を管理対象とみなすか」という分類・正規化段階の不具合（ツールチップ・IME候補窓・トースト通知・Cloakされた別デスクトップの窓・UWPのゴーストフレーム・表示直後のスタイル変更等）も疑似OS上で再現できる。`WindowBehavior`（振る舞い部品）は机上で考案するのではなく、Phase 0の失敗カタログ（18章）で実際に観測されたものから選ぶ。
### 12.2 Fixed-log ReplayとCounterfactual Simulation
Fixed-log Replay：保存した入力イベント列を、ピン留めした当時のCore/Policy/Registryへ流し、同一決定が再現されるか検査する。修正済みCoreが異なる命令を出した後のOS反応は予測しない。

Counterfactual Simulation：保存したSnapshotからForkし、別のCore/Policy/Registryの命令を疑似OSが実行し、新しい観測を生成する。現実のOS挙動を完全に再現する保証はなく、モデル外の事象は`UnknownResponse`や`ModelGap`として扱う。

**完了条件としての位置づけ（v2.1で追加、D-4対応）**：実際の不具合の多くはタイミングの絡み合い（表示とスタイル変更の競合、DPI変更による自己リサイズ、スリープ復帰時の外乱）であり、単一の`WindowBehavior`部品の組合せでは精度に限界がある。Counterfactual Simulationの結果は、修正方針を検討するための補助的な証拠として使い、「Counterfactual Simulationで再現したので修正完了」という完了条件には用いない。修正の完了条件は、実機での再確認（13章Step 6、18章Phase D）を必須とする。
### 12.3 実測モデルの育成
ObservedData → CandidateBehavior[] → DifferentialProbe → ModelFit → Scenario の流れを持つ。候補モデルには、`ModelId`、対象アプリ識別、適用OS/ビルド条件、EvidenceId一覧、評価に利用した実測ケース、未説明ケース、信頼度/不確実性を付ける。モデルの自動修正で不具合が隠れないよう、生の証拠をモデルとは別に保持する。
### 12.4 有界探索・反例縮小
変動させる軸は、ウィンドウ数、モニター数、操作列長、遅延、イベント順序、失敗注入、Seed、振る舞いプロファイル。探索は因果上不可能な順序を除外する。失敗列に対し、イベント削除、時間短縮、窓数削減、モデル部品削減によって最小再現ケースへShrinkする。全ケース網羅を主張せず、探索範囲をレポートする。
## 13. 代表シナリオ：500px要求、700px観測
以下が最初のend-to-end成功シナリオである。これは説明のために選んだ一例であり、実際にPhase Aで疑似OSに実装する振る舞いの網羅的な一覧ではない（12.1参照：`WindowBehavior`はPhase 0の失敗カタログから選ぶ）。特に、このシナリオは「疑似OSが正規化済みイベントより後段の不具合」を扱うため、分類・正規化層の不具合（C-2、ADR-013）や、不可視リサイズ枠による恒常的Mismatch（4.2の座標系契約）、ハングしたアプリによる停止（7.1のADR-012）といった別種の不具合に対する同種のシナリオも、Phase B以降で同じ形式で用意する。

Step 1 — 実機で発見
PolicyRuleId=codingが`master-stack`を選び、純粋F#関数がWindow Aへ幅500pxを提案。PlanValidatorが正当な計画と判定し、Coreが`SetRect(R31, A, width=500)`を生成する。

Step 2 — 不一致
ShellでSetWindowPos相当の要求を呼び出した後に、幅700pxを観測する。Journalには、Rule選択、入力/出力、RequestId、ネイティブ呼出結果、観測、IntentEpoch、Mismatchが記録される。Coreは同一命令を無制限に再送しない。

Step 3 — Live REPL
Explain(A)で判断根拠とPendingを調べる。`UnknownMinSize(A)`、`DpiConversionMismatch`等の候補を作る。`Probe`でDPI・DWM境界・オーナー等を追加収集する。

Step 4 — モデル化
証拠に基づいて`MinimumWidth(700)`等を仮説モデルとして定義し、他候補（遅延、DPI差）と比較する。単一現象だけで確定しない。（v2.6：実機側では、Step 2のMismatchが既に4.5の`MinWidth`の信念を`Inferred 700`（根拠：R31の結果とその後の観測）に修正しており、ExplainはそれをREPLへ示せる。ここで作る疑似OSのモデルは、この窓1つの信念ではなくアプリの振る舞い全体の仮説である点が異なる。）

Step 5 — 疑似OSで再現
同じCore + 当時のPolicy/Registry + 仮想世界を実行し、500要求→700観測→収束不能の経路が再現するか確認する。

Step 6 — 修正と再検証
Layout計算またはConstraint/Fallback方針を修正し、静的Analyzer、PlanValidator、Counterfactual Simulation、Windows実機で検証する。Incident Bundleと回帰シナリオを保存し、次の変更で同じ現象が再発しないことを確認する。
### 13.1 Incident Bundleの最小内容
incident/
  manifest.json          # schema, hashes, versions, privacy flags
  checkpoint.json        # snapshot + initial Core state
  input-journal.ndjson    # reducer inputs (decisions are derived via Replay, not stored; v2.4 fix, R2-24)
  policy.json            # canonical Policy IR
  registry-manifest.json # LayoutId / ApiVersion / digest
  world-model.json       # selected behavior model + confidence/evidence
  evidence-index.json    # provenance and optional attachment references
  scenario.json          # events, expected assertions, model limitations
生タイトル・UI本文・絶対パス等は出力前にフィルタする。Journalの全データを無選別にBundleへ詰めない。
## 14. 成果物識別と再現可能性
PolicyArtifact
  ├─ policySchemaVersion
  ├─ canonicalPolicyIrHash
  ├─ layoutRegistryManifest [(LayoutId, ApiVersion, ImplementationDigest)]
  ├─ coreBuildId
  ├─ normalizerSchemaVersion
  ├─ analyzerVersion
  └─ artifactHash

SimulationArtifact
  ├─ scenarioSchemaVersion
  ├─ worldModelHash
  ├─ simulatorBuildId
  ├─ seed + eventOrderPolicy
  └─ evidenceSetHash
canonical hashはJSONシリアライザの偶発的なプロパティ順や改行に依存しない規約で計算する。ビルド成果物・設定・スキーマ・モデルを関連付け、現在のコードを過去の成果物と同一だと偽らない。スナップショットやJournalには、再生に必要なArtifactIdを保存する。

**PolicyArtifactの二分割（v2.4で追加、ADR-014/5-1・5-2対応）**：「Analyzerを通った場合のみPolicyArtifactを構築できる」という単一のsmart constructorにすると、Fixed-log Replay（12.2「ピン留めした当時のPolicy」）・Rollback（10.2「既知良好版へのRollback経路」）・起動時の最後の正常版の読込み（16.2）が、**今のAnalyzerが拒否する過去のPolicy**を読めなくなって壊れる。用途で型を分ける。

```fsharp
type PinnedPolicy = private { Ir: PolicyIR; Artifact: ArtifactIdentity }       // 構築条件：canonical hashの一致のみ
type DeployablePolicy = private { Pinned: PinnedPolicy; Report: AnalyzerReport } // 構築条件：現行Analyzerで Error なし

module PinnedPolicy =
    val ofStored : PolicyDto -> ArtifactIdentity -> Result<PinnedPolicy, IntegrityError>
module DeployablePolicy =
    val analyze : RegistryManifest -> PinnedPolicy -> Result<DeployablePolicy, AnalyzerReport>
```

- Replay/Fork/Inspectは`PinnedPolicy`を要求する（当時のまま、再分析しない）。
- 新規Deploy（REPL経由）・起動時の新規採用は`DeployablePolicy`を要求する。
- **「以前に承認済み」の根拠は、ファイル自身の`ArtifactIdentity.analyzerVersion`ではなく、Host自身が書いた採用記録に置く（v2.4で訂正、ラウンド2レビューR2-9対応）**：ファイル自身の申告を信頼の根拠にすると、同一ユーザーの別プロセス（F-2）が未分析のPolicyを正しいhashとともに`artifacts/policy/`へ直接置き、「最後の正常版」に見せかけて再分析なしに採用させる迂回が生まれる（5-2で塞いだIPC経由のDeployの穴が、ファイル経由で開いたままになる）。Journalの`PolicyActivated`/Deployエントリに`artifactHash`を記録しておき、**起動時・Rollback時は「採用記録にhashが載っているPinnedPolicyだけ」を再分析なしで採用する**。採用記録にないhashは、ファイルがどこにあっても常に`DeployablePolicy.analyze`を通す。Rollbackの要求はDTOではなく`artifactHash`の参照だけを受け付け、Host自身のストアから読む（10.2）。
- **再分析しないことと「Registryとのリンク検査」は別物（v2.4で追加、R2-10対応）**：Analyzerを再実行しなくても、`layoutRegistryManifest`/`coreBuildId`が現在のビルドで解決できるかの検査は、起動時・Rollback時・Deploy時すべてで必須である。
  ```fsharp
  val link : RegistryManifest -> PinnedPolicy -> Result<ActivePolicy, LinkError>
  ```
  「Analyzerによる品質判定は再実行しない」と「現在のビルドで実行可能かのリンク検査は必ず行う」を分ける。リンクに失敗した場合は16.2の「最後の正常版」をさらに1つ遡るか、組込みの既定Policy（monocle）へ落とす。
- **型による保証はプロセス内に限る**：REPLクライアントが`DeployablePolicy`を構築してIPCで送っても、ワイヤ上はDTOであり、同一ユーザーの別プロセス（F-2）がAnalyzerを通していないDTOを直接送れる。**Host側のDeployハンドラは、受信したDTOに対して必ず`PinnedPolicy.ofStored`→`DeployablePolicy.analyze`をHost自身のRegistry/Analyzerで実行する**（F-1の「クライアントの自己申告を信用しない」と同じ原則）。
- `analyze`はLayout Registryへの関数オブジェクト依存ではなく、`RegistryManifest`（`(LayoutId, ApiVersion, Digest) list`、Contractsの型）だけを受け取る。これによりWm.PolicyはWm.Layoutsに依存しないまま5.5の未登録検査ができる（17章の依存方向を保つ）。
- Analyzerの`Warning`/`Unknown`/`EvidenceRequirement`は`DeployablePolicy.Report`として保持し、`canonicalPolicyIrHash`には含めない（Reportは派生物であり、9.5「派生ビューと原記録を区別する」と同じ扱い）。
- **採用済みPolicyの「報告のみモード」（v2.4で追加、R2-11対応。ユーザー判断：報告のみモードで通知）**：起動時・Rollback時にPinnedPolicyを再分析しない、という決定は「判定に使わない」という意味であり、「現行Analyzerを呼ばない」ことまでは意味しない。起動時・Rollback時には現行Analyzerを**報告のみのモード**（結果を採用判断に使わない）で実行し、`Error`が出たら`PolicyStaleAnalysis`としてJournalに記録し、トレイ通知で利用者に知らせる。採用はブロックしない。分析コストはPolicy数十ルール規模（5.5 A-7）なら無視できる。
## 15. テスト・検証・品質ゲート
| レイヤ | 主な検証内容 |
|---|---|
| Policy DTO/IR | SchemaのRound-trip、ID一意、参照解決、決定的優先順位、三値論理 |
| Static Analyzer | 不正設定の拒否、明白な矛盾、PotentialOverlapを不当に確定しない |
| Layout | 0/1/多窓、比率・丸め、負座標、整数境界、対象重複、Monocleの重なり |
| PlanValidator | 未知/破棄窓、範囲外矩形、不正Monitor、重複Plan、全違反の蓄積(accumulate)、decideのRelayoutWithout/RejectWholePlan分岐、GatedEffectによる全Effect経路のガード |
| EffectFailure分類 | `classify`の決定性（同じNativeCallRecordは同じEffectFailureを返す）、未知コードのUnclassified化、DTOタグの前方互換（v2.4追加） |
| Core/Reconciler | 遅延、拒否、重複、古いTimer、Policy変更、ユーザー操作中の抑止、振動防止 |
| Belief（v2.6追加） | 規則表どおりの統合（LatestWins/Sticky/ProbeOnce/Accumulating）、入力列からの決定的な再計算、1 stepの処理順序、Hung→Respondingの修正による配置対象への復帰、MinWidthのDIP正規化 |
| WM中核状態（v2.6追加） | ドメインをまたぐ不変条件(X1)〜(X3)、`Displays`/`WorkspaceSet`のsmart constructorが不正な状態を作れないこと、モニター0台、cascaded disconnect、view意味論、zipperの操作、フォーカスの単一情報源、Workspace切替中の揺れ（SwitchSettle）、ドラッグによるモニター間移動、ユーザー指定の優先 |
| Journal | Sequence・相関、欠損・クラッシュ、バージョン識別、機密情報サニタイズ |
| REPL/IPC | 読取専用の不変性、権限制限、古いRevision拒否、監査、悪意ある入力の境界 |
| Simulation | Fixed-logとCounterfactualの区別、Seed再現、Unknownモデル、Shrink |
| Windows | WinEvent非同期/再入、DPI、所有ダイアログ、消滅窓、フォーカス拒否、安全停止 |


### 15.1 優先したい性質（Property-based / Model-based）
1. 生存していないWindowIdへ新しいSetRectを発行しない。
2. 同一Intentで未完了の同一要求を重複発行しない。
3. 古いTimerが新しいIntentを完了/失効させない。
4. Core状態とEffect列は同じ入力で一致する。
5. 破棄・再発見されたHWNDを同一の生きたWindowIdと誤認しない。
6. Layout関数の出力はPlanValidatorを必ず経由する（Desired構築の経路）。**加えて**、Shellへ渡されるEffectはGatedEffectを必ず経由する（v2.4で追加、ADR-014/2-1対応。6番は「どこかでPlanValidatorを通った」ことだけを保証し、「直前に再確認した」ことはGatedEffectが保証する）。
7. 制御不能な窓に対して要求数が無制限に増殖しない（PlanValidatorのRelayoutWithoutも1回までの再計算に限る。v2.4でdecideの仕様と整合）。
8. 指定された前提が成立すれば一定の操作列後に安定するか、明示的な停止状態に移る。
9. Inspectと`Fork`は本物のWindowsを変更しない。
10. Replayは指定ArtifactとSeedに対し同じEvent/Effect/Traceのハッシュを生成する（この性質が成立するには、Coreの内部状態がハッシュ非決定なコレクション型に依存しないことが前提になる。11参照）。
11. Coreの判断がプロセス間で再現可能である（`string.GetHashCode`のランダム化に依存する`HashSet`/`Dictionary`の列挙順にCoreの判断が依存しない。v2.1で追加、B-7対応）。
12. `ApplyBatch`は窓ごとに結果（`EffectBatchReturned`）を返し、1窓の失敗が他の生きている窓の結果を汚染しない（v2.1で追加、A-4対応／v2.4でEffectBatchReturnedの窓単位設計により具体化）。
13. ハングした対象へのEffect適用はCoreの入力処理・安全停止の受理を妨げない（v2.1で追加、E-1対応）。
14. `classify`は決定的である：同じ`NativeCallRecord`からは常に同じ`EffectFailure`が得られ、引数は`NativeCallRecord`だけで窓の状態・信念を読まない（v2.4で追加、ADR-014対応。Replay時の再計算に必須。v2.6で「窓の状態を読まない」を明記、R3-7：キーは`(Api, Space, Code)`だが一部エントリは`ElapsedUs`も読むので、「3つ組だけの関数」ではない）。
15. `ValidatedPlan`/`PinnedPolicy`/`DeployablePolicy`等のsmart constructor型は、`ofDto`を経由しない構築経路（直接のリフレクション/シリアライザー等）が存在しないことをアーキテクチャテストで検査する（v2.4で追加、ADR-014/2-5対応）。
16. フォーカスの不変条件（v2.5で追加、v2.6でzipperとグローバルMRUへの再構成に合わせて改訂、R3-3。4.4参照）：Workspace内の「最後にフォーカスされた窓が並びに存在する」はzipperの型で保証されるので検査しない（v2.5ではi3のMRUリストと同型で型の保証が無く、`FocusHistory ⊆ Order`を性質テストで守る必要があった。xmonadがQuickCheckで`StackSet`を検査するのと同じく、FsCheckで書く）。残る性質：(F1) `Focus.History`は重複を持たず、Workspacesに属する窓だけを含む。(F2) `Foreground`が表示中のWorkspaceの管理下の窓`w`へ変わったstepの後では、`w`のWorkspaceのzipperの`Focus = w`かつ`head Focus.History = w`。任意の入力列（窓の生成・消滅・Excluded化、`ForegroundObserved`、全CoreCommand）の後で成り立つこと。入力列には「フォアグラウンド中の未分類の窓の分類確定」「フォアグラウンド中の窓のExcluded化」「別モニターの窓へのフォーカス後の`SwitchWorkspace`」「`FocusMonitor`で空のモニターへ移り、続けて`SwitchWorkspace`」「hiddenなWorkspaceの窓がフォアグラウンドに残った状態での`CloseFocused`（何もしないこと）」を必ず含める（v2.6、R4-4/R4-18/R5-1/R5-4）。
17. ドメインをまたぐ不変条件（v2.6で追加、R3-2/R4-6。4.4の(X1)〜(X3)。`Displays`内の性質はsmart constructorの単体テストで検査する）：任意の入力列（`MonitorTopologyObserved`による0台を含む任意のトポロジ変化、`SwitchWorkspace`/`FocusMonitor`/`MoveFocusedToWorkspace`）の後で成り立ち、かつ`step`がこれらの入力で例外を投げない（インデクサ由来の`KeyNotFoundException`→安全停止、の経路が無いこと）。
18. モニターの切断・再接続で各Workspaceは最初の所有者へ戻る（v2.6で追加、R3-1。Hyprlandの`cascadedDisconnectsReturnEachWorkspaceToItsFirstOwner`と同型）：任意の切断列の後に任意の順序で全モニターを再接続すると、ユーザーの`SwitchWorkspace`が挟まらない限り、各Workspaceは最初に退避した時点のモニターに表示される。`MonitorLifecycle.reconcile`の単体性質としてPhase Aで検査する。
19. 信念は入力列の決定的な関数である（v2.6で追加、ADR-016）：同じCheckpointと同じ入力列からは各ドメインの同じ信念（根拠リストの順序を含む）が得られ、かつ途中のCheckpoint（Replay用）から再開しても最初から畳み込んだ結果と一致する。
20. 信念の統合規則（v2.6で追加、R3-6。4.5の規則表）：`None`のフィールドを持つ`WindowObserved`は対応する信念を変えない。スナップショット由来・イベント由来を問わず、より古い`SampleSequence`の観測は新しい信念を上書きしない（R4-10）。Sticky規則のフィールドは、最初の妥当な`Ok`の後どんな観測列でも値が変わらない。
21. 待ちとタイマーの整合（v2.6で追加、ADR-017）：期限を持つ全ての待ち（Placementの`Pending`/`IntegrityReprobes`、Windowsの`Classification = AwaitingEvidence`、Healthの`Reprobes`、Topologyの`ScreenPhase`の`Reconfiguring`/`Settling`/`Grace`、Focusの`SwitchSettle`。4.4）について、対応する`TimerId`の`StartTimer`がそれ以降取り消されずに発行されている。逆に、待ちが存在しない／`Deadline`が入力時刻より後の`TimerElapsed`は状態を変えない（古いTimerの発火の無害性。#3の具体化）。
22. 1 `step`の処理順序（v2.6で追加、R4-1。7.1）：同じ入力で`Satisfied`になるべきPendingが、照合と信念の更新の順序のために`Mismatch`にならない。`ScreenPhase`が`Reconfiguring`/`Settling`/`Grace`の間に届いた観測も、`step`の後の各ドメインの信念に反映されている。どのドメインの更新関数も他ドメインの状態を返さないこと（7.1）は、`update`の戻り値の型に対するアーキテクチャテストで検査する。
23. CheckpointとIncident Bundleに生のウィンドウタイトルが含まれない（v2.6で追加、R4-8。`TitleMatchVector`だけが残る）。
24. ユーザーの明示的な管理指定（`UserOverride`）は、`ClearUserOverride`を除くその後のどんな信念の修正・再分類の後でも維持される（v2.6で追加、R4-20）。
25. `Truth`の`combineAll`/`combineAnyOf`（4.5）は結合則と単位元の法則を満たす（v2.6で追加）。確度の合成の順序依存のような三値論理の実装のずれを捕まえるため、FsCheckで任意の`Truth`の3つ組について検査する。
### 15.2 テストアプローチ
通常の単体テストに加え、FsCheck等の性質ベーステスト、有限長のイベント列探索、実機Journalを起点とする回帰シナリオ、Windows VMでの統合テストを組み合わせる。AIが仕様とSimulatorを同時に書き換えてテストを通すことを防ぐため、シミュレーターの意味論変更・不変条件の削除は独立したレビュー対象にする。
## 16. Live運用、障害時の動作、セキュリティ
### 16.1 安全境界
- REPLから任意コマンドでHostメモリを直接変更できない。
- 本番Hostへ新しい任意F#コードをロードする場合は信頼済みのローカルコードに限定し、停止不能な関数によるハングを既知のリスクとして扱う。一般配布時には隔離Workerを検討する。
- 全てのOS変更EffectはPlanValidatorや権限ゲートを通る。
- ファイル/ネットワークへの任意アクセスを持つ「安全なDSL」と誤認させない。
- Named Pipeは同一ユーザー許可、必要に応じインスタンス単位の認可、接続数/サイズ/時間制限を持つ。
- Probeは対象・情報項目・取得回数・許可を明示する。
### 16.2 フォールバック
設定失敗：新Artifactを採用せず最後の正常版を維持。
Core例外：自動操作を止めて記録し、安全な停止へ。
Shell/対象アプリ異常：対象を保留またはFloating化、必要なら全体再観測。
Journal Writer異常：記録劣化を可視化し、安全性上不可欠な操作は停止する方針を選択可能にする。
異常終了後：保存状態をOSの真実とみなさず、全体再観測で再同期してから再開。v2.6でCheckpointからの稼働再開は行わないことを確定し、Workspaceの所属とユーザー指定だけをHostの所属ヒントで引き継ぐ（4.5、R4-3）。

外部アプリの終了・ユーザー操作・フォーカス拒否など、WMから巻き戻せない状態はある。完全復旧を保証しない。
## 17. リポジトリ構成と開発環境
src/
  Wm.Contracts/           # OS非依存の識別子、座標、イベント、Effect
  Wm.Policy/              # Policy IR, Evaluator, Analyzer, DTO
  Wm.Layouts/             # F#純粋Layout Registry / PlanValidator
  Wm.Normalization/       # Windows Event Normalizer（純粋関数、ADR-013）。Wm.WindowsとWm.Simulationが共有（v2.10追加、ADR-019）
  Wm.Core/                # reducer, reconciliation, state
  Wm.Runtime/             # Reducerループ、CheckpointファイルのI/O。Windows非依存（v2.10追加、ADR-019）
  Wm.Simulation/          # virtual world, behaviors, scheduler
  Wm.Journal/             # writer, reader（永続化する一次事実の封筒のI/Oのみ。checkpointとprojectionsはWm.Runtime/Wm.Diagnostics側、v2.10）
  Wm.Diagnostics/         # explain, incident bundle, privacy
  Wm.DebugProtocol/       # IPC contracts, permissions, revisions
  Wm.Windows/             # Win32, event pump, DPI, HWND lifetime
  Wm.Host/                # lifecycle, DI, safety and watchdog
  Wm.ReplClient/          # F# client library
  Wm.Cli/                 # inspect, replay, simulate, export

tests/
  Wm.Policy.Tests/
  Wm.Layouts.Tests/
  Wm.Normalization.Tests/ # v2.10追加、ADR-019
  Wm.Core.Tests/
  Wm.Runtime.Tests/       # v2.10追加、ADR-019
  Wm.Simulation.Tests/
  Wm.Journal.Tests/
  Wm.DebugProtocol.Tests/
  Wm.Windows.IntegrationTests/
  Scenarios/

artifacts/
  policy/
  registry-manifests/
  models/
  incidents/

docs/
  architecture.md
  contracts.md
  adr/
  operations.md
依存方向：Windows/Host/Simulation/DebugがCoreの契約を参照し、Core側からWindowsやIPC実装を参照しない。JournalWriterのI/OはShell側、Journalのイベント型は契約側に置く。**CheckpointのDTO変換はWm.Core（`private`な状態の変換はその状態を定義したモジュールでしか書けないため）、Checkpointファイルの書込み・読込（I/O）とReplay開始点としての利用はWm.Runtimeが持つ（v2.10追加、ADR-019）。** Phase Aの範囲でのプロジェクト間依存は次のとおり：Wm.Contracts（依存なし）← Wm.Policy・Wm.Layouts（Contractsのみ、PolicyとLayoutsは互いに依存しない）・Wm.Normalization（Contractsのみ）。Wm.Journal（Contractsのみ）。Wm.Core（Contracts・Policy・Layouts）。Wm.Runtime（Contracts・Core・Journal。Windowsには依存しない）。Wm.Simulation（Core・Runtime・Normalization）。Wm.Windows（Contracts・Normalization。Core/Policy/Layoutsには依存しない）。Wm.Host（Core・Runtime・Journal・Windows・Normalization）。Wm.Diagnostics（Core・Journal・Policy・Layouts）。Wm.Cli（Diagnostics・Runtime・Simulation）。

**ドメイン横断で直接読まれる値型のContracts配置（v2.10追加、ADR-019）：** 下記「信念・タイマーの型の配置」の対象に加え、`ScreenPhase`/`FocusTarget`等、6ドメイン（Topology/Workspaces/Focus/Windows/Health/Placement）が互いの状態型を参照せずに読めるようにするための小さな値型も、例外としてWm.Contractsに置く。**`Displays`自身はこの例外に含めない**：`Displays`はsmart constructorで不変条件を守る型（4.4）なので`private`なままWm.Coreの`Core.Topology`に残り、他ドメインへは`step`が組み立てる読み取り専用の射影だけを渡す。

**エラー型の配置（v2.4で追加、ADR-014対応／ラウンド2レビューR2-20で`GatedEffect`の配置を訂正）**：`NativeCallRecord`/`EffectFailure`/`classify`はWm.Contractsに置く（Windows固有の型をCoreが参照しないため、ADR-013のNormalizerと同じ理由）。`ValidationError`/`ValidatedPlan`/`Disposition`はWm.Layouts、`PinnedPolicy`/`DeployablePolicy`はWm.Policyに置く。**`GatedEffect`はWm.Contractsに型を置き、コンストラクタを`internal`にして`InternalsVisibleTo("Wm.Core")`でWm.Coreだけに構築を許す**（Wm.Coreに置くと、`GatedEffect`を消費するだけのWm.Windows/Wm.SimulationがWm.Coreのアセンブリ全体を参照することになり依存方向が曖昧になるため）。Wm.Windowsの`Native`モジュール（`callBool`/`callBoolNoCode`/`callHr`）はWm.Windows内部（internal）に置き、Wm.Contractsの`NativeCallRecord`を構築するだけで、リッチな診断情報（APIパラメータ等）はWm.Windows内部にとどめ、Journalへは`NativeCallRecord`相当の小さい形だけを渡す。外部から呼べる経路は「`GatedEffect`を受け取る通常のShell」と「8.4の安全停止用の限定API」の2つに絞る（R2-6対応）。

**信念・タイマーの型の配置（v2.6で追加、ADR-016/ADR-017）**：`Belief`/`BeliefSlot`/`Confidence`/`EvidenceRef`/`BeliefKey`と各ドメインの信念の小さなレコード（`IdentityBeliefs`等）はWm.Contractsに置く（Explain・Checkpoint・IPCのDTOに出るため）。各ドメインの状態型・更新関数・信念の修正関数、`MonitorLifecycle.reconcile`はWm.Coreの中でドメインごとのモジュール（`Core.Topology`、`Core.Workspaces`、`Core.Focus`、`Core.Windows`、`Core.Health`、`Core.Placement`）に分けて置く（ADR-018。どれも`classify`と同じくCoreBuildIdでピン留めされる。ドメイン間の参照は親の`step`が渡す読み取り専用の値に限り、ドメインのモジュール同士は互いを直接呼ばない）（`classify`と同じくCoreBuildIdでピン留めされる）。**ただし`ScreenPhase`/`FocusTarget`等、ドメイン横断で直接読まれる小さな値型は例外としてWm.Contractsに置く（`Displays`自身は`private`なままWm.Coreに残り、他ドメインへは射影だけを渡す。v2.10追加、ADR-019 R5-01対応）。**`TimerCommand`/`Response`/`TimerId`/`RetryState`はWm.Contractsに置き、`TimerCommand`の実行器（同じIDの再始動・取消を処理する小さな表）はWindows版（Hostのタイマーサービス）とSimulation版（仮想時計）で共有する純粋なロジックとして書く（7.1の`executeEffectProtocol`と同じ「手順を共有し、手段だけ差し替える」方針）。Shell内のメッセージ専用ウィンドウ上の`SynchronizationContext`（3.3の規約3）はWm.Windows内部に置く。

**依存の例外（3-3対応）**：Wm.Contracts/Wm.Policy/Wm.Layouts/Wm.Coreは外部パッケージ依存なしを維持する。Wm.ReplClientはCoreの決定性制約（3.1）の外にあるため、利便性のための外部パッケージ（`taskResult`相当、21章参照）を許容する。
### 17.1 開発・CI
Linux：F# Core、Policy、Layout、PlanValidator、Journal、Runtime、Normalizationのビルドとテスト（v2.10でWm.Runtime・Wm.Normalizationを追加、ADR-019。「Journalの純粋部分」は「Journal」に簡略化：Wm.JournalはWindowsに依存しないが、I/Oを行うので厳密には純粋ではない）、Simulation、Replay、FsCheck、CLIのビルドとテスト。
Windows：Win32 Shellの動作テスト、DPI・WinEvent・フォーカス・トレイ停止・IPC・配布の統合試験。
共通ゲート：Format/Lint、警告の選定、単体・性質ベース、回帰シナリオ、Schema互換性、artifact hash整合性、Coreの状態型がクロージャ・`Async`/`Task`を含まないことのアーキテクチャテスト（v2.6追加、ADR-017・3.3）、Coreのアセンブリ（v2.10でWm.Contracts・Wm.Normalizationにも拡大、ADR-019）が環境の時刻・乱数のAPI（`DateTime.Now`/`UtcNow`、`Stopwatch`、`Environment.TickCount`、`Random`、`Guid.NewGuid`）を参照しないことのアーキテクチャテスト（v2.6追加、3.4のDST）、Coreのドメインのモジュール同士が直接参照し合わないことのアーキテクチャテスト（v2.6追加、ADR-018）。

Windows専用APIが使えることと、Linuxで同一テストが完走できることは別。OS依存境界を分けてCIを維持する。
## 18. 実装計画とフェーズゲート

**v2.1での見直し（C-1/C-3/C-4対応）**：旧版はPhase 0が弱いスパイクに留まり、常用（複数モニター・混在DPI等）がPhase Eまで来ないため、Phase Dの前提である「実機不具合」の供給源（＝日常利用）がPhase Eまで存在せず、計画が循環していた。本版ではPhase 0を「捨てる前提の素朴なWMによる失敗カタログ収集」に変更し、Phase Bの時点で複数モニター・混在DPI・ホットキーを含む最小限の常用を開始できるようにする。Phase letterの呼称は変えず、各PhaseのスコープとGateを変更する。

#### Phase 0 — 捨てる前提の素朴なWMによる失敗カタログ収集（独立・短期）
旧版の「最小プロセスで要素技術を確認する」という弱いスパイクではなく、数百行程度の捨てる前提の素朴なWM（ホットキー + 固定のmaster-stack + `DeferWindowPos`による一括適用、Policy IRもJournalも無し）を作り、普段使う10〜15個の実アプリ（Chrome/Edge系、Electron系、Office、エクスプローラー、UWP/WinUI＝`ApplicationFrameHost`経由、Windows Terminal、VS Code、Teams/Slack、IME候補窓、ツールチップ等）相手に1〜2日実際に使い、次を具体的に検証・記録する：
- 可視フレーム矩形とウィンドウ矩形（GetWindowRect）の差分（4.2の座標系契約。A-3）をアプリごとに計測する。
- ハングしたアプリへの`SetWindowPos`/`SendMessage`相当の呼出が呼び出し側をどう止めるか、`SendMessageTimeout`/`IsHungAppWindow`での検知可否（7.1のADR-012、E-1）。
- 実際に使いたいキーバインドが`RegisterHotKey`で確保できるか、できない場合の`WH_KEYBOARD_LL`の実現性（8.1、B-2）。
- ホットキー起点でキュー経由の非同期処理を挟んだ後も`SetForegroundWindow`の許可が残るか（8.1、B-3）。
- 表示直後に属性（スタイル・所有関係）が変わる窓、UWPのゴーストフレーム、Cloakされた窓の見え方（分類・正規化の難所、C-2）。
- 最小化・最大化・Snapからの復元時の`SetWindowPos`の振る舞い（6.2のバッチ適用・`RestoreBeforeMove`、A-4）。
- 昇格（管理者）窓への操作がどう失敗するか。F23で`ERROR_ACCESS_DENIED`の失敗自体は確定済み。**v2.9でF35により解消**：`OpenProcessToken`+`GetTokenInformation(TokenIntegrityLevel)`のMedium→High方向は実デスクトップ（`dragonflyg4`）で失敗0件で成功することを確認した。**v2.6でこの項目はPhase Aをブロックしない**：分類は`Movability`の信念だけを読み、その信念は`Denied`の実績からも形成されるので、このAPIが拒否されても挙動は正しく、失うのは「最初の1回の失敗を省く」最適化とExplainの説明材料だけである（F35により、実際には拒否されないことも確定した）。
- スリープ復帰・モニター抜き差し・RDP接続時にOS自身が窓を動かす様子（7.3のE-3対応）。**決定ロジック自体（反応の方針）はPhase Aでシミュレーション対象にできる（v2.5追加、OSS調査対応、7.3参照）**。実機スパイクはOSが実際に窓を動かす部分の観測に集中する。
- 全画面アプリ・ゲームの検出手段（E-4）。
- Explorer再起動時の`TaskbarCreated`、WorkArea変更、トレイアイコン消失（8.4、E-2）。
- DPIが異なるモニター間を移動した際の対象アプリの自己リサイズ（8.3、B-6）。
Gate：上記の検証項目について実際の観測結果を記録した「失敗カタログ」を作成し、それぞれについて「無視してよい」「Phase Aの型に反映する」「Phase Bで対処する」のいずれかに分類する。Phase Aの`WindowBehavior`（12.1）はこのカタログから選ぶ（机上で考案しない）。

#### Phase A — Core + 中核状態モデル + Policy IR + 最小Simulation
- WmStateをドメインごとの型（4.4、ADR-018：Topology/Workspaces/Focus/Windows/Health/Placement）、CoreCommand（ADR-011）を最初に固定する。Policy IRより先にこれを決める。
- 窓の信念と修正規則（4.5、ADR-016）。Policy評価は最初から信念の上に書く（生の`WindowSnapshot`を読む評価器を先に書いて後で置き換えない）。
- 待ちとタイマーのデータ表現（3.3、ADR-017）：`TimerId`、`TimerCommand`の実行器（Simulation版）、Pending/分類期限/抑止区間/ScreenSettle/ScreenGrace/Workspace切替/再probeの各待ち。
- モニターのライフサイクルの純粋関数`MonitorLifecycle.reconcile`（4.4、7.3のS8決定ロジック。v2.5で「シミュレーション対象にできる」と書いたものを、v2.6でPhase Aの実装項目として確定、R3-11）。
- F# Policy IR、Evaluator（三値）、Analyzer（5.5のスコープ縮小版。A-7）。
- Master–StackとMonocle、独立したPlanValidatorとGatedEffect（`ApplyBatch`/`RestoreBeforeMove`の前置契約検査はGatedEffect側、v2.4でR2-8対応として分離。A-4）。
- 単一ReducerとDesired/Observed/Pending、Phase 0の失敗カタログから選んだ疑似窓の振る舞い（Immediate/Constrained/Delayedに加え、分類・正規化層の振る舞いもRaw層経由で表現する、C-2/ADR-013）。
- 最小JournalとCLI Explain（9.1の方針：判断は永続化せずReplay再構成、B-1）。
Gate：（旧版の「500px→700pxの不一致を疑似OS再現」はWindowBehaviorにMinimumWidthを入れれば必ず成立するため検証として不十分、C-4）次の2点で確認する：(1) 性質ベーステスト（15.1 #1〜#8、#11〜#13、および中核状態・信念・待ちに関する#16〜#21。v2.6で#16〜#21を追加、R3-11：Phase Aは中核状態を固定するPhaseなので、その不変条件が未実装のまま通過できてはならない）が通ること、(2) Phase 0の失敗カタログのうち「Phase Aの型に反映する」と分類した項目それぞれについて、対応する疑似窓シナリオが用意され、RuleId/RequestIdで追跡できること。

#### Phase B — Windows Shell + 最小常用の開始
- WinEvent/全体Snapshot/ハンドル寿命/Raw Event記録付きの正規化（純粋Normalizer、C-2/ADR-013）をCoreへ接続。
- OS発行結果と観測結果をJournal上で区別する。Effect適用は専用スレッド＋タイムアウト（7.1のADR-012）。安全停止の制御経路を別スレッド/別プロセスで実装する（8.4、E-1/E-2）。
- Incident Bundle、ログビュー、停止/復旧を組み込む。
- （v2.1で前倒し）複数モニター・混在DPIの基本動作、Phase 0で確保したホットキーによる最小限のキーボード操作（FocusNext/SwapWithMaster/SwitchWorkspace等）を実機で動かす。旧版ではこれらをPhase Eに置いていたが、日常利用（dogfooding）を始めるために必要な最小集合として本Phaseに前倒しする（C-3対応）。
Gate：実機の1件の不具合を、入力→判断→Effect→実観測として調査できる。かつ、開発者自身が複数モニター・混在DPI環境で日常的な作業にこのWMを使い始められる（Phase D以降の実機不具合の供給源をここで確立する）。

#### Phase C — Live REPL + Fork/Replay
- ローカルの型付きIPC、Inspect/Explain/Analyzer、Snapshot Fork（計算はクライアント側、F-6）。
- Fixed-log ReplayとCounterfactual Simulation（12.2の完了条件としての位置づけ、D-4）、Policy差分比較。
- Interveneは最小の安全なCoreCommandに限定。前提検査は窓単位（F-4）、承認はHost側トークン（F-1）、Deployが運べるのはPolicy IR+Registry参照のみ（F-5）。
Gate：稼働中のWMをF# REPLから調査し、実窓を変えずに新PolicyをFork検証できる。

#### Phase D — Active Diagnosis + モデル合成（縮小スコープ、D-2）
- EvidenceGap、観測レベルとProbe予算、Privacy policy。承認はHost側トークン（F-1）。
- 初期は候補列挙・提示までを自動化し、順位付け・実行は人（REPL利用者）が行う（11.3、D-2）。
- Phase Bから続く日常利用で見つかった実機不具合を元に、候補Modelを構成し疑似OSとの不一致を示す。
- シナリオShrink、回帰保存、実機再確認（Counterfactualの結果を完了条件にしない、D-4）。
Gate：実機不具合→追加証拠→疑似OS再現→修正→回帰テストの閉ループを完走。

#### Phase E — 常用の仕上げ・配布
- Phase Bで開始した常用で見つかった、よく使うアプリの例外、手動リサイズ、Policy切替の仕上げ。Phase Bでは扱わなかった複数モニター・混在DPIの高度なケース（タスクバー位置変更、WorkArea変更の連鎖等）。
- リカバリーUX、診断ビュー、性能計測、利用者向けSDK、配布・更新。
- 設定コードのWorker分離と安全なHot Reloadを必要性に応じ検討（Deployはアセンブリを運ばない、F-5の制約を維持）。
Gate：日常利用の回帰テスト群と、起動/停止/異常終了の手順が安定する。
## 19. 最小PoCの具体的な実装単位

**第0の縦切り（Phase 0、v2.1で追加）**：捨てる前提の素朴なWM。`ThrowawayWm.fsx`相当（ホットキー登録、固定master-stack、`DeferWindowPos`一括適用のみ）と、実際に使って記録する`FailureCatalog.md`。このコードはPhase A以降のCoreへそのまま持ち込まない。

第一の縦切り（Phase A）として次のファイル相当を優先実装する。
WmState.fs（4.4の親の状態と`step`の合成、ADR-011/ADR-018）
Topology.fs / Workspaces.fs（Zipperを含む） / Focus.fs / Windows.fs / Health.fs / Placement.fs（4.4のドメインごとのCore。各ファイルが自分の状態型・更新関数・信念の修正関数を持つ。ADR-018、v2.6追加）
CoreCommand.fs
Beliefs.fs（4.5の`Belief`/`BeliefSlot`/`Confidence`と`Truth`の`combineAll`/`combineAnyOf`。修正関数は各ドメインのファイルに置く。ADR-016、v2.6追加）
Timers.fs（3.3の`TimerId`/`TimerCommand`/`RetryState`とSimulation用の実行器。ADR-017、v2.6追加）
MonitorLifecycle.fs（4.4の`reconcile`とcascaded disconnectの性質テスト。v2.6追加、R3-11）
ErrorTypes.fs（`NativeCallRecord`/`EffectFailure`/`classify`。ADR-014、v2.4追加。Shell/Simulation共用）
PolicyTypes.fs
PolicyEvaluator.fs
PolicyAnalyzer.fs
PolicyArtifact.fs（`PinnedPolicy`/`DeployablePolicy`のsmart constructor。ADR-014、v2.4追加）
LayoutRegistry.fs
MasterStack.fs
Monocle.fs
PlanValidator.fs（accumulateする`validate`と`decide`。v2.4で`ValidationInput`/`ValidatedPlan`に具体化。`ApplyBatch`/`RestoreBeforeMove`の前置契約検査はGatedEffect.fs側、v2.4でR2-8対応）
GatedEffect.fs（Shell直前の最終ゲート。ADR-014、v2.4追加）
Reducer.fs（入力キューの処理とJournalへの書き込み順序。`step`の合成そのものはWmState.fs）
JournalTypes.fs（永続化対象を入力・OS呼出結果・Artifactに限定し、判断は含めない。9.1参照）
JournalWriter.fs
WindowEventNormalizer.fs（純粋関数。Shell/Simulation両方から参照する、ADR-013）
VirtualWorld.fs
WindowBehaviors.fs（Phase 0のFailureCatalog.mdから選ぶ）
ScenarioRunner.fs
Tests/ConstrainedWindowScenario.fs
この段階でGUI、一般配布、完全なREPLプロトコルは不要。疑似OS上で不具合を再現し、判断と差分を説明できることが最優先。

**プロジェクト割当と実装順序（v2.10追加、ADR-019）**：上記のファイル優先順位（ADR-011が定める各ドメインの意味・不変条件を固定する優先順位）はこの版で変更しない。本節はこれに、実際にF#としてコンパイルできる順序とプロジェクト（§17）の割当を付録として与える。2ファイル（`Core/DomainEvents.fs`、`DecisionTrace`）は上記の優先順位の一覧に無かったので追加する。

| 実装順 | ファイル | プロジェクト | 備考 |
|---|---|---|---|
| 1 | ErrorTypes.fs（`NativeCallRecord`/`EffectFailure`/`classify`） | Wm.Contracts | Observationより前に必要（決定6層2） |
| 2 | Beliefs.fs（`Belief`/`BeliefSlot`/`Confidence`、各ドメインの信念の値の型・小レコード） | Wm.Contracts | 決定6層3〜4 |
| 3 | （共有語彙：`ScreenPhase`/`FocusTarget`/`Management`/`LayoutRef`/`LayoutArgs`/`EffectiveManagement`/`RegistryManifest`/`WorkspaceId`。`Displays`は含めない） | Wm.Contracts | 決定6層5。個別ファイルを立てず、関連する既存ファイルへ振り分けてよい |
| 4 | Timers.fs（`TimerId`/`TimerCommand`/`RetryState`とタイマー実行器） | Wm.Contracts | 決定6層6。実行器はShell/Simulationが共有する純粋ロジック（基準(4)） |
| 5 | CoreCommand.fs | Wm.Contracts | `InputEvent`/`Effect`/`EventEnvelope`とともに決定6層7 |
| 6 | GatedEffect.fs | Wm.Contracts | `internal`コンストラクタ＋`InternalsVisibleTo("Wm.Core")`。決定6層8 |
| 7 | JournalTypes.fs | Wm.Contracts | Contractsの型だけで書ける永続化対象の種別のみ（決定6層9。`PolicyStaleAnalysis`等の扱いは21章の未決事項） |
| 8 | PolicyTypes.fs / PolicyEvaluator.fs / PolicyAnalyzer.fs / PolicyArtifact.fs | Wm.Policy | |
| 9 | LayoutRegistry.fs / MasterStack.fs / Monocle.fs / PlanValidator.fs | Wm.Layouts | |
| 10 | WindowEventNormalizer.fs | Wm.Normalization | v2.10新設プロジェクト。Wm.WindowsとWm.Simulationが共有（ADR-013） |
| 11 | JournalWriter.fs | Wm.Journal | 一次事実の封筒の書込み・読込（I/O）のみ |
| 12 | **Core/DomainEvents.fs**（`DomainEvent`のDU。`MoveCause`/`ShownBy`/`PendingResolved`の結果DU） | Wm.Core | §19の一覧に無かった追加ファイル（決定7） |
| 13 | **DecisionTrace.fs**（`StepResult.Trace`の要素。4.4の`update`の戻り値、7.1の「導出不整合の疑い」の記録先） | Wm.Core | §19の一覧に無かった追加ファイル（決定7） |
| 14 | Topology.fs（`MonitorLifecycle.reconcile`を同梱） / Workspaces.fs（Zipperを含む） / Focus.fs / Windows.fs / Health.fs / Placement.fs | Wm.Core | `Core.Topology`等。6ファイル間に強制順序は無い（決定2・決定8） |
| 15 | WmState.fs（`WmState`型 → `RuntimePolicy`型 → `StepResult`型 → `step`関数） | Wm.Core | 6ドメインの後。最初はTopologyのフィールドだけを持ち、ドメインを実装するたびにフィールドを追加する（決定8） |
| 16 | Reducer.fs | Wm.Runtime | 入力キューの処理とJournalへの書込み順序。Windows非依存 |
| 17 | VirtualWorld.fs / WindowBehaviors.fs / ScenarioRunner.fs | Wm.Simulation | |
| 18 | Tests/ConstrainedWindowScenario.fs | Wm.Simulation.Tests | |

最初の縦切り（決定8）は`Core.Topology`の`ScreenSettle`について性質ベーステスト#21を、Wm.Contracts全10層・Wm.Journal・Wm.Runtimeの最小のReducerとTimer実行器のSimulation版・`Core/DomainEvents.fs`・`DecisionTrace.fs`・`Core.Topology`・`WmState.fs`を揃えたうえで検証する。詳細はdocs/adr-019-draft.mdの決定8参照。

次の縦切り（Phase B）でWindowsの`EnumWindows`、`SetWinEventHook`、`GetWindowRect`、`SetWindowPos`とイベントキューをつなぐ。Effect適用は専用スレッド（ADR-012）とし、WindowEventNormalizer.fsをShell側からも呼ぶ。P/Invoke呼出しはNative.fs（`callBool`/`callBoolNoCode`/`callHr`。8.1、ADR-014）を経由し、ErrorTypes.fsのEffectFailure型をCore/Simulationと共有する。このPhaseで複数モニター・混在DPI・ホットキーによる最小限の常用を開始する（18章）。REPLはその後、既存のSnapshot/Journal/SimulationインターフェースをIPCへ公開するだけに留める。
## 20. 重大リスクと対策
| リスク | 兆候 | 対策 |
|---|---|---|
| WinEventの再入・通知遅延 | 状態逆転、二重処理 | 薄いCallback→キュー、Sequenceと再観測 |
| DPI・座標変換差 | 常に微妙にサイズがずれる | 物理/可視フレームの分離、変換根拠をJournal化 |
| 異なるアプリ動作 | 配置が震える、無視される | Retry上限、Suspended/Floating、EvidenceGap |
| 任意F#拡張がHostを止める | 動作停止、Hot Reload不能 | Trusted code限定、初期は限定的更新、将来Worker分離 |
| Journalの増大・機密漏洩 | 容量増加、共有危険 | 層別保管、暗号化/アクセス制限、保持期間、サニタイズ |
| Simulatorの誤った自信 | 実機で再現しない | UnknownResponse、証拠由来/モデル版、実機再確認 |
| Analyzerの過大保証 | 「検証済みなのに失敗」 | 保証範囲を明記、Runtime PlanValidatorと統合テスト |
| REPLからの不正操作 | 意図しない窓の移動 | Read-only default、IPC認可、Revisionチェック、監査 |
| 開発が言語処理系へ逸脱 | DSL拡張だけが進む | IR最小化、通常F#関数、縦切りの成果で判断 |
| Win32特殊窓・特権境界 | 取得/操作不可 | Excluded/Suspended、非特権の範囲を明示 |
| ハングしたアプリへの同期的なOS呼出（v2.1追加、E-1） | WM全体が応答しなくなる、安全停止も効かない | 専用Effect適用スレッド、SendMessageTimeout/SWP_ASYNCWINDOWPOS、IsHungAppWindow事前確認（ADR-012） |
| 安全停止の制御経路の実体が未定義（v2.1追加、E-2） | ハング時に停止手段ごと止まる、Explorer再起動でトレイアイコンが消える | 別スレッド/別プロセスでの実装、TaskbarCreated処理による再登録（8.4） |
| 画面構成変化の嵐：スリープ/ロック/モニタ抜き差し/RDP（v2.1追加、E-3） | OS自身の一斉移動と衝突し全窓が振動検知対象になる | ScreenReconfigurationInProgress中は再配置を抑止、安定後に全体再観測（7.3） |
| 全画面アプリ・ゲーム・動画（v2.1追加、E-4） | 意図せずタイル化される | 全画面検出（SHQueryUserNotificationState等）による除外（8.1） |
| 他の配置機構との競合：Snap/FancyZones/アプリ自身の位置復元（v2.1追加、E-5） | アプリ別振る舞いモデルの偽陽性学習（12.3）、WMとの引っ張り合い | 既知の競合シグネチャの除外、振動検知の強化 |
| Journalがタイトル履歴の集積所になる（v2.1追加、E-6） | メール件名・URL・ファイル名等の行動履歴が同一ユーザーの他プロセスから閲覧可能になる | Inspect既定でタイトル除外、TitleContains使用時も記録しない（5.3/9.6） |
| .NETランタイムの特性（v2.1追加、E-7） | GC一時停止がフック/入力経路に重なる、起動時間、NativeAOT時のリフレクション制約、配布先に.NET SDKが必要（REPLクライアント） | 計測して予算化、フックとGC負荷の高い処理を分離 |
| 診断基盤への開発逸脱（v2.1追加、E-8） | Phase A〜Dの間使えない状態が続き開発動機が尽きる | 18章のPhase再構成でPhase Bから常用を開始する |
| 誤った信念の固着（v2.6追加、ADR-016） | 一度`NotMovable`/`Hung`と推定した窓がずっと管理外のまま、Stickyの`AppIdentity`が誤った初期値で固定される | 確度と根拠をExplainで常に示す、規則ごとの再probeの契機（4.5）、ユーザーの明示操作で`Movability`を試し直す、修正規則はCoreBuildIdで版管理しReplayで改良前後を比較できる |


## 21. 未決事項と判断方法
| 未決事項 | 比較軸 | 決めるタイミング |
|---|---|---|
| Journal永続形式（NDJSON/SQLite/組合せ） | 書込み耐久性、Replay速度、範囲検索、移行、容量 | Phase A〜B |
| IPC（Named Pipeの具体実装） | 認可、復旧、Windows対応、診断しやすさ | Phase C |
| Layout Registryのロード | 静的リンク、Assembly Load Context、隔離Worker | Phase Aと常用化 |
| Policy切替の原子性 | Pending失効、状態Migration、Rollback | Phase C〜E |
| Snapshot保存間隔 | 復元コスト、容量、クラッシュ整合性 | Phase B |
| Probe対象の拡張 | UIA/ETW取得可能性、権限、個人情報 | Phase D |
| Scriptable診断DSL（CE等） | 実験の再現性、F#表現力、AST化必要性 | Phase D以降 |
| 将来の独自DSL | IRだけでは不十分な具体的ユースケースの有無 | 常用後 |
| Effect一括適用の実装方式（v2.1追加、v2.2でfeasibility.md §4により既定値決定） | `DeferWindowPos`系とSWP_ASYNCWINDOWPOSの選択、部分失敗時の扱い。既定は個別SetWindowPos+SWP_ASYNCWINDOWPOS（6.2参照）、ちらつき差をPhase 0で比較し再検討 | Phase A〜B |
| ワークスペースの実装方式（v2.1追加／v2.5でCloakを選択肢に追加、OSS調査対応） | 窓を隠す/画面外へ移動/最小化/Cloak（GlazeWMの既定`HideMethod::Cloak`）のいずれか、クラッシュ時の復元容易性。Cloakは文書化された`DWMWA_CLOAK`が外部プロセスのHWNDに対し`E_ACCESSDENIED`で拒否されるため（komorebiも同じエラーに遭遇、ADR-015参照）、実装には未文書化の`IApplicationViewCollection`/`IApplicationView::SetCloak`が必要になり、未文書化APIへの依存リスクを他の3方式と比較検討する。**v2.6で比較軸を2つ追加（R3-8）**：(a) WMが自分で付けたCloakを観測側で区別できるか——シェル起因のCloakは他の仮想デスクトップの窓・UWPのゴーストフレームと同じ`DWM_CLOAKED_SHELL`ビットになり得るので、`CloakState`の理由ビット（4.2）だけでは区別できない可能性がある。自分が発行したCloak Effectの記録と照合して「WM自身によるCloak」を信念として持てるか、を検証する。区別できないと、WMが隠した窓を分類が「他デスクトップの窓」としてExcludedにし、所属Workspaceを失う。(b) 8.4の安全停止の限定APIにCloakの解除（未文書化COMの呼出）を含められるか——含められないとクラッシュ時に窓を戻せない。(a)(b)のどちらかが満たせない場合はCloakを選択肢から外す。**v2.8で(a)に部分的な実機データを追加（F34）**：公開・文書化APIの`IVirtualDesktopManager`（`shobjidl_core.h`）だけで、同一プロセスの窓を別の仮想デスクトップへ移すと`DWMWA_CLOAKED`が正しく`DWM_CLOAKED_SHELL=2`を返すことを確認し、Cloak理由ビットAPIが機能すること自体は実証された。ただし(a)の本題（WM自身によるCloakと他要因によるCloakの判別）はまだ検証していない。**代わりの着想（未検証）**：WMが自分専用の隠しデスクトップを1つ確保しておき、`GetWindowDesktopId`（公開API）の返すGUIDがその隠しデスクトップと一致するかどうかで自己/他要因を判別する——これなら`CloakState`の理由ビットに依存せず、Cloakそのものも使わずに済む可能性がある。ただし`MoveWindowToDesktop`は他プロセスの窓には`E_ACCESSDENIED`で使えないため（確認済み）、WMが管理対象アプリの窓を実際にこの隠しデスクトップへ移す手段は依然未文書化COMが必要で、この着想は「判別方法」の代替であって「移動手段」の代替ではない | Phase A |
| ofDto用の`Result.traverse`/最小`result`ビルダー導入（v2.4追加、ADR-014/3-2対応） | `PolicyIR.ofDto`等が3段以上の`match Ok/Error`ネストを要した最初の時点で、Contractsに自作（20行未満、外部依存なし）を追加する。発動条件は「必要になってから」で、事前に作らない | ofDtoを書く時点（Phase A） |
| ReplClientでの`taskResult`（または同等のCE）許可（v2.4追加、ADR-014/3-3対応） | 10.3の`task { }`は複数手順を連ねる際にResultとのネストが生まれる。Core/Policy/Layoutsは外部依存なしを維持するが、Wm.ReplClientは3.1の決定性制約の外にあるため、利便性優先で外部パッケージ（FsToolkit.ErrorHandling等）か自作`taskResult`の導入を許す | Phase C |
| ~~`DenialKind.TargetIntegrityHigher`の生成手段~~ → **v2.5でADR-015として解決済み**（専用`DenialKind`は増設せず、`WindowSnapshot.TargetIntegrity`という観測属性にする。8.1のclassify対応表・4.2参照）。~~残る論点は対象プロセスの整合性レベルを事前取得する具体的な実装手段のみ~~ → **v2.9でF35により解決済み**：`OpenProcessToken`+`GetTokenInformation(TokenIntegrityLevel)`は実デスクトップ（`dragonflyg4`）でMedium→High方向に失敗0件で成功することを確認した。**v2.6でこの論点の重みを下げた（R3-7）**：v2.5ではこのAPIの成否にADR-015全体が依存していた（失敗すると「開けなかった＝おそらくHigh」というエラーコードからの原因推定しか残らない）が、v2.6では分類が読むのは`Movability`の信念であり、`Denied`の実績だけから形成される。整合性レベルのprobeは「最初の1回の失敗を省く」最適化とExplainの材料に格下げしたままでよい（F35はこの最適化が実際に効くこと自体を確認したという位置づけ） | `OpenProcess`は`PROCESS_QUERY_LIMITED_INFORMATION`が必須、`PROCESS_QUERY_INFORMATION`では同一ユーザーでも上位整合性プロセスの取得に失敗する（F24で確定）。`OpenProcessToken` + `GetTokenInformation(TokenIntegrityLevel)`をMedium整合性プロセスからHigh整合性プロセスに対して呼んだ場合の成否も**F35で実機確認済み（成功）**。`TargetIntegrity`probeの実装はこの前提で進めてよい | 解決済み（F35）。Phase Aへ進む |
| ~~hiddenなWorkspaceの窓が自分でフォアグラウンドになったときWorkspaceを切り替えるか~~ → **v2.6でプロジェクトオーナー確認済み：(A)を採用**（v2.6追加、R4-5/R4-16） | (A) 切り替える（v2.6の既定。OSが見せている窓とWMの状態が一致するが、通知やリマインダで作業中の画面が変わる）／(B) 切り替えず、その窓のWorkspaceを示す通知だけ出す（画面は安定するが、見えない窓にキー入力が流れ得る）。WM自身の切替操作による揺れは`SwitchSettle`（4.4）で既に吸収済みなので、この選択はアプリの自己アクティブ化とタスクバー操作だけに関わる | 確定（v2.6） |
| ~~分類待ち中にユーザーがWorkspaceを切り替えた場合の所属~~ → **v2.6でプロジェクトオーナー確認済み：(A)を採用**（v2.6追加、R5-4） | (A) 分類確定時にその窓がフォアグラウンドならアクティブなWorkspace、そうでなければ出現時のWorkspace（v2.6の既定。使用中の窓が隠れてキー入力が見えない窓へ流れる事故を防ぐ）／(B) 常に出現時のWorkspace（「窓が出現したWorkspaceに整理したい」ワークフローに合うが、上記の事故が起き得る） | 確定（v2.6） |
| ~~ユーザーが最小化したTiled窓のLayout上の扱い~~ → **v2.6でプロジェクトオーナー確認済み：(A)を採用**（v2.6追加、R6-4） | (A) 枠を詰める（v2.6の既定。i3/komorebi寄り。最小化＝一時的に配置から外れる）／(B) 枠を残す（復元したときに元の位置に戻るが、画面に空白が残る）。どちらでも、WMがユーザーの最小化を取り消さないこと（4.4）は共通 | 確定（v2.6） |
| ~~Workspace内のzipperをコマンド（意図）で回すか観測でだけ回すか~~ → **v2.6でプロジェクトオーナー確認済み：(A)を採用**（v2.6追加、R6-6） | (A) コマンドで回し、観測でも上書きする（v2.6の既定。キーの連打に強く、`ForegroundDenied`でも次のキーが期待どおりに進む）／(B) 観測でだけ回す（ADR-003に忠実で、zipperが常に実際のフォーカスと一致するが、観測が届く前の2回目のキーが同じ窓を起点に回る） | 確定（v2.6） |
| ~~Unknown分類の期限超過時の既定~~ → **v2.6でプロジェクトオーナー確認済み：(A)を採用**（v2.6追加、R4-19） | (A) Floating（v2.5からの既定。遅いprobeの窓も見える位置に出て操作できる）／(B) 管理しない（Discoveredのまま。誤ってフォーカス巡回に入る事故が無いが、遅いprobeの窓はユーザーの操作待ちになる）。v2.6で期限の対象を候補条件を満たす窓に限ったので、(A)の主な弊害（不可視窓の大量Floating化）は既に除いてある | 確定（v2.6） |
| 信念の修正規則の具体的な閾値（v2.6追加、ADR-016） | `MinWidth`を配置に反映するまでの観測回数、`Responsiveness`の再probeのバックオフ、`TargetIntegrity`の再probe上限、`ScreenSettle`/`ScreenGrace`の長さ。規則の**種類**（4.5の表）は本版で確定し、数値だけを実測で決める | Phase A（Simulation）→Phase B（実機Journal） |
| `PolicyStaleAnalysis`（永続化はするがPolicy側の型`AnalyzerReport`を運ぶJournalエントリ）のペイロードの具体的な表現（v2.10追加、ADR-019） | Contractsの型だけで書ける他のJournalエントリ（決定6層9）と違い、`AnalyzerReport`はWm.Policyの型なので、そのままでは「Contractsの型だけで書ける」という層9の基準を満たさない。Contracts側に写し（DTO相当）を置くか、Policy側の型への参照を許すレイヤー違反を認めるかの選択 | Phase A |
| TargetIntegrityのProbeOnce規則（§4.5）の起点（v2.17追加、Core.Placement着手時に発見） | Coreが窓の発見時に能動的に`Reprobe (w, KTargetIntegrity)`Effectを発行して初回probeを要求するか（A）、Shellが列挙時に自発的にTargetIntegrityを問い合わせて`WindowSnapshot`に含める前提に置くか（B）。本書は`WindowSnapshot.TargetIntegrity`を「今回Shellが問い合わせた属性だけを運ぶ部分観測」と説明するだけで、どちらかを明記していない → **v2.17で(A)を採用**：ADR-002/ADR-017の一貫性（Coreが全ての待ち・要求を明示的なデータとして発行し、Shellの暗黙の判断に委ねない）と、`Effect.Reprobe`/`BeliefKey`がまさにこの目的のために定義されていること（4.5の`BeliefKey`コメント「再probeの対象を指す名前」）から、(A)が本書の既存設計と整合する。具体的な起点：Placementが`WindowObserved`を処理する際、その窓のPlacement状態に`TargetIntegrity`のエントリがまだ無く（発見時）、かつ`snapshot.TargetIntegrity = None`（Shellが今回は問い合わせていない）なら`Reprobe (w, KTargetIntegrity)`を発行する。`Some obs`ならその場で修正し、新たにProbeは発行しない。失敗時の再probeは既存の`PlacementTimer.IntegrityReprobe`（3.3）で、Healthの`ResponsivenessReprobe`と同じ指数バックオフのパターンで行う | Phase A（Core.Placementの次ラウンドで実装） |
| ~~Windows Event Normalizerの「候補条件」判定とCoreの状態（`ClassificationState`）の関係~~ → **v2.12でWindowEventNormalizer.fs着手時に解決：Normalizerは候補条件を一切判定しない**（v2.10追加、ADR-019。第1ラウンドレビューR3-2で指摘） | Normalizerは純粋関数としてWm.Normalizationに置くが（4.4の`NotCandidate`判定が参照する「可視・非ツール窓・非Cloak等」の候補条件は）、Windows側の生の属性だけで判定できるのか、Core（`ClassificationState`）の状態を読む必要があるのか未確定。後者ならNormalizerをCore非依存の純粋関数に保てない → **Normalizerは候補条件の判定も`ClassificationState`への遷移判断も一切行わない**。Raw観測（`RawWindowEvent`、§12.1）を対応する`InputEvent`へ無条件・無加工で詰め替えるだけの事実変換に限定する。候補条件の評価と、一度`Classified`になった窓を候補条件逸脱で`NotCandidate`へ戻さないという§4.4のスティッキーな規則は、将来のCore.Windowsドメイン（まだ実装していない`ClassificationState`）だけが、Normalizerが渡した生の属性（`WindowSnapshot`の各フィールド）を読んで行う。これによりWm.NormalizationはWm.Contractsのみに依存し続けられ（§17の依存方向表のとおり）、R3-2の懸念（Core非依存を保てない可能性）は生じない | 解決済み（v2.12）。Phase A（Core.Windows着手時に候補条件の具体的な判定ロジックを書く） |


方針：先に必要な能力を実装し、表現形式や追加ツールを目的化しない。F*や全面Deep Embedded DSLを最初の前提に戻さない。
## 22. プロジェクト全体の受け入れ条件
1. Windows ShellとSimulation Shellで同じCoreの意思決定実装を使う。
2. Policy IRが保存・分析・比較可能で、F#レイアウト実装を`LayoutId + Version + Digest`で識別する。
3. Policy条件のUnknownをFalseとして扱わず、Analyzerが未知の検証範囲を明示する。
4. 任意のLayout関数出力もPlanValidatorを通過しなければDesiredになれず、発行元を問わず全EffectはShell直前のGatedEffectを経由しなければ適用されない。**例外：8.4の安全停止・復旧経路**（Coreに依存しないことが要件であり、表示の復元と画面内への移動のみに限定し`SafetyRecoveryAction`としてJournalに記録する）（v2.4でGatedEffect導入・例外明記により具体化。ADR-014/R2-6）。
5. Desired/Observed/Pendingを分離し、OS呼出結果と観測上の実現を混同しない。
6. JournalはRule→Layout→Effect→OS結果→Observationの追跡、復元、サニタイズを支える。
7. Live REPLのInspect/Forkは現実のWMを変更せず、介入は認可・Revision検査・記録を伴う。
8. 固定ログReplayとモデルを使ったCounterfactual Simulationの意味が区別されている。
9. 代表的な実機不具合をEvidenceに基づき疑似OSで再現し、変更前後を比較できる。
10. 不足証拠をEvidenceGapとして示し、安全に追加観測できる。
11. 異常終了・設定エラー・観測欠落でも自動制御を停止してWindows操作へ戻れる。
12. LinuxでCore/Policy/Simulationを日常開発し、Windowsでネイティブ統合テストできる。
13. ワークスペース・窓順序・フォーカス履歴がCoreの第一級の状態として定義され、Policy IRより先に固定されている（v2.1追加、ADR-011）。
14. ハングした対象アプリへのOS呼出がWM全体・安全停止の受理を止めない（v2.1追加、ADR-012）。
15. 開発者自身がPhase Bの時点で複数モニター・混在DPI環境の日常利用を開始できる（v2.1追加、18章のPhase再構成）。
16. 外部の失敗とユーザー提供コード（Layout関数）の失敗は、境界で値（`Result`/`LayoutError`）に変換されてからCoreに入る。Core自身のバグによる例外は変換せず、スレッド単位の最上位ハンドラで捕捉し`InternalFault`として安全停止へ渡す（v2.4追加、ADR-014。ラウンド2レビューR2-19で「生の例外が伝播しない」という誤った表現を訂正：第3層の例外はCore内部では意図的に伝播し、最上位でのみ捕捉する）。
17. PolicyArtifactはPinnedPolicy（当時のまま再生・Rollback可能）とDeployablePolicy（現行Analyzer通過済み）に分離され、REPL経由のDeployはHost側で必ず再分析される（v2.4追加、ADR-014/F-5）。
18. 決定（Policy評価・Reconciler・CoreCommandの解釈）は窓の属性について生の観測ではなく信念を読み、信念は入力列の決定的な純粋関数としてReplayで再計算できる。推定に基づく判断は、確度と根拠とともにExplainで示せる（v2.6追加、ADR-016）。
19. Coreの中の「待ち」はクロージャを含まないデータ（タグ＋データ＋期限）で表され、タイマーは`StepResult`が返すデータとしてShell/Simulationが同じ手順で実行する（v2.6追加、ADR-017）。
## 23. AI開発エージェントへの引き継ぎ規則
変更してよいもの：F# Layout関数、Policy IRの新Rule、Journalの診断投影、追加のScenario/Probe候補、テストケース、`classify`の対応表への**新しいキーの追加**（1-6の意味でのエントリ追加。それまで`Unclassified`だった`(Api, Space, Code)`に分類を与えること）。
必ず独立レビューするもの：PlanValidatorの迂回・弱体化、GatedEffectの迂回・弱体化、Coreの決定性を崩すI/O、Journalの意味論、記録済みRaw入力（9.1/ADR-013、NormalizerのバグをReplayで再現・回帰テスト化するための一次証拠）、`EffectFailure`とそのペイロードのDU（`DenialKind`）のケースの追加・削除（1-8：原因ベースではなくCoreの反応軸で切る方針を崩さないか要検討。v2.6で`DenialKind`を明記、R3-9：v2.5の`TargetIntegrityHigher`削除は「`EffectFailure`のDUケース」という文言に含まれず、この規則の穴を通っていた）、`classify`の対応表の**既存キーの分類の変更**（v2.6追加：`Denied OtherDenial`は`Movability`の、`TargetHung`は`Responsiveness`の信念を修正するので、既存エントリの付け替えは窓の管理状態を変える。新しいキーの追加とは扱いを分ける）、`classify`に`NativeCallRecord`以外の引数（窓の状態・信念）を与える変更（ADR-015）、信念の修正規則（4.5の規則の種類の割当、各ドメインの修正関数の意味論、確度の扱い、信念を1つのオブジェクトに集めないこと。ADR-016/ADR-018）、Coreの待ちの表現（クロージャへの退行、`TimerId`の追加・削除、古いタイマーの判定規則。ADR-017）、ドメインの境界と各ドメインの不変条件（4.4のsmart constructor、(X1)〜(X3)、(F1)/(F2)。ADR-018）、1 `step`の処理順序（7.1）、ユーザーの明示的な管理指定とPolicyの優先順位（4.4）、DTOタグの削除・形状変更と予約済みタグの一覧（9.2）、`PinnedPolicy`/`DeployablePolicy`の構築条件、Simulatorの外部動作モデル、ポリシーのUnknown処理、IPC介入権限、安全停止、セキュリティ/プライバシー境界。

AIがバグ修正時に期待結果、Simulator、検証条件を同時に都合よく書き換えることを禁止する。変更が必要な場合は、実機観測や形式的な契約に基づく理由をADR/Issueへ残す。新規の実装機能には少なくとも一つの具体的な再現シナリオ、期待されるJournal、失敗時の挙動を定義する。
## 24. 参考仕様（一次資料）
- Microsoft — SetWinEventHook: https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwineventhook
- Microsoft — Guarding Against Reentrancy in Hook Functions: https://learn.microsoft.com/en-us/windows/win32/winauto/guarding-against-reentrancy-in-hook-functions
- Microsoft — EnumWindows: https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-enumwindows
- Microsoft — GetWindowRect: https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getwindowrect
- Microsoft — DWM Window Attributes: https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/ne-dwmapi-dwmwindowattribute
- Microsoft — SetForegroundWindow: https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setforegroundwindow
- Microsoft — DPI and device-independent pixels: https://learn.microsoft.com/en-us/windows/win32/learnwin32/dpi-and-device-independent-pixels
- Microsoft — CreateNamedPipeW: https://learn.microsoft.com/en-us/windows/win32/api/namedpipeapi/nf-namedpipeapi-createnamedpipew
- Microsoft — UI Automation Overview: https://learn.microsoft.com/en-us/windows/win32/winauto/uiauto-uiautomationoverview
## 最終原則
本プロジェクトの中心は、「賢いDSLでウィンドウを並べること」ではない。現実のWindowsアプリの不確実性に対して、意図・判断・命令・結果を区別し、証拠を再現可能な知識へ変えていくことである。自由なF#関数と最小の宣言的Policy IRを組み合わせることで、言語処理系の開発負担を抑えつつ、Journal、Live REPL、Active Diagnosis、疑似OS、回帰テストを一つの閉ループに統合する。
