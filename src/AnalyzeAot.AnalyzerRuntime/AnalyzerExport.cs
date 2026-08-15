using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Text;
using AnalyzeAot.Abi;
using AnalyzeAot.RoslynFacade;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace AnalyzeAot.AnalyzerRuntime;

public sealed class AnalyzerExport
{
    public const string EntryPoint = AnalyzerAbi.GetAnalyzerEntryPoint;

    private readonly StrategyBasedComWrappers _comWrappers = new();
    private readonly AnalyzerTransport _transport;

    public AnalyzerExport(DiagnosticAnalyzer analyzer)
    {
        _transport = new AnalyzerTransport(analyzer);
    }

    public nint GetInterface() =>
        _comWrappers.GetOrCreateComInterfaceForObject(
            _transport,
            CreateComInterfaceFlags.None);
}

[GeneratedComClass]
internal sealed unsafe partial class AnalyzerTransport : IAnalyzerTransport
{
    private static readonly StrategyBasedComWrappers s_comWrappers = new();

    private readonly DiagnosticAnalyzer _analyzer;
    private readonly ImmutableArray<DiagnosticDescriptor> _descriptors;
    private readonly Dictionary<DiagnosticDescriptor, int> _descriptorIndexes;
    private readonly Dictionary<int, Action<SyntaxNodeAnalysisContext>> _syntaxActions =
        [];
    private int _nextActionId;

    public AnalyzerTransport(DiagnosticAnalyzer analyzer)
    {
        _analyzer = analyzer;
        _descriptors = analyzer.SupportedDiagnostics;
        _descriptorIndexes = _descriptors
            .Select((descriptor, index) => (descriptor, index))
            .ToDictionary(pair => pair.descriptor, pair => pair.index);
    }

    public int GetVersion(out uint version)
    {
        version = AnalyzerAbi.Version;
        return AnalyzerAbi.Success;
    }

    public int GetDescriptorCount(out int count)
    {
        count = _descriptors.Length;
        return AnalyzerAbi.Success;
    }

    public int GetDescriptorInfo(
        int descriptorIndex,
        out AnalyzerDiagnosticSeverity severity,
        out int enabledByDefault)
    {
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

    public int CopyDescriptorStringUtf8(
        int descriptorIndex,
        AnalyzerDescriptorField field,
        nint buffer,
        int bufferLength,
        out int requiredLength)
    {
        if (!TryGetDescriptor(
                descriptorIndex,
                out DiagnosticDescriptor descriptor) ||
            bufferLength < 0 ||
            !Enum.IsDefined(field))
        {
            requiredLength = 0;
            return AnalyzerAbi.InvalidArgument;
        }

        string value = field switch
        {
            AnalyzerDescriptorField.Id => descriptor.Id,
            AnalyzerDescriptorField.Title => descriptor.Title,
            AnalyzerDescriptorField.MessageFormat => descriptor.MessageFormat,
            AnalyzerDescriptorField.Category => descriptor.Category,
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };

        requiredLength = Encoding.UTF8.GetByteCount(value);
        if (buffer == 0)
        {
            return AnalyzerAbi.Success;
        }

        if (bufferLength < requiredLength)
        {
            return AnalyzerAbi.InvalidArgument;
        }

        Encoding.UTF8.GetBytes(
            value.AsSpan(),
            new Span<byte>((void*)buffer, bufferLength));
        return AnalyzerAbi.Success;
    }

    public int Initialize(nint hostPointer)
    {
        if (!TryGetHost(hostPointer, out IAnalyzerHost host))
        {
            return AnalyzerAbi.IncompatibleVersion;
        }

        _analyzer.Initialize(new TransportAnalysisContext(this, host));
        GC.KeepAlive(host);
        return AnalyzerAbi.Success;
    }

    public int InvokeSyntaxNodeAction(
        int actionId,
        nint hostPointer,
        int nodeHandle)
    {
        if (!_syntaxActions.TryGetValue(actionId, out var action) ||
            !TryGetHost(hostPointer, out IAnalyzerHost host))
        {
            return AnalyzerAbi.InvalidArgument;
        }

        SyntaxNode node = FacadeFactory.CreateSyntaxNode(host, nodeHandle);
        SyntaxNodeAnalysisContext context =
            FacadeFactory.CreateSyntaxNodeAnalysisContext(
            node,
            diagnostic => ReportDiagnostic(host, diagnostic));
        action(context);
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
        if (!_descriptorIndexes.TryGetValue(
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

    private sealed class TransportAnalysisContext(
        AnalyzerTransport transport,
        IAnalyzerHost host) : AnalysisContext
    {
        protected override void RegisterSyntaxNodeActionCore(
            Action<SyntaxNodeAnalysisContext> action,
            int[] rawKinds)
        {
            int result = transport.RegisterSyntaxNodeAction(
                host,
                action,
                rawKinds);
            if (result != AnalyzerAbi.Success)
            {
                throw new InvalidOperationException(
                    $"Registering an analyzer action failed with 0x{result:x8}.");
            }
        }
    }
}
