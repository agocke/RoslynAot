using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using AnalyzeAot.AnalyzerRuntime;
using Microsoft.CodeQuality.CSharp.Analyzers.Documentation;

[assembly: TypeMapAssemblyTarget<AnalyzeAot.RoslynFacade.RoslynProxyTypeMap>(
    "Microsoft.CodeAnalysis")]
[assembly: TypeMapAssemblyTarget<AnalyzeAot.RoslynFacade.RoslynProxyTypeMap>(
    "Microsoft.CodeAnalysis.CSharp")]

namespace AnalyzeAot.CA1200Analyzer.Native;

public static class AnalyzerEntryPoint
{
    private static readonly AnalyzerExport s_export =
        new(new CSharpAvoidUsingCrefTagsWithAPrefixAnalyzer());

    [UnmanagedCallersOnly(
        EntryPoint = AnalyzerExport.EntryPoint,
        CallConvs = [typeof(CallConvCdecl)])]
    public static nint GetAnalyzer() => s_export.GetInterface();
}
