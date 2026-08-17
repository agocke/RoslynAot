using System.Diagnostics;

namespace RoslynAot.DifferentialHarness;

internal enum CompilationSide
{
    Managed,
    Native,
}

internal sealed record CompilationResult(
    CompilationSide Side,
    string CaseName,
    int? ExitCode,
    bool TimedOut,
    bool Crashed,
    string SarifPath,
    string StdOutPath,
    string StdErrPath,
    string OutputAssemblyPath)
{
    public bool SarifProduced => File.Exists(SarifPath);
}

internal sealed class CompilationRunner(
    HarnessEnvironment environment,
    string generatedGlobalConfigPath,
    int timeoutSeconds)
{
    public CompilationResult Run(
        CorpusCase corpusCase,
        CompilationSide side,
        string outputRoot)
    {
        string outputDirectory = Path.Combine(
            outputRoot,
            corpusCase.Name.Replace('/', Path.DirectorySeparatorChar),
            side == CompilationSide.Managed ? "managed" : "native");
        Directory.CreateDirectory(outputDirectory);

        string assemblyName = SanitizeFileName(corpusCase.Name);
        string outputAssemblyPath =
            Path.Combine(outputDirectory, $"{assemblyName}.dll");
        string outputDocPath =
            Path.Combine(outputDirectory, $"{assemblyName}.xml");
        string sarifPath = Path.Combine(outputDirectory, "diagnostics.sarif");
        string stdOutPath = Path.Combine(outputDirectory, "stdout.log");
        string stdErrPath = Path.Combine(outputDirectory, "stderr.log");

        var arguments = new List<string>
        {
            "/nologo",
            "/nostdlib+",
            "/target:library",
            "/deterministic+",
            "/warnaserror-",
            "/preferreduilang:en-US",
            $"/pathmap:{environment.RepoRoot}=/_/",
            $"/analyzerconfig:{generatedGlobalConfigPath}",
            $"/errorlog:{sarifPath},version=2",
            $"/out:{outputAssemblyPath}",
            // Some rules (e.g. CA1200) only analyze doc comments when
            // documentation generation is enabled.
            $"/doc:{outputDocPath}",
        };
        foreach (string reference in Directory.EnumerateFiles(
            environment.ReferenceDirectory, "*.dll"))
        {
            arguments.Add($"/reference:{reference}");
        }

        if (side == CompilationSide.Managed)
        {
            arguments.Add($"/analyzer:{environment.LanguageAgnosticAnalyzerPath}");
            arguments.Add($"/analyzer:{environment.CSharpAnalyzerPath}");
        }
        else
        {
            arguments.Add($"/analyzer:{environment.NativeModulePath}");
        }

        arguments.AddRange(corpusCase.ExtraCompilerArguments);
        arguments.AddRange(corpusCase.SourceFiles);

        string fileName;
        var processArguments = new List<string>();
        if (side == CompilationSide.Managed)
        {
            fileName = "dotnet";
            processArguments.Add("exec");
            processArguments.Add(environment.ManagedCompilerPath);
            processArguments.AddRange(arguments);
        }
        else
        {
            fileName = environment.NativeCompilerPath;
            processArguments.AddRange(arguments);
        }

        var startInfo = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = environment.RepoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string argument in processArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en-US";
        startInfo.Environment["DOTNET_NOLOGO"] = "1";

        using Process process = Process.Start(startInfo) ??
            throw new HarnessEnvironmentException(
                $"Could not start '{fileName}' for case '{corpusCase.Name}'.");
        Task<string> stdOutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stdErrTask = process.StandardError.ReadToEndAsync();

        bool timedOut = !process.WaitForExit(timeoutSeconds * 1000);
        int? exitCode = null;
        if (timedOut)
        {
            TryKill(process);
        }
        else
        {
            // Ensure the async stream readers have observed EOF.
            process.WaitForExit();
            exitCode = process.ExitCode;
        }

        File.WriteAllText(stdOutPath, stdOutTask.GetAwaiter().GetResult());
        File.WriteAllText(stdErrPath, stdErrTask.GetAwaiter().GetResult());

        bool crashed = !timedOut && exitCode is not (0 or 1);

        return new CompilationResult(
            side,
            corpusCase.Name,
            exitCode,
            timedOut,
            crashed,
            sarifPath,
            stdOutPath,
            stdErrPath,
            outputAssemblyPath);
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already exited between the timeout check and the kill.
        }
    }

    private static string SanitizeFileName(string caseName) =>
        caseName.Replace('/', '_');
}
