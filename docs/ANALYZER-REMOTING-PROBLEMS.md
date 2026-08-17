# Analyzer remoting problem inventory

## Scope

RoslynAot must support unchanged analyzer assemblies compiled into independent
NativeAOT modules. The compiler and each analyzer module have separate managed
heaps and NativeAOT runtimes, so analyzer compatibility requires a transparent
remoting implementation of the Roslyn APIs analyzers consume.

“Arbitrary analyzers” means analyzers that are compatible with NativeAOT and
the supported Roslyn API version. Analyzers that fundamentally require dynamic
code generation, unsupported reflection, or runtime assembly loading need a
clear compatibility failure or fallback policy.

This document records problems already encountered. It is an inventory of
required capabilities, not an implementation plan.

## Problems to solve

### 1. Roslyn API usage is transitively large

Supporting an entry-point type such as `Compilation`, `ISymbol`, or
`IOperation` is not enough. Every returned object, collection element, method
parameter, utility type, and callback context expands the reachable API.
Analyzer utility libraries also use deeper Roslyn APIs than analyzer source
alone suggests.

Examples encountered:

- `Compilation` itself could be proxied, but `Compilation.SyntaxTrees` required
  generic collection transport.
- CA1870 required `IMethodSymbol.Construct(ITypeSymbol[])`.
- CA1845 required `INamedTypeSymbol.Construct(ITypeSymbol[])`.
- CA1508 entered Roslyn data-flow analysis and depended on stable operation and
  symbol identity.

The projection generator needs to compute and support this transitive type
closure rather than onboarding isolated members.

**Measured (2026-08-17, migration Step 2).** The closure now exists in the
model, and it is larger than this framing suggests: **609 of 663 projected
types are reachable from the analyzer-facing roots**. The 54 that are not are
source generators, command-line parsing, `RuleSet`, analyzer references, and
the diagnostic formatters. So the transitive surface is not a set to be
narrowed — it is very nearly all of Roslyn, and the work is transporting it,
not selecting it.

### 2. Compiler object identity must be canonical

Returning a new analyzer-side proxy for every occurrence of a compiler object
breaks reference-based caches, dictionaries, sets, and graph algorithms.
Returning a new compiler handle for the same object creates the same problem
one layer earlier.

CA1508 exposed this when Roslyn data-flow dictionaries could not find an
operation key represented by another proxy instance.

Required semantics:

- A compiler object has a stable handle within its lifetime.
- A `(control identity, handle)` maps to one canonical analyzer proxy.
- Proxy equality and hashing remain stable.
- Different compiler control identities never accidentally share proxies.

**Partial status (2026-08-16).** The *semantics* are now correct: `ObjectEquals`
and `ObjectGetHashCode` on `IRoslynControlVtbl` forward `Equals`/`GetHashCode`
to the compiler-side object, and `RoslynObjectProxy` overrides both. Before
this, neither was overridden, so proxies fell back to reference equality while
`RoslynHandleTable.Add` minted a fresh handle per call — every set, dictionary,
and cache keyed on a projected object silently missed. Measured cost: six rules
(CA1851, CA1870, CA2352–CA2355) reported nothing at all, because
`InsecureDeserializationTypeDecider` and friends key on a default-comparer
`HashSet<ITypeSymbol>`.

Still open, and the reason this problem stays on the list: handles are not
stable, proxies are not canonical, and nothing is cached across the boundary,
so equality and hashing are now correct but cost an ABI round trip each. The
graph-algorithm and cache-locality half of this problem is unaddressed.

### 3. Runtime types and polymorphism must be preserved

A declared return type is often less specific than the actual Roslyn object.
Creating only the declared proxy breaks casts and derived API access.

Examples encountered:

- A `ParseOptions` result needed to remain castable to `CSharpParseOptions`.
- Dynamic interface proxies needed to support inherited interfaces such as
  `IEquatable<ISymbol>`.
- Collection elements declared as base Roslyn types can contain multiple
  concrete runtime types.

The bridge needs runtime type identification and most-derived proxy
construction, including complete interface maps.

### 4. Ownership must be explicit

Not every Roslyn-shaped object has the same owner:

- Compiler-owned semantic objects must remain remote handles.
- Analyzer-created objects such as descriptors and diagnostics are local.
- Callback contexts are local wrappers over compiler-owned state.
- Some objects, such as locations and comparers, can be either local or remote.

Mixing these representations caused local objects to execute generated remote
members without a control vtable. Every projected type needs defined ownership,
construction, and local-versus-remote behavior.

### 5. The ABI needs a recursive transport type system

The current transport grew one shape at a time. Arbitrary analyzer support
requires a small, composable set of representations from which signatures can
be generated recursively.

