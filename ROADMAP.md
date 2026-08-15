# AnalyzeAot roadmap

## Vision

AnalyzeAot will provide a NuGet package that replaces the managed C# compiler
process with a NativeAOT executable and runs analyzers as NativeAOT shared
libraries. The compiler and analyzers communicate through a private generated C
ABI rather than exchanging managed Roslyn objects.

The intended outcomes are:

- Lower compiler and analyzer startup overhead.
- Lower steady-state memory use in short-lived and isolated builds.
- Native analyzer isolation without requiring a separate managed runtime.
- Deterministic, cacheable native artifacts for a given analyzer and toolchain.
- Diagnostics and emitted assemblies equivalent to the standard Roslyn build.

## Current implementation status

The Linux prototype now completes a real framework-analyzer vertical slice:

- The generator mirrors the complete public
  `Microsoft.CodeAnalysis` and `Microsoft.CodeAnalysis.CSharp` metadata surface.
- One symbol-driven projection model emits facade bodies, per-type ABI vtables,
  compiler dispatchers, and a deterministic compatibility manifest.
- The current inventory contains 506 per-type vtables and 4,319 supported
  calls; unsupported members retain their API shape and fail explicitly.
- `RoslynInterop` implements only the stable control vtable. Requested typed
  vtables are backed by lazily created per-vtable dispatcher CCWs sharing one
  handle table and error state.
- Polymorphic Roslyn classes are projected as interfaces with per-type creation
  stubs. `IDynamicInterfaceCastable` and .NET's trimmable TypeMap support
  preserve observable derived-type casts without an analyzer-specific global
  runtime-type switch.
- A NativeAOT projection client validates typed vtable lookup, inheritance,
  handles, error isolation, `SyntaxTokenParser`, and UTF-8 string returns across
  the native module boundary.
- `AnalyzeAot.AnalyzerRuntime` now links against the generated facade projects,
  not the handwritten Roslyn shims.
- The unchanged sample analyzer is compiled against official Roslyn, linked
  into a NativeAOT shared library against the generated facades, and reports
  `AA0001` through the NativeAOT compiler.
- The SDK-shipped CA1200 analyzer runs unchanged through the same path,
  including localized descriptors, XML syntax inspection, polymorphic facade
  returns, diagnostic locations, and byte-for-byte equivalent compiler output.
- Native analyzer size is 2.07 MiB for the simple analyzer and 2.86 MiB for
  CA1200. SizeScope identified and removed a 15.3 MiB projection manifest that
  had been embedded as an untrimmable resource in `AnalyzeAot.Abi`.
- With prebuilt managed inputs, a fresh CA1200 NativeAOT publish spends about
  4.3 seconds in the publish/ILC path on the current Linux test machine.
- The NuGet package payload contains the generated facade assemblies under
  `tools/analyzer-runtime/`.

This proves facade binding and one syntax-node analyzer path. It does not yet
establish broad analyzer compatibility: generic transport, most registration
kinds, semantic APIs, array/container projection, and scalable handle release
remain incomplete.

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

The facades are generated independently of any analyzer. For each supported
Roslyn version, tooling mirrors the complete public metadata surface of the
official assemblies. Analyzer preparation happens later and links arbitrary
precompiled analyzers against those already-generated facades.

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
        | private IUnknown-compatible C ABI
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

- Restrictions to the portable AnalyzeAot type and ownership model.
- Cross-platform layout and calling-convention validation.

If the generated COM surface cannot satisfy the transport on all target
platforms, the same interface model can drive custom vtable generation.

### ABI authoring rules

- Every interface has the explicit 128-bit ID required by generated COM.
- The compiler and analyzer endpoints are generated and shipped together.
  Interface IDs, method order, and signatures may change between package
  versions as long as both endpoints and cache keys change together.
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
3. **Build both sides together.** The transport does not provide backward
   compatibility; stale or mismatched native artifacts are rejected and rebuilt.
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
| Facade-binding | An unchanged analyzer binds against a complete, version-specific generated Roslyn facade. |
| Generated wrapper | Package tooling discovers analyzer types and generates the NativeAOT bootstrap automatically. |
| Transport-covered | More syntax, semantic, symbol, operation, option, and additional-file facade members receive working transport implementations. |
| Managed fallback | Unsupported analyzers continue to run under standard Roslyn. |

Every supported Roslyn version has a complete facade metadata surface. Runtime
compatibility is delivered incrementally as mirrored members receive transport
implementations.

## Roslyn facade generation

Hand-authoring the facade is not viable. The public Roslyn surface spans
thousands of types and members across `Microsoft.CodeAnalysis`,
`Microsoft.CodeAnalysis.CSharp`, and related analyzer-facing assemblies.
AnalyzeAot will generate complete version-specific facades from the official
Roslyn binaries without inspecting any analyzer.

The generator must reproduce:

- Assembly name, version, culture, public key, and public signing.
- Namespaces, nested types, accessibility, type kind, inheritance, and
  implemented interfaces.
- Generic parameters and constraints.
- Methods, constructors, properties, events, fields, operators, overloads,
  optional values, `ref` kinds, and custom modifiers.
- Struct and enum layout, underlying types, constants, delegates, and public
  attributes that affect binding or behavior.
- Nullable annotations and other compiler-recognized metadata needed for
  signature fidelity.

Surface generation and behavior generation are separate:

1. The metadata generator emits every public type and member needed to bind
   arbitrary precompiled analyzers.
2. A declarative classification assigns each member an implementation strategy:
   local value behavior, handle-backed query, registration/callback transport,
   serialization, or explicit unsupported failure.
