# Analyzer runtime

`AnalyzeAot.AnalyzerRuntime` is linked into each native analyzer shared library.
It adapts ordinary Roslyn `DiagnosticAnalyzer` instances to the private
[AnalyzeAot ABI](../AnalyzeAot.Abi/README.md).

Analyzer assemblies remain compiled against the official Roslyn API. At native
publish time they bind to the generated `Microsoft.CodeAnalysis` facade
assemblies instead.

## Analyzer module lifecycle

Generated bootstrap code supplies one lazy `Func<DiagnosticAnalyzer>` for every
analyzer discovered in the input assembly. `AnalyzerExport` exposes those
factories as one `IAnalyzerModule`, and `AnalyzerModule` exposes an independent
`IAnalyzerTransport` for each factory.

Analyzer construction is deliberately lazy. Some analyzers initialize static
Roslyn values in their constructors or type initializers, so the runtime creates
the analyzer only while the compiler's Roslyn facade context is active. The
runtime then caches:

- The analyzer instance.
- Its `SupportedDiagnostics` array.
- The descriptor-to-index map used when reporting diagnostics.

This preserves one logical Roslyn analyzer per compiler-side proxy rather than
combining all analyzers in an assembly into a synthetic analyzer.

## Registration and callbacks

During `Initialize`, the runtime gives the analyzer a local `AnalysisContext`
implementation. Supported registration calls are translated into
`IAnalyzerHost` calls and assigned stable action IDs.

When the compiler invokes an action, the runtime:

1. Receives opaque handles for compiler-owned Roslyn values.
2. Creates facade proxy objects bound to the active Roslyn control interface.
3. Constructs the appropriate analyzer context locally.
4. Runs the analyzer callback.
5. Converts reported diagnostics into descriptor indexes and source spans.

Unsupported registration kinds or facade operations fail explicitly with
`PlatformNotSupportedException`. The compiler surfaces these analyzer failures
as `AD0001` rather than silently omitting analysis.

## Facade values

Analyzer-visible Roslyn values have two forms:

- **Local values** are created and owned inside the analyzer module, such as
  diagnostic descriptors, localized strings, and analyzer contexts.
- **Remote proxies** contain an opaque compiler handle plus generated typed
  vtables used to invoke real Roslyn objects in the compiler module.

`RoslynFacadeRuntime` stores the active control interface in an async-local
scope. Proxies verify that related values use the same control identity before
combining handles in a call.

The generated facade and proxy implementation is produced by the
[Roslyn facade generator](../../tools/AnalyzeAot.RoslynFacadeGenerator/README.md).
