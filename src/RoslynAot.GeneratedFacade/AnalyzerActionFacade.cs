using System.Collections.Immutable;
using RoslynAot.Abi;
using RoslynAot.RoslynFacade;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Microsoft.CodeAnalysis.Diagnostics
{

public readonly partial struct SyntaxNodeAnalysisContext
{
    internal SyntaxNodeAnalysisContext(
        IRoslynControlVtbl controlVtbl,
        long handle,
        SyntaxNode node,
        Action<Diagnostic> reportDiagnostic)
    {
        this = default;
        __roslynAotControlVtbl = controlVtbl;
        __roslynAotVtbl =
            RoslynAot.RoslynFacade.RoslynVtblFactory
                .GetSyntaxNodeAnalysisContextVtbl(controlVtbl);
        __roslynAotHandle = handle;
        _dummy = new AnalyzerLocalContext(node, reportDiagnostic);
    }

    internal SyntaxNode __RoslynAotGetLocalNode() =>
        (_dummy as AnalyzerLocalContext)?.Node ??
        throw new InvalidOperationException(
            "This syntax-node analysis context is not analyzer-owned.");

    internal bool __RoslynAotTryReportLocal(Diagnostic diagnostic) =>
        AnalyzerContextHelpers.TryReport(
            (_dummy as AnalyzerLocalContext)?.ReportDiagnostic,
            diagnostic);

    private sealed record AnalyzerLocalContext(
        SyntaxNode Node,
        Action<Diagnostic> ReportDiagnostic);
}

public readonly partial struct OperationAnalysisContext
{
    internal OperationAnalysisContext(
        IRoslynControlVtbl controlVtbl,
        long handle,
        Action<Diagnostic> reportDiagnostic)
    {
        this = default;
        __roslynAotControlVtbl = controlVtbl;
        __roslynAotVtbl =
            RoslynAot.RoslynFacade.RoslynVtblFactory
                .GetOperationAnalysisContextVtbl(controlVtbl);
        __roslynAotHandle = handle;
        _dummy = reportDiagnostic;
    }

    internal bool __RoslynAotTryReportLocal(Diagnostic diagnostic) =>
        AnalyzerContextHelpers.TryReport(_dummy, diagnostic);
}

public readonly partial struct SymbolAnalysisContext
{
    internal SymbolAnalysisContext(
        IRoslynControlVtbl controlVtbl,
        long handle,
        Action<Diagnostic> reportDiagnostic)
    {
        this = default;
        __roslynAotControlVtbl = controlVtbl;
        __roslynAotVtbl =
            RoslynAot.RoslynFacade.RoslynVtblFactory
                .GetSymbolAnalysisContextVtbl(controlVtbl);
        __roslynAotHandle = handle;
        _dummy = reportDiagnostic;
    }

    internal bool __RoslynAotTryReportLocal(Diagnostic diagnostic) =>
        AnalyzerContextHelpers.TryReport(_dummy, diagnostic);
}

public readonly partial struct CompilationAnalysisContext
{
    internal CompilationAnalysisContext(
        IRoslynControlVtbl controlVtbl,
        long handle,
        Action<Diagnostic> reportDiagnostic)
    {
        this = default;
        __roslynAotControlVtbl = controlVtbl;
        __roslynAotVtbl =
            RoslynAot.RoslynFacade.RoslynVtblFactory
                .GetCompilationAnalysisContextVtbl(controlVtbl);
        __roslynAotHandle = handle;
        _dummy = reportDiagnostic;
    }

    internal bool __RoslynAotTryReportLocal(Diagnostic diagnostic) =>
        AnalyzerContextHelpers.TryReport(_dummy, diagnostic);
}

public readonly partial struct OperationBlockAnalysisContext
{
    internal OperationBlockAnalysisContext(
        IRoslynControlVtbl controlVtbl,
        long handle,
        Action<Diagnostic> reportDiagnostic)
    {
        this = default;
        __roslynAotControlVtbl = controlVtbl;
        __roslynAotVtbl =
            RoslynAot.RoslynFacade.RoslynVtblFactory
                .GetOperationBlockAnalysisContextVtbl(controlVtbl);
        __roslynAotHandle = handle;
        _dummy = reportDiagnostic;
    }

    internal bool __RoslynAotTryReportLocal(Diagnostic diagnostic) =>
        AnalyzerContextHelpers.TryReport(_dummy, diagnostic);
}

public readonly partial struct SyntaxTreeAnalysisContext
{
    internal SyntaxTreeAnalysisContext(
        IRoslynControlVtbl controlVtbl,
        long handle,
        Action<Diagnostic> reportDiagnostic)
    {
        this = default;
        __roslynAotControlVtbl = controlVtbl;
        __roslynAotVtbl =
            RoslynAot.RoslynFacade.RoslynVtblFactory
                .GetSyntaxTreeAnalysisContextVtbl(controlVtbl);
        __roslynAotHandle = handle;
        _dummy = reportDiagnostic;
    }

    internal bool __RoslynAotTryReportLocal(Diagnostic diagnostic) =>
        AnalyzerContextHelpers.TryReport(_dummy, diagnostic);
}

public abstract partial class CompilationStartAnalysisContext
{
    internal static CompilationStartAnalysisContext __RoslynAotCreateLocal(
        IRoslynControlVtbl controlVtbl,
        long handle,
        Action<AnalyzerActionKind, object, int[]> registerAction) =>
        new LocalContext(controlVtbl, handle, registerAction);

    private sealed class LocalContext(
        IRoslynControlVtbl controlVtbl,
        long handle,
        Action<AnalyzerActionKind, object, int[]> registerAction)
        : CompilationStartAnalysisContext(
            controlVtbl,
            RoslynAot.RoslynFacade.RoslynVtblFactory
                .GetCompilationStartAnalysisContextVtbl(controlVtbl),
            handle)
    {
        public override void RegisterCompilationEndAction(
            Action<CompilationAnalysisContext> action) =>
            registerAction(AnalyzerActionKind.Compilation, action, []);

        public override void RegisterOperationAction(
            Action<OperationAnalysisContext> action,
            params ImmutableArray<OperationKind> operationKinds) =>
            registerAction(
                AnalyzerActionKind.Operation,
                action,
                AnalyzerActionFacadeFactory.ToInt32(operationKinds));

        public override void RegisterOperationBlockAction(
            Action<OperationBlockAnalysisContext> action) =>
            registerAction(AnalyzerActionKind.OperationBlock, action, []);

        public override void RegisterOperationBlockStartAction(
            Action<OperationBlockStartAnalysisContext> action) =>
            registerAction(AnalyzerActionKind.OperationBlockStart, action, []);

        public override void RegisterSymbolAction(
            Action<SymbolAnalysisContext> action,
            params ImmutableArray<SymbolKind> symbolKinds) =>
            registerAction(
                AnalyzerActionKind.Symbol,
                action,
                AnalyzerActionFacadeFactory.ToInt32(symbolKinds));

        public override void RegisterSymbolStartAction(
            Action<SymbolStartAnalysisContext> action,
            SymbolKind symbolKind) =>
            registerAction(
                AnalyzerActionKind.SymbolStart,
                action,
                [(int)symbolKind]);

        public override void RegisterSyntaxNodeAction<TLanguageKindEnum>(
            Action<SyntaxNodeAnalysisContext> action,
            params ImmutableArray<TLanguageKindEnum> syntaxKinds) =>
            registerAction(
                AnalyzerActionKind.SyntaxNode,
                action,
                AnalyzerActionFacadeFactory.ToInt32(syntaxKinds));

        public override void RegisterSyntaxTreeAction(
            Action<SyntaxTreeAnalysisContext> action) =>
            registerAction(AnalyzerActionKind.SyntaxTree, action, []);

        public override void RegisterCodeBlockAction(
            Action<CodeBlockAnalysisContext> action) =>
            throw AnalyzerActionFacadeFactory.UnsupportedRegistration();

        public override void RegisterCodeBlockStartAction<TLanguageKindEnum>(
            Action<CodeBlockStartAnalysisContext<TLanguageKindEnum>> action) =>
            throw AnalyzerActionFacadeFactory.UnsupportedRegistration();

        public override void RegisterSemanticModelAction(
            Action<SemanticModelAnalysisContext> action) =>
            throw AnalyzerActionFacadeFactory.UnsupportedRegistration();
    }
}

public abstract partial class OperationBlockStartAnalysisContext
{
    internal static OperationBlockStartAnalysisContext __RoslynAotCreateLocal(
        IRoslynControlVtbl controlVtbl,
        long handle,
        Action<AnalyzerActionKind, object, int[]> registerAction) =>
        new LocalContext(controlVtbl, handle, registerAction);

    private sealed class LocalContext(
        IRoslynControlVtbl controlVtbl,
        long handle,
        Action<AnalyzerActionKind, object, int[]> registerAction)
        : OperationBlockStartAnalysisContext(
            controlVtbl,
            RoslynAot.RoslynFacade.RoslynVtblFactory
                .GetOperationBlockStartAnalysisContextVtbl(controlVtbl),
            handle)
    {
        public override void RegisterOperationAction(
            Action<OperationAnalysisContext> action,
            params ImmutableArray<OperationKind> operationKinds) =>
            registerAction(
                AnalyzerActionKind.Operation,
                action,
                AnalyzerActionFacadeFactory.ToInt32(operationKinds));

        public override void RegisterOperationBlockEndAction(
            Action<OperationBlockAnalysisContext> action) =>
            registerAction(AnalyzerActionKind.OperationBlock, action, []);
    }
}

public abstract partial class SymbolStartAnalysisContext
{
    internal static SymbolStartAnalysisContext __RoslynAotCreateLocal(
        IRoslynControlVtbl controlVtbl,
        long handle,
        Action<AnalyzerActionKind, object, int[]> registerAction) =>
        new LocalContext(controlVtbl, handle, registerAction);

    private sealed class LocalContext(
        IRoslynControlVtbl controlVtbl,
        long handle,
        Action<AnalyzerActionKind, object, int[]> registerAction)
        : SymbolStartAnalysisContext(
            controlVtbl,
            RoslynAot.RoslynFacade.RoslynVtblFactory
                .GetSymbolStartAnalysisContextVtbl(controlVtbl),
            handle)
    {
        public override void RegisterOperationAction(
            Action<OperationAnalysisContext> action,
            params ImmutableArray<OperationKind> operationKinds) =>
            registerAction(
                AnalyzerActionKind.Operation,
                action,
                AnalyzerActionFacadeFactory.ToInt32(operationKinds));

        public override void RegisterOperationBlockAction(
            Action<OperationBlockAnalysisContext> action) =>
            registerAction(AnalyzerActionKind.OperationBlock, action, []);

        public override void RegisterOperationBlockStartAction(
            Action<OperationBlockStartAnalysisContext> action) =>
            registerAction(AnalyzerActionKind.OperationBlockStart, action, []);

        public override void RegisterSymbolEndAction(
            Action<SymbolAnalysisContext> action) =>
            registerAction(AnalyzerActionKind.Symbol, action, []);

        public override void RegisterSyntaxNodeAction<TLanguageKindEnum>(
            Action<SyntaxNodeAnalysisContext> action,
            params ImmutableArray<TLanguageKindEnum> syntaxKinds) =>
            registerAction(
                AnalyzerActionKind.SyntaxNode,
                action,
                AnalyzerActionFacadeFactory.ToInt32(syntaxKinds));

        public override void RegisterCodeBlockAction(
            Action<CodeBlockAnalysisContext> action) =>
            throw AnalyzerActionFacadeFactory.UnsupportedRegistration();

        public override void RegisterCodeBlockStartAction<TLanguageKindEnum>(
            Action<CodeBlockStartAnalysisContext<TLanguageKindEnum>> action) =>
            throw AnalyzerActionFacadeFactory.UnsupportedRegistration();
    }
}

internal static class AnalyzerContextHelpers
{
    public static bool TryReport(object? state, Diagnostic diagnostic)
    {
        if (state is not Action<Diagnostic> reportDiagnostic)
        {
            return false;
        }

        reportDiagnostic(diagnostic);
        return true;
    }
}

}

