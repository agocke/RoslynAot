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

Analyzer compatibility is now measured rather than estimated. A differential
harness (`eng/validate-differential.sh`) compiles a corpus through both the
managed and native compilers and diffs the diagnostics, producing a per-rule
burn-down enforced against a checked-in baseline. Two further baselines back
it: per-member boundary call counts, and module size, ILC time, and retained
type count for the whole-assembly module plus four representative
single-analyzer modules.

Those call counts now include the control vtbl, not just the generated
per-member dispatchers. That matters more than it sounds: a single member call
can fan out into many control-vtbl crossings, and leaving them uncounted made
the totals look complete when they were the tip. The control vtbl turns out to
be **70.7% of all boundary traffic**, and the largest single consumer of the
boundary is now `IsObjectType` at 778,492 calls — cast-time type resolution,
which migration Step 4 deliberately left alone.

The honest headline from that measurement: of the module's 37 rules,
**14 pass, 20 fail against a named unimplemented member, and 3 are not yet
exercised by the corpus**. `IOperation.ConstantValue` used to block 7 rules and
is now implemented — the first piece of Step 5's wire grammar, a tagged union
carrying a boxed C# constant by value because the analyzer pattern-matches the
result against real framework types. Three of those seven rules now pass
(CA1802, CA1805, CA1855); the other four moved on to their next blocker, which
is the honest shape of a burn-down. `SyntaxReference.GetSyntax` still blocks 6.
Roughly half of all failures are rooted in generics. Details and the ordered
plan to close them are in the
[analyzer remoting migration plan](docs/ANALYZER-REMOTING-MIGRATION.md).

Two measurement defects were found while doing that work, both of which had
been reporting green. The burn-down's stack-frame parser could not match a
generic method frame at all, so it silently attributed those failures to the
next frame down — analyzer code rather than the Roslyn member. Three rules were
misattributed: CA1508's real blocker is `IOperation.Accept[TArgument,TResult]`,
CA1865's is `SyntaxNode.FirstAncestorOrSelf[TNode]`, and CA2263's is
`SyntaxFactory.SeparatedList[TNode]`. And the projection self-check's facade
client — the only test that drives both sides as separately compiled modules —
has never run: its type map is empty, so its first cast throws. See problem 24.

Casts to derived Roslyn classes now work. `ParseOptions` results cast to
`CSharpParseOptions` — the CA1507 failure, and something the roadmap previously
claimed already worked. Interfaces and classes need opposite strategies here:
an interface cast is interceptable so `IDynamicInterfaceCastable` resolves it
lazily, while a class cast is a plain type check, so the proxy must be
most-derived at construction. That is affordable only because the class family
is small — 13 base classes, 18 derived types, against 498 interfaces. It costs
**+5.0% on the floor module**, because module-initializer registration is
invisible to trimming and roots each registered proxy everywhere; making
registration demand-driven is the follow-up. See problem 3.

Analyzer-constructed visitors now dispatch analyzer-side. An analyzer that
subclasses `OperationWalker` and calls `Visit` was reaching a remoted member on
an object the compiler does not own, which threw on a null control vtbl rather
than naming a missing member — CA1845's failure. `Visit` now resolves the
operation's runtime type in one crossing and calls the matching `VisitXxx`, all
generated from public API rather than declared per member. The shipping module
**shrank 78,832 bytes** as a result, because 129 remoted `VisitXxx` bodies
became one-line local calls. The matching ownership correction is held back:
labelling those two types `Local` regresses three rules from a visible
exception to a silent missing diagnostic, for reasons not yet understood. See
problem 25.

The harness has also earned its keep beyond counting. It caught a defect that
**terminated the compiler process** rather than reporting a failure: a generic
virtual method on a proxied type cannot be dispatched through
`IDynamicInterfaceCastable`, and NativeAOT fails fast in the type loader where
nothing can catch it. Two passing rules went red because an unrelated analyzer
reached that path in the same process — the only failure class that destroys
*other* analyzers' results, and it was two passing rules away from shipping
unnoticed.

That is now fixed: those members are emitted `sealed`, which makes them
non-virtual, so reaching one raises an ordinary `AD0001` and the compilation
completes. Making them work rather than throw is Step 8. See problem 21.

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

