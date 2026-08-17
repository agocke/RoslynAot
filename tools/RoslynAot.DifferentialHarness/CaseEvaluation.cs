namespace RoslynAot.DifferentialHarness;

internal sealed record CaseEvaluation(
    CorpusCase Case,
    CompilationResult ManagedResult,
    CompilationResult NativeResult,
    IReadOnlyList<NormalizedDiagnostic> ManagedDiagnostics,
    IReadOnlyList<NormalizedDiagnostic> NativeDiagnostics,
    IReadOnlyList<DiffEntry> DiffEntries,
    IReadOnlyList<AnalyzerFailure> ManagedFailures,
    IReadOnlyList<AnalyzerFailure> NativeFailures,
    string? ManagedToolVersion,
    string? NativeToolVersion,
    IReadOnlyList<string> ManagedRuleCatalog);

/// <summary>
/// Runs both compilers for one corpus case and reduces the results to
/// everything the burn-down and report need. AD*-ruleId results are
/// pulled out of the diagnostic comparison and parsed as analyzer
/// failures instead - otherwise a crashing analyzer registers as both a
/// crash AND a spurious MissingDiagnostic.
/// </summary>
internal static class CaseEvaluator
{
    public static CaseEvaluation Evaluate(
        HarnessEnvironment environment,
        CompilationRunner runner,
        CorpusCase corpusCase,
        IReadOnlySet<string> comparableRuleIds,
        string outputRoot)
    {
        CompilationResult managedResult =
            runner.Run(corpusCase, CompilationSide.Managed, outputRoot);
        CompilationResult nativeResult =
            runner.Run(corpusCase, CompilationSide.Native, outputRoot);

        (List<NormalizedDiagnostic> managedDiagnostics,
                List<AnalyzerFailure> managedFailures,
                string? managedVersion,
                List<string> managedRuleCatalog) =
            ReadSide(environment, managedResult);
        (List<NormalizedDiagnostic> nativeDiagnostics,
                List<AnalyzerFailure> nativeFailures,
                string? nativeVersion,
                _) =
            ReadSide(environment, nativeResult);

        bool InScope(NormalizedDiagnostic d) =>
            comparableRuleIds.Contains(d.RuleId) ||
            d.RuleId.StartsWith("CS", StringComparison.Ordinal);

        IReadOnlyList<DiffEntry> diffEntries = DiagnosticComparer.Compare(
            managedDiagnostics.Where(InScope).ToArray(),
            nativeDiagnostics.Where(InScope).ToArray());

        return new CaseEvaluation(
            corpusCase,
            managedResult,
            nativeResult,
            managedDiagnostics,
            nativeDiagnostics,
            diffEntries,
            managedFailures,
            nativeFailures,
            managedVersion,
            nativeVersion,
            managedRuleCatalog);
    }

    private static (List<NormalizedDiagnostic>, List<AnalyzerFailure>, string?, List<string>)
        ReadSide(HarnessEnvironment environment, CompilationResult result)
    {
        if (result.TimedOut || result.Crashed || !result.SarifProduced)
        {
            return ([], [], null, []);
        }

        NormalizedSarif sarif = SarifReader.ReadAndNormalize(
            result.SarifPath, environment.RepoRoot);
        var diagnostics = new List<NormalizedDiagnostic>();
        var failures = new List<AnalyzerFailure>();
        foreach (NormalizedDiagnostic diagnostic in sarif.Diagnostics)
        {
            if (diagnostic.RuleId.StartsWith("AD", StringComparison.Ordinal))
            {
                failures.Add(AnalyzerFailureParser.Parse(diagnostic.Message));
            }
            else
            {
                diagnostics.Add(diagnostic);
            }
        }

        return (
            diagnostics,
            failures,
            sarif.ToolVersion,
            sarif.RuleCatalog.Select(rule => rule.Id).ToList());
    }
}
