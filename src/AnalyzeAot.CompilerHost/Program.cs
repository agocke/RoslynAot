using Microsoft.CodeAnalysis;

namespace AnalyzeAot.CompilerHost;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args is ["--validate-roslyn-projection"])
        {
            RoslynProjectionValidation.Run();
            return 0;
        }

        if (args is ["--validate-roslyn-projection", string clientPath])
        {
            RoslynProjectionValidation.Run(clientPath);
            return 0;
        }

        string baseDirectory = AppContext.BaseDirectory;
        var buildPaths = new BuildPaths(
            clientDir: baseDirectory,
            workingDir: Environment.CurrentDirectory,
            sdkDir: baseDirectory,
            tempDir: Path.GetTempPath());

        return AnalyzeAotCSharpCompiler.Run(args, buildPaths, Console.Out);
    }
}
