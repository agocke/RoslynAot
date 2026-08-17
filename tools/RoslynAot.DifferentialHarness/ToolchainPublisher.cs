using System.Diagnostics;

namespace RoslynAot.DifferentialHarness;

/// <summary>
/// Publishes the native compiler and the native analyzer module under
/// test, mirroring the `dotnet publish -r linux-x64 -c Release` calls in
/// the existing eng/validate-*.sh scripts.
/// </summary>
internal static class ToolchainPublisher
{
    public static void PublishAll(HarnessEnvironment environment, TextWriter log)
    {
        Publish(
            environment.RepoRoot,
            Path.Combine("src", "CscAot", "CscAot.csproj"),
            log);
        Publish(
            environment.RepoRoot,
            Path.Combine(
                "samples", "RoslynAot.CSharpNetAnalyzers.Native",
                "RoslynAot.CSharpNetAnalyzers.Native.csproj"),
            log);
    }

    private static void Publish(
        string repoRoot,
        string relativeProjectPath,
        TextWriter log)
    {
        string projectPath = Path.Combine(repoRoot, relativeProjectPath);
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("publish");
        startInfo.ArgumentList.Add(projectPath);
        startInfo.ArgumentList.Add("-r");
        startInfo.ArgumentList.Add("linux-x64");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("Release");
        startInfo.ArgumentList.Add("--nologo");

        log.WriteLine($"Publishing {relativeProjectPath} ...");
        using Process process = Process.Start(startInfo) ??
            throw new HarnessEnvironmentException(
                $"Could not start 'dotnet publish {relativeProjectPath}'.");

        // Drain both pipes concurrently. Reading one to completion before
        // the other deadlocks as soon as the child fills the unread pipe's
        // OS buffer - which a NativeAOT publish's warning stream can do.
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        string stdout = stdoutTask.GetAwaiter().GetResult();
        string stderr = stderrTask.GetAwaiter().GetResult();
        if (process.ExitCode != 0)
        {
            throw new HarnessEnvironmentException(
                $"'dotnet publish {relativeProjectPath}' failed with exit " +
                $"code {process.ExitCode}.{Environment.NewLine}{stdout}" +
                $"{Environment.NewLine}{stderr}");
        }
    }
}