namespace RoslynAot.RoslynFacade
{

public static class AnalyzerActionFacadeFactory
{
    public static AnalysisContext CreateAnalysisContext(
        Action<AnalyzerActionKind, object, int[]> registerAction) =>
        new LocalAnalysisContext(registerAction);

    public static object CreateActionContext(
        AnalyzerActionKind actionKind,
        IRoslynControlVtbl controlVtbl,
        long handle,
        Action<AnalyzerActionKind, object, int[]> registerAction,
        Action<Diagnostic> reportDiagnostic)
    {
        return actionKind switch
        {
            AnalyzerActionKind.CompilationStart =>
                CompilationStartAnalysisContext.__RoslynAotCreateLocal(
                    controlVtbl,
                    handle,
                    registerAction),
            AnalyzerActionKind.Compilation =>
                new CompilationAnalysisContext(
                    controlVtbl,
                    handle,
                    reportDiagnostic),
            AnalyzerActionKind.SyntaxNode =>
                CreateSyntaxNodeContext(
                    controlVtbl,
                    handle,
                    reportDiagnostic),
            AnalyzerActionKind.Operation =>
                new OperationAnalysisContext(
                    controlVtbl,
                    handle,
                    reportDiagnostic),
            AnalyzerActionKind.Symbol =>
                new SymbolAnalysisContext(
                    controlVtbl,
                    handle,
                    reportDiagnostic),
            AnalyzerActionKind.OperationBlock =>
                new OperationBlockAnalysisContext(
                    controlVtbl,
                    handle,
                    reportDiagnostic),
            AnalyzerActionKind.OperationBlockStart =>
                OperationBlockStartAnalysisContext.__RoslynAotCreateLocal(
                    controlVtbl,
                    handle,
                    registerAction),
            AnalyzerActionKind.SymbolStart =>
                SymbolStartAnalysisContext.__RoslynAotCreateLocal(
                    controlVtbl,
                    handle,
                    registerAction),
            AnalyzerActionKind.SyntaxTree =>
                new SyntaxTreeAnalysisContext(
                    controlVtbl,
                    handle,
                    reportDiagnostic),
            _ => throw new ArgumentOutOfRangeException(nameof(actionKind)),
        };
    }

