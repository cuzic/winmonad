/// architecture.md §4.4「WM中核状態：ドメインごとのCore」のWorkspacesドメインが型で保証する2つの不変条件を検査する。
/// (1) 管理下の各窓はちょうど1つのWorkspaceに属する（Index/Spacesの整合性、zipperに重複が無い）。
/// (2) Workspace内の「最後にフォーカスされた窓」は並びの中に必ず存在する——これはzipperの型そのものが保証する
///     （§15.1 #16の解説どおり、検査自体が本質的には不要）。ここでは退行確認のために明示的にも検査する。
module Wm.Core.Tests.WorkspacesInvariantTests

open FsCheck.Xunit
open Wm.Contracts.Identifiers
open Wm.Contracts.SharedVocabulary
open Wm.Core.Workspaces

/// FsCheckに`Op`用のカスタムArbitraryを書く代わりに、単純なタプルのリストを生成させ、
/// ここで有限の操作集合（WorkspaceId 0..3、WindowId 0..5）へ解釈する。
let private interpret (kindRaw: int, wsRaw: int, winRaw: int, flag: bool) (s: WorkspaceSet) : WorkspaceSet =
    let ws = WorkspaceId (abs wsRaw % 4)
    let w = WindowId (int64 (abs winRaw % 6))
    match (abs kindRaw) % 6 with
    | 0 -> WorkspaceSet.addWindow ws w flag s
    | 1 -> WorkspaceSet.removeWindow w s
    | 2 -> WorkspaceSet.moveToWorkspace w ws s
    | 3 -> WorkspaceSet.rotateNext ws s
    | 4 -> WorkspaceSet.rotatePrevious ws s
    | _ -> WorkspaceSet.toggleFloating w s

let private allWorkspaceIds = [ 0 .. 3 ] |> List.map WorkspaceId
let private allWindowIds = [ 0 .. 5 ] |> List.map (fun i -> WindowId (int64 i))

let private stackListOf (ws: WorkspaceId) (s: WorkspaceSet) : WindowId list =
    (WorkspaceSet.stateOf ws s).Stack |> Option.map Zipper.toList |> Option.defaultValue []

/// (2)はzipperの型そのものが保証するので、ここでの検査は「本当に成り立っている」ことの実演（退行確認）に過ぎない。
let private focusAlwaysInStack (s: WorkspaceSet) : bool =
    allWorkspaceIds
    |> List.forall (fun ws ->
        match (WorkspaceSet.stateOf ws s).Stack with
        | None -> true
        | Some z -> List.contains z.Focus (Zipper.toList z))

/// (1)の内容：zipperに重複が無く、FloatingはStackの部分集合で、Index⇔Spacesの所属が双方向に一致する。
let private checkInvariants (s: WorkspaceSet) : bool =
    let membership =
        allWorkspaceIds
        |> List.collect (fun ws -> stackListOf ws s |> List.map (fun w -> w, ws))

    let noDuplicatesAndFloatingIsSubset =
        allWorkspaceIds
        |> List.forall (fun ws ->
            let stackList = stackListOf ws s
            let st = WorkspaceSet.stateOf ws s
            (stackList |> List.distinct |> List.length) = stackList.Length
            && Set.isSubset st.Floating (Set.ofList stackList))

    let eachMemberInExactlyOneWorkspace =
        membership
        |> List.groupBy fst
        |> List.forall (fun (_, xs) -> xs.Length = 1)

    let indexAgreesWithMembership =
        membership |> List.forall (fun (w, ws) -> WorkspaceSet.workspaceOf w s = Some ws)

    // 逆方向：IndexにあるWindowIdは必ずどこかのWorkspaceのStackに実在し、Indexに無ければどこにも現れない。
    let indexHasNoOrphans =
        allWindowIds
        |> List.forall (fun w ->
            match WorkspaceSet.workspaceOf w s with
            | None -> not (membership |> List.exists (fun (mw, _) -> mw = w))
            | Some ws -> membership |> List.exists (fun (mw, mws) -> mw = w && mws = ws))

    noDuplicatesAndFloatingIsSubset
    && eachMemberInExactlyOneWorkspace
    && indexAgreesWithMembership
    && indexHasNoOrphans
    && focusAlwaysInStack s

[<Property>]
let ``不変条件: 任意の操作列の後、各ステップで窓はちょうど1つのWorkspaceに属しzipperに重複が無い`` (ops: (int * int * int * bool) list) =
    let ops = ops |> List.truncate 60 // 性質テストの実行時間を抑える
    let statesIncludingEmpty = ops |> List.scan (fun s op -> interpret op s) WorkspaceSet.empty
    statesIncludingEmpty |> List.forall checkInvariants
