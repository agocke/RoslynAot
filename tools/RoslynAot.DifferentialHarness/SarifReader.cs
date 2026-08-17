using System.Text.Json;

namespace RoslynAot.DifferentialHarness;

internal sealed record NormalizedSarif(
    string? ToolVersion,
    List<SarifRule> RuleCatalog,
    List<NormalizedDiagnostic> Diagnostics);

internal static class SarifReader
{
    private static readonly JsonSerializerOptions s_options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static NormalizedSarif ReadAndNormalize(string sarifPath, string repoRoot)
    {
        if (!File.Exists(sarifPath))
        {
            throw new HarnessEnvironmentException(
                $"Expected SARIF output was not produced: '{sarifPath}'.");
        }

        using FileStream stream = File.OpenRead(sarifPath);
        SarifLog log = JsonSerializer.Deserialize<SarifLog>(stream, s_options) ??
            throw new HarnessEnvironmentException(
                $"Could not parse SARIF file '{sarifPath}'.");

        if (log.Runs.Count == 0)
        {
            throw new HarnessEnvironmentException(
                $"SARIF file '{sarifPath}' has no runs.");
        }

        SarifRun run = log.Runs[0];
        var diagnostics = new List<NormalizedDiagnostic>(run.Results.Count);
        foreach (SarifResult result in run.Results)
        {
            diagnostics.Add(Normalize(result, repoRoot));
        }

        return new NormalizedSarif(
            run.Tool.Driver.Version,
            run.Tool.Driver.Rules,
            diagnostics);
    }

    private static NormalizedDiagnostic Normalize(SarifResult result, string repoRoot)
    {
        SarifLocation? location = result.Locations.Count > 0
            ? result.Locations[0]
            : null;
        SarifRegion? region = location?.PhysicalLocation?.Region;
        string uri = location?.PhysicalLocation?.ArtifactLocation?.Uri ?? "";
        string file = NormalizePath(uri, repoRoot);

        int startLine = region?.StartLine ?? 0;
        int startColumn = region?.StartColumn ?? 0;
        int endLine = region?.EndLine ?? startLine;
        int? endColumn = region?.EndColumn;

        var flags = UncomparedFieldFlags.None;
        if (result.Properties.Count > 0)
        {
            flags |= UncomparedFieldFlags.Properties;
        }

        if (result.Locations.Count > 1)
        {
            flags |= UncomparedFieldFlags.AdditionalLocations;
        }

        if (result.RelatedLocations.Count > 0)
        {
            flags |= UncomparedFieldFlags.RelatedLocations;
        }

        if (result.Suppressions.Count > 0)
        {
            flags |= UncomparedFieldFlags.Suppressions;
        }

        if (result.Fixes.Count > 0)
        {
            flags |= UncomparedFieldFlags.Fixes;
        }

        return new NormalizedDiagnostic(
            result.RuleId,
            // SARIF v2 defaults an absent result level to "warning" for
            // fail-kind results; Roslyn has been observed to always emit
            // it explicitly, but do not assume that holds forever.
            result.Level ?? "warning",
            file,
            startLine,
            startColumn,
            endLine,
            endColumn,
            (result.Message.Text ?? "").TrimEnd(),
            flags);
    }

    private static string NormalizePath(string uri, string repoRoot)
    {
        if (uri.Length == 0)
        {
            return "";
        }

        string path = uri.StartsWith("file://", StringComparison.Ordinal)
            ? Uri.UnescapeDataString(uri["file://".Length..])
            : uri;

        // /pathmap:<repoRoot>=/_/ makes in-repo sources show up as
        // "/_/<relative path>"; strip that prefix so both sides compare
        // as clean, repo-relative POSIX paths regardless of where the
        // repo is checked out. A path that did NOT get pathmapped (e.g.
        // it resolved outside the repo) is left absolute on purpose,
        // so the divergence stays visible instead of being masked.
        if (path.StartsWith("/_/", StringComparison.Ordinal))
        {
            return path["/_/".Length..];
        }

        return path.Replace('\\', '/');
    }
}
