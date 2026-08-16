using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using RoslynAot.Abi;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace RoslynAot.Csc;

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

    internal void InvokeAction(
        int actionId,
        AnalyzerActionKind actionKind,
        object context)
    {
        var host = new CompilerAnalyzerHost(this, context);
        long contextHandle = actionKind switch
        {
            AnalyzerActionKind.CompilationStart =>
                _roslynInterop.AddObject(
                    (CompilationStartAnalysisContext)context),
            AnalyzerActionKind.Compilation =>
                _roslynInterop.Objects.AddValue(
                    (CompilationAnalysisContext)context),
            AnalyzerActionKind.SyntaxNode =>
                _roslynInterop.Objects.AddValue(
                    (SyntaxNodeAnalysisContext)context),
            AnalyzerActionKind.Operation =>
                _roslynInterop.Objects.AddValue(
                    (OperationAnalysisContext)context),
            AnalyzerActionKind.Symbol =>
                _roslynInterop.Objects.AddValue(
                    (SymbolAnalysisContext)context),
            AnalyzerActionKind.OperationBlock =>
                _roslynInterop.Objects.AddValue(
                    (OperationBlockAnalysisContext)context),
            AnalyzerActionKind.OperationBlockStart =>
                _roslynInterop.AddObject(
                    (OperationBlockStartAnalysisContext)context),
            AnalyzerActionKind.SymbolStart =>
                _roslynInterop.AddObject(
                    (SymbolStartAnalysisContext)context),
            AnalyzerActionKind.SyntaxTree =>
                _roslynInterop.Objects.AddValue(
                    (SyntaxTreeAnalysisContext)context),
            _ => throw new ArgumentOutOfRangeException(nameof(actionKind)),
        };
        InvokeWithHost(
            host,
            (hostPointer, interopPointer) =>
                _transport.InvokeAction(
                actionId,
                actionKind,
                hostPointer,
                interopPointer,
                contextHandle));
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
            int result = invoke(hostPointer, interopPointer);
            if (result != AnalyzerAbi.Success)
            {
                string diagnosticIds = string.Join(
                    ", ",
                    SupportedDiagnostics.Select(
                        static descriptor => descriptor.Id));
                string analyzerError = ReadLastAnalyzerError();
                throw new InvalidOperationException(
                    $"Analyzer transport operation for [{diagnosticIds}] failed with 0x{result:x8}." +
                    (analyzerError.Length == 0
                        ? string.Empty
                        : $"{Environment.NewLine}{analyzerError}"));
            }
            GC.KeepAlive(host);
            GC.KeepAlive(_roslynInterop);
        }
        finally
        {
            AnalyzerAbi.Release(interopPointer);
            AnalyzerAbi.Release(hostPointer);
        }
    }

    private string ReadLastAnalyzerError()
    {
        ThrowIfFailed(
            _transport.CopyLastErrorUtf16(
                0,
                0,
                out int charCount));
        return string.Create(
            charCount,
            _transport,
            static (buffer, transport) =>
            {
                fixed (char* bufferPointer = buffer)
                {
                    ThrowIfFailed(
                        transport.CopyLastErrorUtf16(
                            (nint)bufferPointer,
                            buffer.Length,
                            out int copiedCharCount));
                    if (copiedCharCount != buffer.Length)
                    {
                        throw new InvalidOperationException(
                            "The analyzer failure text changed while being copied.");
                    }
                }
            });
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
    private readonly object _context;

    public CompilerAnalyzerHost(
        NativeDiagnosticAnalyzer analyzer,
        AnalysisContext analysisContext)
    {
        _analyzer = analyzer;
        _context = analysisContext;
    }

    public CompilerAnalyzerHost(
        NativeDiagnosticAnalyzer analyzer,
        object context)
    {
        _analyzer = analyzer;
        _context = context;
    }

    public int GetVersion(out uint version)
    {
        version = AnalyzerAbi.Version;
        return AnalyzerAbi.Success;
    }

    public int RegisterAction(
        int actionId,
        AnalyzerActionKind actionKind,
        int argument)
    {
        try
        {
            return _context switch
            {
                AnalysisContext context =>
                    RegisterAction(
                        context,
                        actionId,
                        actionKind,
                        argument),
                CompilationStartAnalysisContext context =>
                    RegisterAction(
                        context,
                        actionId,
                        actionKind,
                        argument),
                OperationBlockStartAnalysisContext context =>
                    RegisterAction(
                        context,
                        actionId,
                        actionKind,
                        argument),
                SymbolStartAnalysisContext context =>
                    RegisterAction(
                        context,
                        actionId,
                        actionKind,
                        argument),
                _ => AnalyzerAbi.InvalidArgument,
            };
        }
        catch (ArgumentException)
        {
            return AnalyzerAbi.InvalidArgument;
        }
    }

    public unsafe int ReportDiagnostic(
        int descriptorIndex,
        int start,
        int length,
        nint message,
        int messageLength)
    {
        if (!_analyzer.TryGetDescriptor(
                descriptorIndex,
                out DiagnosticDescriptor descriptor) ||
            start < 0 ||
            length < 0 ||
            messageLength < 0 ||
            (message == 0 && messageLength != 0))
        {
            return AnalyzerAbi.InvalidArgument;
        }

        SyntaxTree? tree = GetSourceTree();
        Location location = tree is null
            ? Location.None
            : Location.Create(tree, new TextSpan(start, length));
        string diagnosticMessage = messageLength == 0
            ? string.Empty
            : new string((char*)message, 0, messageLength);
        Diagnostic diagnostic =
            new NativeAnalyzerDiagnostic(
                descriptor,
                location,
                diagnosticMessage);
        switch (_context)
        {
            case CompilationAnalysisContext context:
                context.ReportDiagnostic(diagnostic);
                break;
            case SyntaxNodeAnalysisContext context:
                context.ReportDiagnostic(diagnostic);
                break;
            case OperationAnalysisContext context:
                context.ReportDiagnostic(diagnostic);
                break;
            case SymbolAnalysisContext context:
                context.ReportDiagnostic(diagnostic);
                break;
            case OperationBlockAnalysisContext context:
                context.ReportDiagnostic(diagnostic);
                break;
            case SyntaxTreeAnalysisContext context:
                context.ReportDiagnostic(diagnostic);
                break;
            default:
                return AnalyzerAbi.InvalidArgument;
        }

        return AnalyzerAbi.Success;
    }

    private int RegisterAction(
        AnalysisContext context,
        int actionId,
        AnalyzerActionKind actionKind,
        int argument)
    {
        switch (actionKind)
        {
            case AnalyzerActionKind.CompilationStart:
                context.RegisterCompilationStartAction(
                    value => Invoke(actionId, actionKind, value));
                break;
            case AnalyzerActionKind.Compilation:
                context.RegisterCompilationAction(
                    value => Invoke(actionId, actionKind, value));
                break;
            case AnalyzerActionKind.SyntaxNode:
                context.RegisterSyntaxNodeAction(
                    value => Invoke(actionId, actionKind, value),
                    (SyntaxKind)argument);
                break;
            case AnalyzerActionKind.Operation:
                context.RegisterOperationAction(
                    value => Invoke(actionId, actionKind, value),
                    (OperationKind)argument);
                break;
            case AnalyzerActionKind.Symbol:
                context.RegisterSymbolAction(
                    value => Invoke(actionId, actionKind, value),
                    (SymbolKind)argument);
                break;
            case AnalyzerActionKind.OperationBlock:
                context.RegisterOperationBlockAction(
                    value => Invoke(actionId, actionKind, value));
                break;
            case AnalyzerActionKind.OperationBlockStart:
                context.RegisterOperationBlockStartAction(
                    value => Invoke(actionId, actionKind, value));
                break;
            case AnalyzerActionKind.SymbolStart:
                context.RegisterSymbolStartAction(
                    value => Invoke(actionId, actionKind, value),
                    (SymbolKind)argument);
                break;
            case AnalyzerActionKind.SyntaxTree:
                context.RegisterSyntaxTreeAction(
                    value => Invoke(actionId, actionKind, value));
                break;
            default:
                return AnalyzerAbi.InvalidArgument;
        }

        return AnalyzerAbi.Success;
    }

    private int RegisterAction(
        CompilationStartAnalysisContext context,
        int actionId,
        AnalyzerActionKind actionKind,
        int argument)
    {
        switch (actionKind)
        {
            case AnalyzerActionKind.Compilation:
                context.RegisterCompilationEndAction(
                    value => Invoke(actionId, actionKind, value));
                break;
            case AnalyzerActionKind.SyntaxNode:
                context.RegisterSyntaxNodeAction(
                    value => Invoke(actionId, actionKind, value),
                    (SyntaxKind)argument);
                break;
            case AnalyzerActionKind.Operation:
                context.RegisterOperationAction(
                    value => Invoke(actionId, actionKind, value),
                    (OperationKind)argument);
                break;
            case AnalyzerActionKind.Symbol:
                context.RegisterSymbolAction(
                    value => Invoke(actionId, actionKind, value),
                    (SymbolKind)argument);
                break;
            case AnalyzerActionKind.OperationBlock:
                context.RegisterOperationBlockAction(
                    value => Invoke(actionId, actionKind, value));
                break;
            case AnalyzerActionKind.OperationBlockStart:
                context.RegisterOperationBlockStartAction(
                    value => Invoke(actionId, actionKind, value));
                break;
            case AnalyzerActionKind.SymbolStart:
                context.RegisterSymbolStartAction(
                    value => Invoke(actionId, actionKind, value),
                    (SymbolKind)argument);
                break;
            case AnalyzerActionKind.SyntaxTree:
                context.RegisterSyntaxTreeAction(
                    value => Invoke(actionId, actionKind, value));
                break;
            default:
                return AnalyzerAbi.InvalidArgument;
        }

        return AnalyzerAbi.Success;
    }

    private int RegisterAction(
        OperationBlockStartAnalysisContext context,
        int actionId,
        AnalyzerActionKind actionKind,
        int argument)
    {
        switch (actionKind)
        {
            case AnalyzerActionKind.Operation:
                context.RegisterOperationAction(
                    value => Invoke(actionId, actionKind, value),
                    (OperationKind)argument);
                break;
            case AnalyzerActionKind.OperationBlock:
                context.RegisterOperationBlockEndAction(
                    value => Invoke(actionId, actionKind, value));
                break;
            default:
                return AnalyzerAbi.InvalidArgument;
        }

        return AnalyzerAbi.Success;
    }

    private int RegisterAction(
        SymbolStartAnalysisContext context,
        int actionId,
        AnalyzerActionKind actionKind,
        int argument)
    {
        switch (actionKind)
        {
            case AnalyzerActionKind.SyntaxNode:
                context.RegisterSyntaxNodeAction(
                    value => Invoke(actionId, actionKind, value),
                    (SyntaxKind)argument);
                break;
            case AnalyzerActionKind.Operation:
                context.RegisterOperationAction(
                    value => Invoke(actionId, actionKind, value),
                    (OperationKind)argument);
                break;
            case AnalyzerActionKind.Symbol:
                context.RegisterSymbolEndAction(
                    value => Invoke(actionId, actionKind, value));
                break;
            case AnalyzerActionKind.OperationBlock:
                context.RegisterOperationBlockAction(
                    value => Invoke(actionId, actionKind, value));
                break;
            case AnalyzerActionKind.OperationBlockStart:
                context.RegisterOperationBlockStartAction(
                    value => Invoke(actionId, actionKind, value));
                break;
            default:
                return AnalyzerAbi.InvalidArgument;
        }

        return AnalyzerAbi.Success;
    }

    private void Invoke(
        int actionId,
        AnalyzerActionKind actionKind,
        object context) =>
        _analyzer.InvokeAction(actionId, actionKind, context);

    private SyntaxTree? GetSourceTree() =>
        _context switch
        {
            SyntaxNodeAnalysisContext context =>
                context.Node.SyntaxTree,
            OperationAnalysisContext context =>
                context.Operation.Syntax.SyntaxTree,
            SymbolAnalysisContext context =>
                context.Symbol.Locations.FirstOrDefault(
                    static location => location.IsInSource)?.SourceTree,
            OperationBlockAnalysisContext context =>
                context.OperationBlocks.IsDefaultOrEmpty
                    ? null
                    : context.OperationBlocks[0].Syntax.SyntaxTree,
            SyntaxTreeAnalysisContext context =>
                context.Tree,
            _ => null,
        };
}

