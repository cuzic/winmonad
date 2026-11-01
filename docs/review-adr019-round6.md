# ADR-019ドラフトv6 批判的レビュー 第6ラウンド

対象：`docs/adr-019-draft.md`（v6）
照合先：`docs/architecture.md` v2.8、`docs/review-adr019-round5.md`
範囲は前回までと同じで、combinator vs DSLの判断は扱わない。

**結論：収束。** 第5ラウンドの5件（重大1件、軽微4件）は、すべて正しく反映されている。新たに持ち込まれた構造上の問題は無い。残るのは軽微な文言の指摘1件（R6-01）だけで、再レビューは要らない。採用時の反映作業の中で直せばよい。

---

## 第5ラウンドの指摘の反映確認

| ID | 確認結果 |
|---|---|
| R5-01（重大） | 反映済み。(A)案を採用している。決定2の新しい段落（39行）、基準(1)（49行）、層5（70行）、決定9の§17の型の配置の段落への追記文（91行）の4か所で、`Displays`をContractsに置かず、Core.Topologyの`private`な型のまま残すことが一貫している。根拠として§3.4・§4.4の注・§15の表・§15.1 #17・ADR-018を正しく引いている。§15.1 #17の「`Displays`内の性質はsmart constructorの単体テストで検査する」とも整合する。`ScreenPhase`と`FocusTarget`は不変条件を持たない普通のDUなので、Contractsに置いても問題ない |
| R5-02 | 反映済み（43行）。`EffectiveManagement`の根拠は`GateInput`だけになった |
| R5-03 | 反映済み（74行）。層9の例はPhase Aの範囲に絞られ、`ReplIntervention`/`ProbeRequested`/`EvidenceCaptured`は除外の理由（`ApprovalToken`）とともに外されている |
| R5-04 | 反映済み（78行、92行）。§4.4の`update`の戻り値に`DecisionTrace list`を加える改訂が、決定7と決定9の反映先の両方に入っている |
| R5-05 | 反映済み（81行）。縦切りの対象は#21だけになり、#3はPlacementの`PendingDeadline`を実装する時点の対象として外された。「古い発火の無害性は#21の後半が#3の具体化として含む」という説明も、§15.1 #21の文言どおりである |

## 新たに持ち込まれた問題が無いかの確認
- **`Displays`の射影が使えること**：§7.1の段①（MinWidthのDIP正規化に要るモニターのDPI）、段⑤（Workspacesが`UserDrag`で落とされたモニターに表示中のWSを読む）、段⑦（Placementが可視WSごとに`LayoutContext.WorkArea`/`Dpi`を作る）の3つとも、`Screen = { Monitor: MonitorSnapshot; Workspace: WorkspaceId }`と同じ情報を運ぶ読み取り専用のレコードがあれば満たせる。`MonitorSnapshot`は§4.1の型で、決定6の層1に入る。したがってContractsの型だけで書け、決定2(ii)の枠に収まる。
- **決定2の主張（6ドメイン間に型の参照が無い）**：`Displays`を射影に置き換えても、第4ラウンドで当たった8段の表の判定は変わらない。
- **決定3〜決定9の間の矛盾**：無い。基準(1)の一覧、層5、決定9の§17への追記文の3つで、Contractsへ移す値型（`ScreenPhase`/`FocusTarget`/信念の値の型）が一致している。

---

## 軽微（再レビューは不要）

### R6-01 [軽微] `Displays`の射影を「識別子だけでできた」としているが、DPIとWorkAreaを運ぶには`MonitorSnapshot`が要る
- **ドラフト**決定2（39行）「他ドメイン・他段が読む分（①のDPI、⑤のWorkspaces、⑦のPlacement）は、…現在／可視のモニターとWorkspaceの組だけを運ぶ、**識別子（`MonitorId`/`WorkspaceId`、Contracts）だけでできた**小さなレコード」。
- **問題**：同じ文が段①のDPIを例に挙げているが、DPIは`MonitorId`からは得られず、`MonitorSnapshot.Dpi`が要る。段⑦の`LayoutContext.WorkArea`も同様に`MonitorSnapshot.WorkArea`が要る。射影は「識別子と`MonitorSnapshot`（どちらもContracts、層1）でできたレコード」と書くのが正しい。設計には影響しない。§4.4の`Screen`のフィールドをそのまま公開する読み取り専用のレコードで足りる。

---

## 6ラウンドを通じた到達点（参考）
- プロジェクトの依存グラフ（決定0・1）、Contractsへ移す基準(0)〜(4)（決定3）、DTOをそれが写す型と同じプロジェクトに置く原則（決定4）、決定性ゲートの範囲の拡大（決定5）、Contractsの層の順序（決定6）、Coreのファイル順（決定7）、縦切り（決定8）、architecture.mdへの反映先（決定9）が、相互に矛盾なく閉じた。
- 第1ラウンドで致命とした項目（CoreのPolicy/Layoutsへの依存、Contracts内の順序、`CoreCommand`/`InputEvent`/`GateInput`の配置、§19とADR-011の関係、Normalizerの退避先）は、すべて解消している。
- 本ADRの範囲外として21章へ正しく先送りされた項目：`PolicyStaleAnalysis`のペイロードの表現、Normalizerの候補条件の判定とCoreの状態の関係。
