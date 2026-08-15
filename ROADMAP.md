# AnalyzeAot roadmap

## Vision

AnalyzeAot will provide a NuGet package that replaces the managed C# compiler
process with a NativeAOT executable and runs analyzers as NativeAOT shared
libraries. The compiler and analyzers communicate through a stable, versioned C
ABI rather than exchanging managed Roslyn objects.

The intended outcomes are:

- Lower compiler and analyzer startup overhead.
- Lower steady-state memory use in short-lived and isolated builds.
- Native analyzer isolation without requiring a separate managed runtime.
- Deterministic, cacheable native artifacts for a given analyzer and toolchain.
- Diagnostics and emitted assemblies equivalent to the standard Roslyn build.

## Fundamental constraint

Independent NativeAOT modules do not share a managed heap or runtime type
identity. A `Compilation`, `SyntaxNode`, `ISymbol`, or analysis context created
inside the compiler cannot safely be passed to an analyzer shared library.

AnalyzeAot therefore uses opaque integer handles and generated
`IUnknown`-compatible interfaces. The compiler owns all Roslyn objects and
analyzers call back into the compiler to query them.

Existing analyzer DLLs remain unchanged. The analyzer-side native module
substitutes public-signed facade assemblies named `Microsoft.CodeAnalysis` and
`Microsoft.CodeAnalysis.CSharp`, with the same assembly version and public-key
token expected by the analyzer. These facades present Roslyn-shaped managed
types whose implementations serialize operations over the C ABI.

The public signature supplies assembly identity only; it is not a Microsoft
signature or authenticity claim. Facades are package tooling inputs under
`tools/`, used only while privately linking analyzer native modules. They must
never be exposed as application compile or runtime assets.

## Target architecture

```text
MSBuild / NuGet package
        |
        | standard csc arguments / response file
        v
NativeAOT csc-compatible stub
  - Microsoft.CodeAnalysis.CSharp command-line parser
  - Roslyn parsing and compilation
  - real DiagnosticAnalyzer proxy
  - compiler-side Roslyn object/handle tables
        |
        | registrations, callbacks, handles, diagnostics
        | versioned IUnknown-compatible C ABI
        v
NativeAOT analyzer module
  - analyzer transport runtime
  - drop-in Microsoft.CodeAnalysis facade assemblies
  - unchanged precompiled analyzer DLL
  - generated bootstrap that instantiates known analyzer types
```

The NuGet package will contain `buildTransitive` targets. These targets configure
the SDK's existing `Csc` task to invoke the NativeAOT stub, build or locate the
appropriate native artifacts, and preserve a managed fallback.

## Compiler strategy

The compiler executable will behave as a `csc` stub rather than introducing a
separate project manifest:

1. MSBuild's existing `Csc` task continues to translate project state into
   compiler switches and response files.
2. The package disables shared compilation and points `CscToolPath` and
   `CscToolExe` at the NativeAOT executable.
3. The stub derives from Roslyn's internal `CSharpCompiler`, which parses the
   standard arguments through `CSharpCommandLineParser` and executes the
   existing `CommonCompiler` pipeline.
4. The stub overrides only analyzer resolution, preserving Roslyn's existing
   source parsing, reference resolution, generators, diagnostics, and emit
   orchestration.
5. An earlier MSBuild preparation target compiles managed analyzer DLLs into
   cached native modules and replaces the `@(Analyzer)` items passed to `Csc`.
   The stub receives native module paths and creates compiler-side
   `DiagnosticAnalyzer` proxies before constructing the compilation.

This avoids reproducing MSBuild's compiler-input normalization and keeps the
stub compatible with existing SDK targets. The Roslyn package exposes the
parser and compiler object model, but not a single supported public entry point
that performs every `csc` driver operation. The stub must still orchestrate
source loading, reference resolution, resources, analyzers, generators,
diagnostic output, signing, PDBs, Source Link, and emitting.

### Internal Csc driver reuse

Roslyn's `CSharpCompiler` contains the complete command-line compilation
pipeline and exposes analyzer resolution as a protected virtual method.
`MockCSharpCompiler` in Roslyn's tests demonstrates the intended extension
shape by overriding `ResolveAnalyzersFromArguments`.

Released `Microsoft.CodeAnalysis` and `Microsoft.CodeAnalysis.CSharp`
assemblies grant internals access to
`Microsoft.CodeAnalysis.CSharp.Test.Utilities`. The compiler driver uses that
friend identity and Roslyn's test public key, with public signing, to derive
from `CSharpCompiler`. Its override converts native `/analyzer:` paths directly
into compiler-side proxy instances and returns them to `CommonCompiler`.

