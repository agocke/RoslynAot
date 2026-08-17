using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using RoslynAot.Abi;

namespace RoslynAot.Csc;

[GeneratedComClass]
internal sealed partial class RoslynInterop : IRoslynControlVtbl
{
    /// <summary>
    /// The one control interop for the whole compiler process. Every analyzer
    /// registered against every loaded module shares it, which is what
    /// collapses their handle tables into one (migration Step 4) and makes
    /// the COM proxy each analyzer module receives for
    /// <see cref="IRoslynControlVtbl"/> reference-equal across modules loaded
    /// together: <c>GetOrCreateComInterfaceForObject</c> and
    /// <c>GetOrCreateObjectForComInstance</c> both cache by object identity,
    /// so one shared source object yields one native pointer and one managed
    /// proxy per analyzer module. A compiler-owned object shared across
    /// several analyzers — a <c>Compilation</c>, a <c>SyntaxTree</c> — then
    /// resolves to the same proxy wherever it is read from, matching Roslyn's
    /// own reference-equality guarantees instead of breaking them per
    /// analyzer.
    /// </summary>
    internal static RoslynInterop Shared { get; } = new();

    private readonly object _dispatcherGate = new();
    private readonly Dictionary<(long Low, long High), object> _dispatchers = [];
    private readonly StrategyBasedComWrappers _comWrappers = new();
    private readonly ThreadLocal<RemoteError?> _lastError = new();
    private readonly RoslynHandleTable _objects = new();

    internal RoslynHandleTable Objects => _objects;

    public long AddObject<T>(T value)
        where T : class =>
        _objects.AddObject(value);

    public int GetManifestIdentity(
        out long identityLow,
        out long identityHigh)
    {
        identityLow = RoslynAbi.ManifestIdentityLow;
        identityHigh = RoslynAbi.ManifestIdentityHigh;
        return RoslynAbi.Success;
    }

    public int GetVtbl(
        long vtblIdLow,
        long vtblIdHigh,
        out nint vtbl)
    {
        vtbl = 0;
        try
        {
            object? dispatcher;
            lock (_dispatcherGate)
            {
                if (!_dispatchers.TryGetValue(
                        (vtblIdLow, vtblIdHigh),
                        out dispatcher))
                {
                    dispatcher = RoslynDispatcherRegistry.Create(
                        vtblIdLow,
                        vtblIdHigh,
                        this);
                    _dispatchers.Add(
                        (vtblIdLow, vtblIdHigh),
                        dispatcher);
                }
            }

            vtbl = _comWrappers.GetOrCreateComInterfaceForObject(
                dispatcher ?? throw new InvalidOperationException(
                    "The Roslyn vtable dispatcher cache returned null."),
                CreateComInterfaceFlags.None);
            return RoslynAbi.Success;
        }
        catch (Exception exception)
        {
            return SetError(exception);
        }
    }

    public unsafe int CreateSourceTextUtf16(
        nint utf16Text,
        int utf16Length,
        int checksumAlgorithm,
        out long result)
    {
        result = default;
        try
        {
            if (utf16Length < 0 ||
                (utf16Length != 0 && utf16Text == 0))
            {
                throw new ArgumentException(
                    "The UTF-16 source text buffer is invalid.");
            }

            string text = new(
                new ReadOnlySpan<char>(
                    (void*)utf16Text,
                    utf16Length));
            result = _objects.AddObject(
                global::Microsoft.CodeAnalysis.Text.SourceText.From(
                    text,
                    encoding: null,
                    (global::Microsoft.CodeAnalysis.Text.SourceHashAlgorithm)
                    checksumAlgorithm));
            return RoslynAbi.Success;
        }
        catch (Exception exception)
        {
            return SetError(exception);
        }
    }

    public int IsObjectType(
        long handle,
        long vtblIdLow,
        long vtblIdHigh,
        out int isType)
    {
        isType = 0;
        try
        {
            object value = _objects.GetObject(handle);
            isType = RoslynDispatcherRegistry.IsRuntimeType(
                value,
                vtblIdLow,
                vtblIdHigh)
                ? 1
                : 0;
            return RoslynAbi.Success;
        }
        catch (Exception exception)
        {
            return SetError(exception);
        }
    }

