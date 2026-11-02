/// 決定6層10（ADR-019）：Contracts自身の型のDTO/ofDto基盤。architecture.md §9.2/§14。
/// 本層が置くのは共通基盤（タグ付け規約、Unrecognizedケース）だけ。各型のDTO本体は、
/// それを実際に永続化し始める層（まずWm.Journal）で、9.2のタグ付け規約に従って個別に書く。
module Wm.Contracts.Dto

/// 未知のタグへの対応（§9.2）。表示/Explain/Bundle閲覧モードはこれをそのまま表示し、
/// Replayモードは`Unrecognized`を1つでも含むJournalをFail-fastで拒否する。
type Unrecognized = { Tag: string; Raw: string }

/// Result<'T,'E>自体はDTOの型にせず、{"outcome": "ok"|"failed", "failure": {...}}の形に固定する（§9.2）。
type ResultDto<'TOk, 'TFailure> =
    | OkDto of 'TOk
    | FailedDto of 'TFailure