This keeps Roslyn's parsing, resources, generators, diagnostics, signing, PDB,
Source Link, and emit behavior without entering `AnalyzerFileReference` or
performing reflection over native modules. The prototype successfully reports
native analyzer diagnostics and emits portable PDBs from a NativeAOT compiler.

This is intentionally version-coupled to Roslyn internals. The package must pin
or validate compatible SDK compiler versions, and CI must fail clearly if the
friend grant, constructor, or override signature changes.

## ABI generator and ComWrappers

The C ABI will be generated from annotated C# interfaces rather than handwritten
unmanaged vtables. The authored surface should remain readable:

```csharp
[GeneratedComInterface]
[Guid("8d67b782-69fc-41d7-93c6-4c41f841c65c")]
[AbiVersion(1)]
public partial interface IAnalyzerHost
{
    [PreserveSig]
    AbiResult GetRoot(
        CompilationHandle compilation,
        out SyntaxHandle root);

    [PreserveSig]
    AbiResult CopyText(
        SyntaxHandle node,
        AbiBuffer<byte> destination,
        out nuint requiredLength);
}
```

The prototype uses .NET's source-generated COM support and `ComWrappers` as the
runtime substrate. Despite the COM terminology, its core contract is an
`IUnknown`-style vtable ABI. The NativeAOT round trip has been demonstrated on
Linux; Windows and macOS remain to be validated. It provides:

- Runtime implementations of `QueryInterface`, `AddRef`, and `Release`.
- Managed-object to native-interface identity and lifetime tracking.
- Native-interface to managed-proxy identity and lifetime tracking.
- Generated unmanaged vtables, NativeAOT-callable thunks, and managed proxies.
- `ComInterfaceDispatch.GetInstance<T>` dispatch back to managed implementations.

AnalyzeAot will not use COM activation, registration, apartments, automation
types, Windows marshaling, or RCW/CCW APIs from the built-in Windows-only COM
system. Interfaces will be passed directly between loaded native modules.

Our generator and analyzers will add the parts that `ComWrappers` does not
provide:

- Portable C11 headers for native consumers.
- An ABI manifest containing interface IDs, versions, method slots, signatures,
  ownership, and layout hashes.
- Compatibility diagnostics comparing the current manifest with the previous
  published version.
- Restrictions to the portable AnalyzeAot type and ownership model.
- Cross-platform layout and calling-convention validation.

A Roslyn source generator cannot reliably emit arbitrary package files such as C
headers, so header and manifest emission will be an MSBuild-invoked tool. It
will inspect the same interfaces consumed by the .NET COM interface generator.
If the generated COM surface cannot satisfy the portable ABI on all target
platforms, the same interface model can drive custom vtable generation without
changing the authored contracts.

### ABI authoring rules

- Every interface has an explicit immutable 128-bit ID and version.
- Existing method order and signatures are immutable within an interface ID.
  New functionality is introduced with a new derived interface and immutable
  interface ID, following `IUnknown` interface-versioning rules.
- Cross-boundary types are limited to fixed-width integers, explicitly based
  enums, pointers, opaque handles, generated blittable structs, and pointer-plus-
  length spans.
- Strings are UTF-8 buffers with explicit lengths; no `string`, `BSTR`, or
  platform `wchar_t` appears in the ABI.
- Arrays use pointer/count views or enumeration methods; no `SAFEARRAY` or
  managed array crosses the boundary.
- Ownership and lifetime are explicit through attributes such as borrowed,
  retained, consumed, caller-allocated, and callee-allocated.
- Exceptions never cross the ABI. Generated thunks convert them to stable
  AnalyzeAot result codes and report details through an error sink.
- Threading and reentrancy requirements are part of the generated contract.
- Unsupported C# constructs, including generics, `Task`, delegates, reference
  types, and ambiguous platform-sized fields, are generator errors.

The generated ABI deliberately uses the portable subset of the `IUnknown`
contract while avoiding Windows COM infrastructure.

## Design principles

1. **Correctness before performance.** Native compilation must not silently
   change diagnostics, generated code, or emitted assemblies.
2. **No managed objects across the ABI.** All cross-module data uses handles,
   fixed-layout values, UTF-8 buffers, or explicit serialization.
3. **Version every boundary.** Function tables include ABI version and size so
   compatible fields can be added without breaking older analyzers.
4. **Fail explicitly.** Unsupported Roslyn APIs and incompatible analyzers must
   produce actionable build errors.
5. **Make fallback safe.** Projects can return to the standard compiler when a
   platform, analyzer, or language feature is unsupported.
6. **Cache by content.** Native outputs are keyed by analyzer content,
   dependencies, Roslyn version, SDK version, RID, and build configuration.

## Compatibility tiers

Analyzer compatibility will be delivered incrementally:

