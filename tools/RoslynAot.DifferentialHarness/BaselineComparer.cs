namespace RoslynAot.DifferentialHarness;

internal enum BaselineVerdict
{
    Match,
    Regression,
    Stale,
}

internal sealed record BaselineComparisonResult(
    BaselineVerdict Verdict,
    IReadOnlyList<string> Regressions,
    IReadOnlyList<string> StaleReasons);

/// <summary>
/// Exact-match comparison against the checked-in baseline, with the
/// diff classified so "you broke something" (regression, exit 1) and
/// "you fixed something, commit the baseline" (stale, exit 2) get
/// different verdicts. Collapsing the two into one "failed" is how
/// ratchets get disabled.
/// </summary>
internal static class BaselineComparer
{
    public static BaselineComparisonResult Compare(
        BaselineDocument? baseline,
        IReadOnlyList<BurndownEntry> current)
    {
        var regressions = new List<string>();
        var stale = new List<string>();

        if (baseline is null)
        {
            stale.Add(
                "No baseline file exists yet. Run with --update-baseline " +
                "to create one.");
            return new BaselineComparisonResult(
                BaselineVerdict.Stale, regressions, stale);
        }

        Dictionary<string, BaselineEntry> baselineByRule =
            baseline.Entries.ToDictionary(e => e.RuleId);
        Dictionary<string, BurndownEntry> currentByRule =
            current.ToDictionary(e => e.RuleId);

        foreach (BurndownEntry entry in current)
        {
            if (!baselineByRule.TryGetValue(entry.RuleId, out BaselineEntry? baselineEntry))
            {
                stale.Add($"{entry.RuleId}: new rule, not in baseline yet");
                continue;
            }

            string was = baselineEntry.Status;
            string now = entry.Status.ToString();

            // Any transition that loses ground is a regression, not just
            // losing a Pass. With most rules starting at NotExercised,
            // NotExercised -> Fail is the dominant way a real break shows
            // up, and Fail -> NotExercised is how one gets silenced by
            // deleting the corpus case that exposed it.
            bool newlyFailing = now == nameof(BurndownStatus.Fail) &&
                was != nameof(BurndownStatus.Fail);
            bool lostAPass = was == nameof(BurndownStatus.Pass) &&
                now != nameof(BurndownStatus.Pass);
            bool coverageSilenced = was == nameof(BurndownStatus.Fail) &&
                now == nameof(BurndownStatus.NotExercised);

            if (newlyFailing || lostAPass || coverageSilenced)
            {
                regressions.Add(
                    $"{entry.RuleId}: regressed from {was} to {now} " +
                    $"({DescribeReason(entry)})");
                continue;
            }

            if (was != now)
            {
                stale.Add($"{entry.RuleId}: improved from {was} to {now}");
                continue;
            }

            if (!ReasonsEqual(baselineEntry.Reason, entry.Reason))
            {
                stale.Add(
                    $"{entry.RuleId}: {now} reason changed " +
                    $"({DescribeReason(entry)})");
            }
        }

        foreach (BaselineEntry baselineEntry in baseline.Entries)
        {
            if (!currentByRule.ContainsKey(baselineEntry.RuleId))
            {
                regressions.Add(
                    $"{baselineEntry.RuleId}: present in baseline but not in " +
                    "this run (module or corpus shrank)");
            }
        }

        BaselineVerdict verdict = regressions.Count > 0
            ? BaselineVerdict.Regression
            : stale.Count > 0
                ? BaselineVerdict.Stale
                : BaselineVerdict.Match;
        return new BaselineComparisonResult(verdict, regressions, stale);
    }

    private static bool ReasonsEqual(BaselineReason? baseline, BurndownReason? current)
    {
        if (baseline is null || current is null)
        {
            return baseline is null && current is null;
        }

        return baseline.Kind == current.Kind &&
            baseline.Case == current.Case &&
            baseline.ActionKind == current.ActionKind &&
            baseline.ExceptionType == current.ExceptionType &&
            baseline.FailingMember == current.FailingMember &&
            baseline.Detail == current.Detail;
    }

    private static string DescribeReason(BurndownEntry entry) =>
        entry.Reason is null
            ? "no reason recorded"
            : $"{entry.Reason.Kind} in {entry.Reason.Case}";
}
