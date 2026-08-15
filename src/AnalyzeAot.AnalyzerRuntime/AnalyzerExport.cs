using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using AnalyzeAot.Abi;
using AnalyzeAot.RoslynFacade;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace AnalyzeAot.AnalyzerRuntime;

public sealed class AnalyzerExport
{
    public const string EntryPoint = AnalyzerAbi.GetAnalyzerModuleEntryPoint;

    private readonly StrategyBasedComWrappers _comWrappers = new();
    private readonly AnalyzerModule _module;

    public AnalyzerExport(params DiagnosticAnalyzer[] analyzers)
        : this((analyzers ??
            throw new ArgumentNullException(nameof(analyzers))).Select(
            analyzer =>
            {
                ArgumentNullException.ThrowIfNull(analyzer);
                return (Func<DiagnosticAnalyzer>)(() => analyzer);
            }).ToArray())
    {
    }

    public AnalyzerExport(
        params Func<DiagnosticAnalyzer>[] analyzerFactories)
    {
        ArgumentNullException.ThrowIfNull(analyzerFactories);
        _module = new AnalyzerModule(analyzerFactories);
    }

    public nint GetInterface() =>
        _comWrappers.GetOrCreateComInterfaceForObject(
            _module,
            CreateComInterfaceFlags.None);
}

[GeneratedComClass]
internal sealed partial class AnalyzerModule : IAnalyzerModule
{
    private static readonly StrategyBasedComWrappers s_comWrappers = new();

    private readonly ImmutableArray<AnalyzerTransport> _analyzers;

    public AnalyzerModule(
        IEnumerable<Func<DiagnosticAnalyzer>> analyzerFactories)
    {
        _analyzers = analyzerFactories
            .Select(factory =>
                new AnalyzerTransport(
                    factory ??
                    throw new ArgumentException(
                        "Analyzer factory collections cannot contain null values.",
                        nameof(analyzerFactories))))
            .ToImmutableArray();
        if (_analyzers.IsEmpty)
        {
            throw new ArgumentException(
                "At least one analyzer factory is required.",
                nameof(analyzerFactories));
        }
    }

    public int GetVersion(out uint version)
    {
        version = AnalyzerAbi.Version;
        return AnalyzerAbi.Success;
    }

    public int GetAnalyzerCount(out int count)
    {
        count = _analyzers.Length;
        return AnalyzerAbi.Success;
    }

    public int GetAnalyzer(int analyzerIndex, out nint analyzer)
    {
        if ((uint)analyzerIndex >= (uint)_analyzers.Length)
        {
            analyzer = 0;
            return AnalyzerAbi.InvalidArgument;
        }

        analyzer = s_comWrappers.GetOrCreateComInterfaceForObject(
            _analyzers[analyzerIndex],
            CreateComInterfaceFlags.None);
        return AnalyzerAbi.Success;
    }
}

[GeneratedComClass]
internal sealed unsafe partial class AnalyzerTransport : IAnalyzerTransport
{
    private static readonly StrategyBasedComWrappers s_comWrappers = new();

    private readonly Func<DiagnosticAnalyzer> _analyzerFactory;
    private readonly object _initializationLock = new();
    private DiagnosticAnalyzer? _analyzer;
    private ImmutableArray<DiagnosticDescriptor> _descriptors;
    private Dictionary<DiagnosticDescriptor, int>? _descriptorIndexes;
    private readonly Dictionary<int, Action<SyntaxNodeAnalysisContext>> _syntaxActions =
        [];
    private IRoslynControlVtbl? _roslynControlVtbl;
    private int _nextActionId;

    public AnalyzerTransport(Func<DiagnosticAnalyzer> analyzerFactory)
    {
        _analyzerFactory = analyzerFactory;
    }

    public int GetVersion(out uint version)
    {
        version = AnalyzerAbi.Version;
        return AnalyzerAbi.Success;
    }

    public int GetDescriptorCount(
        nint roslynInteropPointer,
        out int count)
    {
        EnsureAnalyzer(roslynInteropPointer);
        count = _descriptors.Length;
        return AnalyzerAbi.Success;
    }

