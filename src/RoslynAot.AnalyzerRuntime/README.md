# Analyzer runtime

`RoslynAot.AnalyzerRuntime` is linked into each native analyzer shared library.
It adapts ordinary Roslyn `DiagnosticAnalyzer` instances to the private
[RoslynAot ABI](../RoslynAot.Abi/README.md).

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

Every projected type carries a declared or derived ownership class — `Remote`,
`Value`, `Local`, `Dual`, or `Facade` — which decides how its instances may
cross. In the runtime that shows up as two representations:

- **Local values** are created and owned inside the analyzer module, such as
  diagnostic descriptors, localized strings, and analyzer contexts.
- **Remote proxies** contain an opaque compiler handle plus generated typed
  vtables used to invoke real Roslyn objects in the compiler module.

`Dual` types have both, and a member on one answers locally or remotes
depending on which it is holding. `RoslynFacadeRuntime` stores the active
control interface in an async-local scope; handles themselves are
process-global, so there is no control identity for a call to reconcile.

The generated facade and proxy implementation is produced by the
[Roslyn facade generator](../../tools/RoslynAot.RoslynFacadeGenerator/README.md).
