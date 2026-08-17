using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RoslynAot.DifferentialHarness;

/// <summary>
/// The deterministic half of a module measurement: what the trimming baseline
/// ratchets against. Wall-clock timings are deliberately not here — they are
/// nondeterministic, and a baseline that churns every run stops being read.
/// </summary>
internal sealed class ModuleSizeEntry
{
    [JsonPropertyName("module")]
    public string Module { get; set; } = "";

    [JsonPropertyName("sizeBytes")]
    public long SizeBytes { get; set; }

    [JsonPropertyName("retainedTypeCount")]
    public int RetainedTypeCount { get; set; }

    [JsonPropertyName("retainedMethodCount")]
    public int RetainedMethodCount { get; set; }
}

internal sealed class ModuleBaseline
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = 1;

    [JsonPropertyName("modules")]
    public List<ModuleSizeEntry> Modules { get; set; } = [];
}

/// <summary>
/// A measurement plus the timings that stay out of the baseline.
/// </summary>
internal sealed class ModuleMeasurementResult
{
    [JsonPropertyName("module")]
    public string Module { get; set; } = "";

    [JsonPropertyName("sizeBytes")]
    public long SizeBytes { get; set; }

    [JsonPropertyName("retainedTypeCount")]
    public int RetainedTypeCount { get; set; }

    [JsonPropertyName("retainedMethodCount")]
    public int RetainedMethodCount { get; set; }

    [JsonPropertyName("ilcMilliseconds")]
    public int IlcMilliseconds { get; set; }

    [JsonPropertyName("publishSeconds")]
    public double PublishSeconds { get; set; }

    [JsonPropertyName("failure")]
    public string? Failure { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(ModuleBaseline))]
[JsonSerializable(typeof(List<ModuleMeasurementResult>))]
internal sealed partial class ModuleJsonContext : JsonSerializerContext;

internal sealed class ModuleMeasurement(
    string repoRoot,
    string projectPath,
    string modulePath,
    string mstatPath)
{
    /// <summary>
    /// Publishes the module restricted to <paramref name="analyzers"/> (empty
    /// meaning every analyzer) and measures the result.
    /// </summary>
    public ModuleMeasurementResult Measure(
        string moduleName,
        IReadOnlyCollection<string> analyzers)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string argument in new[]
        {
            "publish",
            projectPath,
            "-r", "linux-x64",
            "-c", "Release",
            "--nologo",
            $"-p:RoslynAotAnalyzers={string.Join(";", analyzers)}",
            "-p:IlcGenerateMstatFile=true",
            "-clp:PerformanceSummary",
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        var stopwatch = Stopwatch.StartNew();
        using Process process = Process.Start(startInfo) ??
            throw new HarnessEnvironmentException(
                $"Could not start 'dotnet publish' for module '{moduleName}'.");
        Task<string> stdOut = process.StandardOutput.ReadToEndAsync();
        Task<string> stdErr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        stopwatch.Stop();

        string output = stdOut.GetAwaiter().GetResult();
        var result = new ModuleMeasurementResult
        {
            Module = moduleName,
            PublishSeconds = Math.Round(stopwatch.Elapsed.TotalSeconds, 1),
            IlcMilliseconds = ParseIlcMilliseconds(output),
        };

        if (process.ExitCode != 0)
        {
            result.Failure =
                $"dotnet publish exited {process.ExitCode}: " +
                FirstError(output + stdErr.GetAwaiter().GetResult());
            return result;
        }

        result.SizeBytes = new FileInfo(modulePath).Length;
        ModuleMetricsReport metrics = MstatReader.Read(mstatPath);
        result.RetainedTypeCount = metrics.RetainedTypeCount;
        result.RetainedMethodCount = metrics.RetainedMethodCount;
        return result;
    }

    /// <summary>
    /// Pulls the IlcCompile target time out of an MSBuild performance summary.
    /// Informational only; never part of the baseline.
    /// </summary>
    private static int ParseIlcMilliseconds(string output)
    {
        foreach (string line in output.Split('\n'))
        {
            // Rows look like "  2505 ms  IlcCompile   1 calls". The target
            // name must match exactly: ComputeIlcCompileInputs and
            // _ComputeIlcCompileInputs both contain "IlcCompile", both sit
            // above it in the summary, and both report 0 ms.
            string[] parts = line.Trim().Split(
                ' ',
                StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 3 &&
                parts[1] == "ms" &&
                parts[2] == "IlcCompile" &&
                int.TryParse(parts[0], out int milliseconds))
            {
                return milliseconds;
            }
        }

        return 0;
    }

    private static string FirstError(string output)
    {
        foreach (string line in output.Split('\n'))
        {
            if (line.Contains(" error ", StringComparison.Ordinal))
            {
                return line.Trim();
            }
        }

        return "no error line found in output";
    }
}
