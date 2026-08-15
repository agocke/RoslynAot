using System.Collections.Immutable;
using AnalyzeAot.Abi;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace Microsoft.CodeAnalysis
{

public sealed partial class DiagnosticDescriptor
{
    private bool __analyzeAotIsLocal;
    private string __analyzeAotLocalId = string.Empty;
    private string __analyzeAotLocalTitle = string.Empty;
    private string __analyzeAotLocalMessageFormat = string.Empty;
    private string __analyzeAotLocalCategory = string.Empty;
    private DiagnosticSeverity __analyzeAotLocalDefaultSeverity;
    private bool __analyzeAotLocalIsEnabledByDefault;

    internal bool __AnalyzeAotIsLocal => __analyzeAotIsLocal;
    internal string __AnalyzeAotLocalId => __analyzeAotLocalId;
    internal string __AnalyzeAotLocalCategory => __analyzeAotLocalCategory;
    internal DiagnosticSeverity __AnalyzeAotLocalDefaultSeverity =>
        __analyzeAotLocalDefaultSeverity;
    internal bool __AnalyzeAotLocalIsEnabledByDefault =>
        __analyzeAotLocalIsEnabledByDefault;

    internal void __AnalyzeAotInitializeLocal(
        string id,
        string title,
        string messageFormat,
        string category,
        DiagnosticSeverity defaultSeverity,
        bool isEnabledByDefault,
        string? description,
        string? helpLinkUri,
        string[] customTags)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(messageFormat);
        ArgumentNullException.ThrowIfNull(category);
        ArgumentNullException.ThrowIfNull(customTags);

        __analyzeAotIsLocal = true;
        __analyzeAotLocalId = id;
        __analyzeAotLocalTitle = title;
        __analyzeAotLocalMessageFormat = messageFormat;
        __analyzeAotLocalCategory = category;
        __analyzeAotLocalDefaultSeverity = defaultSeverity;
        __analyzeAotLocalIsEnabledByDefault = isEnabledByDefault;
    }

    internal string __AnalyzeAotGetLocalString(
        AnalyzerDescriptorField field)
    {
        if (!__analyzeAotIsLocal)
        {
            throw new InvalidOperationException(
                "The diagnostic descriptor is not analyzer-owned.");
        }

        return field switch
        {
            AnalyzerDescriptorField.Id => __analyzeAotLocalId,
            AnalyzerDescriptorField.Title => __analyzeAotLocalTitle,
            AnalyzerDescriptorField.MessageFormat =>
                __analyzeAotLocalMessageFormat,
            AnalyzerDescriptorField.Category => __analyzeAotLocalCategory,
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };
    }
}

public abstract partial class Diagnostic
{
    internal static Diagnostic __AnalyzeAotCreateLocal(
        DiagnosticDescriptor descriptor,
        Location? location,
        object?[]? messageArgs) =>
        new AnalyzerLocalDiagnostic(
            descriptor,
            location ?? Location.__AnalyzeAotCreateNone(),
            messageArgs ?? []);

    private sealed class AnalyzerLocalDiagnostic(
        DiagnosticDescriptor descriptor,
        Location location,
        object?[] messageArgs) : Diagnostic
    {
        public override IReadOnlyList<Location> AdditionalLocations =>
            Array.Empty<Location>();
        public override DiagnosticSeverity DefaultSeverity =>
            descriptor.__AnalyzeAotLocalDefaultSeverity;
        public override DiagnosticDescriptor Descriptor => descriptor;
        public override string Id => descriptor.__AnalyzeAotLocalId;
        public override bool IsSuppressed => false;
        public override Location Location => location;
        public override ImmutableDictionary<string, string?> Properties =>
            ImmutableDictionary<string, string?>.Empty;
        public override DiagnosticSeverity Severity =>
            descriptor.__AnalyzeAotLocalDefaultSeverity;
        public override int WarningLevel => 1;

        public override bool Equals(Diagnostic? obj) =>
            ReferenceEquals(this, obj);

        public override int GetHashCode() =>
            System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);

        public override string GetMessage(IFormatProvider? formatProvider = null)
        {
            string format = descriptor.__AnalyzeAotGetLocalString(
                AnalyzerDescriptorField.MessageFormat);
            return messageArgs.Length == 0
                ? format
                : string.Format(formatProvider, format, messageArgs);
        }
    }
}

public abstract partial class Location
{
    internal static Location __AnalyzeAotCreateLocal(TextSpan sourceSpan) =>
        new AnalyzerLocalLocation(LocationKind.SourceFile, sourceSpan);

