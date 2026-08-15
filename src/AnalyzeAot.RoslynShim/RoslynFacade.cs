using System.Collections.Immutable;
using System.Text;
using AnalyzeAot.Abi;

namespace Microsoft.CodeAnalysis
{
    public static class LanguageNames
    {
        public const string CSharp = "C#";
    }

    public enum DiagnosticSeverity
    {
        Hidden,
        Info,
        Warning,
        Error,
    }

    public sealed class DiagnosticDescriptor
    {
        public DiagnosticDescriptor(
            string id,
            string title,
            string messageFormat,
            string category,
            DiagnosticSeverity defaultSeverity,
            bool isEnabledByDefault,
            string? description = null,
            string? helpLinkUri = null,
            params string[] customTags)
        {
            Id = id;
            Title = title;
            MessageFormat = messageFormat;
            Category = category;
            DefaultSeverity = defaultSeverity;
            IsEnabledByDefault = isEnabledByDefault;
            Description = description;
            HelpLinkUri = helpLinkUri;
            CustomTags = customTags;
        }

        public string Id { get; }

        public string Title { get; }

        public string MessageFormat { get; }

        public string Category { get; }

        public DiagnosticSeverity DefaultSeverity { get; }

        public bool IsEnabledByDefault { get; }

        public string? Description { get; }

        public string? HelpLinkUri { get; }

        public IEnumerable<string> CustomTags { get; }
    }

    public sealed class Diagnostic
    {
        private Diagnostic(
            DiagnosticDescriptor descriptor,
            Location location,
            object?[] messageArgs)
        {
            Descriptor = descriptor;
            Location = location;
            MessageArgs = messageArgs;
        }

        public DiagnosticDescriptor Descriptor { get; }

        public Location Location { get; }

        internal object?[] MessageArgs { get; }

        public static Diagnostic Create(
            DiagnosticDescriptor descriptor,
            Location location,
            params object?[] messageArgs) =>
            new(descriptor, location, messageArgs);
    }

    public sealed class Location
    {
        internal Location(Text.TextSpan sourceSpan)
        {
            SourceSpan = sourceSpan;
        }

        public Text.TextSpan SourceSpan { get; }
    }

    public class SyntaxNode
    {
        private readonly IAnalyzerHost _host;
        private readonly int _handle;

        internal SyntaxNode(IAnalyzerHost host, int handle)
        {
            _host = host;
            _handle = handle;
        }

        public int RawKind
        {
            get
            {
                ThrowIfFailed(_host.GetRawKind(_handle, out int rawKind));
                return rawKind;
            }
        }

        public Text.TextSpan Span
        {
            get
            {
                ThrowIfFailed(_host.GetSpanStart(_handle, out int start));
                ThrowIfFailed(_host.GetSpanLength(_handle, out int length));
                return new Text.TextSpan(start, length);
            }
        }

        public Location GetLocation() => new(Span);

        public override unsafe string ToString()
        {
            ThrowIfFailed(
                _host.CopyTextUtf8(_handle, 0, 0, out int byteCount));
            Span<byte> buffer = byteCount <= 512
                ? stackalloc byte[byteCount]
                : new byte[byteCount];
            fixed (byte* bufferPointer = buffer)
            {
                ThrowIfFailed(
                    _host.CopyTextUtf8(
                        _handle,
                        (nint)bufferPointer,
                        buffer.Length,
                        out _));
            }

            return Encoding.UTF8.GetString(buffer);
        }

        private static void ThrowIfFailed(int result)
        {
            if (result != AnalyzerAbi.Success)
            {
                throw new InvalidOperationException(
                    $"Analyzer host operation failed with 0x{result:x8}.");
            }
        }
    }
}

namespace Microsoft.CodeAnalysis.Text
{
    public readonly record struct TextSpan(int Start, int Length);
}

namespace Microsoft.CodeAnalysis.Diagnostics
{
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public sealed class DiagnosticAnalyzerAttribute : Attribute
    {
        public DiagnosticAnalyzerAttribute(
            string firstLanguage,
            params string[] additionalLanguages)
        {
            Languages = [firstLanguage, .. additionalLanguages];
        }

        public ImmutableArray<string> Languages { get; }
    }

    public abstract class DiagnosticAnalyzer
    {
        public abstract ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
        {
            get;
        }

        public abstract void Initialize(AnalysisContext context);
    }

    public abstract class AnalysisContext
    {
        public void RegisterSyntaxNodeAction<TLanguageKindEnum>(
            Action<SyntaxNodeAnalysisContext> action,
            params TLanguageKindEnum[] syntaxKinds)
            where TLanguageKindEnum : struct
        {
            ArgumentNullException.ThrowIfNull(action);
            ArgumentNullException.ThrowIfNull(syntaxKinds);
            RegisterSyntaxNodeActionCore(
                action,
                syntaxKinds.Select(kind => Convert.ToInt32(kind)).ToArray());
        }

        public virtual void EnableConcurrentExecution()
        {
        }

        public virtual void ConfigureGeneratedCodeAnalysis(
            GeneratedCodeAnalysisFlags analysisMode)
        {
        }

        protected abstract void RegisterSyntaxNodeActionCore(
            Action<SyntaxNodeAnalysisContext> action,
            int[] rawKinds);
    }

    [Flags]
    public enum GeneratedCodeAnalysisFlags
    {
        None = 0,
        Analyze = 1,
        ReportDiagnostics = 2,
    }

    public readonly struct SyntaxNodeAnalysisContext
    {
        private readonly Action<Diagnostic> _reportDiagnostic;

        internal SyntaxNodeAnalysisContext(
            SyntaxNode node,
            Action<Diagnostic> reportDiagnostic)
        {
            Node = node;
            _reportDiagnostic = reportDiagnostic;
        }

        public SyntaxNode Node { get; }

        public void ReportDiagnostic(Diagnostic diagnostic) =>
            _reportDiagnostic(diagnostic);
    }
}

namespace AnalyzeAot.RoslynFacade
{
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.Diagnostics;

    public static class FacadeFactory
    {
        public static SyntaxNode CreateSyntaxNode(
            IAnalyzerHost host,
            int handle) =>
            new(host, handle);

        public static SyntaxNodeAnalysisContext CreateSyntaxNodeAnalysisContext(
            SyntaxNode node,
            Action<Diagnostic> reportDiagnostic) =>
            new(node, reportDiagnostic);
    }
}
