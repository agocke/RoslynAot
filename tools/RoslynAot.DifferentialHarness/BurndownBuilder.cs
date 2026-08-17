namespace RoslynAot.DifferentialHarness;

/// <param name="UnattributableFailures">
/// Failures that could not be tied to any rule in the catalog - a
/// native crash or an Initialize-time exception on a case whose declared
/// rules are all outside the module's rule set. These must never be
/// dropped silently, so the caller turns them into a hard error.
/// </param>
internal sealed record BurndownResult(
    IReadOnlyList<BurndownEntry> Entries,
    IReadOnlyList<string> UnattributableFailures);

internal static class BurndownBuilder
{
    /// <param name="caseEvaluations">
    /// Must be in deterministic (ordinal case-name) order - the first
    /// case to exhibit a given failure for a rule is what gets recorded
    /// as that rule's reason, so evaluation order is load-bearing.
    /// </param>
    public static BurndownResult Build(
        IReadOnlyList<string> ruleIds,
        IReadOnlyList<CaseEvaluation> caseEvaluations)
    {
        var unattributable = new List<string>();
        var aggregates = ruleIds.ToDictionary(id => id, _ => new RuleAggregate());

        foreach (CaseEvaluation evaluation in caseEvaluations)
        {
            foreach (string ruleId in ruleIds)
            {
                RuleAggregate aggregate = aggregates[ruleId];
                aggregate.ManagedDiagnosticCount += evaluation.ManagedDiagnostics
                    .Count(d => d.RuleId == ruleId);
            }

            foreach (DiffEntry entry in evaluation.DiffEntries)
            {
                if (entry.Kind == DiffEntryKind.Match ||
                    !aggregates.TryGetValue(entry.RuleId, out RuleAggregate? aggregate))
                {
                    continue;
                }

                aggregate.NonMatchEntries.Add((evaluation.Case.Name, entry));
            }

            foreach (AnalyzerFailure failure in evaluation.NativeFailures)
            {
                IEnumerable<string> namedRules = failure.RuleIds.Count > 0
                    ? failure.RuleIds
                    // A failure during Initialize (before any rule ID is
                    // known) still belongs to every rule this case
                    // declares - otherwise it silently vanishes from the
                    // burn-down.
                    : evaluation.Case.DeclaredRuleIds;
                bool attributed = false;
                foreach (string ruleId in namedRules)
                {
                    if (aggregates.TryGetValue(ruleId, out RuleAggregate? aggregate))
                    {
                        aggregate.NativeFailures.Add((evaluation.Case.Name, failure));
                        attributed = true;
                    }
                }

                if (!attributed)
                {
                    unattributable.Add(
                        $"{evaluation.Case.Name}: analyzer failure could not " +
                        $"be attributed to any rule in the module's catalog " +
                        $"(reported rule ids: " +
                        $"[{string.Join(", ", failure.RuleIds)}], case declares: " +
                        $"[{string.Join(", ", evaluation.Case.DeclaredRuleIds)}])");
                }
            }

            if (evaluation.NativeResult.TimedOut || evaluation.NativeResult.Crashed)
            {
                string kind = evaluation.NativeResult.TimedOut
                    ? "Timeout"
                    : "CompilerCrash";
                string detail = evaluation.NativeResult.TimedOut
                    ? "native compilation timed out"
                    : $"native compiler exited with code " +
                        $"{evaluation.NativeResult.ExitCode?.ToString() ?? "?"}";
                bool attributed = false;
                foreach (string ruleId in evaluation.Case.DeclaredRuleIds)
                {
                    if (aggregates.TryGetValue(ruleId, out RuleAggregate? aggregate))
                    {
                        aggregate.Crashes.Add((evaluation.Case.Name, kind, detail));
                        attributed = true;
                    }
                }

                if (!attributed)
                {
                    unattributable.Add(
                        $"{evaluation.Case.Name}: native {kind} could not be " +
                        $"attributed to any rule in the module's catalog " +
                        $"(case declares: " +
                        $"[{string.Join(", ", evaluation.Case.DeclaredRuleIds)}]) - " +
                        detail);
                }
            }
        }

        var entries = new List<BurndownEntry>(ruleIds.Count);
        foreach (string ruleId in ruleIds.OrderBy(id => id, StringComparer.Ordinal))
        {
            entries.Add(Resolve(ruleId, aggregates[ruleId]));
        }

        return new BurndownResult(entries, unattributable);
    }

    private static BurndownEntry Resolve(string ruleId, RuleAggregate aggregate)
    {
        if (aggregate.Crashes.Count > 0)
        {
            (string CaseName, string Kind, string Detail) crash =
                aggregate.Crashes.FirstOrDefault(c => c.Kind == "CompilerCrash")
                    is { CaseName: not null } compilerCrash
                    ? compilerCrash
                    : aggregate.Crashes[0];
            return Fail(ruleId, null, crash);
        }

        if (aggregate.NativeFailures.Count > 0)
        {
            (string caseName, AnalyzerFailure failure) = aggregate.NativeFailures[0];
            return new BurndownEntry(
                ruleId,
                failure.AnalyzerType,
                BurndownStatus.Fail,
                new BurndownReason(
                    "AnalyzerException",
                    caseName,
                    failure.ActionKind,
                    failure.ExceptionType,
                    failure.FailingMember,
                    failure.ParseFailed
                        ? "AD0001 message did not match the expected format"
                        : null));
        }

        if (aggregate.NonMatchEntries.Count > 0)
        {
            (string caseName, DiffEntry entry) = aggregate.NonMatchEntries
                .OrderBy(
                    pair => Array.IndexOf(
                        ReasonPrecedence.Order, pair.Entry.Kind.ToString()),
                    Comparer<int>.Default)
                .First();
            // Deliberately no Detail: spans and message text shift when a
            // corpus source is edited, which would churn the checked-in
            // baseline without any behavior change. The full diff detail
            // lives in report.json.
            return new BurndownEntry(
                ruleId,
                null,
                BurndownStatus.Fail,
                new BurndownReason(
                    entry.Kind.ToString(),
                    caseName,
                    null,
                    null,
                    null,
                    null));
        }

        if (aggregate.ManagedDiagnosticCount > 0)
        {
            return new BurndownEntry(
                ruleId, null, BurndownStatus.Pass, null);
        }

        return new BurndownEntry(ruleId, null, BurndownStatus.NotExercised, null);
    }

    private static BurndownEntry Fail(
        string ruleId,
        string? analyzerType,
        (string CaseName, string Kind, string Detail) crash) =>
        new(
            ruleId,
            analyzerType,
            BurndownStatus.Fail,
            new BurndownReason(crash.Kind, crash.CaseName, null, null, null, crash.Detail));

    private sealed class RuleAggregate
    {
        public int ManagedDiagnosticCount { get; set; }

        public List<(string CaseName, DiffEntry Entry)> NonMatchEntries { get; } = [];

        public List<(string CaseName, AnalyzerFailure Failure)> NativeFailures { get; } = [];

        public List<(string CaseName, string Kind, string Detail)> Crashes { get; } = [];
    }
}
