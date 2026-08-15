# RoslynAot roadmap

## Goal

A user installs the RoslynAot NuGet package and their normal build uses:

- A NativeAOT-compiled C# compiler instead of the standard JIT-compiled compiler
  process.
- NativeAOT-compiled versions of the project's Roslyn analyzers instead of
  loading their managed DLLs into the compiler process.

The project should otherwise build normally. Existing project files, analyzer
packages, compiler arguments, diagnostics, and outputs should continue to work.

NativeAOT may improve startup time and memory use, and the prototype has shown
promising results. Performance is an important reason to evaluate the project,
but the immediate roadmap is simpler: make the package work reliably and
measure the result.

## Intended user experience

The final integration should require only a package reference:

```xml
<PackageReference Include="RoslynAot" Version="..." />
```

The package should then:

1. Select the native compiler for the current platform.
2. Find the analyzers already supplied to the C# compiler.
3. Produce or reuse a cached native module for each analyzer assembly.
4. Pass those native modules to the RoslynAot compiler.
5. Preserve the normal build's diagnostics and outputs.

Users should not need to create native wrapper projects, list analyzer types,
invoke generators, or understand the compiler/analyzer ABI.

## Definition of done

RoslynAot is usable when a representative C# project can add the package and:

- Build through the NativeAOT compiler using the existing MSBuild `Csc` inputs.
- Run its existing analyzer assemblies through generated native modules.
- Produce equivalent diagnostics, assemblies, documentation files, and debug
  information for supported inputs.
- Reuse native compiler and analyzer artifacts on subsequent builds.
- Report unsupported analyzers or compiler features clearly.
- Disable RoslynAot and return to the standard compiler without project
  restructuring.
- Work on supported Windows, Linux, and macOS environments.

Performance measurements should accompany releases, but no particular speedup
is required to prove the integration works.

## Current state

The Linux prototype proves the core path:

- A NativeAOT executable reuses Roslyn's command-line compiler pipeline.
- The compiler loads native analyzer modules and presents each analyzer as a
  real `DiagnosticAnalyzer`.
- Existing analyzers remain compiled against the official Roslyn assemblies.
- Generated facade assemblies and a private native ABI let analyzers call back
  into compiler-owned Roslyn objects without sharing managed objects across
  NativeAOT modules.
- Build-time metadata discovery generates a native module containing all 43 C#
  analyzers from `Microsoft.CodeAnalysis.CSharp.NetAnalyzers.dll`.
- CA1200 produces the expected diagnostic and byte-for-byte equivalent assembly
  and documentation output through that whole-assembly module.
- The simple sample analyzer and the generated multi-analyzer module both
  NativeAOT-publish and expose the expected analyzer count.

The prototype is not yet a transparent package integration. Analyzer transport
coverage is still limited, native analyzer preparation is not wired into normal
builds, caching is incomplete, and only Linux has been validated.

## Remaining work

### Package integration

- Make the package configure the existing `Csc` task to invoke the RoslynAot
  compiler.
- Convert the managed `@(Analyzer)` inputs into native module paths before
  compilation.
- Generate wrapper projects and entry points automatically from analyzer
  assemblies.
- Select the correct native artifacts for the current platform and architecture.
- Define correct incremental MSBuild inputs and outputs.
- Provide a simple opt-out and managed fallback policy.

### Native artifact caching

- Key compiler and analyzer artifacts by all inputs that affect native output.
- Reuse artifacts across no-change and repeated builds.
- Avoid publishing the same analyzer assembly independently for every project.
- Keep project-local cleanup separate from shared cache cleanup.
- Detect stale or incompatible native modules and rebuild them.

### Compiler compatibility

- Exercise the compiler switches and response files used by real projects.
- Preserve resources, signing, PDBs, Source Link, generated sources,
  documentation output, deterministic builds, and diagnostic formatting.
- Detect incompatible Roslyn compiler internals with an actionable error.
- Compare supported builds against the standard compiler in automated tests.

### Analyzer compatibility

- Add the registration kinds required by the analyzers already present in the
  whole-assembly test module.
- Expand syntax, semantic, symbol, operation, options, additional-file, and
  diagnostic transport according to observed analyzer requirements.
- Add safe handle lifetime management for larger compilations.
- Preserve analyzer failures as explicit build diagnostics rather than crashes
  or silent omissions.
- Detect analyzers that cannot run through the native path and apply the
  configured failure or fallback policy.

### Platform and release engineering

- Validate the native module ABI on Windows and macOS as well as Linux.
- Pack all compiler, runtime, facade, generator, and build assets in the NuGet
  package without exposing private facade assemblies as application references.
- Test package installation on clean machines and in multi-project builds.
- Add CI coverage for package creation, end-to-end builds, cache behavior, and
  managed-versus-native equivalence.
- Publish reproducible performance and artifact-size measurements for each
  supported release configuration.

## Near-term priorities

1. Wire analyzer assembly discovery and generated wrapper publication into the
   package's MSBuild targets.
2. Replace managed analyzer items with the resulting native modules and run a
   complete project build through the package.
3. Implement the analyzer registration kinds currently producing `AD0001` in
   the whole C# NetAnalyzers test.
4. Add incremental inputs, outputs, and a content-addressed native artifact
   cache.
5. Expand compiler and analyzer equivalence tests before enabling the native
   path by default.
6. Validate the same packaged workflow on Windows and macOS.

## Core implementation constraint

The compiler executable and analyzer shared libraries are independent
NativeAOT modules. They do not share a managed heap or Roslyn object identity.
Compiler-owned objects therefore cross the module boundary only as opaque
handles through a private generated ABI.

This constraint is fundamental, but most implementation details are not part of
the product roadmap. The
[native ABI](src/RoslynAot.Abi/README.md),
[analyzer runtime](src/RoslynAot.AnalyzerRuntime/README.md),
[C# compiler](src/CscAot/README.md), and
[Roslyn facade generator](tools/RoslynAot.RoslynFacadeGenerator/README.md)
document their own engineering semantics.

## Non-goals

- IDE code fixes, refactorings, or live analysis.
- Passing managed Roslyn objects directly between NativeAOT modules.
- Requiring analyzer authors to target a new public analyzer API.
- Supporting every analyzer before the package can be used for compatible
  projects.
- Treating a specific performance improvement as a correctness requirement.