| Tier | Description |
| --- | --- |
| Facade-compatible | An unchanged analyzer uses only Roslyn APIs implemented by the drop-in facades. |
| Generated wrapper | Package tooling discovers analyzer types and generates the NativeAOT bootstrap automatically. |
| Expanded facade | More syntax, semantic, symbol, operation, option, and additional-file APIs are transported. |
| Rewritten fallback | Assembly-reference or call-site rewriting is available if a dependency cannot bind to the drop-in identity. |
| Managed fallback | Unsupported analyzers continue to run under standard Roslyn. |

The first usable release will support unchanged, facade-compatible analyzer
DLLs. Claims of broad compatibility will wait until representative analyzer
packages pass equivalence tests.

## Milestone 0: prove the runtime model

**Goal:** demonstrate that the core process and module architecture works.

Deliverables:

- NativeAOT Roslyn compiler executable.
- NativeAOT analyzer module containing an unchanged precompiled analyzer.
- Public-signed `Microsoft.CodeAnalysis` facade assemblies.
- A real compiler-side `DiagnosticAnalyzer` proxy.
- Versioned host and analyzer interfaces.
- Opaque syntax handles and UTF-8 buffer methods.
- Diagnostic reporting from analyzer to compiler.
- Compilation of a small C# input into a valid assembly.

Exit criteria:

- The compiler and analyzer publish without unresolved NativeAOT errors.
- The native host loads the analyzer and reports an expected diagnostic.
- The analyzer DLL is first compiled against official Roslyn and is not rebuilt
  or rewritten for the NativeAOT module.
- No managed object reference crosses the ABI.
- The emitted sample assembly can be loaded and executed or inspected.

## Milestone 1: production-quality ABI foundation

**Goal:** establish rules that later API expansion can preserve.

Deliverables:

- Annotated C# ABI interfaces based on `[GeneratedComInterface]`.
- A cross-platform NativeAOT spike using `StrategyBasedComWrappers` in separate
  compiler and analyzer modules, with Linux completed first.
- Generated vtables, thunks, and managed proxies supplied by the .NET COM
  interface generator where viable.
- Build-time generator for C11 headers and compatibility manifests.
- Handle type, ownership, lifetime, and invalidation rules.
- Host allocator or caller-provided-buffer conventions.
- Cancellation, exception, and error-code conventions.
- Thread-safety and analyzer concurrency rules.
- Syntax tree, source text, location, and diagnostic APIs.
- ABI compatibility tests across at least two versions.

Exit criteria:

- C# and C ABI declarations are generated from one authoritative interface
  model.
- Handwritten unmanaged vtables are removed from product code.
- CI rejects method-order, signature, ownership, layout, and interface-ID
  breaks.
- The same interface round-trips across NativeAOT modules on Windows, Linux,
  and macOS.
- Invalid handles and buffer sizes cannot corrupt either module.
- Older analyzers can run against a host with additive ABI changes.
- Analyzer failures are isolated and reported as build failures.

## Milestone 2: MSBuild and NuGet integration

**Goal:** make the prototype usable by adding one package reference.

Deliverables:

- `buildTransitive` props and targets.
- Configuration of `CscToolPath`, `CscToolExe`, and shared-compilation behavior.
- Compatibility with compiler switches and response files emitted by the SDK's
  `Csc` task.
- Analyzer preparation before `Csc`, including native wrapper generation,
  publishing, caching, and replacement of managed `@(Analyzer)` items with
  native module paths.
- RID selection and native artifact discovery.
- Incremental build inputs and outputs.
- Content-addressed native artifact cache.
- Opt-in property and managed compiler fallback.

Exit criteria:

- A sample SDK-style project builds through the NativeAOT `csc` stub without
  replacing the SDK's `Csc` task.
- No rebuild occurs when compiler and analyzer inputs are unchanged.
- `dotnet clean` removes project-local outputs without damaging shared caches.
- Unsupported environments fall back or fail according to an explicit policy.

The package will initially require explicit opt-in before overriding the
compiler path. It will become the default package behavior only after the stub
supports the compiler contract exercised by the test matrix.

## Milestone 3: analyzer SDK

**Goal:** provide a practical API for writing native analyzers.

Deliverables:

- Safe C# wrappers over raw ABI function tables.
- Registration for syntax kinds and compilation events.
- Syntax traversal without copying entire trees.
- Diagnostic descriptors, locations, severities, and properties.
- Analyzer options, additional files, generated-code flags, and cancellation.
- Templates and packaging conventions for NativeAOT analyzer projects.

Exit criteria:

- Multiple syntax-only analyzers can run concurrently.
- An analyzer author normally uses safe wrappers rather than pointers.
- Analyzer packages produce RID-specific native libraries deterministically.
- Native and managed implementations of sample analyzers report equivalent
  diagnostics.

