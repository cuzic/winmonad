/// ADR-019実装順9：LayoutRegistry.fs。architecture.md §5.6。
module Wm.Layouts.LayoutRegistry

open Wm.Contracts.Identifiers
open Wm.Contracts.Beliefs
open Wm.Contracts.SharedVocabulary

type LayoutContext = {
    WorkArea: PixelRect
    Dpi: int
    /// 順序はWorkspaceのzipperの順（Floatingを除く）。
    TiledWindows: LayoutWindow list
    /// このWorkspaceのzipperのFocusがTiledの窓ならSome（R3-3）。
    Focused: WindowId option
}

/// Layoutが読む信念だけを集めた、その場で組み立てる読み取り専用の投影（状態として保存しない）。
and LayoutWindow = { Id: WindowId; MinWidthDip: Belief<int> option }

type Placement = { Window: WindowId; Target: PixelRect }

type LayoutError =
    | InvalidInput of string
    | Unsatisfiable of string
    /// メッセージは非決定的なので型名のみ記録する（v2.4、ADR-014/4-2対応）。
    | LayoutCrashed of exnType: string

type LayoutFunction = LayoutArgs -> LayoutContext -> Result<Placement list, LayoutError>

type LayoutImplementation = {
    Id: LayoutId
    ApiVersion: int
    ImplementationDigest: string
    Calculate: LayoutFunction
}

/// `Calculate`を呼ぶ唯一の地点。自由なF#製レイアウトアルゴリズムが内部で投げる例外を捕捉し
/// `LayoutCrashed`へ変換する（§5.6）。`OutOfMemoryException`/`InsufficientExecutionStackException`は
/// 実行環境依存で非決定的なため、ここでは捕捉せずそのまま伝播させる（第3層`InternalFault`の対象、
/// R2-26対応）。
let invokeLayout (impl: LayoutImplementation) (args: LayoutArgs) (ctx: LayoutContext) : Result<Placement list, LayoutError> =
    try
        impl.Calculate args ctx
    with
    | :? System.OutOfMemoryException
    | :? System.InsufficientExecutionStackException -> reraise ()
    | ex -> Error (LayoutCrashed (ex.GetType().FullName))

/// PolicyIRは`LayoutId + ApiVersion + Args`のみを保存し、RegistryがF#関数を解決する（§5.6）。
type Registry = private { Implementations: Map<LayoutId * int, LayoutImplementation> }

module Registry =
    let empty: Registry = { Implementations = Map.empty }

    /// 同じ`(LayoutId, ApiVersion)`を2度登録しようとした場合は後優先で上書きする
    /// （実装の差し替えはビルド成果物の再デプロイでのみ起こり、1プロセス内の競合は想定しない）。
    let register (impl: LayoutImplementation) (reg: Registry) : Registry =
        { Implementations = Map.add (impl.Id, impl.ApiVersion) impl reg.Implementations }

    let resolve (id: LayoutId) (apiVersion: int) (reg: Registry) : LayoutImplementation option =
        Map.tryFind (id, apiVersion) reg.Implementations

    /// AnalyzerがLayout Registryへの関数オブジェクト依存ではなく`(LayoutId, ApiVersion, Digest)`
    /// のリストだけを受け取れるようにする（§14、Wm.PolicyがWm.Layoutsに依存しないため）。
    let toManifest (reg: Registry) : RegistryManifest =
        reg.Implementations
        |> Map.toList
        |> List.map (fun (_, impl) ->
            { LayoutId = impl.Id; ApiVersion = impl.ApiVersion; ImplementationDigest = impl.ImplementationDigest })