Required shapes include:

- Primitives, enums, strings, and nullable values.
- Compiler object references.
- Arrays and `params` arrays.
- `ImmutableArray<T>`, enumerable interfaces, and Roslyn list types.
- `Optional<T>` and other tagged values.
- Roslyn value types and discriminated values such as constants.
- Nested combinations of these shapes.

Unsupported generic substitutions cannot remain the default for public
analyzer-facing Roslyn APIs.

### 6. Roslyn value types need real semantics

Many Roslyn structs are not interchangeable with an opaque object handle.
Their default value, equality, iteration, and field-like behavior are
observable.

Examples include:

- `Optional<T>`
- `TypedConstant`
- `TextSpan` and line-position types
- `SyntaxToken` and `SyntaxNodeOrToken`
- `SeparatedSyntaxList<T>` and its enumerator
- Immutable arrays, including the distinction between default and empty

The bridge needs a consistent rule for snapshotting, proxying, or explicitly
implementing each value shape.

### 7. Collection semantics must be defined

Collection transport must preserve:

- Element ordering and runtime types.
- Null elements where permitted.
- Default-versus-empty state where observable.
- Snapshot-versus-live behavior.
- Enumeration behavior and repeated enumeration.
- Collection-handle lifetime.

String arrays, Roslyn object arrays, immutable arrays, syntax lists, and
enumerables should be instances of shared transport rules rather than separate
member-specific implementations.

### 8. Arrays and overloads must be generated correctly

Analyzer APIs frequently expose array and `params` overloads. Missing array
transport blocked symbol construction and initially blocked analyzer action
registration.

Overloaded members also exposed declaration-to-symbol matching bugs, including
incorrectly generated `GetTypeMembers` bodies. Generated operations must use
canonical signatures, not adjacency or name-only matching.

**Partly addressed (2026-08-17, migration Step 2).** Every model entry is keyed
by canonical id, so a rule can no longer apply to the wrong overload, and the
three `GetTypeMembers` overloads are now withdrawn by an explicit model entry
carrying that reason rather than by a name check inside the validator. The
overloads themselves are still withdrawn, not fixed: that needs overload
identity to reach the vtbl slot.

### 9. Equality and hashing have multiple meanings

The bridge must distinguish:

- Remote object identity.
- Roslyn semantic symbol equality.
- Value equality for Roslyn structs.
- Analyzer-local object identity.

`SymbolEqualityComparer.Default` and `IncludeNullability` cannot be remote
singletons bound permanently to one compiler identity. Their analyzer-side
facades must dispatch each comparison through the symbols’ active compiler
identity. `ISymbol.Equals` overloads must use the supplied comparer rather than
trying to marshal the comparer as a compiler object.

### 10. Null and default values are part of the contract

Metadata nullability does not fully describe runtime behavior.

Examples encountered:

- `ISymbol.ContainingSymbol` returns null at the root despite its nominal
  annotation.
- `[MaybeNull]` returns need null-handle support.
- Optional and immutable value types distinguish default from populated state.
- Collection elements and analyzer context properties have their own null
  rules.

The generator must consider attributes and known Roslyn semantics in addition
to `NullableAnnotation`.

### 11. Every analyzer callback kind needs equivalent behavior

Arbitrary analyzers can register compilation, syntax, semantic-model, symbol,
operation, code-block, operation-block, syntax-tree, additional-file, start,
and end actions. Start actions can register nested actions.

The bridge must preserve:

- Registration arguments such as syntax, symbol, and operation kinds.
- Generic language-kind APIs.
- Nested registration scope.
- End-action timing.
- Generated-code and concurrent-execution configuration.

Initialization succeeding does not imply that the registered callbacks work.

### 12. Callback contexts need complete usable state

Each action context exposes more than its primary symbol, node, or operation.
Analyzers have already required:

- `Compilation`
- `AnalyzerOptions`
- `CancellationToken`
- Containing symbols and semantic models
- Operation blocks and control-flow graphs
- Syntax trees and parse options
- Diagnostic reporting

Returning `CancellationToken.None` may be a temporary compatibility measure,
but real cancellation and all context-specific state need defined transport.

### 13. Analyzer-created values must cross back correctly

Analyzers create values that the compiler consumes or that are passed back into
compiler-owned APIs. These cannot be treated as compiler handles unless the
compiler created them.

Examples include:

- `DiagnosticDescriptor`
- `Diagnostic`
- `Location`
- `SymbolDisplayFormat`
- `SyntaxAnnotation`
- `SourceText`

Each such type needs local semantics, serialization into a compiler operation,
or an explicit compiler-side construction operation.

