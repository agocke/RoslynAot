using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RoslynAot.SampleAnalyzer;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ThrowingAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor Rule = new(
        "AA0002",
        "Deliberate failure",
        "Deliberate failure",
        "Diagnostics",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(
            GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(
            AnalyzeClassDeclaration,
            SyntaxKind.ClassDeclaration);
    }

    private static void AnalyzeClassDeclaration(
        SyntaxNodeAnalysisContext context)
    {
        if (context.Node.ToString().IndexOf(
                "class Throwing",
                StringComparison.Ordinal) >= 0)
        {
            throw new InvalidOperationException(
                "ThrowingAnalyzer deliberately failed for AD0001 legibility verification.");
        }
    }
}
