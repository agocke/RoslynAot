using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using AnalyzeAot.AnalyzerRuntime;
using AnalyzeAot.SampleAnalyzer;

[assembly: TypeMapAssemblyTarget<AnalyzeAot.RoslynFacade.RoslynProxyTypeMap>(
    "Microsoft.CodeAnalysis")]
[assembly: TypeMapAssemblyTarget<AnalyzeAot.RoslynFacade.RoslynProxyTypeMap>(
    "Microsoft.CodeAnalysis.CSharp")]

namespace AnalyzeAot.SampleAnalyzer.Native;

public static class AnalyzerEntryPoint
{
    private static readonly AnalyzerExport s_export =
        new(new BadClassNameAnalyzer());

    [UnmanagedCallersOnly(
        EntryPoint = AnalyzerExport.EntryPoint,
        CallConvs = [typeof(CallConvCdecl)])]
    public static nint GetAnalyzer() => s_export.GetInterface();
}
