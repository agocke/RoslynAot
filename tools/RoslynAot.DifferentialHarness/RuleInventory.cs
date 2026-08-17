namespace RoslynAot.DifferentialHarness;

internal static class RuleInventory
{
    /// <summary>
    /// Discovers the rule IDs the native module under test exposes, by
    /// reading the SARIF rule catalog from a throwaway compile. The
    /// catalog reflects every loaded analyzer's SupportedDiagnostics
    /// regardless of whether a globalconfig enables them, so this needs
    /// no severity forcing - it is what tells the harness which IDs to
    /// force on in the first place.
    /// </summary>
    public static IReadOnlyList<string> ProbeNativeRuleIds(
        HarnessEnvironment environment,
        string workDirectory)
    {
        Directory.CreateDirectory(workDirectory);
        string probeConfigPath = Path.Combine(workDirectory, "probe.globalconfig");
        File.WriteAllText(probeConfigPath, "is_global = true\n");

        var probeCase = new CorpusCase(
            "Probe/Native",
            workDirectory,
            [WriteProbeSource(workDirectory)],
            [],
            []);
        var runner = new CompilationRunner(environment, probeConfigPath, 60);
        CompilationResult result =
            runner.Run(probeCase, CompilationSide.Native, workDirectory);
        if (!result.SarifProduced)
        {
            throw new HarnessEnvironmentException(
                "Probing the native module for its rule catalog produced " +
                $"no SARIF output (exit code {result.ExitCode}). See " +
                $"{result.StdErrPath}.");
        }

        NormalizedSarif sarif =
            SarifReader.ReadAndNormalize(result.SarifPath, environment.RepoRoot);
        if (sarif.RuleCatalog.Count == 0)
        {
            throw new HarnessEnvironmentException(
                $"The native module at '{environment.NativeModulePath}' " +
                "reported an empty rule catalog.");
        }

        // One rules[] entry is written per distinct DiagnosticDescriptor, so
        // two descriptors sharing an ID (common where an analyzer declares
        // per-language variants) yield duplicate IDs here. Everything
        // downstream keys dictionaries on the rule ID, so de-duplicate.
        return sarif.RuleCatalog
            .Select(rule => rule.Id)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
    }

    private static string WriteProbeSource(string workDirectory)
    {
        string path = Path.Combine(workDirectory, "Probe.cs");
        File.WriteAllText(path, "internal class __DifferentialHarnessProbe { }\n");
        return path;
    }
}