    internal static int[] ToInt32<T>(ImmutableArray<T> values)
    {
        int[] result = new int[values.Length];
        for (int index = 0; index < values.Length; index++)
        {
            result[index] = Convert.ToInt32(values[index]);
        }

        return result;
    }

    internal static PlatformNotSupportedException UnsupportedRegistration() =>
        new("This analyzer registration kind is not implemented by RoslynAot.");

    private static SyntaxNodeAnalysisContext CreateSyntaxNodeContext(
        IRoslynControlVtbl controlVtbl,
        long handle,
        Action<Diagnostic> reportDiagnostic)
    {
        ISyntaxNodeAnalysisContextVtbl vtbl =
            RoslynVtblFactory.GetSyntaxNodeAnalysisContextVtbl(controlVtbl);
        int status = vtbl.SyntaxNodeAnalysisContext_get_Node(
            handle,
            out long nodeHandle);
        RoslynFacadeRuntime.ThrowIfFailed(controlVtbl, status);
        return new SyntaxNodeAnalysisContext(
            controlVtbl,
            handle,
            SyntaxNode.__RoslynAotCreateProxy(controlVtbl, nodeHandle),
            reportDiagnostic);
    }

    private sealed class LocalAnalysisContext(
        Action<AnalyzerActionKind, object, int[]> registerAction)
        : AnalysisContext
    {
        public override DiagnosticSeverity MinimumReportedSeverity =>
            DiagnosticSeverity.Hidden;

        public override void ConfigureGeneratedCodeAnalysis(
            GeneratedCodeAnalysisFlags analysisMode)
        {
        }

        public override void EnableConcurrentExecution()
        {
        }

        public override void RegisterCompilationAction(
            Action<CompilationAnalysisContext> action) =>
            registerAction(AnalyzerActionKind.Compilation, action, []);

        public override void RegisterCompilationStartAction(
            Action<CompilationStartAnalysisContext> action) =>
            registerAction(AnalyzerActionKind.CompilationStart, action, []);

        public override void RegisterOperationAction(
            Action<OperationAnalysisContext> action,
            params ImmutableArray<OperationKind> operationKinds) =>
            registerAction(
                AnalyzerActionKind.Operation,
                action,
                ToInt32(operationKinds));

        public override void RegisterOperationBlockAction(
            Action<OperationBlockAnalysisContext> action) =>
            registerAction(AnalyzerActionKind.OperationBlock, action, []);

        public override void RegisterOperationBlockStartAction(
            Action<OperationBlockStartAnalysisContext> action) =>
            registerAction(AnalyzerActionKind.OperationBlockStart, action, []);

        public override void RegisterSymbolAction(
            Action<SymbolAnalysisContext> action,
            params ImmutableArray<SymbolKind> symbolKinds) =>
            registerAction(
                AnalyzerActionKind.Symbol,
                action,
                ToInt32(symbolKinds));

        public override void RegisterSymbolStartAction(
            Action<SymbolStartAnalysisContext> action,
            SymbolKind symbolKind) =>
            registerAction(
                AnalyzerActionKind.SymbolStart,
                action,
                [(int)symbolKind]);

        public override void RegisterSyntaxNodeAction<TLanguageKindEnum>(
            Action<SyntaxNodeAnalysisContext> action,
            params ImmutableArray<TLanguageKindEnum> syntaxKinds) =>
            registerAction(
                AnalyzerActionKind.SyntaxNode,
                action,
                ToInt32(syntaxKinds));

        public override void RegisterSyntaxTreeAction(
            Action<SyntaxTreeAnalysisContext> action) =>
            registerAction(AnalyzerActionKind.SyntaxTree, action, []);

        public override void RegisterCodeBlockAction(
            Action<CodeBlockAnalysisContext> action) =>
            throw UnsupportedRegistration();

        public override void RegisterCodeBlockStartAction<TLanguageKindEnum>(
            Action<CodeBlockStartAnalysisContext<TLanguageKindEnum>> action) =>
            throw UnsupportedRegistration();

        public override void RegisterSemanticModelAction(
            Action<SemanticModelAnalysisContext> action) =>
            throw UnsupportedRegistration();
    }
}

}
