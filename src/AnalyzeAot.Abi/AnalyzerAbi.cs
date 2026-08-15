using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace AnalyzeAot.Abi;

public static unsafe class AnalyzerAbi
{
    public const uint Version = 5;
    public const string GetAnalyzerModuleEntryPoint =
        "analyze_aot_get_analyzer_module_v5";

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
    Description,
    HelpLinkUri,
}

[GeneratedComInterface]
[Guid("d9e72345-0901-49d5-b1e4-cdedb34e07ab")]
public partial interface IAnalyzerModule
{
    [PreserveSig]
    int GetVersion(out uint version);

    [PreserveSig]
    int GetAnalyzerCount(out int count);

    [PreserveSig]
    int GetAnalyzer(int analyzerIndex, out nint analyzer);
}

[GeneratedComInterface]
[Guid("d3d4c4ab-e589-4aa6-a23d-713c1782cebf")]
public partial interface IAnalyzerTransport
{
    [PreserveSig]
    int GetVersion(out uint version);

    [PreserveSig]
    int GetDescriptorCount(nint roslynInterop, out int count);

    [PreserveSig]
    int GetDescriptorInfo(
        nint roslynInterop,
        int descriptorIndex,
        out AnalyzerDiagnosticSeverity severity,
        out int enabledByDefault);

    [PreserveSig]
    int CopyDescriptorStringUtf16(
        nint roslynInterop,
        int descriptorIndex,
        AnalyzerDescriptorField field,
        nint buffer,
        int bufferLength,
        out int requiredLength);

    [PreserveSig]
    int Initialize(nint host, nint roslynInterop);

    [PreserveSig]
    int InvokeSyntaxNodeAction(
        int actionId,
        nint host,
        nint roslynInterop,
        long nodeHandle);
}

[GeneratedComInterface]
[Guid("712beacb-d43f-5c8a-a60f-4f00321a5b07")]
public partial interface IAnalyzerHost
{
    [PreserveSig]
    int GetVersion(out uint version);

    [PreserveSig]
    int RegisterSyntaxNodeAction(int actionId, int rawKind);

    [PreserveSig]
    int ReportDiagnostic(int descriptorIndex, int start, int length);
}
