using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Text;
using AnalyzeAot.Abi;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace AnalyzeAot.CompilerHost;

internal sealed unsafe class NativeDiagnosticAnalyzer : DiagnosticAnalyzer
{
    private static readonly StrategyBasedComWrappers s_comWrappers = new();
    private static readonly List<nint> s_loadedLibraries = [];

    private readonly IAnalyzerTransport _transport;
    private readonly RoslynInterop _roslynInterop = new();

    internal NativeDiagnosticAnalyzer(string analyzerPath)
        : this(LoadTransport(analyzerPath))
    {
    }

    private NativeDiagnosticAnalyzer(IAnalyzerTransport transport)
    {
        _transport = transport;
        SupportedDiagnostics = ReadSupportedDiagnostics(transport);
    }

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
    {
        get;
    }

    private static IAnalyzerTransport LoadTransport(string analyzerPath)
    {
        nint library = NativeLibrary.Load(analyzerPath);
        s_loadedLibraries.Add(library);

        nint export = NativeLibrary.GetExport(
            library,
            AnalyzerAbi.GetAnalyzerEntryPoint);
        var getAnalyzer = (delegate* unmanaged[Cdecl]<nint>)export;
        nint analyzerPointer = getAnalyzer();
        if (analyzerPointer == 0)
        {
            throw new InvalidOperationException(
                $"Analyzer '{analyzerPath}' returned no transport interface.");
        }

        try
        {
            var transport = (IAnalyzerTransport)s_comWrappers
                .GetOrCreateObjectForComInstance(
                    analyzerPointer,
                    CreateObjectFlags.None);
            int result = transport.GetVersion(out uint version);
            if (result != AnalyzerAbi.Success ||
                version != AnalyzerAbi.Version)
            {
                throw new InvalidOperationException(
                    $"Analyzer '{analyzerPath}' uses an incompatible ABI.");
            }

            return transport;
        }
        finally
        {
            AnalyzerAbi.Release(analyzerPointer);
        }
    }

    public override void Initialize(AnalysisContext context)
    {
        var host = new CompilerAnalyzerHost(this, context);
        InvokeWithHost(
            host,
            (hostPointer, interopPointer) =>
                _transport.Initialize(hostPointer, interopPointer));
    }

    internal void InvokeSyntaxNodeAction(
        int actionId,
        SyntaxNodeAnalysisContext context)
    {
        var host = new CompilerAnalyzerHost(this, context);
        long nodeHandle = _roslynInterop.AddObject(context.Node);
        InvokeWithHost(
            host,
            (hostPointer, interopPointer) =>
                _transport.InvokeSyntaxNodeAction(
                actionId,
                hostPointer,
                interopPointer,
                nodeHandle));
    }

    internal bool TryGetDescriptor(
        int descriptorIndex,
        out DiagnosticDescriptor descriptor)
    {
        if ((uint)descriptorIndex >=
            (uint)SupportedDiagnostics.Length)
        {
            descriptor = null!;
            return false;
        }

        descriptor = SupportedDiagnostics[descriptorIndex];
        return true;
    }

    private static ImmutableArray<DiagnosticDescriptor>
        ReadSupportedDiagnostics(IAnalyzerTransport transport)
    {
        ThrowIfFailed(transport.GetDescriptorCount(out int count));
        var descriptors =
            ImmutableArray.CreateBuilder<DiagnosticDescriptor>(count);
        for (int index = 0; index < count; index++)
        {
            ThrowIfFailed(
                transport.GetDescriptorInfo(
                    index,
                    out AnalyzerDiagnosticSeverity severity,
                    out int enabledByDefault));
            descriptors.Add(
                new DiagnosticDescriptor(
                    ReadDescriptorString(
                        transport,
                        index,
                        AnalyzerDescriptorField.Id),
                    ReadDescriptorString(
                        transport,
                        index,
                        AnalyzerDescriptorField.Title),
                    ReadDescriptorString(
                        transport,
                        index,
                        AnalyzerDescriptorField.MessageFormat),
                    ReadDescriptorString(
                        transport,
                        index,
                        AnalyzerDescriptorField.Category),
                    (DiagnosticSeverity)severity,
                    enabledByDefault != 0,
                    description: ReadDescriptorString(
                        transport,
                        index,
                        AnalyzerDescriptorField.Description),
                    helpLinkUri: ReadDescriptorString(
                        transport,
                        index,
                        AnalyzerDescriptorField.HelpLinkUri)));
        }

        return descriptors.MoveToImmutable();
    }