### 14. Diagnostic transport must preserve full semantics

Transporting only an ID, formatted message, and primary span is insufficient.
The CA1200 diagnostic was initially lost because the compiler’s configured
severity transformation called `WithSeverity`, which returned the unchanged
diagnostic.

Required diagnostic state includes:

- Effective and default severity.
- Suppression state and warning level.
- Primary and additional locations.
- Properties.
- Message arguments and formatting.
- Descriptor metadata, help links, and custom tags.

Compiler-side `WithSeverity`, `WithLocation`, and `WithIsSuppressed` operations
must behave like real Roslyn diagnostics.

`Location.None` is one of the states that does not survive today. Observed
2026-08-16 while probing the silent no-op class: a probe analyzer reporting at
`Location.None` produced an unlocated diagnostic under managed csc and one
attributed to `<first source file>(1,1)` under `csc-aot`. No corpus rule
reports at `Location.None`, so the differential burn-down does not currently
show it as a `SpanMismatch`.

### 15. Error reporting must cross the boundary with context

An HRESULT alone hid the unsupported API that caused an analyzer failure.
Failures need to retain:

- Analyzer type and diagnostic IDs.
- Initialization or action kind.
- Exception type, message, and stack trace.
- Relevant compilation, symbol, operation, node, and tree context.

The compiler must surface failures as explicit `AD0001` diagnostics or a
configured compatibility failure. Silent omission is never acceptable.

### 16. Lifetime, concurrency, and reentrancy need a model

Handles currently tend to accumulate, while analyzer statics can outlive one
compilation or compiler control identity. Concurrent analyzer execution and
nested callbacks add further pressure.

The design needs:

- Scoped handle ownership and release.
- Safe collection and value-handle lifetimes.
- Thread-safe proxy and dispatcher caches.
- Reentrant callback support.
- No static facade state bound accidentally to one compiler identity.
- Defined behavior across multiple compilations in one process.

### 17. Generator correctness is part of runtime correctness

The generated facade, ABI, compiler dispatcher, analyzer runtime, and manifest
must come from one projection model. Bugs encountered so far include overload
misassociation, incomplete proxy discovery, unsupported inherited interfaces,
and overly broad collection classification.

The generator needs deterministic validation for:

- Canonical member identity.
- ABI symmetry in both directions.
- Complete proxy/type-map discovery.
- Runtime-type factory coverage.
- Generic transport instantiations.
- Unsupported-reason accuracy.

Member-name exceptions should be limited to genuine Roslyn semantic exceptions,
not used as the normal onboarding mechanism.

**Largely addressed (2026-08-17, migration Step 2).** Members are keyed by an
assembly-qualified `DocumentationCommentId`, the name-matched rules are gone in
favour of three tables keyed on that id with mandatory reasons, and
`ProjectionValidation` fails generation on canonical id collisions, table
entries matching nothing, vtbl asymmetry, and missing proxy factories. Still
open from the list above: generic transport instantiations (Step 8) and
unsupported-reason accuracy, which stays a per-member judgement.

### 18. ABI and Roslyn-version compatibility must be explicit

Native analyzer modules and the compiler must agree on:

- Analyzer ABI version.
- Generated projection identity.
- Roslyn assembly API version.
- Runtime type IDs and member IDs.
- Transport representations.

Incompatibility must be detected before analyzer execution with an actionable
error and a cache key that prevents stale native modules from being reused.

### 19. Validation must execute analyzer behavior

Constructing all analyzers and running `Initialize` found some gaps, but the
representative CA corpus exposed many more only after callbacks executed.

Compatibility validation needs:

- All analyzers in an assembly discovered and instantiated.
- Every non-empty supported diagnostic explicitly enabled.
- Source corpora that exercise every registration and major API family.
- No `AD0001`.
- Managed-versus-native diagnostic equivalence, including diagnostic details.
- Compiler output equivalence.
- Regression cases for every transport bug fixed.

### 20. Analyzer NativeAOT compatibility is a separate dimension

Even a complete Roslyn bridge cannot make all managed implementation techniques
NativeAOT-compatible. Analyzer preparation must independently detect trim/AOT
issues such as unsupported reflection, dynamic code generation, or runtime
assembly loading.

Roslyn transport failures and analyzer NativeAOT incompatibility must produce
different, actionable diagnostics and participate in the project’s fallback
policy.

## Architectural conclusion

The repeated failures are not evidence that remoting is impossible. They show
that the project needs a general ownership, identity, runtime-type, and
recursive-value transport layer beneath the generated Roslyn member ABI.
Continuing to add isolated API methods without those foundations will repeatedly
recreate the problems above.
