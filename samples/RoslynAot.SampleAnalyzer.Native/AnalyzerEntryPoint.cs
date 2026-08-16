using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using RoslynAot.AnalyzerRuntime;
using RoslynAot.SampleAnalyzer;

[assembly: TypeMapAssemblyTarget<RoslynAot.RoslynFacade.RoslynProxyTypeMap>(
    "Microsoft.CodeAnalysis")]
[assembly: TypeMapAssemblyTarget<RoslynAot.RoslynFacade.RoslynProxyTypeMap>(
    "Microsoft.CodeAnalysis.CSharp")]

namespace RoslynAot.SampleAnalyzer.Native;

public static class AnalyzerEntryPoint
{
    private static readonly AnalyzerExport s_export =
        new(new BadClassNameAnalyzer(), new ThrowingAnalyzer());

    [UnmanagedCallersOnly(
        EntryPoint = AnalyzerExport.EntryPoint,
        CallConvs = [typeof(CallConvCdecl)])]
    public static nint GetAnalyzer() => s_export.GetInterface();
}
