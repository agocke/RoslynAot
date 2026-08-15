using System.Collections.Immutable;
using System.Globalization;
using System.Resources;
using AnalyzeAot.Abi;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace Microsoft.CodeAnalysis
{

public abstract partial class LocalizableString
{
    private EventHandler<Exception>? __analyzeAotOnException;

    internal bool __AnalyzeAotIsLocal =>
        __analyzeAotControlVtbl is null;

    internal void __AnalyzeAotAddExceptionHandler(
        EventHandler<Exception>? handler) =>
        __analyzeAotOnException += handler;

    internal void __AnalyzeAotRemoveExceptionHandler(
        EventHandler<Exception>? handler) =>
        __analyzeAotOnException -= handler;

    internal bool __AnalyzeAotAreEqual(object? other) =>
        AreEqual(other);

    internal int __AnalyzeAotGetHash() => GetHash();

    internal string __AnalyzeAotGetText(
        IFormatProvider? formatProvider)
    {
        try
        {
            return GetText(formatProvider);
        }
        catch (Exception exception)
        {
            __analyzeAotOnException?.Invoke(this, exception);
            return string.Empty;
        }
    }

    internal static LocalizableString __AnalyzeAotCreateFixed(
        string? value) =>
        new AnalyzerFixedLocalizableString(value ?? string.Empty);

    private sealed class AnalyzerFixedLocalizableString : LocalizableString
    {
        private readonly string _value;

        public AnalyzerFixedLocalizableString(string value)
        {
            _value = value;
        }

        protected override bool AreEqual(object? other) =>
            other is AnalyzerFixedLocalizableString fixedString &&
            string.Equals(
                _value,
                fixedString._value,
                StringComparison.Ordinal);

        protected override int GetHash() =>
            StringComparer.Ordinal.GetHashCode(_value);

        protected override string GetText(
            IFormatProvider? formatProvider) =>
            _value;
    }
}

public sealed partial class LocalizableResourceString
{
    private string __analyzeAotResourceName = string.Empty;
    private ResourceManager? __analyzeAotResourceManager;
    private Type? __analyzeAotResourceSource;
    private string[] __analyzeAotFormatArguments = [];

    internal void __AnalyzeAotInitializeLocal(
        string resourceName,
        ResourceManager resourceManager,
        Type resourceSource,
        string[] formatArguments)
    {
        ArgumentNullException.ThrowIfNull(resourceName);
        ArgumentNullException.ThrowIfNull(resourceManager);
        ArgumentNullException.ThrowIfNull(resourceSource);
        ArgumentNullException.ThrowIfNull(formatArguments);

        __analyzeAotResourceName = resourceName;
        __analyzeAotResourceManager = resourceManager;
        __analyzeAotResourceSource = resourceSource;
        __analyzeAotFormatArguments = formatArguments;
    }

    internal bool __AnalyzeAotAreEqualLocal(object? other) =>
        other is LocalizableResourceString resourceString &&
        string.Equals(
            __analyzeAotResourceName,
            resourceString.__analyzeAotResourceName,
            StringComparison.Ordinal) &&
        ReferenceEquals(
            __analyzeAotResourceManager,
            resourceString.__analyzeAotResourceManager) &&
        __analyzeAotResourceSource ==
            resourceString.__analyzeAotResourceSource &&
        __analyzeAotFormatArguments.SequenceEqual(
            resourceString.__analyzeAotFormatArguments,
            StringComparer.Ordinal);

    internal int __AnalyzeAotGetHashLocal()
    {
        var hash = new HashCode();
        hash.Add(__analyzeAotResourceName, StringComparer.Ordinal);
        hash.Add(__analyzeAotResourceManager);
        hash.Add(__analyzeAotResourceSource);
        foreach (string argument in __analyzeAotFormatArguments)
        {
            hash.Add(argument, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }

    internal string __AnalyzeAotGetTextLocal(
        IFormatProvider? formatProvider)
    {
        ResourceManager resourceManager =
            __analyzeAotResourceManager ??
            throw new InvalidOperationException(
                "The localizable resource string is not initialized.");
        CultureInfo? culture = formatProvider as CultureInfo;
        string resource =
            resourceManager.GetString(
                __analyzeAotResourceName,
                culture ?? CultureInfo.CurrentUICulture) ??
            __analyzeAotResourceName;
        return __analyzeAotFormatArguments.Length == 0
            ? resource
            : string.Format(
                formatProvider,
                resource,
                __analyzeAotFormatArguments);
    }
}

public sealed partial class DiagnosticDescriptor
{
    private bool __analyzeAotIsLocal;
    private string __analyzeAotLocalId = string.Empty;
    private string __analyzeAotLocalTitle = string.Empty;
    private string __analyzeAotLocalMessageFormat = string.Empty;
    private string __analyzeAotLocalCategory = string.Empty;
    private LocalizableString __analyzeAotLocalTitleValue =
        LocalizableString.__AnalyzeAotCreateFixed(string.Empty);
    private LocalizableString __analyzeAotLocalMessageFormatValue =
        LocalizableString.__AnalyzeAotCreateFixed(string.Empty);
    private LocalizableString __analyzeAotLocalDescriptionValue =
        LocalizableString.__AnalyzeAotCreateFixed(string.Empty);
    private string __analyzeAotLocalHelpLinkUri = string.Empty;
    private string[] __analyzeAotLocalCustomTags = [];
    private DiagnosticSeverity __analyzeAotLocalDefaultSeverity;
    private bool __analyzeAotLocalIsEnabledByDefault;

    internal bool __AnalyzeAotIsLocal => __analyzeAotIsLocal;
    internal string __AnalyzeAotLocalId => __analyzeAotLocalId;
    internal string __AnalyzeAotLocalCategory => __analyzeAotLocalCategory;
    internal DiagnosticSeverity __AnalyzeAotLocalDefaultSeverity =>
        __analyzeAotLocalDefaultSeverity;
    internal bool __AnalyzeAotLocalIsEnabledByDefault =>
        __analyzeAotLocalIsEnabledByDefault;
    internal LocalizableString __AnalyzeAotLocalTitleValue =>
        __analyzeAotLocalTitleValue;
    internal LocalizableString __AnalyzeAotLocalMessageFormatValue =>
        __analyzeAotLocalMessageFormatValue;
    internal LocalizableString __AnalyzeAotLocalDescriptionValue =>
        __analyzeAotLocalDescriptionValue;
    internal string __AnalyzeAotLocalHelpLinkUri =>
        __analyzeAotLocalHelpLinkUri;
    internal IEnumerable<string> __AnalyzeAotLocalCustomTags =>
        __analyzeAotLocalCustomTags;

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

        __AnalyzeAotInitializeLocal(
            id,
            LocalizableString.__AnalyzeAotCreateFixed(title),
            LocalizableString.__AnalyzeAotCreateFixed(messageFormat),
            category,
            defaultSeverity,
            isEnabledByDefault,
            LocalizableString.__AnalyzeAotCreateFixed(description),
            helpLinkUri,
            customTags);
    }

    internal void __AnalyzeAotInitializeLocal(
        string id,
        LocalizableString title,
        LocalizableString messageFormat,
        string category,
        DiagnosticSeverity defaultSeverity,
        bool isEnabledByDefault,
        LocalizableString? description,
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
        __analyzeAotLocalTitleValue = title;
        __analyzeAotLocalMessageFormatValue = messageFormat;
        __analyzeAotLocalDescriptionValue =
            description ??
            LocalizableString.__AnalyzeAotCreateFixed(string.Empty);
        __analyzeAotLocalTitle = title.__AnalyzeAotGetText(null);
        __analyzeAotLocalMessageFormat =
            messageFormat.__AnalyzeAotGetText(null);
        __analyzeAotLocalCategory = category;
        __analyzeAotLocalHelpLinkUri = helpLinkUri ?? string.Empty;
        __analyzeAotLocalCustomTags = [.. customTags];
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
            AnalyzerDescriptorField.Description =>
                __analyzeAotLocalDescriptionValue.__AnalyzeAotGetText(null),
            AnalyzerDescriptorField.HelpLinkUri =>
                __analyzeAotLocalHelpLinkUri,
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
        new AnalyzerLocalLocation(
            LocationKind.SourceFile,
            sourceTree: null,
            sourceSpan);

    internal static Location __AnalyzeAotCreateLocal(
        SyntaxTree sourceTree,
        TextSpan sourceSpan) =>
        new AnalyzerLocalLocation(
            LocationKind.SourceFile,
            sourceTree,
            sourceSpan);

    internal static Location __AnalyzeAotCreateNone() =>
        new AnalyzerLocalLocation(
            LocationKind.None,
            sourceTree: null,
            default);

    private sealed class AnalyzerLocalLocation(
        LocationKind kind,
        SyntaxTree? sourceTree,
        TextSpan sourceSpan) : Location
    {
        public override LocationKind Kind => kind;
        public override TextSpan SourceSpan => sourceSpan;
        public override SyntaxTree? SourceTree => sourceTree;

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
