/// architecture.md §4.4（v2.6、R4-17）。PolicyではHost内部採番のMonitorIdではなく、
/// ユーザーが読める識別で指定する。ADR-019決定3で、基準(0)〜(4)を満たさないためWm.Policyに残る型。
module Wm.Policy.MonitorSelector

type MonitorSelector =
    | PrimaryMonitor
    | MonitorKey of string