## Milestone 4: semantic analysis

**Goal:** support analyzers that depend on binding and semantic information.

Deliverables:

- Semantic model handles.
- Symbol identity and symbol query APIs.
- Type information, conversions, constants, and declared symbols.
- Operation tree handles and traversal.
- Compilation-wide and symbol analysis callbacks.
- Stable equality and lifetime rules for semantic handles.

Exit criteria:

- Representative semantic analyzers produce equivalent diagnostics.
- Symbol and operation handles remain valid for their documented scope.
- Large solutions do not require serializing complete semantic models.

## Milestone 5: existing analyzer adaptation

**Goal:** run useful existing Roslyn analyzers without manually rewriting each
one.

Work streams:

1. Inventory Roslyn APIs used by representative analyzer packages.
2. Define a supported Roslyn facade mapped onto the ABI.
3. Prototype source generation or source rewriting for analyzers built from
   source.
4. Evaluate IL rewriting for analyzer packages distributed only as assemblies.
5. Detect unsupported reflection, dynamic loading, and runtime code generation.

Exit criteria:

- A documented subset of existing analyzers is translated automatically.
- Unsupported API use is detected before native publishing.
- StyleCop Analyzers, Roslynator, or similarly broad packages run a meaningful
  subset with diagnostic equivalence.

Code fixes and refactorings are outside this milestone because they normally run
inside an IDE host rather than the command-line compiler.

## Milestone 6: compiler compatibility and performance

**Goal:** make NativeAOT compilation a credible alternative to the standard
command-line compiler.

Deliverables:

- Golden tests comparing diagnostics and emitted assembly behavior.
- Multi-project, generated-source, resource, signing, and deterministic-build
  coverage.
- Windows, Linux, and macOS RID matrix.
- Cold-start, throughput, memory, and artifact-size benchmarks.
- Analyzer crash and timeout handling.
- Reproducible native publishing and supply-chain documentation.

Exit criteria:

- Supported projects produce equivalent compiler diagnostics and outputs.
- Performance measurements show a clear target workload where NativeAOT is an
  improvement.
- Failures identify the compiler, ABI, or analyzer component responsible.
- Preview package is safe to enable per project with an immediate fallback.

## Testing strategy

Testing will use four layers:

1. ABI unit tests for layout, versioning, buffers, handles, and failures.
2. Analyzer equivalence tests comparing native and managed diagnostics.
3. Compiler golden tests comparing standard Roslyn and AnalyzeAot outputs.
4. End-to-end MSBuild tests using packed NuGet artifacts and clean machines.

Performance results will always include the cost of native analyzer publishing
separately from cached build performance.

## Major risks

| Risk | Mitigation |
| --- | --- |
| Roslyn uses NativeAOT-incompatible code paths | Keep a warning-free AOT build, test real compilation features early, and avoid unsupported reflection paths. |
| ABI grows into a second Roslyn API | Add APIs from measured analyzer usage and prefer coarse operations where chatty callbacks are expensive. |
| Per-node callbacks are too slow | Batch queries, registration filters, compact snapshots, and shared read-only buffers. |
| An analyzer uses Roslyn APIs absent from the facade | Detect missing members during NativeAOT linking, expand measured API coverage, and retain managed fallback. |
| Drop-in facade assemblies escape into application assets | Keep them under package `tools/`, validate generated project outputs, and fail packaging if they appear under `lib/` or `runtimes/`. |
| Build-time native publishing is too expensive | Use content-addressed caches and optionally distribute precompiled RID assets. |
| Roslyn and SDK version skew breaks compatibility | Include toolchain versions in cache keys and package compatibility metadata. |
| Native analyzer crashes terminate compilation | Investigate optional process isolation after the in-process ABI is proven. |

## Non-goals for the initial releases

- Full compatibility with every existing Roslyn analyzer.
- IDE code fixes, refactorings, or live analysis.
- Passing Roslyn managed objects directly between NativeAOT modules.
- Reimplementing the complete Roslyn object model in the first ABI version.
- Removing the standard managed compiler fallback before equivalence is proven.

## Immediate next steps

1. Generate analyzer-type discovery and NativeAOT wrapper projects from
   existing analyzer DLLs instead of using the handwritten sample bootstrap.
2. Add an explicit compatibility check for the selected SDK Roslyn version,
   friend-assembly grant, and `CSharpCompiler` override signatures.
3. Add an executable managed-versus-native diagnostic equivalence test for the
   unchanged sample analyzer.
4. Validate the working Linux source-generated `ComWrappers` prototype on
   Windows and macOS, then use the result to finalize the ABI generator.
5. Select three representative analyzers and inventory their Roslyn API usage
   to guide ABI priorities.
