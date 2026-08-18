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

**Status (2026-08-17): closed.** `RoslynHandleTable.Add` now keeps a reverse
map (`Dictionary<object, long>`, reference-equality-keyed) and returns the
existing handle for an object already in the table instead of minting a new
one; the analyzer side mirrors this with a weak-valued handle→proxy cache
(`RoslynObjectProxy.GetOrCreate`) so the same handle resolves to the same
proxy instance. A compiler object shared across several analyzer callbacks —
a `Compilation`, a `SyntaxTree` — now round-trips to the literal same proxy,
not merely an equal one, so `Equals` usually short-circuits on
`ReferenceEquals` before ever crossing the boundary, and reference-keyed
dictionaries and sets built on the analyzer side work without a comparer.
Measured on the differential corpus: total boundary calls dropped from
1,352,763 to 1,141,953 (-15.6%) with the burn-down unchanged, from
comparisons that no longer need `ObjectEquals`/`ObjectGetHashCode` round
trips. This closes as part of migration Step 4, alongside retiring
control-scoped handle identity (problem 16's scoping half) — see the
migration plan for what stayed out of scope (memoizing immutable
collection-valued members, and class-hierarchy proxy caching beyond the
IDIC/interface family).

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

**Status (2026-08-17): closed for the class family; the interface family was
already correct.** The two need opposite strategies, and conflating them was
the mistake.

An *interface* cast is interceptable, so `IDynamicInterfaceCastable` plus the
type map resolves it lazily at the cast, and only the casts an analyzer
actually writes retain entries. That is the design in §3.3 and it stays: there
are 498 projected interfaces, and a factory reaching all of them would root the
projection.

A *class* cast is a plain runtime type check with nothing to intercept, so the
proxy has to be most-derived when it is constructed or the cast simply throws.
That was `ParseOptions` → `CSharpParseOptions`, the CA1507 failure, and it had
no mechanism at all — the roadmap's claim that this case "already worked" was
wrong.

What makes it affordable is that the class family is nothing like the interface
family: **13 base classes have projected derived types, none has more than
three, and 18 derived types exist in total**. Each derived type registers
itself against its base's vtbl id in a registry in the runtime assembly; the
base's proxy factory consults it and falls back to its own proxy. Registration
is emitted per assembly by whichever one owns the derived type, because that is
the only direction the facades reference each other — `Microsoft.CodeAnalysis`
cannot name `CSharpParseOptions`, while `Microsoft.CodeAnalysis.CSharp` reaches
`ParseOptions` through the friend declaration Roslyn already ships. Four of the
thirteen hierarchies cross assemblies that way and they are exactly the
language-specific ones.

Two things this cost, both worth stating:

- **The registry cannot live on the facade type.** Registering onto
  `SyntaxTree` runs its class constructor during module initialization, and
  `SyntaxTree.EmptyDiagnosticOptions` is a static field projected as throwing,
  so the process died before `Main`. Keying the registry on the base's vtbl id
  instead touches nothing but the registry.
- **Module-initializer registration is invisible to trimming**, so every
  registered derived proxy and its vtbl is rooted in every module whether or
  not that module casts. Registering all 18 cost **+9.1% on the floor module**
  — `CSharpSyntaxWalker` and `CSharpSyntaxRewriter` carry a vtbl member per
  syntax kind. Restricting registration to base types some supported member
  actually returns, which is the only situation where a proxy is built at the
  base, drops that to **+5.0%** (+130,288 bytes floor, +160,272 whole-assembly)
  and excludes the visitor hierarchy entirely. That is still a real bill paid
  by modules that never cast, and the follow-up is to make registration
  demand-driven rather than eager so trimming can see through it.

### 4. Ownership must be explicit

Not every Roslyn-shaped object has the same owner:

- Compiler-owned semantic objects must remain remote handles.
- Analyzer-created objects such as descriptors and diagnostics are local.
- Callback contexts are local wrappers over compiler-owned state.
- Some objects, such as locations and comparers, can be either local or remote.

Mixing these representations caused local objects to execute generated remote
members without a control vtable. Every projected type needs defined ownership,
construction, and local-versus-remote behavior.

**Status (2026-08-17): closed.** All 700 projected types carry an ownership
class and a reason, and ownership is now the single predicate deciding whether
an instance may cross as a handle — consulted by the ABI classifier, the proxy
collector, and proxy-factory emission alike. A type whose ownership forbids
crossing can no longer be given a proxy factory, which is the specific shape the
"local object executing remote members" failure took; `ProjectionValidation`
rejects it. It found two live instances the moment it was added.

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

**Status (2026-08-17): partly closed.** The comparers are analyzer-local
singletons carrying only a kind tag, and the type can no longer be given a proxy
factory, so marshalling one as a compiler object is now unrepresentable rather
than merely avoided. `ISymbol.Equals(ISymbol, SymbolEqualityComparer)` delegates
to the comparer, and that overload is now *classified* unsupported for the right
reason — the comparer is analyzer-owned, so no handle to it exists — instead of
occupying a vtbl slot nothing could implement. Analyzer-local object identity is
settled for the Dual types, which fall back to reference equality when either
side is local. What remains is remote object identity itself: two handles to the
same compiler object are still two unequal facades, which is Step 4.

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

**Status (2026-08-17): partially closed.** `Diagnostic`, `Location`, and
`DiagnosticDescriptor` now have one runtime type each holding both states, with
every member dispatching on a discriminator rather than assuming a handle;
`Location.None` is a shared analyzer-side singleton. `SymbolDisplayFormat` and
`SyntaxAnnotation` remain compiler-owned deliberately — see Step 3 in the
migration plan — and `SourceText` is not yet projected. What crossing *back*
still cannot express is a diagnostic's full state, which is problem 14.

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

**Scoping half closed (2026-08-17).** Migration Step 4 retired control-scoped
handle identity: `RoslynHandleTable` no longer encodes which table a handle
came from (there is only one, process-global, shared by every analyzer via
`RoslynInterop.Shared`), and the "no static facade state bound accidentally to
one compiler identity" bullet is satisfied by construction rather than by
convention — there is exactly one compiler identity per process to bind to.
**Still open:** scoped handle *release*, thread-safe caches under concurrent
analyzer execution beyond what the two new caches individually guard
(`RoslynHandleTable._gate` and `RoslynObjectProxy.s_cacheGate` are each
internally synchronized, but nothing coordinates release across them, because
v1 never releases), reentrant callback support, and multi-compilation
behavior in one process.

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

### 21. Generic virtual methods terminate the process rather than failing

Discovered 2026-08-17, during migration Step 3. A generic virtual or interface
method on a proxied type cannot be dispatched through
`IDynamicInterfaceCastable`: NativeAOT's type loader cannot construct the slot
for an instantiation it did not see, and it **fails fast** rather than throwing.

```
Process terminated. Generic virtual method pointer lookup failure.
Declaring type: Microsoft.CodeAnalysis.IOperation
Target type: RoslynAot.RoslynFacade.RoslynObjectProxy
Method name: Accept
Instantiation: System.Object, PointsToAbstractValue
```

This is categorically worse than every other failure in this inventory. A
failing member raises `AD0001` and the compilation continues; this kills the
compiler process, so **every other analyzer's diagnostics are lost too**. Two
passing rules were observed going to `CompilerCrash` because an unrelated
analyzer reached this path in the same process.

It also cannot be caught. The failure happens inside the runtime type loader
before any managed frame the bridge controls, so no `try`/`catch` and no
per-analyzer isolation in the current process model can contain it.

The specific trigger is `IOperation.Accept<TArgument, TResult>`, which the
analyzer utilities' dataflow analysis calls for every visited operation. The
facade does declare the member with a `PlatformNotSupportedException` body — it
is simply never reached. `IOperation.Accept(OperationVisitor)`, the non-generic
overload, dispatches normally.

**Status (2026-08-17): the process-kill half is closed.** Generic methods on
facade interfaces are emitted `sealed`, which makes them non-virtual, so the
call resolves directly to the facade body and never consults a GVM slot
mapping. Reaching one now raises a catchable `PlatformNotSupportedException`
that surfaces as `AD0001` naming the member. Verified end-to-end: `CA1508`
reaches dataflow analysis, calls `IOperation.Accept<TArgument, TResult>` at the
same `DataFlowOperationVisitor.VisitCore` frame that previously killed the
compiler, and the compilation completes. The `GetControlFlowGraph` members
withdrawn as a tourniquet are restored.

`ProjectionValidation` now refuses to generate a *supported* generic call on a
dynamic-interface proxy, since that would mean something intends to dispatch it
virtually.

**What remains** is making these members work rather than throw. That needs a
statically implemented shim on the proxy for ILC to build real GVM slots
against — demonstrated to work for reference and struct instantiations alike,
including through prebuilt analyzer IL. Seven signatures cover the whole
surface. See [generic virtual dispatch](GENERIC-VIRTUAL-DISPATCH.md).

### 22. Copying a collection preserves its contents and discards its behavior

Discovered 2026-08-17, while auditing a proposed memoization of
`IAssemblySymbol.NamespaceNames`. The `StringCollection` and `ObjectCollection`
transports were defined as "read the elements across and rebuild an array."
That is faithful for the elements and wrong for everything else the collection
knows how to do.

Two things are lost, and only one of them is benign:

- **Lookup complexity.** Roslyn backs these with sets — `IdentifierCollection`,
  `ImmutableSegmentedHashSet`. An O(1) membership test becomes an O(n) array
  scan.
- **Equality semantics.** The copy answers `Contains` with
  `EqualityComparer<string>.Default` no matter what the source used. Roslyn's
  analyzer config keys compare case-insensitively through
  `CaseInsensitiveComparison.OneToOneUnicodeComparer` — not even
  `StringComparer.OrdinalIgnoreCase`, so there is no BCL comparer to name on
  the wire. Measured directly: the source collection answers `true` and the
  copied `string[]` answers `false` for the same query.

Declaring a member `IEnumerable<string>` does not avoid this.
`Enumerable.Contains` dispatches to `ICollection<T>.Contains` whenever the
runtime type provides one, so the comparer leaks to callers regardless of the
declared type. Verified on `INamedTypeSymbol.MemberNames`, whose runtime type
is a hash set.

**This is the worst failure shape in the inventory after problem 21, and for
the opposite reason.** Problem 21 kills the process, which is at least
impossible to miss. This one produces a wrong answer with no exception, no
`AD0001`, and no crash — a wrong diagnostic that the differential harness
catches only if some corpus case happens to observe it. `AnalyzerConfigOptions.Keys`
is projected and reachable, and no corpus case exercises it today.

**Fixed for string collections.** They now cross as a handle: `Count` and
`Contains` are answered by the collection Roslyn built, using its own comparer
and its own complexity. Enumeration — the one operation that cannot be answered
a question at a time — snapshots through `SnapshotStringCollection`, so only
enumerating callers pay for materialization. The analyzer-side
`RoslynStringCollection` implements `ICollection<string>` rather than only
`IEnumerable<string>` precisely so that `Enumerable.Contains` defers to the
faithful implementation.

`ProjectionValidation` now rejects any copied collection whose declared type
promises membership (`ICollection<T>`, `ISet<T>`, `IReadOnlySet<T>`). Object
collections pass that check today: 210 of 261 are `ImmutableArray<T>`, whose
`Contains` is `EqualityComparer<T>.Default` and therefore provably identical to
the array a copy produces, and the remaining 51 are lazy iterator sequences
implementing no `ICollection<T>` for anything to defer to. The residual risk is
stated in the check's own remarks: were one of those ever backed by a set with
a custom comparer, the copy would diverge and this check would not catch it.
Proxying every lazy sequence to close that gap would turn a tree walk into a
snapshot plus a crossing per element, which is why the line is drawn at the
declared contract.

Fixing an object collection, if one ever fails the check, does **not** need a
per-element-type special case. Shared generics canonicalize every reference-type
instantiation, so the constraint is not the element type but that the control
vtbl is non-generic and has no `T` at the `Contains` call site. The dispatcher
is generated code that knows the static type, so it can register a typed
closure alongside the handle.

The regression test is in `RoslynProjectionValidation`: a
`HashSet<string>(StringComparer.OrdinalIgnoreCase)` crossed as a handle must
answer `Contains("ALPHA")` for `"Alpha"`. Mutation-tested — reverting the
implementation to the copy-based one makes it fail.

**The measurement that was missing.** Instrumenting the control vtbl settled a
question the per-member counters could not even express. Across the corpus:
`StringCollectionContains` is called 548,636 times, while
`SnapshotStringCollection` and `CopyStringCollectionItemUtf16` are called
**zero** times. Callers only ever probe these collections; nothing enumerates
them. So the copy was materializing 177 strings — `1 + 2N` crossings — on every
get to answer a membership test that now costs one crossing, and the
correctness fix removed roughly two orders of magnitude of traffic as a side
effect rather than as its purpose.

### 23. The projection cannot substitute itself for a type it does not own

Discovered 2026-08-17, generalizing problem 22 from collections to every type
in a projected signature.

Problem 22 was fixed by giving string collections a handle. That fix was only
available because `RoslynStringCollection` could be handed back where an
`IEnumerable<string>` was expected — the analyzer binds to an *interface*, so
an analyzer-side implementation is indistinguishable from Roslyn's. The same
move is unavailable for `ImmutableArray<T>`. The generator owns the name
`Microsoft.CodeAnalysis.ISymbol` in the analyzer's closure and can put anything
behind it; it does not own `System.Collections.Immutable.ImmutableArray<T>`,
and the framework's copy is a struct over a `T[]` with nowhere to hide a
handle. A real instance has to be produced, and whether producing one is
faithful is a claim about that specific type.

So the generalized rule is not "interface versus concrete" but **can this side
supply the behavior**: an interface can be implemented, an abstract or virtual
class can be subclassed, a sealed class or a struct can only be rebuilt. That
distinction moves `StringComparer`, `Encoding`, `Stream`, and `TextWriter` out
of the "must clone" set — they are abstract, so a forwarding subclass is
faithful — and leaves the genuinely forced clones as a small set.

**Measured, because a closed set is the whole point.** Across every projected
signature there are **73** types no facade assembly owns. Classified:

| Transport | Types | Reached by a supported call |
|---|---|---|
| Primitive — bit-identical, copying is the identity | 21 | 14 |
| Proxy — this side supplies the behavior | 15 | 2 |
| Clone — an instance must be rebuilt | 14 | 3 |
| Callback — a delegate, crosses as a registration | 9 | 0 |
| Unrepresentable — nothing usable can cross | 14 | 0 |

Only 21 are derivable without a claim about the type. The other 52 are declared
in `ProjectionForeignTypes` with a mandatory reason, and `ProjectionValidation`
fails the build on an undeclared non-primitive type reached by a supported
call, or on an unrepresentable type reached by one at all. Both are
mutation-tested. That is the fail-closed half: a Roslyn upgrade that puts a new
framework type into the analyzer surface stops the build rather than silently
acquiring whatever the derivation guessed.

**What the measurement says that was not visible before:**

- The keyed collections cannot cross at all yet, and are classified
  `Unrepresentable` to say so. `ImmutableDictionary<K,V>` is a forced clone —
  sealed, so no behaviour can be supplied — but it carries a `KeyComparer` and
  a `ValueComparer`, so copying the pairs reproduces problem 22 exactly, in a
  type the problem-22 guard did not cover: `PromisesMembership` matched arity-1
  `ICollection`/`ISet`/`IReadOnlySet` and let every keyed collection through.
  `Dictionary<K,V>` has the identical defect for a different structural reason,
  its members not being virtual. Not yet live — all 14 members returning one
  are unsupported — but `Diagnostic.Properties` is among them and is due at
  migration Step 6. The guard is now widened to the keyed and immutable set
  types, and mutation-tested by admitting `IEnumerable` to it, which makes 34
  existing calls fail.
- **Classified by what can be built, not by what could be built.** Recording
  these as `Clone` would have described the intended design while leaving the
  build willing to accept the broken one; `Unrepresentable` fails closed, so
  marking any such member supported stops the build and names it. The path back
  to `Clone` is short and stays written down in the entries themselves: copy
  the pairs, **proxy the comparer** — `IEqualityComparer<T>` is an interface,
  so it crosses by the Proxy rule — and rebuild with
  `CreateRange(keyComparer, valueComparer, pairs)`. Neither "copy and lose it"
  nor "leave it unsupported forever" is the end state.
- `CancellationToken` is the largest unimplemented foreign type in the surface:
  **239 uses, 0 supported**, and unrepresentable. Its behavior is a live
  registration list on a source the other side owns, so a clone is a token that
  never cancels and never fires a callback — which is worse than not having one
  because it looks like it works. Step 7's plan to round-trip
  `IsCancellationRequested` is a *declared degradation*, and this table is where
  it has to be written down when it lands.
- Delegates are 9 distinct types and 0 supported uses, which is the size of the
  callback gap Step 7 closes.

**Precedent worth recording.** C#/WinRT never has to answer this question,
because it controls its own ABI vocabulary: WinRT's type system has
`IVector<T>`, `IMap<K,V>`, and `IIterable<T>` and nothing else, so a
`ImmutableDictionary<string,string>` crosses as a CCW over the live instance
through `IDictionary<K,V>` and its comparer never moves. Its hand-written
mapping table registers only interfaces and open generic definitions — no
`Dictionary`, no `List`, no immutable collection — and the entries needing real
semantic knowledge are the non-collection conversions (`DateTimeOffset`,
`TimeSpan`, `Exception`/`HResult`, `Uri`). RoslynAot cannot make that choice:
it must reproduce Roslyn's existing public API, and that API says
`ImmutableArray<T>` in return position 271 times. The forced-clone set is
therefore irreducible, which is exactly why it needs enumerating rather than
avoiding.

### 24. The projection self-check's facade client has never run

Discovered 2026-08-17, while adding a constant round-trip test to it.

`csc-aot --validate-roslyn-projection <client>` loads
`RoslynAot.RoslynProjection.Client` as a second NativeAOT module and drives the
real analyzer-side facade through the real control vtbl. It is the only test in
the repository that exercises both sides of the boundary as separately compiled
modules — everything else either runs compiler-side dispatchers directly or
goes through the differential harness, which sees diagnostics rather than
transport.

It fails on its first cast, and does so at `HEAD` with no local changes:

```
System.InvalidCastException: Specified cast is not valid.
   at RoslynAot.RoslynProjection.Client.ProjectionClient.Validate
```

Instrumented, the cause is that the client module's proxy type map is empty:

```
STEP: map has SyntaxNode = False, impl =
STEP: IsInterfaceImplemented = False
```

`TypeMapping.GetOrCreateProxyTypeMapping<RoslynProxyTypeMap>` finds no entry
for `SyntaxNode`, so `IDynamicInterfaceCastable` answers "not implemented" for
every Roslyn interface and `IsObjectType` is never reached. The 196
`TypeMapAssociation` attributes are assembly-level in the facade
(`RoslynAotTypeMap.g.cs`); the two native analyzer modules that work both set
`<TypeMapEntryAssembly>$(AssemblyName)</TypeMapEntryAssembly>` and this client
does not. Adding it is **not** sufficient — verified, including after deleting
the client's obj/bin and republishing — so the entry assembly is necessary but
something further is missing.

**Why it went unnoticed is the more useful half.** Nothing in `eng/` invokes
the client path. `validate-ca1200.sh`, `validate-sample-analyzer.sh`, and
`validate-differential.sh` never pass a client argument, and
`RoslynProjectionValidation.Run()` without one passes, which is what the
"projection self-check passes" line in recent commit messages has been
reporting. So the repository has a cross-module transport test that has been
dead for an unknown number of commits while reading as green.

Two consequences worth stating separately from the fix:

- The claim "both sides agree" currently rests on the differential harness
  alone. That is a strong end-to-end signal, but it observes diagnostics, so it
  cannot distinguish a transport that is wrong from one that is merely
  unreached — which is exactly the gap problem 22 lived in.
- Whatever the type-map fix turns out to be, the client has to be wired into an
  `eng/` script afterwards. A test nothing runs is worse than no test, because
  it is counted.

## Architectural conclusion

The repeated failures are not evidence that remoting is impossible. They show
that the project needs a general ownership, identity, runtime-type, and
recursive-value transport layer beneath the generated Roslyn member ABI.
Continuing to add isolated API methods without those foundations will repeatedly
recreate the problems above.
