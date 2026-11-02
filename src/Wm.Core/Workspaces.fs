/// Core.Workspaces（ADR-019実装順14、architecture.md §4.4）。
/// Workspaceごとの窓の並びとWorkspace内のフォーカス（zipper）、窓→Workspaceの索引、切断時の復帰先。
/// 型で保証する不変条件：(1) 管理下の各窓はちょうど1つのWorkspaceに属する。
/// (2) Workspace内の「最後にフォーカスされた窓」は並びの中に必ず存在する（zipperの型そのものが保証する、
/// §15.1 #16の解説：「Focusは並びの要素そのもの」なのでこの不変条件は検査自体が不要になる）。
module Wm.Core.Workspaces

open Wm.Contracts.Identifiers
open Wm.Contracts.SharedVocabulary

/// xmonadのStack（focus/up/down）と同じzipper。Before/Afterは順序付きリストで、
/// Beforeは逆順（Focusに近い順）。(2)の不変条件は、この型そのものによって保証される：
/// Focusは別の場所に複製されたコピーではなく、並びの「今の要素」を表すフィールドである。
type Zipper<'T> = { Before: 'T list; Focus: 'T; After: 'T list }

module Zipper =

    let single (x: 'T) : Zipper<'T> = { Before = []; Focus = x; After = [] }

    /// Before（逆順）++ [Focus] ++ Afterの順で並びを復元する。
    let toList (z: Zipper<'T>) : 'T list = (List.rev z.Before) @ [ z.Focus ] @ z.After

    /// 新しい窓はWorkspaceのStackの末尾に挿入する（§4.4）。
    let appendTail (x: 'T) (z: Zipper<'T>) : Zipper<'T> = { z with After = z.After @ [ x ] }

    /// xを取り除く。Focusだった場合はAfterの先頭、無ければBeforeの先頭へ移る
    /// （xmonadのdelete、§7.4「窓を閉じた後のフォーカス」）。全要素を除いた結果Noneになることもある。
    let remove (x: 'T) (z: Zipper<'T>) : Zipper<'T> option =
        if z.Focus = x then
            match z.After with
            | next :: rest -> Some { Before = z.Before; Focus = next; After = rest }
            | [] ->
                match z.Before with
                | prev :: rest -> Some { Before = rest; Focus = prev; After = [] }
                | [] -> None
        else
            Some
                { z with
                    Before = List.filter (fun y -> y <> x) z.Before
                    After = List.filter (fun y -> y <> x) z.After }

    /// 並び順（Before逆順++[Focus]++After）を変えずに、既存の要素xへFocusを移す
    /// （§7.1段⑥「WorkspacesがFocusObserved wを受けてzipperのFocusをwへ」）。xが並びに無ければNone。
    let focusOn (x: 'T) (z: Zipper<'T>) : Zipper<'T> option =
        if z.Focus = x then
            Some z
        else
            let whole = toList z
            match List.tryFindIndex (fun y -> y = x) whole with
            | None -> None
            | Some idx ->
                let before = whole |> List.take idx |> List.rev
                let after = whole |> List.skip (idx + 1)
                Some { Before = before; Focus = x; After = after }

    /// 1つ先へ回す。末尾では先頭へ巡回する（xmonadのfocusDown、§4.4のFocusNext）。
    let rotateNext (z: Zipper<'T>) : Zipper<'T> =
        match z.After with
        | next :: rest -> { Before = z.Focus :: z.Before; Focus = next; After = rest }
        | [] ->
            match List.rev z.Before with
            | first :: restForward -> { Before = []; Focus = first; After = restForward @ [ z.Focus ] }
            | [] -> z

    let rotatePrevious (z: Zipper<'T>) : Zipper<'T> =
        match z.Before with
        | prev :: rest -> { Before = rest; Focus = prev; After = z.Focus :: z.After }
        | [] ->
            match List.rev z.After with
            | last :: restBackward -> { Before = restBackward @ [ z.Focus ]; Focus = last; After = [] }
            | [] -> z

    /// Focusを並びの先頭（master）へ移す。他の要素の相対順序は変えない（xmonadのshiftMaster、
    /// §4.4のPromoteToMaster）。既にmaster（Before=[]）なら何もしない。
    let promoteToMaster (z: Zipper<'T>) : Zipper<'T> =
        match z.Before with
        | [] -> z
        | _ -> { Before = []; Focus = z.Focus; After = (List.rev z.Before) @ z.After }

    /// Focusと先頭（master）を入れ替える。2要素だけが位置を交換し、他の要素の相対順序は変えない
    /// （xmonadのswapMaster、§4.4のSwapWithMaster）。既にmasterなら何もしない。
    let swapWithMaster (z: Zipper<'T>) : Zipper<'T> =
        match List.rev z.Before with
        | [] -> z
        | master :: restOfBefore -> { Before = []; Focus = z.Focus; After = restOfBefore @ (master :: z.After) }

type WorkspaceState = {
    Id: WorkspaceId
    /// 管理下の窓（Tiled/Floating）。Layoutに渡すTiledの並びはStackの順からFloatingを除いたもの。
    Stack: Zipper<WindowId> option
    /// Stackの要素のうちFloatingのもの（Stackに属さない独立リストではない、§4.4）。
    Floating: Set<WindowId>
    LayoutOverride: LayoutRef option
}

module WorkspaceState =
    let empty (id: WorkspaceId) : WorkspaceState =
        { Id = id; Stack = None; Floating = Set.empty; LayoutOverride = None }

/// コンストラクタはprivate。窓の追加・除去・Workspace間の移動・zipperの回転はこのモジュールの関数だけが行い、
/// 「各窓はちょうど1つのWorkspaceに属する」「索引とStackが一致する」を構造で保つ（§4.4）。
/// 全域（v2.6、R6-1）：SpacesにないWorkspaceIdは「空のWorkspace」として振る舞う。
type WorkspaceSet =
    private { Spaces: Map<WorkspaceId, WorkspaceState>; Index: Map<WindowId, WorkspaceId> }

module WorkspaceSet =

    let empty: WorkspaceSet = { Spaces = Map.empty; Index = Map.empty }

    /// 全域性：Spacesに実体が無いWorkspaceIdも空のWorkspaceとして読める。
    let stateOf (ws: WorkspaceId) (s: WorkspaceSet) : WorkspaceState =
        s.Spaces |> Map.tryFind ws |> Option.defaultValue (WorkspaceState.empty ws)

    let workspaceOf (w: WindowId) (s: WorkspaceSet) : WorkspaceId option = Map.tryFind w s.Index

    let private isEmptyState (st: WorkspaceState) =
        st.Stack.IsNone && Set.isEmpty st.Floating && st.LayoutOverride.IsNone

    /// 実体が空になったら取り除く（全域なので保持する必要が無い。Mapを無限に太らせないため）。
    let private setState (st: WorkspaceState) (s: WorkspaceSet) : WorkspaceSet =
        if isEmptyState st then
            { s with Spaces = Map.remove st.Id s.Spaces }
        else
            { s with Spaces = Map.add st.Id st s.Spaces }

    /// 現在属しているWorkspaceから取り除く内部ヘルパー。属していなければ何もしない。
    let private removeFromCurrent (w: WindowId) (s: WorkspaceSet) : WorkspaceSet =
        match workspaceOf w s with
        | None -> s
        | Some ws ->
            let st = stateOf ws s
            let stack' = st.Stack |> Option.bind (Zipper.remove w)
            let s' = setState { st with Stack = stack'; Floating = Set.remove w st.Floating } s
            { s' with Index = Map.remove w s'.Index }

    /// 新しい窓を分類確定時にWorkspaceのStack末尾へ挿入する（§4.4）。既に別のWorkspaceに属していれば、
    /// まずそこから取り除く——これが不変条件(1)「ちょうど1つのWorkspaceに属する」を構造で保つ理由である。
    let addWindow (ws: WorkspaceId) (w: WindowId) (floating: bool) (s: WorkspaceSet) : WorkspaceSet =
        let s' = removeFromCurrent w s
        let st = stateOf ws s'
        let stack' =
            match st.Stack with
            | None -> Some (Zipper.single w)
            | Some z -> Some (Zipper.appendTail w z)
        let floating' = if floating then Set.add w st.Floating else st.Floating
        let s'' = setState { st with Stack = stack'; Floating = floating' } s'
        { s'' with Index = Map.add w ws s''.Index }

    /// 消滅・Excluded化した窓をどのWorkspaceからも除く（§4.4「WindowsがDomainEvent WindowUnmanagedを出し、後段が処理する」）。
    let removeWindow (w: WindowId) (s: WorkspaceSet) : WorkspaceSet = removeFromCurrent w s

    /// xmonadのdeleteと同じ（§7.4「MoveFocusedToWorkspaceで窓を他のWorkspaceへ送る」）。
    /// Floating状態は移動先でも保たれる。管理下に無い窓は何もしない。
    let moveToWorkspace (w: WindowId) (dest: WorkspaceId) (s: WorkspaceSet) : WorkspaceSet =
        match workspaceOf w s with
        | None -> s
        | Some srcWs ->
            let wasFloating = Set.contains w (stateOf srcWs s).Floating
            addWindow dest w wasFloating s

    let rotateNext (ws: WorkspaceId) (s: WorkspaceSet) : WorkspaceSet =
        let st = stateOf ws s
        match st.Stack with
        | None -> s
        | Some z -> setState { st with Stack = Some (Zipper.rotateNext z) } s

    let rotatePrevious (ws: WorkspaceId) (s: WorkspaceSet) : WorkspaceSet =
        let st = stateOf ws s
        match st.Stack with
        | None -> s
        | Some z -> setState { st with Stack = Some (Zipper.rotatePrevious z) } s

    /// §4.4のPromoteToMaster。Zipper.promoteToMaster参照。
    let promoteToMaster (ws: WorkspaceId) (s: WorkspaceSet) : WorkspaceSet =
        let st = stateOf ws s
        match st.Stack with
        | None -> s
        | Some z -> setState { st with Stack = Some (Zipper.promoteToMaster z) } s

    /// §4.4のSwapWithMaster。Zipper.swapWithMaster参照。
    let swapWithMaster (ws: WorkspaceId) (s: WorkspaceSet) : WorkspaceSet =
        let st = stateOf ws s
        match st.Stack with
        | None -> s
        | Some z -> setState { st with Stack = Some (Zipper.swapWithMaster z) } s

    /// Stackの所属は変えず、Floating集合への出入りだけを切り替える（§4.4のToggleFloating）。
    let toggleFloating (w: WindowId) (s: WorkspaceSet) : WorkspaceSet =
        match workspaceOf w s with
        | None -> s
        | Some ws ->
            let st = stateOf ws s
            let floating' = if Set.contains w st.Floating then Set.remove w st.Floating else Set.add w st.Floating
            setState { st with Floating = floating' } s

    /// 指定WorkspaceのzipperのFocusをwへ動かす（§7.1段⑥「FocusObserved wを受けてzipperのFocusをwへ」）。
    /// wがそのWorkspaceのStackに無ければNone（呼び出し側は何もしない選択ができる）。
    let setFocus (ws: WorkspaceId) (w: WindowId) (s: WorkspaceSet) : WorkspaceSet option =
        let st = stateOf ws s
        st.Stack
        |> Option.bind (Zipper.focusOn w)
        |> Option.map (fun z' -> setState { st with Stack = Some z' } s)
