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
        InvokeWithHost(host, pointer => _transport.Initialize(pointer));
    }

    internal void InvokeSyntaxNodeAction(
        int actionId,
        SyntaxNodeAnalysisContext context)
    {
        var host = new CompilerAnalyzerHost(this, context);
        InvokeWithHost(
            host,
            pointer => _transport.InvokeSyntaxNodeAction(
                actionId,
                pointer,
                nodeHandle: 0));
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
                    enabledByDefault != 0));
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

    private static void InvokeWithHost(
        CompilerAnalyzerHost host,
        Func<nint, int> operation)
    {
        nint hostPointer =
            s_comWrappers.GetOrCreateComInterfaceForObject(
                host,
                CreateComInterfaceFlags.None);
        try
        {
            ThrowIfFailed(operation(hostPointer));
            GC.KeepAlive(host);
        }
        finally
        {
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
internal sealed unsafe partial class CompilerAnalyzerHost : IAnalyzerHost
{
    private readonly NativeDiagnosticAnalyzer _analyzer;
    private readonly AnalysisContext? _analysisContext;
    private readonly SyntaxNodeAnalysisContext _syntaxContext;
    private readonly bool _hasSyntaxContext;
    private readonly SyntaxSnapshot? _snapshot;

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
        _snapshot = SyntaxSnapshot.Create(syntaxContext.Node);
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

    public int GetRawKind(int handle, out int rawKind)
    {
        if (!TryGetEntry(handle, out SyntaxEntry entry))
        {
            rawKind = 0;
            return AnalyzerAbi.InvalidArgument;
        }

        rawKind = entry.RawKind;
        return AnalyzerAbi.Success;
    }

    public int GetSpanStart(int handle, out int start)
    {
        if (!TryGetEntry(handle, out SyntaxEntry entry))
        {
            start = 0;
            return AnalyzerAbi.InvalidArgument;
        }

        start = entry.Start;
        return AnalyzerAbi.Success;
    }

    public int GetSpanLength(int handle, out int length)
    {
        if (!TryGetEntry(handle, out SyntaxEntry entry))
        {
            length = 0;
            return AnalyzerAbi.InvalidArgument;
        }

        length = entry.Length;
        return AnalyzerAbi.Success;
    }

    public int GetChildCount(int handle, out int count)
    {
        if (!TryGetEntry(handle, out SyntaxEntry entry))
        {
            count = 0;
            return AnalyzerAbi.InvalidArgument;
        }

        count = entry.Children.Length;
        return AnalyzerAbi.Success;
    }

    public int GetChild(int handle, int index, out int child)
    {
        if (!TryGetEntry(handle, out SyntaxEntry entry) ||
            (uint)index >= (uint)entry.Children.Length)
        {
            child = 0;
            return AnalyzerAbi.InvalidArgument;
        }

        child = entry.Children[index];
        return AnalyzerAbi.Success;
    }

    public int CopyTextUtf8(
        int handle,
        nint buffer,
        int bufferLength,
        out int requiredLength)
    {
        if (!TryGetEntry(handle, out SyntaxEntry entry) ||
            bufferLength < 0)
        {
            requiredLength = 0;
            return AnalyzerAbi.InvalidArgument;
        }

        requiredLength = Encoding.UTF8.GetByteCount(entry.Text);
        if (buffer == 0)
        {
            return AnalyzerAbi.Success;
        }

        if (bufferLength < requiredLength)
        {
            return AnalyzerAbi.InvalidArgument;
        }

        Encoding.UTF8.GetBytes(
            entry.Text.AsSpan(),
            new Span<byte>((void*)buffer, bufferLength));
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

    private bool TryGetEntry(int handle, out SyntaxEntry entry)
    {
        if (_snapshot is null)
        {
            entry = default;
            return false;
        }

        return _snapshot.TryGetEntry(handle, out entry);
    }
}

internal sealed class SyntaxSnapshot
{
    private readonly List<SyntaxEntry> _entries;

    private SyntaxSnapshot(List<SyntaxEntry> entries)
    {
        _entries = entries;
    }

    public bool TryGetEntry(int handle, out SyntaxEntry entry)
    {
        if ((uint)handle >= (uint)_entries.Count)
        {
            entry = default;
            return false;
        }

        entry = _entries[handle];
        return true;
    }

    public static SyntaxSnapshot Create(SyntaxNode root)
    {
        var entries = new List<SyntaxEntry>();
        Add(root, entries);
        return new SyntaxSnapshot(entries);
    }

    private static int Add(
        SyntaxNodeOrToken item,
        List<SyntaxEntry> entries)
    {
        int handle = entries.Count;
        entries.Add(default);

        int[] children = item.ChildNodesAndTokens()
            .Select(child => Add(child, entries))
            .ToArray();
        entries[handle] = new SyntaxEntry(
            item.RawKind,
            item.SpanStart,
            item.Span.Length,
            item.ToString(),
            children);
        return handle;
    }
}

internal readonly record struct SyntaxEntry(
    int RawKind,
    int Start,
    int Length,
    string Text,
    int[] Children);
