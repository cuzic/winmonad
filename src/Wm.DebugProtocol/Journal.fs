/// Wm.DebugProtocol：Phase CのLive REPL（architecture.md §10.2「Inspect：Journalを見る」、v2.147）が
/// プロセス境界を越えて運ぶJournalの型。`Snapshot`/`Policy`と違い、Journalが運ぶ`InputEvent`/
/// `Effect`/`NativeCallRecord`等の型全体をもう一度並行して型安全なDTOへ写すのは（Journal自身が
/// すでに`Wm.Contracts`の広い範囲を運ぶため）このスライスの範囲を大きく超える——ユーザー決定
/// 2026-11-06「直近50〜100件だけ、シンプルに保つ」を踏まえ、`Wm.Journal.JournalWriter.entryToJson`
/// （§9.2の規約、既に正しい唯一の符号化）が作る`JsonNode`をそのまま運ぶ。Inspectは人間が読むための
/// ものであり、プログラム的な強い型付けが無くても十分に価値がある。
module Wm.DebugProtocol.Journal

open System.Text.Json.Nodes

/// `Entries`は新しい順（先頭が最新）。各要素は`Wm.Journal.JournalWriter.entryToJson`が返す
/// `JsonNode`そのもの。
type JournalDto = { Entries: JsonNode list }
