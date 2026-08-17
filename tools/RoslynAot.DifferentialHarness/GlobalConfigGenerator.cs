namespace RoslynAot.DifferentialHarness;

internal static class GlobalConfigGenerator
{
    /// <summary>
    /// Forces every rule ID to "warning" severity. Most CA rules default
    /// to a severity Roslyn skips analyzer execution for entirely, so
    /// without this only a handful of analyzers in a 43-analyzer module
    /// ever call Initialize. This is problem 19's "every non-empty
    /// supported diagnostic explicitly enabled".
    /// </summary>
    public static string Generate(
        IEnumerable<string> ruleIds,
        string? optionsPreambleContent)
    {
        var writer = new StringWriter();
        writer.WriteLine("is_global = true");
        writer.WriteLine("global_level = 100");
        writer.WriteLine();

        if (!string.IsNullOrEmpty(optionsPreambleContent))
        {
            writer.WriteLine(optionsPreambleContent.TrimEnd());
            writer.WriteLine();
        }

        foreach (string ruleId in ruleIds.OrderBy(id => id, StringComparer.Ordinal))
        {
            writer.WriteLine($"dotnet_diagnostic.{ruleId}.severity = warning");
        }

        return writer.ToString();
    }
}
