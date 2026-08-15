using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
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

    private NativeDiagnosticAnalyzer(IAnalyzerTransport transport)
    {
        _transport = transport;
        SupportedDiagnostics = ReadSupportedDiagnostics();
    }

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
    {
        get;
    }

    internal static ImmutableArray<NativeDiagnosticAnalyzer> Load(
        string analyzerPath)
    {
        nint library = NativeLibrary.Load(analyzerPath);
        s_loadedLibraries.Add(library);

        nint export = NativeLibrary.GetExport(
            library,
            AnalyzerAbi.GetAnalyzerModuleEntryPoint);
        var getAnalyzerModule = (delegate* unmanaged[Cdecl]<nint>)export;
        nint modulePointer = getAnalyzerModule();
        if (modulePointer == 0)
        {
            throw new InvalidOperationException(
                $"Analyzer module '{analyzerPath}' returned no module interface.");
        }

        try
        {
            var module = (IAnalyzerModule)s_comWrappers
                .GetOrCreateObjectForComInstance(
                    modulePointer,
                    CreateObjectFlags.None);
            int result = module.GetVersion(out uint version);
            if (result != AnalyzerAbi.Success ||
                version != AnalyzerAbi.Version)
            {
                throw new InvalidOperationException(
                    $"Analyzer module '{analyzerPath}' uses an incompatible ABI.");
            }

            ThrowIfFailed(module.GetAnalyzerCount(out int analyzerCount));
            if (analyzerCount <= 0)
            {
                throw new InvalidOperationException(
                    $"Analyzer module '{analyzerPath}' contains no analyzers.");
            }

            var analyzers =
                ImmutableArray.CreateBuilder<NativeDiagnosticAnalyzer>(
                    analyzerCount);
            for (int index = 0; index < analyzerCount; index++)
            {
                ThrowIfFailed(
                    module.GetAnalyzer(index, out nint analyzerPointer));
                if (analyzerPointer == 0)
                {
                    throw new InvalidOperationException(
                        $"Analyzer module '{analyzerPath}' returned no analyzer at index {index}.");
                }

                try
                {
                    var transport = (IAnalyzerTransport)s_comWrappers
                        .GetOrCreateObjectForComInstance(
                            analyzerPointer,
                            CreateObjectFlags.None);
                    ThrowIfFailed(transport.GetVersion(out version));
                    if (version != AnalyzerAbi.Version)
                    {
                        throw new InvalidOperationException(
                            $"Analyzer {index} in module '{analyzerPath}' uses an incompatible ABI.");
                    }

                    analyzers.Add(new NativeDiagnosticAnalyzer(transport));
                }
                finally
                {
                    AnalyzerAbi.Release(analyzerPointer);
                }
            }

            return analyzers.MoveToImmutable();
        }
        finally
        {
            AnalyzerAbi.Release(modulePointer);
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

    private ImmutableArray<DiagnosticDescriptor>
        ReadSupportedDiagnostics()
    {
        nint interopPointer =
            s_comWrappers.GetOrCreateComInterfaceForObject(
                _roslynInterop,
                CreateComInterfaceFlags.None);
        try
        {
            ThrowIfFailed(
                _transport.GetDescriptorCount(
                    interopPointer,
                    out int count));
            var descriptors =
                ImmutableArray.CreateBuilder<DiagnosticDescriptor>(count);
            for (int index = 0; index < count; index++)
            {
                ThrowIfFailed(
                    _transport.GetDescriptorInfo(
                        interopPointer,
                        index,
                        out AnalyzerDiagnosticSeverity severity,
                        out int enabledByDefault));
                descriptors.Add(
                    new DiagnosticDescriptor(
                        ReadDescriptorString(
                            interopPointer,
                            index,
                            AnalyzerDescriptorField.Id),
                        ReadDescriptorString(
                            interopPointer,
                            index,
                            AnalyzerDescriptorField.Title),
                        ReadDescriptorString(
                            interopPointer,
                            index,
                            AnalyzerDescriptorField.MessageFormat),
                        ReadDescriptorString(
                            interopPointer,
                            index,
                            AnalyzerDescriptorField.Category),
                        (DiagnosticSeverity)severity,
                        enabledByDefault != 0,
                        description: ReadDescriptorString(
                            interopPointer,
                            index,
                            AnalyzerDescriptorField.Description),
                        helpLinkUri: ReadDescriptorString(
                            interopPointer,
                            index,
                            AnalyzerDescriptorField.HelpLinkUri)));
            }

            return descriptors.MoveToImmutable();
        }
        finally
        {
            AnalyzerAbi.Release(interopPointer);
        }
    }

    private string ReadDescriptorString(
        nint interopPointer,
        int descriptorIndex,
        AnalyzerDescriptorField field)
    {
        ThrowIfFailed(
            _transport.CopyDescriptorStringUtf16(
                interopPointer,
                descriptorIndex,
                field,
                0,
                0,
                out int charCount));
        return string.Create(
            charCount,
            (_transport, interopPointer, descriptorIndex, field),
            static (buffer, state) =>
            {
                fixed (char* bufferPointer = buffer)
                {
                    ThrowIfFailed(
                        state._transport.CopyDescriptorStringUtf16(
                            state.interopPointer,
                            state.descriptorIndex,
                            state.field,
                            (nint)bufferPointer,
                            buffer.Length,
                            out int copiedCharCount));
                    if (copiedCharCount != buffer.Length)
                    {
                        throw new InvalidOperationException(
                            "The analyzer descriptor string changed while being copied.");
                    }
                }
            });
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