internal sealed class NativeAnalyzerDiagnostic(
    DiagnosticDescriptor descriptor,
    Location location,
    string message,
    DiagnosticSeverity? effectiveSeverity = null,
    bool isSuppressed = false) : Diagnostic
{
    public override IReadOnlyList<Location> AdditionalLocations =>
        Array.Empty<Location>();
    public override DiagnosticSeverity DefaultSeverity =>
        descriptor.DefaultSeverity;
    public override DiagnosticDescriptor Descriptor => descriptor;
    public override string Id => descriptor.Id;
    public override bool IsSuppressed => isSuppressed;
    public override Location Location => location;
    public override ImmutableDictionary<string, string?> Properties =>
        ImmutableDictionary<string, string?>.Empty;
    public override DiagnosticSeverity Severity =>
        effectiveSeverity ?? descriptor.DefaultSeverity;
    public override int WarningLevel =>
        Severity == DiagnosticSeverity.Error ? 0 : 1;

    public override bool Equals(Diagnostic? obj) =>
        ReferenceEquals(this, obj);

    public override int GetHashCode() =>
        RuntimeHelpers.GetHashCode(this);

    public override string GetMessage(
        IFormatProvider? formatProvider = null) =>
        message;

    internal override Diagnostic WithLocation(Location newLocation) =>
        new NativeAnalyzerDiagnostic(
            descriptor,
            newLocation,
            message,
            effectiveSeverity,
            isSuppressed);

    internal override Diagnostic WithSeverity(
        DiagnosticSeverity severity) =>
        new NativeAnalyzerDiagnostic(
            descriptor,
            location,
            message,
            severity,
            isSuppressed);

    internal override Diagnostic WithIsSuppressed(bool isSuppressed) =>
        new NativeAnalyzerDiagnostic(
            descriptor,
            location,
            message,
            effectiveSeverity,
            isSuppressed);
}