    public int GetDescriptorInfo(
        nint roslynInteropPointer,
        int descriptorIndex,
        out AnalyzerDiagnosticSeverity severity,
        out int enabledByDefault)
    {
        EnsureAnalyzer(roslynInteropPointer);
        if (!TryGetDescriptor(
                descriptorIndex,
                out DiagnosticDescriptor descriptor))
        {
            severity = default;
            enabledByDefault = 0;
            return AnalyzerAbi.InvalidArgument;
        }

        severity = (AnalyzerDiagnosticSeverity)descriptor.DefaultSeverity;
        enabledByDefault = descriptor.IsEnabledByDefault ? 1 : 0;
        return AnalyzerAbi.Success;
    }

    public unsafe int CopyDescriptorStringUtf16(
        nint roslynInteropPointer,
        int descriptorIndex,
        AnalyzerDescriptorField field,
        nint buffer,
        int bufferLength,
        out int requiredLength)
    {
        EnsureAnalyzer(roslynInteropPointer);
        if (!TryGetDescriptor(
                descriptorIndex,
                out DiagnosticDescriptor descriptor) ||
            bufferLength < 0 ||
            !Enum.IsDefined(field))
        {
            requiredLength = 0;
            return AnalyzerAbi.InvalidArgument;
        }

        string value =
            AnalyzerFacadeFactory.GetDescriptorString(descriptor, field);

        requiredLength = value.Length;
        if (buffer == 0)
        {
            return AnalyzerAbi.Success;
        }

        if (bufferLength < requiredLength)
        {
            return AnalyzerAbi.InvalidArgument;
        }

        value.AsSpan().CopyTo(
            new Span<char>((void*)buffer, bufferLength));
        return AnalyzerAbi.Success;
    }

    public int Initialize(nint hostPointer, nint roslynInteropPointer)
    {
        DiagnosticAnalyzer analyzer =
            EnsureAnalyzer(roslynInteropPointer);
        if (!TryGetHost(hostPointer, out IAnalyzerHost host) ||
            !TryGetRoslynControlVtbl(
                roslynInteropPointer,
                out IRoslynControlVtbl controlVtbl))
        {
            return AnalyzerAbi.IncompatibleVersion;
        }

        using (RoslynFacadeRuntime.Enter(controlVtbl))
        {
            analyzer.Initialize(
                AnalyzerFacadeFactory.CreateAnalysisContext(
                    (action, rawKinds) =>
                    {
                        int result = RegisterSyntaxNodeAction(
                            host,
                            action,
                            rawKinds);
                        if (result != AnalyzerAbi.Success)
                        {
                            throw new InvalidOperationException(
                                $"Registering an analyzer action failed with 0x{result:x8}.");
                        }
                    }));
        }

        GC.KeepAlive(host);
        return AnalyzerAbi.Success;
    }

    public int InvokeSyntaxNodeAction(
        int actionId,
        nint hostPointer,
        nint roslynInteropPointer,
        long nodeHandle)
    {
        if (!_syntaxActions.TryGetValue(actionId, out var action) ||
            !TryGetHost(hostPointer, out IAnalyzerHost host) ||
            !TryGetRoslynControlVtbl(
                roslynInteropPointer,
                out IRoslynControlVtbl controlVtbl))
        {
            return AnalyzerAbi.InvalidArgument;
        }

        SyntaxNode node = RoslynProxyFactory.CreateSyntaxNode(
            controlVtbl,
            nodeHandle);
        SyntaxNodeAnalysisContext context =
            AnalyzerFacadeFactory.CreateSyntaxNodeAnalysisContext(
            node,
            diagnostic => ReportDiagnostic(host, diagnostic));
        using (RoslynFacadeRuntime.Enter(controlVtbl))
        {
            action(context);
        }

        GC.KeepAlive(host);
        return AnalyzerAbi.Success;
    }

