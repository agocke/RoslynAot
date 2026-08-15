using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RoslynAot.SampleAnalyzer;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class BadClassNameAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor Rule = new(
        "AA0001",
        "Avoid classes named Bad",
        "Classes named 'Bad' are not allowed",
        "Naming",
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
                "class Bad",
                StringComparison.Ordinal) >= 0)
        {
            context.ReportDiagnostic(
                Diagnostic.Create(Rule, context.Node.GetLocation()));
        }
    }
}
