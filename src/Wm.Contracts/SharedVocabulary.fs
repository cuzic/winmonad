/// 決定6層5（ADR-019）：基準(1)(2)(3)で確定した共有語彙。ドメイン横断で直接読まれる小さな値型。
/// `Displays`自身はここに含めない（privateなままCore.Topologyに残る。architecture.md §17）。
module Wm.Contracts.SharedVocabulary

open Wm.Contracts.Identifiers

[<Struct>]
type WorkspaceId = WorkspaceId of int

/// 「今フォーカスされている窓」の導出結果（§4.4、R3-3/R4-4/R5-4）。
type FocusTarget =
    | ManagedWindow of WindowId
    | Elsewhere

/// Policyが宣言する管理区分（§5.2）。Core組込み規則のUnplacedはここに含めない（EffectiveManagementが持つ）。
type Management =
    | Tiled
    | Floating
    | Excluded

type LayoutArgs =
    | NoArgs
    | MasterRatio of decimal
    | ColumnCount of int

type LayoutRef = {
    Id: LayoutId
    ApiVersion: int
    Args: LayoutArgs
}

/// Coreが実際に使う実効的な管理区分。UnplacedはNotMovableの窓（v2.6、R5-3）。
/// ケース名にEff接頭辞を付ける（architecture.mdの表記はTiled|Floating|Unplaced|Excludedだが、
/// 同一モジュール内でManagementと同名ケースが衝突しF#の名前解決があいまいになるのを避けるための表記上の差分のみ。意味は同じ）。
type EffectiveManagement =
    | EffTiled
    | EffFloating
    | EffUnplaced
    | EffExcluded

/// §14のPolicyArtifact。AnalyzerのlinkがLayout Registryへの関数オブジェクト依存ではなく、
/// (LayoutId, ApiVersion, Digest) listだけを受け取れるようにする（Wm.PolicyがWm.Layoutsに依存しないため）。
type RegistryManifestEntry = {
    LayoutId: LayoutId
    ApiVersion: int
    ImplementationDigest: string
}

type RegistryManifest = RegistryManifestEntry list

/// 画面構成の抑止・安定待ち・grace window（§7.3、R4-15/R5-5）。
type ScreenPhase =
    | Stable
    | Reconfiguring of since: int64
    | Settling of latest: MonitorSnapshot list * deadline: int64 * hardDeadline: int64
    | Grace of deadline: int64
