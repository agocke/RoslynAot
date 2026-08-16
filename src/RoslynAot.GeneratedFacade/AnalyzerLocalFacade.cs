using System.Collections.Immutable;
using System.Globalization;
using System.Resources;
using RoslynAot.Abi;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

[assembly: System.Runtime.InteropServices.TypeMapAssociation<
    RoslynAot.RoslynFacade.RoslynProxyTypeMap>(
        typeof(IEquatable<ISymbol?>),
        typeof(ISymbol.__RoslynAotImplementation))]

namespace Microsoft.CodeAnalysis
{

public partial interface ISymbol
{
    bool IEquatable<ISymbol?>.Equals(ISymbol? other) =>
        SymbolEqualityComparer.Default.Equals(this, other);
}

public abstract partial class LocalizableString
{
    private EventHandler<Exception>? __roslynAotOnException;

    internal bool __RoslynAotIsLocal =>
        __roslynAotControlVtbl is null;

    internal void __RoslynAotAddExceptionHandler(
        EventHandler<Exception>? handler) =>
        __roslynAotOnException += handler;

    internal void __RoslynAotRemoveExceptionHandler(
        EventHandler<Exception>? handler) =>
        __roslynAotOnException -= handler;

    internal bool __RoslynAotAreEqual(object? other) =>
        AreEqual(other);

    internal int __RoslynAotGetHash() => GetHash();

    internal string __RoslynAotGetText(
        IFormatProvider? formatProvider)
    {
        try
        {
            return GetText(formatProvider);
        }
        catch (Exception exception)
        {
            __roslynAotOnException?.Invoke(this, exception);
            return string.Empty;
        }
    }