    private static string ReadDescriptorString(
        IAnalyzerTransport transport,
        int descriptorIndex,
        AnalyzerDescriptorField field)
    {
        ThrowIfFailed(
            transport.CopyDescriptorStringUtf8(
                descriptorIndex,
                field,
                0,
                0,
                out int byteCount));
        Span<byte> buffer = byteCount <= 256
            ? stackalloc byte[byteCount]
            : new byte[byteCount];
        fixed (byte* bufferPointer = buffer)
        {
            ThrowIfFailed(
                transport.CopyDescriptorStringUtf8(
                    descriptorIndex,
                    field,
                    (nint)bufferPointer,
                    buffer.Length,
                    out _));
        }

        return Encoding.UTF8.GetString(buffer);
    }

    private void InvokeWithHost(
        CompilerAnalyzerHost host,
        Func<nint, nint, int> invoke)
    {
        nint hostPointer =
            s_comWrappers.GetOrCreateComInterfaceForObject(
                host,
                CreateComInterfaceFlags.None);
        nint interopPointer =
            s_comWrappers.GetOrCreateComInterfaceForObject(
                _roslynInterop,
                CreateComInterfaceFlags.None);
        try
        {
            ThrowIfFailed(invoke(hostPointer, interopPointer));
            GC.KeepAlive(host);
            GC.KeepAlive(_roslynInterop);
        }
        finally
        {
            AnalyzerAbi.Release(interopPointer);
            AnalyzerAbi.Release(hostPointer);
        }
    }

    private static void ThrowIfFailed(int result)
    {
        if (result != AnalyzerAbi.Success)
        {
            throw new InvalidOperationException(
                $"Analyzer transport operation failed with 0x{result:x8}.");
        }
    }
}

[GeneratedComClass]
internal sealed partial class CompilerAnalyzerHost : IAnalyzerHost
{
    private readonly NativeDiagnosticAnalyzer _analyzer;
    private readonly AnalysisContext? _analysisContext;
    private readonly SyntaxNodeAnalysisContext _syntaxContext;
    private readonly bool _hasSyntaxContext;

    public CompilerAnalyzerHost(
        NativeDiagnosticAnalyzer analyzer,
        AnalysisContext analysisContext)
    {
        _analyzer = analyzer;
        _analysisContext = analysisContext;
    }

    public CompilerAnalyzerHost(
        NativeDiagnosticAnalyzer analyzer,
        SyntaxNodeAnalysisContext syntaxContext)
    {
        _analyzer = analyzer;
        _syntaxContext = syntaxContext;
        _hasSyntaxContext = true;
    }

    public int GetVersion(out uint version)
    {
        version = AnalyzerAbi.Version;
        return AnalyzerAbi.Success;
    }

    public int RegisterSyntaxNodeAction(int actionId, int rawKind)
    {
        if (_analysisContext is null)
        {
            return AnalyzerAbi.InvalidArgument;
        }

        _analysisContext.RegisterSyntaxNodeAction(
            context => _analyzer.InvokeSyntaxNodeAction(
                actionId,
                context),
            (SyntaxKind)rawKind);
        return AnalyzerAbi.Success;
    }

    public int ReportDiagnostic(
        int descriptorIndex,
        int start,
        int length)
    {
        if (!_hasSyntaxContext ||
            !_analyzer.TryGetDescriptor(
                descriptorIndex,
                out DiagnosticDescriptor descriptor) ||
            start < 0 ||
            length < 0)
        {
            return AnalyzerAbi.InvalidArgument;
        }

        Location location = Location.Create(
            _syntaxContext.Node.SyntaxTree,
            new TextSpan(start, length));
        _syntaxContext.ReportDiagnostic(
            Diagnostic.Create(descriptor, location));
        return AnalyzerAbi.Success;
    }
}