    internal static Location __AnalyzeAotCreateNone() =>
        new AnalyzerLocalLocation(LocationKind.None, default);

    private sealed class AnalyzerLocalLocation(
        LocationKind kind,
        TextSpan sourceSpan) : Location
    {
        public override LocationKind Kind => kind;
        public override TextSpan SourceSpan => sourceSpan;
        public override SyntaxTree? SourceTree => null;

        public override bool Equals(object? obj) =>
            ReferenceEquals(this, obj);

        public override int GetHashCode() =>
            System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
    }
}

}

namespace Microsoft.CodeAnalysis.Diagnostics
{

public readonly partial struct SyntaxNodeAnalysisContext
{
    internal SyntaxNodeAnalysisContext(
        SyntaxNode node,
        Action<Diagnostic> reportDiagnostic)
    {
        this = default;
        _dummy = new AnalyzerLocalContext(
            node ?? throw new ArgumentNullException(nameof(node)),
            reportDiagnostic ??
                throw new ArgumentNullException(nameof(reportDiagnostic)));
    }

    internal SyntaxNode __AnalyzeAotGetLocalNode() =>
        (_dummy as AnalyzerLocalContext)?.Node ??
        throw new InvalidOperationException(
            "This syntax-node analysis context is not analyzer-owned.");

    internal bool __AnalyzeAotTryReportLocal(Diagnostic diagnostic)
    {
        if (_dummy is not AnalyzerLocalContext context)
        {
            return false;
        }

        context.ReportDiagnostic(diagnostic);
        return true;
    }

    private sealed record AnalyzerLocalContext(
        SyntaxNode Node,
        Action<Diagnostic> ReportDiagnostic);
}

}

namespace AnalyzeAot.RoslynFacade
{

public static class AnalyzerFacadeFactory
{
    public static AnalysisContext CreateAnalysisContext(
        Action<Action<SyntaxNodeAnalysisContext>, int[]> registerSyntaxNodeAction)
        => new AnalyzerLocalAnalysisContext(registerSyntaxNodeAction);

    public static SyntaxNode CreateSyntaxNode(
        IRoslynControlVtbl controlVtbl,
        long handle) =>
        SyntaxNode.__AnalyzeAotCreateProxy(controlVtbl, handle);

    public static SyntaxNodeAnalysisContext CreateSyntaxNodeAnalysisContext(
        SyntaxNode node,
        Action<Diagnostic> reportDiagnostic) =>
        new(node, reportDiagnostic);

    public static string GetDescriptorString(
        DiagnosticDescriptor descriptor,
        AnalyzerDescriptorField field)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return descriptor.__AnalyzeAotGetLocalString(field);
    }

    private sealed class AnalyzerLocalAnalysisContext(
        Action<Action<SyntaxNodeAnalysisContext>, int[]> registerSyntaxNodeAction)
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

        public override void RegisterSyntaxNodeAction<TLanguageKindEnum>(
            Action<SyntaxNodeAnalysisContext> action,
            params ImmutableArray<TLanguageKindEnum> syntaxKinds)
        {
            ArgumentNullException.ThrowIfNull(action);
            int[] rawKinds = new int[syntaxKinds.Length];
            for (int index = 0; index < syntaxKinds.Length; index++)
            {
                rawKinds[index] = Convert.ToInt32(syntaxKinds[index]);
            }

            registerSyntaxNodeAction(action, rawKinds);
        }

        public override void RegisterCodeBlockAction(
            Action<CodeBlockAnalysisContext> action) =>
            throw UnsupportedRegistration();

        public override void RegisterCodeBlockStartAction<TLanguageKindEnum>(
            Action<CodeBlockStartAnalysisContext<TLanguageKindEnum>> action) =>
            throw UnsupportedRegistration();

        public override void RegisterCompilationAction(
            Action<CompilationAnalysisContext> action) =>
            throw UnsupportedRegistration();

        public override void RegisterCompilationStartAction(
            Action<CompilationStartAnalysisContext> action) =>
            throw UnsupportedRegistration();

        public override void RegisterSemanticModelAction(
            Action<SemanticModelAnalysisContext> action) =>
            throw UnsupportedRegistration();

        public override void RegisterSymbolAction(
            Action<SymbolAnalysisContext> action,
            params ImmutableArray<SymbolKind> symbolKinds) =>
            throw UnsupportedRegistration();

        public override void RegisterSyntaxTreeAction(
            Action<SyntaxTreeAnalysisContext> action) =>
            throw UnsupportedRegistration();

        private static PlatformNotSupportedException
            UnsupportedRegistration() =>
            new(
                "This analyzer registration kind is not implemented by AnalyzeAot.");
    }
}

}