    internal static LocalizableString __RoslynAotCreateFixed(
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
    private string __roslynAotResourceName = string.Empty;
    private ResourceManager? __roslynAotResourceManager;
    private Type? __roslynAotResourceSource;
    private string[] __roslynAotFormatArguments = [];

    internal void __RoslynAotInitializeLocal(
        string resourceName,
        ResourceManager resourceManager,
        Type resourceSource,
        string[] formatArguments)
    {
        ArgumentNullException.ThrowIfNull(resourceName);
        ArgumentNullException.ThrowIfNull(resourceManager);
        ArgumentNullException.ThrowIfNull(resourceSource);
        ArgumentNullException.ThrowIfNull(formatArguments);

        __roslynAotResourceName = resourceName;
        __roslynAotResourceManager = resourceManager;
        __roslynAotResourceSource = resourceSource;
        __roslynAotFormatArguments = formatArguments;
    }

    internal bool __RoslynAotAreEqualLocal(object? other) =>
        other is LocalizableResourceString resourceString &&
        string.Equals(
            __roslynAotResourceName,
            resourceString.__roslynAotResourceName,
            StringComparison.Ordinal) &&
        ReferenceEquals(
            __roslynAotResourceManager,
            resourceString.__roslynAotResourceManager) &&
        __roslynAotResourceSource ==
            resourceString.__roslynAotResourceSource &&
        __roslynAotFormatArguments.SequenceEqual(
            resourceString.__roslynAotFormatArguments,
            StringComparer.Ordinal);

    internal int __RoslynAotGetHashLocal()
    {
        var hash = new HashCode();
        hash.Add(__roslynAotResourceName, StringComparer.Ordinal);
        hash.Add(__roslynAotResourceManager);
        hash.Add(__roslynAotResourceSource);
        foreach (string argument in __roslynAotFormatArguments)
        {
            hash.Add(argument, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }

    internal string __RoslynAotGetTextLocal(
        IFormatProvider? formatProvider)
    {
        ResourceManager resourceManager =
            __roslynAotResourceManager ??
            throw new InvalidOperationException(
                "The localizable resource string is not initialized.");
        CultureInfo? culture = formatProvider as CultureInfo;
        string resource =
            resourceManager.GetString(
                __roslynAotResourceName,
                culture ?? CultureInfo.CurrentUICulture) ??
            __roslynAotResourceName;
        return __roslynAotFormatArguments.Length == 0
            ? resource
            : string.Format(
                formatProvider,
                resource,
                __roslynAotFormatArguments);
    }
}

public sealed partial class DiagnosticDescriptor
{
    private bool __roslynAotIsLocal;
    private string __roslynAotLocalId = string.Empty;
    private string __roslynAotLocalTitle = string.Empty;
    private string __roslynAotLocalMessageFormat = string.Empty;
    private string __roslynAotLocalCategory = string.Empty;
    private LocalizableString __roslynAotLocalTitleValue =
        LocalizableString.__RoslynAotCreateFixed(string.Empty);
    private LocalizableString __roslynAotLocalMessageFormatValue =
        LocalizableString.__RoslynAotCreateFixed(string.Empty);
    private LocalizableString __roslynAotLocalDescriptionValue =
        LocalizableString.__RoslynAotCreateFixed(string.Empty);
    private string __roslynAotLocalHelpLinkUri = string.Empty;
    private string[] __roslynAotLocalCustomTags = [];
    private DiagnosticSeverity __roslynAotLocalDefaultSeverity;
    private bool __roslynAotLocalIsEnabledByDefault;

    internal bool __RoslynAotIsLocal => __roslynAotIsLocal;
    internal string __RoslynAotLocalId => __roslynAotLocalId;
    internal string __RoslynAotLocalCategory => __roslynAotLocalCategory;
    internal DiagnosticSeverity __RoslynAotLocalDefaultSeverity =>
        __roslynAotLocalDefaultSeverity;
    internal bool __RoslynAotLocalIsEnabledByDefault =>
        __roslynAotLocalIsEnabledByDefault;
    internal LocalizableString __RoslynAotLocalTitleValue =>
        __roslynAotLocalTitleValue;
    internal LocalizableString __RoslynAotLocalMessageFormatValue =>
        __roslynAotLocalMessageFormatValue;
    internal LocalizableString __RoslynAotLocalDescriptionValue =>
        __roslynAotLocalDescriptionValue;
    internal string __RoslynAotLocalHelpLinkUri =>
        __roslynAotLocalHelpLinkUri;
    internal IEnumerable<string> __RoslynAotLocalCustomTags =>
        __roslynAotLocalCustomTags;

    internal void __RoslynAotInitializeLocal(
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

        __RoslynAotInitializeLocal(
            id,
            LocalizableString.__RoslynAotCreateFixed(title),
            LocalizableString.__RoslynAotCreateFixed(messageFormat),
            category,
            defaultSeverity,
            isEnabledByDefault,
            LocalizableString.__RoslynAotCreateFixed(description),
            helpLinkUri,
            customTags);
    }

    internal void __RoslynAotInitializeLocal(
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

        __roslynAotIsLocal = true;
        __roslynAotLocalId = id;
        __roslynAotLocalTitleValue = title;
        __roslynAotLocalMessageFormatValue = messageFormat;
        __roslynAotLocalDescriptionValue =
            description ??
            LocalizableString.__RoslynAotCreateFixed(string.Empty);
        __roslynAotLocalTitle = title.__RoslynAotGetText(null);
        __roslynAotLocalMessageFormat =
            messageFormat.__RoslynAotGetText(null);
        __roslynAotLocalCategory = category;
        __roslynAotLocalHelpLinkUri = helpLinkUri ?? string.Empty;
        __roslynAotLocalCustomTags = [.. customTags];
        __roslynAotLocalDefaultSeverity = defaultSeverity;
        __roslynAotLocalIsEnabledByDefault = isEnabledByDefault;
    }

    internal string __RoslynAotGetLocalString(
        AnalyzerDescriptorField field)
    {
        if (!__roslynAotIsLocal)
        {
            throw new InvalidOperationException(
                "The diagnostic descriptor is not analyzer-owned.");
        }

        return field switch
        {
            AnalyzerDescriptorField.Id => __roslynAotLocalId,
            AnalyzerDescriptorField.Title => __roslynAotLocalTitle,
            AnalyzerDescriptorField.MessageFormat =>
                __roslynAotLocalMessageFormat,
            AnalyzerDescriptorField.Category => __roslynAotLocalCategory,
            AnalyzerDescriptorField.Description =>
                __roslynAotLocalDescriptionValue.__RoslynAotGetText(null),
            AnalyzerDescriptorField.HelpLinkUri =>
                __roslynAotLocalHelpLinkUri,
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };
    }
}

public abstract partial class Diagnostic
{
    internal static Diagnostic __RoslynAotCreateLocal(
        DiagnosticDescriptor descriptor,
        Location? location,
        object?[]? messageArgs) =>
        new AnalyzerLocalDiagnostic(
            descriptor,
            location ?? Location.__RoslynAotCreateNone(),
            Array.Empty<Location>(),
            ImmutableDictionary<string, string?>.Empty,
            messageArgs ?? []);

    internal static Diagnostic __RoslynAotCreateLocal(
        DiagnosticDescriptor descriptor,
        Location? location,
        IEnumerable<Location>? additionalLocations,
        ImmutableDictionary<string, string?>? properties,
        object?[]? messageArgs) =>
        new AnalyzerLocalDiagnostic(
            descriptor,
            location ?? Location.__RoslynAotCreateNone(),
            additionalLocations?.ToArray() ?? [],
            properties ?? ImmutableDictionary<string, string?>.Empty,
            messageArgs ?? []);

    private sealed class AnalyzerLocalDiagnostic(
        DiagnosticDescriptor descriptor,
        Location location,
        IReadOnlyList<Location> additionalLocations,
        ImmutableDictionary<string, string?> properties,
        object?[] messageArgs) : Diagnostic
    {
        public override IReadOnlyList<Location> AdditionalLocations =>
            additionalLocations;
        public override DiagnosticSeverity DefaultSeverity =>
            descriptor.__RoslynAotLocalDefaultSeverity;
        public override DiagnosticDescriptor Descriptor => descriptor;
        public override string Id => descriptor.__RoslynAotLocalId;
        public override bool IsSuppressed => false;
        public override Location Location => location;
        public override ImmutableDictionary<string, string?> Properties =>
            properties;
        public override DiagnosticSeverity Severity =>
            descriptor.__RoslynAotLocalDefaultSeverity;
        public override int WarningLevel => 1;

        public override bool Equals(Diagnostic? obj) =>
            ReferenceEquals(this, obj);

        public override int GetHashCode() =>
            System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);

        public override string GetMessage(IFormatProvider? formatProvider = null)
        {
            string format = descriptor.__RoslynAotGetLocalString(
                AnalyzerDescriptorField.MessageFormat);
            return messageArgs.Length == 0
                ? format
                : string.Format(formatProvider, format, messageArgs);
        }
    }
}

public abstract partial class Location
{
    internal bool __RoslynAotIsLocal =>
        this is AnalyzerLocalLocation;

    internal static Location __RoslynAotCreateLocal(TextSpan sourceSpan) =>
        new AnalyzerLocalLocation(
            LocationKind.SourceFile,
            sourceTree: null,
            sourceSpan);

    internal static Location __RoslynAotCreateLocal(
        SyntaxTree sourceTree,
        TextSpan sourceSpan) =>
        new AnalyzerLocalLocation(
            LocationKind.SourceFile,
            sourceTree,
            sourceSpan);

    internal static Location __RoslynAotCreateNone() =>
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

public sealed partial class SymbolEqualityComparer
{
    private RoslynWellKnownObject? __roslynAotKind;

    internal RoslynWellKnownObject __RoslynAotKind =>
        __roslynAotKind ??
        throw new InvalidOperationException(
            "The symbol equality comparer kind is unavailable.");

    internal static SymbolEqualityComparer __RoslynAotCreateLocal(
        RoslynWellKnownObject kind) =>
        new()
        {
            __roslynAotKind = kind,
        };
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

    internal SyntaxNode __RoslynAotGetLocalNode() =>
        (_dummy as AnalyzerLocalContext)?.Node ??
        throw new InvalidOperationException(
            "This syntax-node analysis context is not analyzer-owned.");

    internal bool __RoslynAotTryReportLocal(Diagnostic diagnostic)
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

namespace RoslynAot.RoslynFacade
{

public static class AnalyzerFacadeFactory
{
    public static AnalysisContext CreateAnalysisContext(
        Action<Action<SyntaxNodeAnalysisContext>, int[]> registerSyntaxNodeAction)
        => new AnalyzerLocalAnalysisContext(registerSyntaxNodeAction);

    public static SyntaxNode CreateSyntaxNode(
        IRoslynControlVtbl controlVtbl,
        long handle) =>
        SyntaxNode.__RoslynAotCreateProxy(controlVtbl, handle);

    public static SyntaxNodeAnalysisContext CreateSyntaxNodeAnalysisContext(
        SyntaxNode node,
        Action<Diagnostic> reportDiagnostic) =>
        new(node, reportDiagnostic);

    public static string GetDescriptorString(
        DiagnosticDescriptor descriptor,
        AnalyzerDescriptorField field)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return descriptor.__RoslynAotGetLocalString(field);
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
                "This analyzer registration kind is not implemented by RoslynAot.");
    }
}

}
