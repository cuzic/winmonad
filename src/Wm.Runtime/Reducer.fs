/// Wm.Runtime（ADR-019実装順16）：入力キューの処理とJournalへの書込み順序。Windows非依存。
/// architecture.md §3.1/§7.1/§9.4「入力をJournalに書いてから`step`を呼ぶ」。
module Wm.Runtime.Reducer

open System
open Wm.Contracts.CoreCommand
open Wm.Contracts.JournalTypes
open Wm.Core.DecisionTrace
open Wm.Core.WmState
open Wm.Policy.PolicyTypes
open Wm.Policy.PolicyEvaluator
open Wm.Runtime.TimerExecutor

/// 直近何件の`DecisionTrace`を蓄えておくか（Phase C Explain、architecture.md §10.2、v2.147）。
/// JournalのInspect側（直近50〜100件、ユーザー決定2026-11-06）と同じ桁数に揃える——`DecisionTrace`
/// 自体はJournalに永続化しない揮発性の値（`DecisionTrace.fs`の型直前のコメント「ExplainはReplayで
/// 再構成する」参照）なので、この固定長バッファがその場しのぎの代替になる。将来Replayベースの
/// 再構成（§18）を実装する際は、この固定長バッファ方式は不要になる可能性がある。
let recentTraceCapacity = 64