    public unsafe int CreateObjectCollection(
        nint handles,
        int count,
        out long result)
    {
        result = default;
        try
        {
            if (count < 0 ||
                (count != 0 && handles == 0))
            {
                throw new ArgumentException(
                    "The object collection handle buffer is invalid.");
            }

            var values = new object[count];
            var source = new ReadOnlySpan<long>((void*)handles, count);
            for (int index = 0; index < values.Length; index++)
            {
                values[index] = _objects.GetObject(source[index]);
            }

            result = _objects.AddObject(values);
            return RoslynAbi.Success;
        }
        catch (Exception exception)
        {
            return SetError(exception);
        }
    }

    public int GetCollectionCount(
        long handle,
        out int count)
    {
        count = default;
        try
        {
            count = _objects.GetObject<Array>(handle).Length;
            return RoslynAbi.Success;
        }
        catch (Exception exception)
        {
            return SetError(exception);
        }
    }

    public int GetObjectCollectionItem(
        long handle,
        int index,
        out long result)
    {
        result = default;
        try
        {
            Array values = _objects.GetObject<Array>(handle);
            object value = values.GetValue(index) ??
                throw new InvalidOperationException(
                    "The Roslyn collection contains null.");
            result = _objects.AddObject(value);
            return RoslynAbi.Success;
        }
        catch (Exception exception)
        {
            return SetError(exception);
        }
    }