3. Facade bodies and compiler-side transport dispatch are generated from that
   shared classification.
4. Unimplemented members retain the correct metadata shape and fail explicitly
   when called; they must not fail assembly, type, or member binding.

Generation is keyed by the exact Roslyn binary set. Facades, both transport
endpoints, and native cache keys advance together. API compatibility validation
compares official and generated assemblies and fails on any missing or
mismatched public surface.

Reflection compatibility is not an initial goal. Static binding and the
polymorphic Roslyn hierarchies observed through normal `is`, cast, and virtual
interface operations are supported by generated facades and trimmable type
maps. Analyzers that inspect Roslyn through reflection, depend on private
implementation details, dynamically load assemblies, or generate runtime code
may require managed fallback.

## Milestone 0: prove the runtime model

**Goal:** demonstrate that the core process and module architecture works.

**Status:** completed on Linux for the unchanged sample analyzer.

Deliverables:

- NativeAOT Roslyn compiler executable.
- NativeAOT analyzer module containing an unchanged precompiled analyzer.
- Public-signed `Microsoft.CodeAnalysis` facade assemblies.
- A real compiler-side `DiagnosticAnalyzer` proxy.
- Private host and analyzer transport interfaces.
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

**Status:** in progress. Per-type vtables, control identity, generated
dispatchers, handle validation, deterministic manifests, trimmable polymorphic
facades, and cross-NativeAOT validation are implemented on Linux.

Deliverables:

- Annotated C# ABI interfaces based on `[GeneratedComInterface]`.
- A cross-platform NativeAOT spike using `StrategyBasedComWrappers` in separate
  compiler and analyzer modules, with Linux completed first.
- Generated vtables, thunks, and managed proxies supplied by the .NET COM
  interface generator where viable.
- Handle type, ownership, lifetime, and invalidation rules.
- Host allocator or caller-provided-buffer conventions.
- Cancellation, exception, and error-code conventions.
- Thread-safety and analyzer concurrency rules.
- Syntax tree, source text, location, and diagnostic APIs.
- Endpoint agreement tests for generated interface layouts and stale-cache
  rejection.

Exit criteria:

- Both transport endpoints are generated from one authoritative interface
  model.
- Handwritten unmanaged vtables are removed from product code.
- CI rejects endpoint disagreement in method order, signatures, ownership,
  layout, and interface IDs.
- The same interface round-trips across NativeAOT modules on Windows, Linux,
  and macOS.
- Invalid handles and buffer sizes cannot corrupt either module.
- Stale analyzer modules are rejected and rebuilt when the private transport
  changes.
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

## Milestone 5: complete Roslyn facade generation

**Goal:** bind arbitrary precompiled analyzers against generated, version-exact
Roslyn facade assemblies without hand-maintaining the API surface.

**Status:** in progress. Complete facade metadata generation, per-type transport
generation, trimmable polymorphic projection, and one SDK framework analyzer
are working on Linux.

Work streams:

1. Read the complete public metadata surface from official Roslyn assemblies.
2. Generate public-signed facades with matching assembly and member identity.
3. Build compositional signature classification rules and generate facade
   implementations plus compiler-side transport dispatch from one in-memory
   projection model. Keep explicit semantic overrides small and auditable.
4. Verify metadata equivalence with API compatibility and reflection-based
   signature tests.
5. Detect unsupported private implementation dependencies, dynamic loading,
   and runtime code generation.

Exit criteria:

- The generated facade contains every public API from the selected Roslyn
  binary set with matching metadata identity.
- Multiple unchanged analyzer packages bind without assembly rewriting.
- Representative syntax and semantic analyzers execute through generated
  transport implementations.
- Calling a mirrored but unsupported member produces a specific failure and can
  trigger managed fallback.

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

1. ABI unit tests for endpoint layout agreement, buffers, handles, and failures.
2. Analyzer equivalence tests comparing native and managed diagnostics.
3. Compiler golden tests comparing standard Roslyn and AnalyzeAot outputs.
4. End-to-end MSBuild tests using packed NuGet artifacts and clean machines.

Performance results will separate managed input generation, fresh NativeAOT
publishing from already-built IL, no-change publishing, and cached compilation
latency. Source-tree `dotnet clean` measurements must not be reported as analyzer
ILC cost when production consumes pregenerated facade, ABI, and analyzer IL.

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

1. Add automated API compatibility validation between official Roslyn and the
   generated facade assemblies.
2. Generate analyzer-type discovery and NativeAOT wrapper projects from
   existing analyzer DLLs instead of using the handwritten sample bootstrap.
3. Add release or arena lifetime management for non-disposable handles before
   exercising large syntax trees and broad analyzers.
4. Implement measured container and callback shapes, beginning with
   `ImmutableArray<T>` and additional analyzer registration kinds.
5. Add an explicit compatibility check for the selected SDK Roslyn version,
   friend-assembly grant, and `CSharpCompiler` override signatures.
6. Expand the executable managed-versus-native equivalence suite beyond CA1200
   to representative syntax, symbol, semantic, and operation analyzers.
7. Split generated COM declarations into compiler CCW-only and analyzer
   RCW-only forms to improve NativeAOT trimming.
8. Validate the working Linux source-generated `ComWrappers` prototype on
   Windows and macOS, then use the result to finalize the ABI generator.
9. Add correct MSBuild `Inputs` and `Outputs` for compiler and analyzer native
   publishing, including all generated and managed inputs.
10. Use SizeScope to reduce the remaining approximately 0.87 MiB above an empty
    NativeAOT shared library, with particular attention to CoreLib reflection
    and TypeLoader costs introduced by dynamic interface projection.
