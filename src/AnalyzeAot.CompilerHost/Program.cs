using System.Collections.Immutable;
using AnalyzeAot.CompilerHost;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Emit;

if (!CommandLine.TryParse(
        args,
        out CommandLine? commandLine,
        out string? error))
{
    Console.Error.WriteLine(error);
    Console.Error.WriteLine(
        "Usage: analyze-aot --source <file.cs> --output <assembly.dll> " +
        "[--reference <assembly.dll>]... [--analyzer <analyzer.so>]...");
    return 2;
}

CommandLine options = commandLine!;
string source = File.ReadAllText(options.Source);
SyntaxTree syntaxTree = CSharpSyntaxTree.ParseText(
    source,
    path: options.Source);
IEnumerable<MetadataReference> references =
    options.References.Select(
        path => MetadataReference.CreateFromFile(path));
CSharpCompilation compilation = CSharpCompilation.Create(
    Path.GetFileNameWithoutExtension(options.Output),
    [syntaxTree],
    references,
    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

ImmutableArray<DiagnosticAnalyzer> analyzers = options.Analyzers
    .Select(NativeDiagnosticAnalyzer.Load)
    .Cast<DiagnosticAnalyzer>()
    .ToImmutableArray();
ImmutableArray<Diagnostic> analyzerDiagnostics = analyzers.IsEmpty
    ? []
    : compilation
        .WithAnalyzers(analyzers)
        .GetAnalyzerDiagnosticsAsync()
        .GetAwaiter()
        .GetResult();

foreach (Diagnostic diagnostic in analyzerDiagnostics)
{
    Console.Error.WriteLine(diagnostic);
}

Directory.CreateDirectory(
    Path.GetDirectoryName(Path.GetFullPath(options.Output))!);
EmitResult emitResult = compilation.Emit(options.Output);
foreach (Diagnostic diagnostic in emitResult.Diagnostics)
{
    Console.Error.WriteLine(diagnostic);
}

bool hasAnalyzerErrors = analyzerDiagnostics.Any(
    diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
return emitResult.Success && !hasAnalyzerErrors ? 0 : 1;

namespace AnalyzeAot.CompilerHost
{
    internal sealed class CommandLine
    {
        private CommandLine(
            string source,
            string output,
            List<string> references,
            List<string> analyzers)
        {
            Source = source;
            Output = output;
            References = references;
            Analyzers = analyzers;
        }

        public string Source { get; }

        public string Output { get; }

        public List<string> References { get; }

        public List<string> Analyzers { get; }

        public static bool TryParse(
            string[] args,
            out CommandLine? commandLine,
            out string? error)
        {
            string? source = null;
            string? output = null;
            var references = new List<string>();
            var analyzers = new List<string>();

            for (int index = 0; index < args.Length; index++)
            {
                string option = args[index];
                if (index + 1 >= args.Length)
                {
                    commandLine = null;
                    error = $"Missing value for '{option}'.";
                    return false;
                }

                string value = args[++index];
                switch (option)
                {
                    case "--source":
                        source = value;
                        break;
                    case "--output":
                        output = value;
                        break;
                    case "--reference":
                        references.Add(value);
                        break;
                    case "--analyzer":
                        analyzers.Add(value);
                        break;
                    default:
                        commandLine = null;
                        error = $"Unknown option '{option}'.";
                        return false;
                }
            }

            if (source is null || output is null)
            {
                commandLine = null;
                error = "Both --source and --output are required.";
                return false;
            }

            commandLine = new CommandLine(
                source,
                output,
                references,
                analyzers);
            error = null;
            return true;
        }
    }
}
