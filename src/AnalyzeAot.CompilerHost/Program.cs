using Microsoft.CodeAnalysis;

namespace AnalyzeAot.CompilerHost;

internal static class Program
{
    private static int Main(string[] args)
    {
        string baseDirectory = AppContext.BaseDirectory;
        var buildPaths = new BuildPaths(
            clientDir: baseDirectory,
            workingDir: Environment.CurrentDirectory,
            sdkDir: baseDirectory,
            tempDir: Path.GetTempPath());

        return AnalyzeAotCSharpCompiler.Run(args, buildPaths, Console.Out);
    }
}
