using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace AnalyzeAot.CompilerHost;

internal sealed class AnalyzeAotCSharpCompiler : CSharpCompiler
{
    private AnalyzeAotCSharpCompiler(
        string responseFile,
        BuildPaths buildPaths,
        string[] args)
        : base(
            CSharpCommandLineParser.Default,
            responseFile,
            args,
            buildPaths,
            Environment.GetEnvironmentVariable("LIB"),
            RejectingAnalyzerAssemblyLoader.Instance)
    {
    }

    internal static int Run(
        string[] args,
        BuildPaths buildPaths,
        TextWriter textWriter)
    {
        string responseFile = Path.Combine(
            buildPaths.ClientDirectory,
            ResponseFileName);
        var compiler = new AnalyzeAotCSharpCompiler(
            responseFile,
            buildPaths,
            args);

        return compiler.Run(textWriter);
    }

    protected override void ResolveAnalyzersFromArguments(
        List<DiagnosticInfo> diagnostics,
        CommonMessageProvider messageProvider,
        CompilationOptions compilationOptions,
        bool skipAnalyzers,
        out ImmutableArray<DiagnosticAnalyzer> analyzers,
        out ImmutableArray<ISourceGenerator> generators)
    {
        generators = [];
        if (skipAnalyzers)
        {
            analyzers = [];
            return;
        }

        var builder = ImmutableArray.CreateBuilder<DiagnosticAnalyzer>();
        var loadedPaths = new HashSet<string>(StringComparer.Ordinal);

        foreach (CommandLineAnalyzerReference reference in
                 Arguments.AnalyzerReferences)
        {
            string path = Path.GetFullPath(
                reference.FilePath,
                Arguments.BaseDirectory ?? Environment.CurrentDirectory);
            if (!loadedPaths.Add(path))
            {
                continue;
            }

            try
            {
                builder.AddRange(NativeDiagnosticAnalyzer.Load(path));
            }
            catch (Exception exception)
            {
                DiagnosticInfo? diagnostic = new(
                    messageProvider,
                    messageProvider.WRN_UnableToLoadAnalyzer,
                    path,
                    exception.ToString());
                diagnostic = messageProvider.FilterDiagnosticInfo(
                    diagnostic,
                    compilationOptions);
                if (diagnostic is not null)
                {
                    diagnostics.Add(diagnostic);
                }
            }
        }

        analyzers = builder.ToImmutable();
    }

    private sealed class RejectingAnalyzerAssemblyLoader :
        IAnalyzerAssemblyLoader
    {
        internal static readonly RejectingAnalyzerAssemblyLoader Instance =
            new();

        public void AddDependencyLocation(string fullPath)
        {
        }

        public System.Reflection.Assembly LoadFromPath(string fullPath) =>
            throw new PlatformNotSupportedException(
                $"Managed analyzer loading is unavailable: '{fullPath}'.");
    }
}