- Implement the ownership, identity, runtime-type, recursive-value, callback,
  diagnostic, and lifetime capabilities in the
  [analyzer remoting problem inventory](docs/ANALYZER-REMOTING-PROBLEMS.md),
  in the order set out in the
  [migration plan](docs/ANALYZER-REMOTING-MIGRATION.md).
- Generate analyzer-facing Roslyn transport from composable type-shape rules
  rather than onboarding APIs one member at a time. The projection model is now
  the source of truth for that: every member is keyed by an assembly-qualified
  documentation comment id, deviations live in reviewable tables that must each
  carry a reason, and the generator validates its own output before emitting.
- Exercise every analyzer callback and major Roslyn API family used by
  representative analyzer assemblies.
- Preserve analyzer failures as explicit build diagnostics rather than crashes
  or silent omissions. Unimplemented members now surface as `AD0001` naming the
  analyzer and member, but silent wrong answers remain the harder class: three
  have been found and fixed so far. Two were `object` virtuals that facade
  interfaces structurally cannot occupy. The third was a transport rule rather
  than a member — copying a collection across the boundary preserved its
  elements while discarding its comparer, so `Contains` answered with ordinal
  equality whatever the source used. String collections now cross as handles,
  and the generator refuses to copy any collection whose declared type promises
  membership. See problem 22.
- Account for every type in a projected signature that the projection does not
  own. The boundary can substitute itself for a Roslyn type — it owns that name
  in the analyzer's closure — but never for a framework type the analyzer binds
  to directly, so an instance of one has to be rebuilt and whether the rebuild
  is faithful is a claim about that specific type. There are 73 such types; 21
  are derivably bit-identical and the other 52 are declared with a reason, with
  the build failing on an undeclared one reached by a supported call. Two things
  the measurement surfaced: the keyed collections cannot cross until their
  comparers do, so `ImmutableDictionary` and `Dictionary` are classified
  unrepresentable rather than as clones the build would accept, and
  `CancellationToken` is the largest unimplemented foreign type in the surface
  at 239 uses. See problem 23.
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
3. Work the differential burn-down in the order the migration plan sets out:
   ownership and identity are done, the wire grammar is next. The ranked
   blocking members are the measurement that says which work pays.

   Identity closed with a smaller footprint than expected: the process now
   shares one handle table and canonicalizes both handles and proxies by
   reference, which cut differential-corpus boundary calls 15.6% on its own
   even before the deferred `IAssemblySymbol.NamespaceNames` memoization
   (still 65.8% of all traffic) or class-hierarchy proxy caching. The
   burn-down itself did not move — no rule was blocked on identity — which is
   the expected result, not a shortfall; see migration Step 4.

   Generic virtual dispatch was briefly pulled ahead of identity because it
   killed the compiler rather than reporting. That is fixed — those members are
   sealed, so reaching one raises `AD0001` — and the remaining half, giving them
   a statically implemented shim so they *work* rather than throw, is ordinary
   Step 8 generics work with no claim on priority. Note that generic
   **marshalling** is a separate problem from dispatch: a type argument that
   must be represented in the transport cannot be resolved reflectively, since
   `MakeGenericMethod` and `MakeGenericType` do not work for struct
   instantiations under NativeAOT.
4. Add incremental inputs, outputs, and a content-addressed native artifact
   cache.
5. Expand compiler and analyzer equivalence tests before enabling the native
   path by default. The differential harness is the mechanism; widening it
   means growing the corpus and, at migration Step 6, comparing the diagnostic
   fields the ABI cannot yet carry.
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

The architectural compatibility problems discovered while expanding the C#
NetAnalyzers module are tracked in the
[analyzer remoting problem inventory](docs/ANALYZER-REMOTING-PROBLEMS.md), with
a target architecture in the
[design document](docs/ANALYZER-REMOTING-DESIGN.md) and an order of operations
to reach it in the
[migration plan](docs/ANALYZER-REMOTING-MIGRATION.md). The
[differential harness](tools/RoslynAot.DifferentialHarness/README.md) is what
turns that inventory into a measured list.

## Non-goals

- IDE code fixes, refactorings, or live analysis.
- Passing managed Roslyn objects directly between NativeAOT modules.
- Requiring analyzer authors to target a new public analyzer API.
- Supporting every analyzer before the package can be used for compatible
  projects.
- Treating a specific performance improvement as a correctness requirement.
