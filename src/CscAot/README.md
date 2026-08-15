# Compiler host

`CscAot` is the NativeAOT `csc`-compatible executable. It
reuses Roslyn's command-line compiler pipeline and replaces managed analyzer
loading with native analyzer module loading.

## Compiler behavior

`RoslynAotCSharpCompiler` derives from Roslyn's internal `CSharpCompiler`, so
normal command-line arguments and response files continue through Roslyn's
existing parsing, compilation, diagnostic, and emit paths. Its analyzer
resolution override interprets `/analyzer:` inputs as native shared libraries.

Each native library is loaded for the compiler process lifetime. The compiler:

1. Resolves the versioned analyzer module entry point.
2. Validates the private ABI version.
3. Enumerates every analyzer transport in the module.
4. Creates one `NativeDiagnosticAnalyzer` proxy per transport.
5. Gives those proxies to Roslyn's normal analyzer driver.

## Roslyn marshalling

Each proxy owns a `RoslynInterop` instance. It provides:

- A generation-checked handle table for compiler-owned Roslyn objects.
- The stable control interface used by analyzer-side facades.
- Lazily created generated dispatchers for typed Roslyn vtables.
- A thread-local remote error category and message.

Generated dispatchers resolve handles, invoke the real Roslyn API, marshal
results back into ABI values or handles, and convert exceptions into explicit
status codes. Analyzer-side facade methods reverse that process and present the
result as ordinary Roslyn-shaped managed APIs.

Analyzer registration calls are translated into Roslyn `AnalysisContext`
registrations. Roslyn callback contexts are converted to handles before entering
the analyzer module, and reported descriptor indexes and source spans are
reconstructed as real compiler-side `Diagnostic` values.

The shared wire contract and ownership rules are documented in
[`RoslynAot.Abi`](../RoslynAot.Abi/README.md).
