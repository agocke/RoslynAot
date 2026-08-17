namespace RoslynAot.DifferentialHarness;

/// <summary>
/// A SARIF result reduced to the fields the current ABI can carry, plus
/// flags for the fields it cannot (see <see cref="UncomparedFieldFlags"/>).
/// Only <see cref="ComparisonKey"/>'s fields plus <see cref="Level"/> and
/// <see cref="Message"/> participate in the differential comparison.
/// </summary>
internal sealed record NormalizedDiagnostic(
    string RuleId,
    string Level,
    string File,
    int StartLine,
    int StartColumn,
    int EndLine,
    int? EndColumn,
    string Message,
    UncomparedFieldFlags UncomparedFields)
{
    public (string RuleId, string File, int StartLine, int StartColumn,
        int EndLine, int? EndColumn) ComparisonKey =>
        (RuleId, File, StartLine, StartColumn, EndLine, EndColumn);
}

/// <summary>
/// Marks which SARIF fields a diagnostic carried that the harness does
/// not compare, because the current ABI (src/RoslynAot.Abi/AnalyzerAbi.cs
/// ReportDiagnostic; src/CscAot/NativeDiagnosticAnalyzer.cs
/// NativeAnalyzerDiagnostic) cannot transport them at all. Declared and
/// counted in the report rather than silently dropped — see migration
/// Step 6.
/// </summary>
[Flags]
internal enum UncomparedFieldFlags
{
    None = 0,
    Properties = 1 << 0,
    AdditionalLocations = 1 << 1,
    RelatedLocations = 1 << 2,
    Suppressions = 1 << 3,
    Fixes = 1 << 4,
}
