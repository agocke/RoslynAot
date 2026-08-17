using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace RoslynAot.DifferentialHarness;

/// <summary>
/// Builds one NativeAOT module per analyzer and records its size, retained
/// type count, and ILC time — the trimming baseline migration Step 1 asks for,
/// established before anything can regress it.
/// </summary>
internal static class ModuleRunner
{
    private const string ProjectPath =
        "samples/RoslynAot.CSharpNetAnalyzers.Native/" +
        "RoslynAot.CSharpNetAnalyzers.Native.csproj";

    private const string ModuleRelativePath =
        "artifacts/publish/RoslynAot.CSharpNetAnalyzers.Native/" +
        "release_linux-x64/libroslyn-aot-csharp-net-analyzers.so";

    private const string MstatRelativePath =
        "artifacts/obj/RoslynAot.CSharpNetAnalyzers.Native/" +
        "release_linux-x64/native/roslyn-aot-csharp-net-analyzers.mstat";

    /// <summary>The whole-assembly module, measured alongside the singles.</summary>
    private const string AllAnalyzersModule = "(all analyzers)";

    public static IReadOnlyList<ModuleMeasurementResult> Run(
        HarnessEnvironment environment,
        string outputDirectory,
        string? filter)
    {
        IReadOnlyList<string> analyzers = ListAnalyzers(environment);
        if (filter is not null)
        {
            analyzers = analyzers
                .Where(name => name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        var measurement = new ModuleMeasurement(
            environment.RepoRoot,
            ProjectPath,
            Path.Combine(environment.RepoRoot, ModuleRelativePath),
            Path.Combine(environment.RepoRoot, MstatRelativePath));

        var results = new List<ModuleMeasurementResult>();

        // Measured first: it is the number every single-analyzer module is
        // compared against, and the one most likely to fail if the tree is
        // broken.
        if (filter is null)
        {
            Console.WriteLine($"Measuring {AllAnalyzersModule} ...");
            results.Add(measurement.Measure(AllAnalyzersModule, []));
        }

        foreach (string analyzer in analyzers)
        {
            Console.WriteLine($"Measuring {analyzer} ...");
            results.Add(measurement.Measure(analyzer, [analyzer]));
        }

        Directory.CreateDirectory(outputDirectory);
        File.WriteAllText(
            Path.Combine(outputDirectory, "modules.json"),
            JsonSerializer.Serialize(
                results,
                ModuleJsonContext.Default.ListModuleMeasurementResult));
        File.WriteAllText(
            Path.Combine(outputDirectory, "modules.md"),
            WriteMarkdown(results));
        return results;
    }

    public static ModuleBaseline ToBaseline(
        IReadOnlyList<ModuleMeasurementResult> results) =>
        new()
        {
            Modules = results
                .Where(result => result.Failure is null)
                .OrderBy(result => result.Module, StringComparer.Ordinal)
                .Select(result => new ModuleSizeEntry
                {
                    Module = result.Module,
                    SizeBytes = result.SizeBytes,
                    RetainedTypeCount = result.RetainedTypeCount,
                    RetainedMethodCount = result.RetainedMethodCount,
                })
                .ToList(),
        };

    private static IReadOnlyList<string> ListAnalyzers(
        HarnessEnvironment environment)
    {
        string generator = Path.Combine(
            environment.RepoRoot,
            "artifacts/bin/RoslynAot.RoslynFacadeGenerator/release/" +
            "RoslynAot.RoslynFacadeGenerator.dll");
        if (!File.Exists(generator))
        {
            throw new HarnessEnvironmentException(
                $"The facade generator was not found at '{generator}'. " +
                "Build it before measuring modules.");
        }

        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = environment.RepoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string argument in new[]
        {
            generator,
            "generate-analyzer-entrypoint",
            "--list",
            "--language", "C#",
            "--reference", environment.AnalyzerDirectory,
            "--reference", environment.RoslynBincoreDirectory,
            Path.Combine(
                environment.AnalyzerDirectory,
                "Microsoft.CodeAnalysis.CSharp.NetAnalyzers.dll"),
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(startInfo) ??
            throw new HarnessEnvironmentException(
                "Could not start the facade generator to list analyzers.");
        Task<string> stdOut = process.StandardOutput.ReadToEndAsync();
        Task<string> stdErr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new HarnessEnvironmentException(
                "Listing analyzers failed: " +
                stdErr.GetAwaiter().GetResult().Trim());
        }

        return stdOut.GetAwaiter().GetResult()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .OrderBy(line => line, StringComparer.Ordinal)
            .ToArray();
    }

    private static string WriteMarkdown(
        IReadOnlyList<ModuleMeasurementResult> results)
    {
        var writer = new StringWriter();
        writer.WriteLine("# Per-analyzer module baseline");
        writer.WriteLine();
        writer.WriteLine(
            "One NativeAOT module per analyzer, plus the whole-assembly " +
            "module. Size and retained counts are deterministic and are what " +
            "`eng/module-baseline.json` ratchets; ILC and publish times are " +
            "informational and deliberately excluded from the baseline.");
        writer.WriteLine();
        writer.WriteLine(
            "| Module | Size (bytes) | Retained types | Retained methods | ILC ms | Publish s |");
        writer.WriteLine("|---|---|---|---|---|---|");
        foreach (ModuleMeasurementResult result in results
            .OrderByDescending(result => result.SizeBytes))
        {
            if (result.Failure is not null)
            {
                writer.WriteLine(
                    $"| `{result.Module}` | FAILED | | | | {result.Failure} |");
                continue;
            }

            // Zero means the incremental build skipped IlcCompile entirely,
            // not that it was instantaneous.
            string ilc = result.IlcMilliseconds == 0
                ? "n/a (incremental)"
                : result.IlcMilliseconds.ToString();
            writer.WriteLine(
                $"| `{result.Module}` | {result.SizeBytes} | " +
                $"{result.RetainedTypeCount} | {result.RetainedMethodCount} | " +
                $"{ilc} | {result.PublishSeconds} |");
        }

        return writer.ToString();
    }

    /// <summary>
    /// Exact-match comparison against the checked-in baseline, reported as a
    /// unified list rather than a verdict enum: any size or count change is
    /// worth a human look, in either direction.
    /// </summary>
    public static IReadOnlyList<string> Compare(
        ModuleBaseline baseline,
        ModuleBaseline observed)
    {
        var differences = new List<string>();
        var baselineByModule = baseline.Modules.ToDictionary(
            entry => entry.Module,
            StringComparer.Ordinal);
        var observedByModule = observed.Modules.ToDictionary(
            entry => entry.Module,
            StringComparer.Ordinal);

        foreach (string module in baselineByModule.Keys
            .Union(observedByModule.Keys, StringComparer.Ordinal)
            .OrderBy(module => module, StringComparer.Ordinal))
        {
            if (!baselineByModule.TryGetValue(module, out ModuleSizeEntry? before))
            {
                differences.Add($"{module}: new module");
                continue;
            }

            if (!observedByModule.TryGetValue(module, out ModuleSizeEntry? after))
            {
                differences.Add($"{module}: no longer measured");
                continue;
            }

            var changes = new StringBuilder();
            if (before.SizeBytes != after.SizeBytes)
            {
                changes.Append(
                    $" size {before.SizeBytes} -> {after.SizeBytes} " +
                    $"({after.SizeBytes - before.SizeBytes:+#;-#;0});");
            }

            if (before.RetainedTypeCount != after.RetainedTypeCount)
            {
                changes.Append(
                    $" types {before.RetainedTypeCount} -> " +
                    $"{after.RetainedTypeCount};");
            }

            if (before.RetainedMethodCount != after.RetainedMethodCount)
            {
                changes.Append(
                    $" methods {before.RetainedMethodCount} -> " +
                    $"{after.RetainedMethodCount};");
            }

            if (changes.Length > 0)
            {
                differences.Add($"{module}:{changes}");
            }
        }

        return differences;
    }
}
