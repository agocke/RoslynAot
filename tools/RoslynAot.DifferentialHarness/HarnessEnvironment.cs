using System.Diagnostics;

namespace RoslynAot.DifferentialHarness;

/// <summary>
/// Resolved paths for both toolchains. Every path is validated to exist
/// before the harness does anything else, and every one is individually
/// overridable from the command line.
/// </summary>
internal sealed record HarnessEnvironment(
    string RepoRoot,
    string SdkDirectory,
    string ReferenceDirectory,
    string AnalyzerDirectory,
    string RoslynBincoreDirectory,
    string NativeCompilerPath,
    string NativeModulePath)
{
    public string ManagedCompilerPath =>
        Path.Combine(RoslynBincoreDirectory, "csc.dll");

    public string LanguageAgnosticAnalyzerPath =>
        Path.Combine(AnalyzerDirectory, "Microsoft.CodeAnalysis.NetAnalyzers.dll");

    public string CSharpAnalyzerPath =>
        Path.Combine(
            AnalyzerDirectory,
            "Microsoft.CodeAnalysis.CSharp.NetAnalyzers.dll");

    public static HarnessEnvironment Resolve(HarnessOptions options)
    {
        string repoRoot = options.RepoRoot ?? FindRepoRoot();

        string sdkDirectory = options.SdkDirectory ?? ResolveNewestSdkDirectory();

        // SDK installs are laid out as <dotnetRoot>/sdk/<version>; walk up
        // two levels to find the dotnet root the "packs" directory lives
        // under, regardless of whether SdkDirectory came from discovery or
        // an explicit override.
        string dotnetRoot = Directory.GetParent(sdkDirectory)?.Parent?.FullName ??
            throw new HarnessEnvironmentException(
                $"Could not derive the dotnet root from SDK directory " +
                $"'{sdkDirectory}'.");

        string referenceDirectory = options.ReferenceDirectory ??
            ResolveNewestReferencePack(dotnetRoot);
        string analyzerDirectory = options.AnalyzerDirectory ??
            Path.Combine(sdkDirectory, "Sdks", "Microsoft.NET.Sdk", "analyzers");
        string roslynBincoreDirectory = options.RoslynBincoreDirectory ??
            Path.Combine(sdkDirectory, "Roslyn", "bincore");
        string nativeCompilerPath = options.NativeCompilerPath ??
            Path.Combine(
                repoRoot,
                "artifacts", "publish", "CscAot", "release_linux-x64",
                "csc-aot");
        string nativeModulePath = options.NativeModulePath ??
            Path.Combine(
                repoRoot,
                "artifacts", "publish", "RoslynAot.CSharpNetAnalyzers.Native",
                "release_linux-x64", "libroslyn-aot-csharp-net-analyzers.so");

        var environment = new HarnessEnvironment(
            repoRoot,
            sdkDirectory,
            referenceDirectory,
            analyzerDirectory,
            roslynBincoreDirectory,
            nativeCompilerPath,
            nativeModulePath);
        environment.Validate();
        return environment;
    }

    private void Validate()
    {
        RequireDirectory(RepoRoot, "repository root");
        RequireFile(
            Path.Combine(RepoRoot, "RoslynAot.slnx"),
            "repository root (RoslynAot.slnx not found)");
        RequireDirectory(SdkDirectory, "SDK directory");
        RequireDirectory(ReferenceDirectory, "net11.0 reference assembly directory");
        RequireFile(
            Path.Combine(ReferenceDirectory, "System.Runtime.dll"),
            "System.Runtime.dll in the reference assembly directory");
        RequireDirectory(AnalyzerDirectory, "SDK analyzer directory");
        RequireFile(CSharpAnalyzerPath, "Microsoft.CodeAnalysis.CSharp.NetAnalyzers.dll");
        RequireFile(LanguageAgnosticAnalyzerPath, "Microsoft.CodeAnalysis.NetAnalyzers.dll");
        RequireDirectory(RoslynBincoreDirectory, "Roslyn bincore directory");
        RequireFile(ManagedCompilerPath, "managed csc.dll");
    }

    // NativeCompilerPath and NativeModulePath are not validated here:
    // they are produced by ToolchainPublisher and may not exist yet
    // when the environment is first resolved.

    private static void RequireDirectory(string path, string description)
    {
        if (!Directory.Exists(path))
        {
            throw new HarnessEnvironmentException(
                $"Could not find the {description}: '{path}'.");
        }
    }

    private static void RequireFile(string path, string description)
    {
        if (!File.Exists(path))
        {
            throw new HarnessEnvironmentException(
                $"Could not find the {description}: '{path}'.");
        }
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "RoslynAot.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new HarnessEnvironmentException(
            "Could not locate the repository root (no RoslynAot.slnx found " +
            $"walking up from '{AppContext.BaseDirectory}'). Pass " +
            "--repo-root explicitly.");
    }

    private static string ResolveNewestSdkDirectory()
    {
        string output = RunCaptured("dotnet", "--list-sdks");
        (DottedVersion Version, string BasePath)? best = null;
        foreach (string line in output.Split(
            '\n',
            StringSplitOptions.RemoveEmptyEntries))
        {
            int bracket = line.IndexOf('[');
            if (bracket < 0)
            {
                continue;
            }

            string versionText = line[..bracket].Trim();
            string basePath = line[(bracket + 1)..].TrimEnd().TrimEnd(']');
            var version = new DottedVersion(versionText);
            if (best is null || version.CompareTo(best.Value.Version) > 0)
            {
                best = (version, basePath);
            }
        }

        if (best is null)
        {
            throw new HarnessEnvironmentException(
                "'dotnet --list-sdks' reported no installed SDKs.");
        }

        return Path.Combine(best.Value.BasePath, best.Value.Version.Text);
    }

    private static string ResolveNewestReferencePack(string dotnetRoot)
    {
        string packsRoot = Path.Combine(
            dotnetRoot, "packs", "Microsoft.NETCore.App.Ref");
        if (!Directory.Exists(packsRoot))
        {
            throw new HarnessEnvironmentException(
                $"Could not find the Microsoft.NETCore.App.Ref pack " +
                $"directory: '{packsRoot}'.");
        }

        (DottedVersion Version, string Path)? best = null;
        foreach (string packVersionDirectory in Directory.EnumerateDirectories(
            packsRoot))
        {
            string candidate = Path.Combine(
                packVersionDirectory, "ref", "net11.0");
            if (!Directory.Exists(candidate))
            {
                continue;
            }

            var version = new DottedVersion(
                Path.GetFileName(packVersionDirectory));
            if (best is null || version.CompareTo(best.Value.Version) > 0)
            {
                best = (version, candidate);
            }
        }

        return best?.Path ??
            throw new HarnessEnvironmentException(
                $"No net11.0 reference assembly directory found under " +
                $"'{packsRoot}'.");
    }

    private static string RunCaptured(string fileName, string arguments)
    {
        var startInfo = new ProcessStartInfo(fileName, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using Process process = Process.Start(startInfo) ??
            throw new HarnessEnvironmentException(
                $"Could not start '{fileName} {arguments}'.");

        // Both pipes must be drained concurrently; see ToolchainPublisher.
        Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
        Task<string> errorTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        string output = outputTask.GetAwaiter().GetResult();
        if (process.ExitCode != 0)
        {
            throw new HarnessEnvironmentException(
                $"'{fileName} {arguments}' exited with {process.ExitCode}: " +
                errorTask.GetAwaiter().GetResult());
        }

        return output;
    }
}

internal sealed class HarnessOptions
{
    public string? RepoRoot { get; set; }

    public string? SdkDirectory { get; set; }

    public string? ReferenceDirectory { get; set; }

    public string? AnalyzerDirectory { get; set; }

    public string? RoslynBincoreDirectory { get; set; }

    public string? NativeCompilerPath { get; set; }

    public string? NativeModulePath { get; set; }

    public bool NoPublish { get; set; }

    public bool NoLedger { get; set; }

    public bool UpdateBaseline { get; set; }

    /// <summary>
    /// 'modules': measure every analyzer rather than the representatives the
    /// baseline keeps. An audit, not a baseline run.
    /// </summary>
    public bool AllModules { get; set; }

    public string? Filter { get; set; }

    public int TimeoutSeconds { get; set; } = 120;

    public string? OutputDirectory { get; set; }
}
