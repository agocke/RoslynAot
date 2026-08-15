using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using RoslynAot.Abi;

namespace RoslynAot.Csc;

[GeneratedComClass]
internal sealed partial class RoslynInterop : IRoslynControlVtbl
{
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

    internal int SetError(Exception exception)
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

        _lastError.Value = new RemoteError(kind, exception.Message);

        return status;
    }

    private readonly record struct RemoteError(
        RoslynRemoteErrorKind Kind,
        string Message);
}
