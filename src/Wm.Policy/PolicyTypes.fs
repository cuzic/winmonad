/// ADR-019実装順8：PolicyTypes.fs。architecture.md §5.2。
module Wm.Policy.PolicyTypes

open Wm.Contracts.Identifiers
open Wm.Contracts.Observation
open Wm.Contracts.SharedVocabulary
open Wm.Policy.MonitorSelector

type WindowPredicate =
    | AnyWindow
    /// どの導出元（`AppIdentity`の4フィールド）を指すかを明示する（§4.2/§5.2）。
    | AppIs of AppIdSource * string
    | TitleContains of string
    | DialogIs of bool
    /// DIP。比較相手のMinWidthの信念もDIPに正規化して持つので分類はDPIに依存しない（§5.2/§8.3）。
    | MinWidthAtLeast of int
    /// 4.5のMovabilityの信念を読む（ADR-015）。
    | MovableIs of bool
    | All of WindowPredicate list
    | AnyOf of WindowPredicate list
    | Not of WindowPredicate

type WindowRule = {
    Id: PolicyRuleId
    Priority: int
    When: WindowPredicate
    Apply: Management
}

type LayoutPredicate =
    | Always
    | TiledCountAtLeast of int
    /// DIP（§5.2/§8.3。モニター単位の判定なので、そのモニターのDPIで変換する）。
    | MonitorWidthAtLeast of int
    | Both of LayoutPredicate list

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
    /// i3の`workspace X output Y`相当（§4.4）。
    WorkspaceAssignments: (WorkspaceId * MonitorSelector) list
}

module PolicyIR =
    /// `PolicyActivated`の配線（§21の未決事項、未実装）が無い間、`RuntimePolicy`が使う安全な既定値。
    /// ルールを一切持たず、全ての窓を`DefaultManagement`（Tiled）へ分類する（ADR-019実装順14、
    /// Core.Windows着手時に追加）。
    let defaults: PolicyIR =
        { SchemaVersion = 1
          WindowRules = []
          DefaultManagement = Tiled
          LayoutRules = []
          DefaultLayout = { Id = LayoutId "monocle"; ApiVersion = 1; Args = NoArgs }
          WorkspaceAssignments = [] }

    /// WindowRules製品判断（project_windowrules_product_decision.md）2026-11-05に決定：実機
    /// （`dragonflyg4`、ユーザー自身の日常使用PC）でPhase B round 1のアンビエント列挙診断を実行し
    /// （`visible top-level windows: 24`、`EnumWindows`＋`GetWindowThreadProcessId`で取得）、得られた
    /// 実際の窓一覧を分類した結果からこの既定ルールを組んだ（architecture.md §21参照）。個人固有の
    /// アプリ名（`herdr-app`/`iScrRec`系/`LINE`等）は一般化できないため除外し、OSが提供する2つの
    /// カテゴリだけを一般則として残した：
    /// 1. デスクトップシェル自身（`explorer.exe`の"Program Manager"）——タイル対象になるべきでない
    ///    実在しない「窓」（デスクトップ背景そのもの）。`explorer.exe`は`C:\Windows\explorer.exe`に
    ///    固定（OS保護ファイル）なので、フルパス完全一致（`AppIs`の既存セマンティクス、§5.4）でも
    ///    環境差に強い。
    /// 2. IME変換候補ウィンドウ（`TextInputHost.exe`）——入力中にだけ現れる一時的なオーバーレイで、
    ///    タイル対象にする意味が無い。`C:\Windows\System32\TextInputHost.exe`も同様にOS保護パス。
    /// 観測された他のオーバーレイ（`Microsoft.CmdPal.UI`/`PowerToys.QuickAccess`）はインストール先が
    /// ユーザー・バージョン依存でフルパスが安定しないため、個別の`AppIs`ルールにはせず、代わりに
    /// パスに依存しない`DialogIs true -> Floating`という一般則でダイアログ全般（観測された
    /// `OfficeC2RClient`の更新通知等を含む）を拾う形にした。
    ///
    /// **まだ`defaults`/`RuntimePolicy.defaults`へは配線していない**：`defaults`は上記コメントの
    /// 通り「`PolicyActivated`の配線が無い間の安全な空既定値」という別の役割を持ち、これを変えると
    /// `PolicyIR.defaults`/`RuntimePolicy.defaults`をそのまま使う既存の多数のテスト（Wm.Cli.Tests・
    /// Wm.Core.Tests・Wm.Simulation.Tests）が、新たに`Exe`/`IsDialog`信念のAwaitingEvidenceを経由する
    /// ようになり副作用を受ける。実際に配信される既定値として有効化するのはPhase C
    /// のPolicy活性化・pin機構（project_remaining_tasks_checklist.mdのPhase C節）の仕事であり、
    /// このリストはその時に使う値として先に用意したもの。
    let recommendedWindowRules: WindowRule list =
        [ { Id = PolicyRuleId "exclude-desktop-shell"
            Priority = 100
            When = AppIs (Exe, @"C:\Windows\explorer.exe")
            Apply = Excluded }
          { Id = PolicyRuleId "exclude-ime-candidate-window"
            Priority = 100
            When = AppIs (Exe, @"C:\Windows\System32\TextInputHost.exe")
            Apply = Excluded }
          { Id = PolicyRuleId "float-dialogs"
            Priority = 50
            When = DialogIs true
            Apply = Floating } ]