    public unsafe int CopyStringCollectionItemUtf16(
        long handle,
        int index,
        nint buffer,
        int bufferLength,
        out int requiredLength)
    {
        requiredLength = default;
        try
        {
            if (bufferLength < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(bufferLength));
            }

            string value = _objects.GetObject<string[]>(handle)[index];
            requiredLength = value.Length;
            if (buffer == 0)
            {
                return RoslynAbi.Success;
            }

            if (bufferLength < requiredLength)
            {
                throw new ArgumentException(
                    "The UTF-16 result buffer is too small.",
                    nameof(bufferLength));
            }

            value.AsSpan().CopyTo(
                new Span<char>((void*)buffer, bufferLength));
            return RoslynAbi.Success;
        }
        catch (Exception exception)
        {
            return SetError(exception);
        }
    }

    public int GetWellKnownObject(
        RoslynWellKnownObject kind,
        out long result)
    {
        result = default;
        try
        {
            object value = kind switch
            {
                RoslynWellKnownObject.SymbolEqualityComparerDefault =>
                    global::Microsoft.CodeAnalysis.SymbolEqualityComparer.Default,
                RoslynWellKnownObject.SymbolEqualityComparerIncludeNullability =>
                    global::Microsoft.CodeAnalysis.SymbolEqualityComparer.IncludeNullability,
                _ => throw new ArgumentOutOfRangeException(nameof(kind)),
            };
            result = _objects.AddObject(value);
            return RoslynAbi.Success;
        }
        catch (Exception exception)
        {
            return SetError(exception);
        }
    }

    public int SymbolEqualityComparerEquals(
        RoslynWellKnownObject kind,
        long x,
        long y,
        out int result)
    {
        result = default;
        try
        {
            global::Microsoft.CodeAnalysis.SymbolEqualityComparer comparer =
                GetSymbolEqualityComparer(kind);
            result = comparer.Equals(
                _objects.GetObject<global::Microsoft.CodeAnalysis.ISymbol>(x),
                _objects.GetObject<global::Microsoft.CodeAnalysis.ISymbol>(y))
                ? 1
                : 0;
            return RoslynAbi.Success;
        }
        catch (Exception exception)
        {
            return SetError(exception);
        }
    }

    public int SymbolEqualityComparerGetHashCode(
        RoslynWellKnownObject kind,
        long symbol,
        out int result)
    {
        result = default;
        try
        {
            result = GetSymbolEqualityComparer(kind).GetHashCode(
                _objects.GetObject<global::Microsoft.CodeAnalysis.ISymbol>(
                    symbol));
            return RoslynAbi.Success;
        }
        catch (Exception exception)
        {
            return SetError(exception);
        }
    }

    private static global::Microsoft.CodeAnalysis.SymbolEqualityComparer
        GetSymbolEqualityComparer(RoslynWellKnownObject kind) =>
        kind switch
        {
            RoslynWellKnownObject.SymbolEqualityComparerDefault =>
                global::Microsoft.CodeAnalysis.SymbolEqualityComparer.Default,
            RoslynWellKnownObject.SymbolEqualityComparerIncludeNullability =>
                global::Microsoft.CodeAnalysis.SymbolEqualityComparer.IncludeNullability,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

    public unsafe int CopyObjectToStringUtf16(
        long handle,
        nint buffer,
        int bufferLength,
        out int requiredLength)
    {
        requiredLength = default;
        try
        {
            if (bufferLength < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(bufferLength));
            }

            string value = _objects.GetObject(handle).ToString() ??
                string.Empty;
            requiredLength = value.Length;
            if (buffer == 0)
            {
                return RoslynAbi.Success;
            }

            if (bufferLength < requiredLength)
            {
                throw new ArgumentException(
                    "The UTF-16 result buffer is too small.",
                    nameof(bufferLength));
            }

            value.AsSpan().CopyTo(
                new Span<char>((void*)buffer, bufferLength));
            return RoslynAbi.Success;
        }
        catch (Exception exception)
        {
            return SetError(exception);
        }
    }

    public int ObjectEquals(
        long handle,
        long other,
        out int result)
    {
        result = default;
        try
        {
            result = _objects.GetObject(handle).Equals(
                _objects.GetObject(other))
                ? 1
                : 0;
            return RoslynAbi.Success;
        }
        catch (Exception exception)
        {
            return SetError(exception);
        }
    }

    public int ObjectGetHashCode(long handle, out int result)
    {
        result = default;
        try
        {
            result = _objects.GetObject(handle).GetHashCode();
            return RoslynAbi.Success;
        }
        catch (Exception exception)
        {
            return SetError(exception);
        }
    }

    public unsafe int CopyLastErrorUtf16(
        nint buffer,
        int bufferLength,
        out int requiredLength,
        out RoslynRemoteErrorKind errorKind)
    {
        RemoteError error = _lastError.Value ??
            new RemoteError(
                RoslynRemoteErrorKind.None,
                string.Empty);
        string message = error.Message;
        errorKind = error.Kind;

        if (bufferLength < 0)
        {
            requiredLength = 0;
            return RoslynAbi.InvalidArgument;
        }

        requiredLength = message.Length;
        if (buffer == 0)
        {
            return RoslynAbi.Success;
        }

        if (bufferLength < requiredLength)
        {
            return RoslynAbi.InvalidArgument;
        }

        message.AsSpan().CopyTo(
            new Span<char>((void*)buffer, bufferLength));
        return RoslynAbi.Success;
    }

    internal int SetError(
        Exception exception,
        [CallerMemberName] string memberName = "")
    {
        RoslynRemoteErrorKind kind;
        int status;
        switch (exception)
        {
            case ArgumentException:
                kind = RoslynRemoteErrorKind.Argument;
                status = RoslynAbi.InvalidArgument;
                break;
            case ObjectDisposedException:
                kind = RoslynRemoteErrorKind.ObjectDisposed;
                status = RoslynAbi.ObjectDisposed;
                break;
            case PlatformNotSupportedException:
                kind = RoslynRemoteErrorKind.Unsupported;
                status = RoslynAbi.Unsupported;
                break;
            case OperationCanceledException:
                kind = RoslynRemoteErrorKind.OperationCanceled;
                status = RoslynAbi.Failure;
                break;
            default:
                kind = RoslynRemoteErrorKind.Failure;
                status = RoslynAbi.Failure;
                break;
        }

        string message = string.IsNullOrEmpty(memberName)
            ? exception.Message
            : $"IRoslynControlVtbl.{memberName}: {exception.Message}";
        _lastError.Value = new RemoteError(kind, message);

        return status;
    }

    private readonly record struct RemoteError(
        RoslynRemoteErrorKind Kind,
        string Message);
}
