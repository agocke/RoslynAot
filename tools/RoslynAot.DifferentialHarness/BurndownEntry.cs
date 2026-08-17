namespace RoslynAot.DifferentialHarness;

internal enum BurndownStatus
{
    Pass,
    NotExercised,
    Fail,
}

/// <summary>
/// Deliberately excludes volatile text (exception message, deep stack
/// frames, absolute paths) so the baseline file only churns when the
/// underlying behavior actually changes. The full detail - including
/// exception messages - lives in report.json instead.
/// </summary>
internal sealed record BurndownReason(
    string Kind,
    string Case,
    string? ActionKind,
    string? ExceptionType,
    string? FailingMember,
    string? Detail);

internal sealed record BurndownEntry(
    string RuleId,
    string? AnalyzerType,
    BurndownStatus Status,
    BurndownReason? Reason);

/// <summary>
/// Reason kinds in the precedence order used when a rule matches more
/// than one condition - determinism here is load-bearing, since the
/// reason is part of the checked-in baseline.
/// </summary>
internal static class ReasonPrecedence
{
    public static readonly string[] Order =
    [
        "CompilerCrash",
        "Timeout",
        "AnalyzerException",
        "MissingDiagnostic",
        "ExtraDiagnostic",
        "SpanMismatch",
        "SeverityMismatch",
        "MessageMismatch",
    ];
}