    private int RegisterSyntaxNodeAction(
        IAnalyzerHost host,
        Action<SyntaxNodeAnalysisContext> action,
        int[] rawKinds)
    {
        int actionId = _nextActionId++;
        _syntaxActions.Add(actionId, action);
        foreach (int rawKind in rawKinds)
        {
            int result = host.RegisterSyntaxNodeAction(actionId, rawKind);
            if (result != AnalyzerAbi.Success)
            {
                return result;
            }
        }

        return AnalyzerAbi.Success;
    }

    private void ReportDiagnostic(
        IAnalyzerHost host,
        Diagnostic diagnostic)
    {
        if (_descriptorIndexes is null ||
            !_descriptorIndexes.TryGetValue(
                diagnostic.Descriptor,
                out int descriptorIndex))
        {
            throw new InvalidOperationException(
                "The diagnostic descriptor is not in SupportedDiagnostics.");
        }

        int result = host.ReportDiagnostic(
            descriptorIndex,
            diagnostic.Location.SourceSpan.Start,
            diagnostic.Location.SourceSpan.Length);
        if (result != AnalyzerAbi.Success)
        {
            throw new InvalidOperationException(
                $"Reporting a diagnostic failed with 0x{result:x8}.");
        }
    }

    private static bool TryGetHost(
        nint hostPointer,
        out IAnalyzerHost host)
    {
        if (hostPointer == 0)
        {
            host = null!;
            return false;
        }

        host = (IAnalyzerHost)s_comWrappers
            .GetOrCreateObjectForComInstance(
                hostPointer,
                CreateObjectFlags.None);
        return host.GetVersion(out uint version) == AnalyzerAbi.Success &&
            version == AnalyzerAbi.Version;
    }

    private bool TryGetRoslynControlVtbl(
        nint interopPointer,
        out IRoslynControlVtbl controlVtbl)
    {
        if (interopPointer == 0)
        {
            controlVtbl = null!;
            return false;
        }

        try
        {
            IRoslynControlVtbl candidate =
                RoslynFacadeRuntime.GetOrCreateControlVtbl(
                    interopPointer);
            if (_roslynControlVtbl is null)
            {
                _roslynControlVtbl = candidate;
            }
            else if (!ReferenceEquals(_roslynControlVtbl, candidate))
            {
                controlVtbl = null!;
                return false;
            }

            controlVtbl = _roslynControlVtbl;
            return true;
        }
        catch (InvalidOperationException)
        {
            controlVtbl = null!;
            return false;
        }
    }

    private DiagnosticAnalyzer EnsureAnalyzer(nint roslynInteropPointer)
    {
        lock (_initializationLock)
        {
            if (!TryGetRoslynControlVtbl(
                    roslynInteropPointer,
                    out IRoslynControlVtbl controlVtbl))
            {
                throw new InvalidOperationException(
                    "The compiler Roslyn interop interface is invalid.");
            }

            if (_analyzer is not null)
            {
                return _analyzer;
            }

            using (RoslynFacadeRuntime.Enter(controlVtbl))
            {
                DiagnosticAnalyzer analyzer =
                    _analyzerFactory() ??
                    throw new InvalidOperationException(
                        "The analyzer factory returned null.");
                ImmutableArray<DiagnosticDescriptor> descriptors =
                    analyzer.SupportedDiagnostics;
                Dictionary<DiagnosticDescriptor, int> descriptorIndexes =
                    descriptors
                        .Select((descriptor, index) => (descriptor, index))
                        .ToDictionary(
                            pair => pair.descriptor,
                            pair => pair.index);
                _descriptors = descriptors;
                _descriptorIndexes = descriptorIndexes;
                _analyzer = analyzer;
                return analyzer;
            }
        }
    }

    private bool TryGetDescriptor(
        int descriptorIndex,
        out DiagnosticDescriptor descriptor)
    {
        if ((uint)descriptorIndex >= (uint)_descriptors.Length)
        {
            descriptor = null!;
            return false;
        }

        descriptor = _descriptors[descriptorIndex];
        return true;
    }

}