/// 最小のReducer：単一の入力キューを順に処理し、各ステップの前に入力をJournalへ書く。
/// Simulation版のTimer実行器を内包し、外部入力が無い間は最も早い期限のタイマーを仮想時計で発火できる。
type Reducer(runtimePolicy: RuntimePolicy, sessionId: string, coreBuildId: string, policyArtifactHash: string, journalPath: string option) =
    // PolicyActivated実配線（vNext、architecture.md §14/§21）：`runtimePolicy`はコンストラクタ引数を
    // 同名の`let mutable`で覆い、Deploy成功時に実際にswapできるようにする——`state`/`sequence`と同じ
    // 可変の環境になるが、ADR-012の単一スレッドアクセス不変条件はこの新しい可変フィールドにも適用する
    // （書込みは`ActivatePolicy`経由でReducer所有スレッドからのみ、下記参照）。
    let mutable runtimePolicy = runtimePolicy
    let mutable state = initial runtimePolicy
    let mutable sequence = 0L
    let timers = SimulationTimerExecutor()
    // 新しい順（先頭が最新）。`DecisionTrace`自体はウィンドウIDを運ばないケースが多い（`DecisionTrace.fs`
    // 参照）ため、ここでは特定の窓へは絞らず、直近の全件をそのまま保持する——Explain呼び出し側
    // （`Wm.Host/ResidentHost.fs`）が窓ごとに関係しそうなものを拾うかはそちら側の判断に委ねる。
    let mutable recentTraces : DecisionTrace list = []

    member _.State = state
    member _.Timers = timers

    /// Journal Inspect（architecture.md §10.2、v2.147）がJournalファイルを読むために必要なパス。
    /// Hostの外からもファイルパスという不変の環境値を読むだけなので`Policy`と同じ理由で安全。
    member _.JournalPath = journalPath

    /// 直近の`DecisionTrace`（新しい順、最大`recentTraceCapacity`件）。Phase C Explain（v2.147）向け。
    member _.RecentTrace = recentTraces

    /// Phase CのPolicy Inspect（architecture.md §10.2、v2.145）が読む採用中のPolicy。vNextで
    /// `ActivatePolicy`（下記）がこれを書き換えられるようになったため、Reducer所有スレッド以外
    /// （Named Pipe接続スレッド）からの読み取りは「書込みと競合しても値が壊れない（.NETの参照型
    /// フィールド代入はアトミック、torn readは起きない）が、書換え直前・直後どちらの値を読むかは
    /// タイミング次第」というJournal Inspect（`RecentJournal`）と同じ最善努力の一貫性になる——
    /// ADR-012の単一スレッドアクセス不変条件は「書込みは所有スレッドのみ」を要求するものであり、
    /// この種の最善努力読み取りまでは禁じない（Journal Inspectの既存の割り切りと同じ）。
    member _.Policy = runtimePolicy.Policy

    /// Analyzer Inspect（architecture.md §10.2、v2.149）が読む採用中のLayout Registry。デプロイされる
    /// 候補Policyは常にこのHostが起動時に組み立てたLayoutRegistryを前提にする（§14の`link`検査）ため、
    /// `ActivatePolicy`はこのフィールドには触れない——コンストラクタ以降一度も再代入されない、
    /// 本当に不変の環境値のまま。
    member _.LayoutRegistry = runtimePolicy.LayoutRegistry

    /// PolicyActivated実配線（vNext、architecture.md §14/§21、ユーザー決定2026-11-06）：`op=deploy`が
    /// 検証・保存・Journal記録に成功した新Policyを、実行中のReducerへ実際に反映する。`PatternSet`は
    /// `Policy`と対になって再計算する（`RuntimePolicy`型直前のコメント「両者は常に対になって更新
    /// される」参照）。既に`Classified (_, ByPolicy _)`で確定済みの窓を遡って再評価することは
    /// しない（ユーザー決定2026-11-06「再評価しない」、`Windows.fs`の`reconsider`が元からこの状態を
    /// 増分再分類の対象外にしている——§4.4 R5-2のStickyルールそのもの）。新しいPolicyは、この呼び出し
    /// より後に投入される入力（`WindowObserved`で新しく発見される窓の初回分類等）からだけ効く。
    /// `ResidentHost.Run()`を呼んでいるスレッド（＝Reducer所有スレッド）以外から呼ばない——`state`/
    /// `sequence`と同じくADR-012の単一スレッドアクセス不変条件の対象（`ResidentHost.EnqueueActivatePolicy`
    /// がこの制約を守るためのキュー経由の書込み要求口になる）。
    member _.ActivatePolicy(policy: PolicyIR) : unit =
        runtimePolicy <- { runtimePolicy with Policy = policy; PatternSet = buildTitlePatternSet policy }

    /// Deploy Inspect（architecture.md §14/§10.2、v2.150）がJournal記録（`PolicyActivatedKind`）に
    /// 使うSessionId。コンストラクタで渡されたまま再代入されない不変の環境値。
    member _.SessionId = sessionId

    /// Deploy Inspect（v2.150）が`ArtifactIdentity`の組み立て・Journal記録に使うCoreBuildId。
    /// `SessionId`と同じ理由で安全に読める不変の環境値。
    member _.CoreBuildId = coreBuildId

    /// 次にSubmitされる入力へ割り当てられる値（＝これまでに処理済みの入力件数）。Phase Cの
    /// Snapshot/Intervene（architecture.md §10.1/10.4）が参照する楽観的排他の起点として、
    /// Reducer所有スレッド以外からも安全に読める単調増加値を公開する。
    member _.Sequence = sequence

    /// §7.1固定順序：「入力をJournalに書いてから`step`を呼ぶ」。これにより`step`実行中に例外を起こした
    /// 入力（poison input）が必ずJournalに残り、Replayで再現できる。
    member _.Submit (now: int64) (source: string) (input: InputEvent) : StepResult =
        let envelope: EventEnvelope<InputEvent> =
            { Sequence = sequence; MonotonicTime = now; Source = source; SessionId = sessionId; Input = input }
        sequence <- sequence + 1L

        journalPath
        |> Option.iter (fun path ->
            // v2.82：`Kind`だけを入力の種類で分類する（`Payload`は常に`InputPayload input`のまま、
            // 無損失の既存の符号化——`inputEventToJson`/`inputEventOfJson`、v2.57以来の往復対応）。
            // `EffectCallReturned`を専用の`EffectCallReturnedPayload`（v2.81で追加）へ乗せる案を
            // 検討したが、その型は`dispatchSeq`フィールドを持たず（`CoreCommand.fs`の`EffectCallReturned`
            // 自身は`RequestId * Result<unit, NativeCallRecord> * dispatchSeq: int64 option`の3つ組）、
            // `dispatchSeq`は`Placement.onEffectCallReturned`経由で`PendingRequest.DispatchSeq`へ渡り
            // `Mismatch → Reevaluate`（v2.42）の判定そのものに使われる——Replayで`dispatchSeq`が
            // 欠落すると、同じ入力列のはずなのにReplay時の`step`の分岐が実行時と食い違う（§9.2
            // 「stepの入力分岐 ⊆ Journalの往復可能集合」違反）。`InputPayload`は`EffectCallReturned`の
            // 3つ組全体を無損失に運ぶので、こちらを使う。
            let kind =
                match input with
                | EffectCallReturned _ -> EffectCallReturnedKind
                | _ -> InputReceived
            let entry =
                { SchemaVersion = 1
                  SessionId = sessionId
                  Sequence = envelope.Sequence
                  MonotonicTicks = now
                  WallTimeUtc = DateTimeOffset.UtcNow
                  CorrelationId = None
                  CausationId = None
                  PolicyArtifactHash = policyArtifactHash
                  CoreBuildId = coreBuildId
                  Kind = kind
                  Payload = InputPayload input }
            Wm.Journal.JournalWriter.appendEntry path entry)

        let result = step runtimePolicy state envelope
        state <- result.Next
        timers.Apply now result.Timers
        if not (List.isEmpty result.Trace) then
            recentTraces <- (List.rev result.Trace) @ recentTraces |> List.truncate recentTraceCapacity
        result

    /// Simulation駆動：次に発火すべきタイマーがあれば、仮想時計をその時刻へ進めてTimerElapsedを投入する。
    /// 外部入力が尽きたとき（§3.3の規約2「ランタイムがTimerCommandを実行し、発火をTimerElapsed入力として
    /// Reducerのキューへ戻す」）に呼ぶ。
    member this.AdvanceToNextTimer () : StepResult option =
        match timers.PopNext () with
        | None -> None
        | Some (id, fireAt) -> Some (this.Submit fireAt "simulation-timer" (TimerElapsed id))
