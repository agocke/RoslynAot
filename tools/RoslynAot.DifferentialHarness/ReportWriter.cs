using System.Text.Json;
using System.Text.Json.Serialization;

namespace RoslynAot.DifferentialHarness;

internal sealed class HarnessReport
{
    [JsonPropertyName("generatedAtUtc")]
    public string GeneratedAtUtc { get; set; } = "";

    [JsonPropertyName("environment")]
    public ReportEnvironment Environment { get; set; } = new();

    [JsonPropertyName("ruleIds")]
    public List<string> RuleIds { get; set; } = [];

    [JsonPropertyName("burndown")]
    public List<BaselineEntry> Burndown { get; set; } = [];

    [JsonPropertyName("uncomparedFields")]
    public List<UncomparedFieldReport> UncomparedFields { get; set; } = [];

    [JsonPropertyName("coverageLedger")]
    public List<string> CoverageLedger { get; set; } = [];

    [JsonPropertyName("cases")]
    public List<ReportCase> Cases { get; set; } = [];

    [JsonPropertyName("baselineVerdict")]
    public string? BaselineVerdict { get; set; }

    [JsonPropertyName("baselineRegressions")]
    public List<string> BaselineRegressions { get; set; } = [];

    [JsonPropertyName("baselineStaleReasons")]
    public List<string> BaselineStaleReasons { get; set; } = [];
}

internal sealed class ReportEnvironment
{
    [JsonPropertyName("sdkDirectory")]
    public string SdkDirectory { get; set; } = "";

    [JsonPropertyName("referenceDirectory")]
    public string ReferenceDirectory { get; set; } = "";

    [JsonPropertyName("managedToolVersion")]
    public string? ManagedToolVersion { get; set; }

    [JsonPropertyName("nativeToolVersion")]
    public string? NativeToolVersion { get; set; }

    [JsonPropertyName("toolVersionSkew")]
    public bool ToolVersionSkew { get; set; }

    [JsonPropertyName("note")]
    public string Note { get; set; } =
        "All comparable rules are forced to 'warning' severity for every " +
        "case, both sides. This measures the forced-on world, not each " +
        "rule's default-enabled behavior.";
}

internal sealed class UncomparedFieldReport
{
    [JsonPropertyName("field")]
    public string Field { get; set; } = "";

    [JsonPropertyName("managedResultCount")]
    public int ManagedResultCount { get; set; }

    [JsonPropertyName("reason")]
    public string Reason { get; set; } = "";
}

internal sealed class ReportCase
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("managedExitCode")]
    public int? ManagedExitCode { get; set; }

    [JsonPropertyName("nativeExitCode")]
    public int? NativeExitCode { get; set; }

    [JsonPropertyName("nativeCrashed")]
    public bool NativeCrashed { get; set; }

    [JsonPropertyName("nativeTimedOut")]
    public bool NativeTimedOut { get; set; }

    [JsonPropertyName("diffEntryCounts")]
    public Dictionary<string, int> DiffEntryCounts { get; set; } = [];
}

internal static class ReportWriter
{
    private static readonly (UncomparedFieldFlags Flag, string Name, string Reason)[]
        s_uncomparedFields =
        [
            (UncomparedFieldFlags.Properties, "Properties",
                "NativeAnalyzerDiagnostic.Properties is hardcoded empty " +
                "(src/CscAot/NativeDiagnosticAnalyzer.cs) - Step 6"),
            (UncomparedFieldFlags.AdditionalLocations, "AdditionalLocations",
                "NativeAnalyzerDiagnostic.AdditionalLocations is hardcoded " +
                "empty (src/CscAot/NativeDiagnosticAnalyzer.cs) - Step 6"),
            (UncomparedFieldFlags.RelatedLocations, "RelatedLocations",
                "not transported by ReportDiagnostic " +
                "(src/RoslynAot.Abi/AnalyzerAbi.cs) - Step 6"),
            (UncomparedFieldFlags.Suppressions, "Suppressions/IsSuppressed",
                "not transported by ReportDiagnostic " +
                "(src/RoslynAot.Abi/AnalyzerAbi.cs) - Step 6"),
            (UncomparedFieldFlags.Fixes, "Fixes",
                "no code-fix transport exists in the ABI"),
        ];

