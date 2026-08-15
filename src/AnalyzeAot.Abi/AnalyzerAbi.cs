using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace AnalyzeAot.Abi;

public static unsafe class AnalyzerAbi
{
    public const uint Version = 1;
    public const string GetAnalyzerEntryPoint = "analyze_aot_get_analyzer_v1";

    public const int Success = 0;
    public const int InvalidArgument = unchecked((int)0x80070057);
    public const int IncompatibleVersion = unchecked((int)0x8007000B);
    public const int Failure = unchecked((int)0x80004005);

    public static uint Release(nint instance)
    {
        if (instance == 0)
        {
            return 0;
        }

        nint* vtable = *(nint**)instance;
        var release = (delegate* unmanaged<nint, uint>)vtable[2];
        return release(instance);
    }
}

public enum AnalyzerDiagnosticSeverity
{
    Hidden,
    Info,
    Warning,
    Error,
}

public enum AnalyzerDescriptorField
{
    Id,
    Title,
    MessageFormat,
    Category,
}

[GeneratedComInterface]
[Guid("274e22df-306d-4b8d-9f55-235e949c7863")]
public partial interface IAnalyzerTransport
{
    [PreserveSig]
    int GetVersion(out uint version);

    [PreserveSig]
    int GetDescriptorCount(out int count);

    [PreserveSig]
    int GetDescriptorInfo(
        int descriptorIndex,
        out AnalyzerDiagnosticSeverity severity,
        out int enabledByDefault);

    [PreserveSig]
    int CopyDescriptorStringUtf8(
        int descriptorIndex,
        AnalyzerDescriptorField field,
        nint buffer,
        int bufferLength,
        out int requiredLength);

    [PreserveSig]
    int Initialize(nint host);

    [PreserveSig]
    int InvokeSyntaxNodeAction(int actionId, nint host, int nodeHandle);
}

[GeneratedComInterface]
[Guid("8d67b782-69fc-41d7-93c6-4c41f841c65c")]
public partial interface IAnalyzerHost
{
    [PreserveSig]
    int GetVersion(out uint version);

    [PreserveSig]
    int RegisterSyntaxNodeAction(int actionId, int rawKind);

    [PreserveSig]
    int GetRawKind(int handle, out int rawKind);

    [PreserveSig]
    int GetSpanStart(int handle, out int start);

    [PreserveSig]
    int GetSpanLength(int handle, out int length);

    [PreserveSig]
    int GetChildCount(int handle, out int count);

    [PreserveSig]
    int GetChild(int handle, int index, out int child);

    [PreserveSig]
    int CopyTextUtf8(
        int handle,
        nint buffer,
        int bufferLength,
        out int requiredLength);

    [PreserveSig]
    int ReportDiagnostic(int descriptorIndex, int start, int length);
}
