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
        RoslynCallCounters.Record(
            RoslynCallCounters.ControlGetManifestIdentity);
        identityLow = RoslynAbi.ManifestIdentityLow;
        identityHigh = RoslynAbi.ManifestIdentityHigh;
        return RoslynAbi.Success;
    }

    public int GetVtbl(
        long vtblIdLow,
        long vtblIdHigh,
        out nint vtbl)
    {
        RoslynCallCounters.Record(
            RoslynCallCounters.ControlGetVtbl);
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
        RoslynCallCounters.Record(
            RoslynCallCounters.ControlCreateSourceTextUtf16);
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

    /// <summary>
    /// Mints the compiler-side source standing in for an analyzer's token.
    /// </summary>
    /// <remarks>
    /// The source, not the token, is what the handle names: a token is a
    /// readonly view over it, so holding the source is what lets
    /// <see cref="CancelCancellationTokenSource"/> move it later. Roslyn
    /// receives <c>source.Token</c>, an ordinary token over an ordinary
    /// source, and cannot tell it from the driver's own.
    /// </remarks>
    public int CreateCancellationTokenSource(out long result)
    {
        RoslynCallCounters.Record(
            RoslynCallCounters.ControlCreateCancellationTokenSource);
        result = default;
        try
        {
            result = _objects.AddObject(new CancellationTokenSource());
            return RoslynAbi.Success;
        }
        catch (Exception exception)
        {
            return SetError(exception);
        }
    }

    /// <summary>
    /// Delivers the one edge a remoted cancellation token carries.
    /// </summary>
    /// <remarks>
    /// Idempotent because <see cref="CancellationTokenSource.Cancel()"/> is,
    /// which is what makes a duplicated or replayed edge harmless rather than
    /// something the transport has to deduplicate.
    ///
    /// <see cref="CancellationTokenSource.Cancel()"/> runs Roslyn's own
    /// registrations synchronously on this thread and wraps their failures in
    /// an <see cref="AggregateException"/>. That is caught here like any other
    /// failure and reported through the boundary rather than left to unwind
    /// into the analyzer's call to <c>Cancel</c>, where it would be attributed
    /// to the analyzer.
    /// </remarks>
    public int CancelCancellationTokenSource(long handle)
    {
        RoslynCallCounters.Record(
            RoslynCallCounters.ControlCancelCancellationTokenSource);
        try
        {
            _objects.GetObject<CancellationTokenSource>(handle).Cancel();
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
        RoslynCallCounters.Record(
            RoslynCallCounters.ControlIsObjectType);
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
        RoslynCallCounters.Record(
            RoslynCallCounters.ControlCreateObjectCollection);
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
        RoslynCallCounters.Record(
            RoslynCallCounters.ControlGetCollectionCount);
        count = default;
        try
        {
            // Object collections still cross as arrays; string collections
            // now cross as the live Roslyn collection, so both shapes reach
            // here and neither may be assumed.
            count = _objects.GetObject<object>(handle) switch
            {
                Array array => array.Length,
                ICollection<string> strings => strings.Count,
                System.Collections.ICollection collection => collection.Count,
                var other => throw new InvalidOperationException(
                    $"The Roslyn handle refers to '{other.GetType()}', " +
                    "which is not a countable collection."),
            };
            return RoslynAbi.Success;
        }
        catch (Exception exception)
        {
            return SetError(exception);
        }
    }

    public int StringCollectionContains(
        long handle,
        string value,
        out int result)
    {
        RoslynCallCounters.Record(
            RoslynCallCounters.ControlStringCollectionContains);
        result = default;
        try
        {
            // The whole point of the handle: this is the collection Roslyn
            // built, so Contains runs against its comparer rather than the
            // ordinal default a copied array would impose.
            result = _objects.GetObject<ICollection<string>>(handle)
                .Contains(value)
                ? 1
                : 0;
            return RoslynAbi.Success;
        }
        catch (Exception exception)
        {
            return SetError(exception);
        }
    }

    public int SnapshotStringCollection(
        long handle,
        out long result)
    {
        RoslynCallCounters.Record(
            RoslynCallCounters.ControlSnapshotStringCollection);
        result = default;
        try
        {
            result = _objects.AddObject(
                _objects.GetObject<IEnumerable<string>>(handle).ToArray());
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
        RoslynCallCounters.Record(
            RoslynCallCounters.ControlGetObjectCollectionItem);
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
        RoslynCallCounters.Record(
            RoslynCallCounters.ControlCopyStringCollectionItemUtf16);
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
        RoslynCallCounters.Record(
            RoslynCallCounters.ControlGetWellKnownObject);
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
        RoslynCallCounters.Record(
            RoslynCallCounters.ControlSymbolEqualityComparerEquals);
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
        RoslynCallCounters.Record(
            RoslynCallCounters.ControlSymbolEqualityComparerGetHashCode);
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
        RoslynCallCounters.Record(
            RoslynCallCounters.ControlCopyObjectToStringUtf16);
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

    /// <summary>
    /// Writes a boxed C# constant onto the wire as a tag plus two words of
    /// payload.
    /// </summary>
    /// <remarks>
    /// Two words rather than one because <see cref="decimal"/> is sixteen
    /// bytes; truncating it would be a silent wrong answer in the one
    /// transport whose entire job is carrying constants exactly.
    ///
    /// The default arm is the degradation, and it throws rather than guesses.
    /// Roslyn uses <c>object</c> in return position for constants, but the
    /// projection's structural rule admits a few members that return something
    /// else — <c>AnalyzerReference.Id</c> and two <c>IEnumerator.Current</c>
    /// explicit implementations. Naming the runtime type in the message means
    /// those raise an <c>AD0001</c> a reader can act on, which is what they did
    /// when the whole member was unsupported. Answering <c>null</c> or a
    /// handle instead would be problem 22's failure shape: a wrong answer with
    /// nothing to notice.
    /// </remarks>
    internal void WriteConstant(
        object? value,
        out RoslynConstantKind kind,
        out long low,
        out long high)
    {
        low = 0;
        high = 0;
        switch (value)
        {
            case null:
                kind = RoslynConstantKind.Null;
                return;
            case bool typed:
                kind = RoslynConstantKind.Boolean;
                low = typed ? 1 : 0;
                return;
            case sbyte typed:
                kind = RoslynConstantKind.SByte;
                low = typed;
                return;
            case byte typed:
                kind = RoslynConstantKind.Byte;
                low = typed;
                return;
            case short typed:
                kind = RoslynConstantKind.Int16;
                low = typed;
                return;
            case ushort typed:
                kind = RoslynConstantKind.UInt16;
                low = typed;
                return;
            case int typed:
                kind = RoslynConstantKind.Int32;
                low = typed;
                return;
            case uint typed:
                kind = RoslynConstantKind.UInt32;
                low = typed;
                return;
            case long typed:
                kind = RoslynConstantKind.Int64;
                low = typed;
                return;
            case ulong typed:
                kind = RoslynConstantKind.UInt64;
                low = unchecked((long)typed);
                return;
            case char typed:
                kind = RoslynConstantKind.Char;
                low = typed;
                return;
            case float typed:
                kind = RoslynConstantKind.Single;
                low = BitConverter.SingleToInt32Bits(typed);
                return;
            case double typed:
                kind = RoslynConstantKind.Double;
                low = BitConverter.DoubleToInt64Bits(typed);
                return;
            case decimal typed:
                kind = RoslynConstantKind.Decimal;
                int[] bits = decimal.GetBits(typed);
                low = (uint)bits[0] | ((long)bits[1] << 32);
                high = (uint)bits[2] | ((long)bits[3] << 32);
                return;
            case DateTime typed:
                kind = RoslynConstantKind.DateTime;
                low = typed.ToBinary();
                return;
            case string typed:
                kind = RoslynConstantKind.String;
                low = _objects.AddObject(typed);
                return;
            default:
                throw new PlatformNotSupportedException(
                    "This Roslyn API returned " +
                    $"'{value.GetType().FullName}', which RoslynAot does not " +
                    "carry as a constant value.");
        }
    }

    public int GetObjectRuntimeVtblId(
        long handle,
        out long vtblIdLow,
        out long vtblIdHigh)
    {
        RoslynCallCounters.Record(
            RoslynCallCounters.ControlGetObjectRuntimeVtblId);
        vtblIdLow = default;
        vtblIdHigh = default;
        try
        {
            RoslynDispatcherRegistry.TryGetRuntimeVtblId(
                _objects.GetObject(handle),
                out vtblIdLow,
                out vtblIdHigh);
            return RoslynAbi.Success;
        }
        catch (Exception exception)
        {
            return SetError(exception);
        }
    }

    public unsafe int CopyConstantStringUtf16(
        long handle,
        nint buffer,
        int bufferLength,
        out int requiredLength)
    {
        RoslynCallCounters.Record(
            RoslynCallCounters.ControlCopyConstantStringUtf16);
        requiredLength = default;
        try
        {
            if (bufferLength < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(bufferLength));
            }

            string value = _objects.GetObject<string>(handle);
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
        RoslynCallCounters.Record(
            RoslynCallCounters.ControlObjectEquals);
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
        RoslynCallCounters.Record(
            RoslynCallCounters.ControlObjectGetHashCode);
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
        RoslynCallCounters.Record(
            RoslynCallCounters.ControlCopyLastErrorUtf16);
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