    public static HarnessReport Build(
        HarnessEnvironment environment,
        IReadOnlyList<string> comparableRuleIds,
        IReadOnlyList<CaseEvaluation> caseEvaluations,
        IReadOnlyList<BurndownEntry> burndown,
        IReadOnlyList<string> coverageLedger,
        BaselineComparisonResult? baselineComparison)
    {
        var inScope = new HashSet<string>(comparableRuleIds, StringComparer.Ordinal);
        bool InScope(NormalizedDiagnostic d) =>
            inScope.Contains(d.RuleId) ||
            d.RuleId.StartsWith("CS", StringComparison.Ordinal);

        List<NormalizedDiagnostic> allManagedInScope = caseEvaluations
            .SelectMany(e => e.ManagedDiagnostics.Where(InScope))
            .ToList();

        var uncomparedFields = s_uncomparedFields
            .Select(entry => new UncomparedFieldReport
            {
                Field = entry.Name,
                ManagedResultCount = allManagedInScope
                    .Count(d => (d.UncomparedFields & entry.Flag) != 0),
                Reason = entry.Reason,
            })
            .ToList();

        string? managedVersion = caseEvaluations
            .Select(e => e.ManagedToolVersion)
            .FirstOrDefault(v => v is not null);
        string? nativeVersion = caseEvaluations
            .Select(e => e.NativeToolVersion)
            .FirstOrDefault(v => v is not null);

        BaselineDocument burndownAsBaseline = BaselineDocument.FromBurndown(burndown);

        var report = new HarnessReport
        {
            GeneratedAtUtc = DateTime.UtcNow.ToString("O"),
            Environment = new ReportEnvironment
            {
                SdkDirectory = environment.SdkDirectory,
                ReferenceDirectory = environment.ReferenceDirectory,
                ManagedToolVersion = managedVersion,
                NativeToolVersion = nativeVersion,
                ToolVersionSkew = managedVersion is not null &&
                    nativeVersion is not null &&
                    managedVersion != nativeVersion,
            },
            RuleIds = comparableRuleIds
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToList(),
            Burndown = burndownAsBaseline.Entries,
            UncomparedFields = uncomparedFields,
            CoverageLedger = coverageLedger
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToList(),
            Cases = caseEvaluations.Select(e => new ReportCase
            {
                Name = e.Case.Name,
                ManagedExitCode = e.ManagedResult.ExitCode,
                NativeExitCode = e.NativeResult.ExitCode,
                NativeCrashed = e.NativeResult.Crashed,
                NativeTimedOut = e.NativeResult.TimedOut,
                DiffEntryCounts = e.DiffEntries
                    .GroupBy(d => d.Kind.ToString())
                    .ToDictionary(g => g.Key, g => g.Count()),
            }).ToList(),
            BaselineVerdict = baselineComparison?.Verdict.ToString(),
            BaselineRegressions = baselineComparison?.Regressions.ToList() ?? [],
            BaselineStaleReasons = baselineComparison?.StaleReasons.ToList() ?? [],
        };
        return report;
    }

    public static void WriteJson(HarnessReport report, string path)
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(path, JsonSerializer.Serialize(report, options));
    }

    public static void WriteMarkdown(HarnessReport report, string path)
    {
        int pass = report.Burndown.Count(e => e.Status == "Pass");
        int notExercised = report.Burndown.Count(e => e.Status == "NotExercised");
        int fail = report.Burndown.Count(e => e.Status == "Fail");

        var writer = new StringWriter();
        writer.WriteLine("# Differential harness report");
        writer.WriteLine();
        writer.WriteLine(
            $"**{pass} passing / {notExercised} not exercised / {fail} " +
            $"failing of {report.Burndown.Count} rules**");
        writer.WriteLine();

        if (report.BaselineVerdict is not null)
        {
            writer.WriteLine($"Baseline verdict: **{report.BaselineVerdict}**");
            foreach (string regression in report.BaselineRegressions)
            {
                writer.WriteLine($"- REGRESSION: {regression}");
            }

            foreach (string staleReason in report.BaselineStaleReasons)
            {
                writer.WriteLine($"- STALE: {staleReason}");
            }

            writer.WriteLine();
        }

        writer.WriteLine("## Environment");
        writer.WriteLine();
        writer.WriteLine($"- SDK directory: `{report.Environment.SdkDirectory}`");
        writer.WriteLine(
            $"- Reference directory: `{report.Environment.ReferenceDirectory}`");
        writer.WriteLine(
            $"- Managed tool version: {report.Environment.ManagedToolVersion ?? "(unknown)"}");
        writer.WriteLine(
            $"- Native tool version: {report.Environment.NativeToolVersion ?? "(unknown)"}");
        if (report.Environment.ToolVersionSkew)
        {
            writer.WriteLine(
                "- **WARNING: tool versions differ.** Byte-equality and " +
                "message comparisons are not meaningful under version skew.");
        }

        writer.WriteLine($"- {report.Environment.Note}");
        writer.WriteLine();

        writer.WriteLine("## Burn-down");
        writer.WriteLine();
        writer.WriteLine("| Rule | Status | Reason |");
        writer.WriteLine("|---|---|---|");
        foreach (BaselineEntry entry in report.Burndown)
        {
            string reason = entry.Reason is null
                ? ""
                : $"{entry.Reason.Kind} in `{entry.Reason.Case}`" +
                    (entry.Reason.FailingMember is null
                        ? ""
                        : $" — `{entry.Reason.FailingMember}`");
            writer.WriteLine($"| {entry.RuleId} | {entry.Status} | {reason} |");
        }

        writer.WriteLine();
        writer.WriteLine("## Uncompared fields");
        writer.WriteLine();
        writer.WriteLine(
            "These SARIF fields are not part of the pass/fail comparison " +
            "because the current ABI cannot transport them at all " +
            "(migration Step 6). Counts are how many in-scope managed " +
            "results carry a non-default value the native path structurally " +
            "cannot reproduce today.");
        writer.WriteLine();
        writer.WriteLine("| Field | Managed results affected | Why |");
        writer.WriteLine("|---|---|---|");
        foreach (UncomparedFieldReport field in report.UncomparedFields)
        {
            writer.WriteLine(
                $"| {field.Field} | {field.ManagedResultCount} | {field.Reason} |");
        }

        if (report.CoverageLedger.Count > 0)
        {
            writer.WriteLine();
            writer.WriteLine("## Not in the native module");
            writer.WriteLine();
            writer.WriteLine(
                $"{report.CoverageLedger.Count} rule(s) the managed side's " +
                "loaded analyzer assemblies support but the native module " +
                "under test does not link. This is a scoping fact about " +
                "which analyzers were compiled into the module, not a " +
                "pass/fail signal.");
            writer.WriteLine();
            writer.WriteLine(string.Join(", ", report.CoverageLedger));
        }

        File.WriteAllText(path, writer.ToString());
    }
}
