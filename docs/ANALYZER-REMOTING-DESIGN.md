# Analyzer remoting design

A response to `ANALYZER-REMOTING-PROBLEMS.md`. The inventory's conclusion is the
premise here: the project has a generated member ABI with nothing underneath it.
This document specifies the layer underneath, then re-derives the member ABI as
a mechanical consequence of it.

Read in order. Part 1 is context and constraints, and it determines what the
rest is allowed to say — in particular, most of the design is already
implemented or deliberately deferred, and knowing which is which first makes the
rest much shorter. `ANALYZER-REMOTING-MIGRATION.md` sequences the work against
the existing implementation.

| | |
|---|---|
| **Part 1** | Constraints, what already exists, what to defer |
| **Part 2** | The projection model — the vocabulary everything else uses |
| **Part 3** | The layers, L0 to L5 |
| **Part 4** | Cross-cutting mechanisms |
| **Part 5** | Generic support, in three phases |
| **Part 6** | What the build enforces |

---

# Part 1: Constraints and starting position

## 1.1 Assumptions

Stated explicitly because the design depends on them. Each is worth confirming
before building on it.

1. The analyzer module is a NativeAOT shared library loaded into the compiler
   process. Calls are direct function-pointer calls, not IPC.
2. Calls are therefore synchronous and reentrant on the calling thread: the
   compiler calls into the analyzer, which calls back out, on one stack.
3. Native AOT compilation happens **on the user's machine, against their
   resolved compiler bits**. There is no build under our control where Roslyn
   internals are knowable, and no per-build budget for discovering them.
4. Therefore the projection and its implementations are **pregenerated**. Only
   limited, per-analyzer code generation happens on the user's machine.
5. The analyzer assembly is available at native-module build time, so its call
   sites are visible to the AOT compiler.
6. **The two sides have opposite cost budgets.** The compiler image is built
   rarely and kept for a long time, so its size does not matter. Analyzer
   modules are built per analyzer on user machines, so they must be small and
   compile fast.

Assumptions 3 and 4 give a three-artifact model:

| Artifact | Produced | Binds to |
|---|---|---|
| Projection model, facade, proxies, encoders/decoders, dispatcher, classifier | Offline, once per Roslyn **major** version; ships in the package | Roslyn **reference** assembly — public surface only |
| Analyzer glue: registration stubs, AOT roots, used-set manifest | On the user's machine, per analyzer | The analyzer assembly + the pregenerated projection |
| Native analyzer module | On the user's machine (AOT compile/link) | The **contract** only |

The consequence that does the most work: **the analyzer module never links
Roslyn.** It talks to the compiler exclusively through the ABI, so it has no
binding to a specific patch's assemblies. Patch-range portability is a property
of the contract rather than something each build re-establishes.

Assumption 1 is a performance decision, not a semantic one. Every ABI operation
is defined over a serialized buffer rather than a fixed native signature, so the
same generated code runs over a pipe. That gives a managed sidecar host for free
as the fallback path in problem 20, and it is why the buffer's cost is worth
paying over per-member blittable signatures.

## 1.2 Two hard constraints

Everything downstream is shaped by these, so they come before the design rather
than being discovered inside it.

### Public API only

No stage of this system may enumerate a Roslyn implementation type. The offline
generator has only reference assemblies; the user's resolved bits will differ
from ours anyway; and the on-machine step has neither the budget to discover
internals nor any use for a result that is patch-specific.

This is what forces the classification design in §3.3 and the shape derivation
that goes with it.

### Trimming

Assumption 6 is the sharpest constraint in this document, because it is the one
a design can violate silently. ILC is demand-driven — unreferenced types cost
metadata-read time, not codegen time — so a large pregenerated projection is
fine **provided nothing roots it all**. Exactly one construct roots it all, and
it is the natural way to write half the tables here:

> **Any exhaustive table over every TypeId, MemberId, or instantiation roots the
> entire projection into every analyzer module.**

A `switch` over all TypeIds returning proxy constructors is the canonical
example. So is a string array of member names for diagnostics, a registry of all
encoders, or an array of every instantiation's decoder. Each looks like sensible
generated code and each turns a 200 KB module into a 40 MB one.

Four rules follow, and they are cited by name later rather than re-argued:

- **Type maps, not switches**, for anything keyed by TypeId or instantiation, so
  entries survive only when their type does. The implementation already does
  this: `[assembly: TypeMapAssociation<RoslynProxyTypeMap>(facadeType,
  implType)]` per type, resolved via
  `TypeMapping.GetOrCreateProxyTypeMapping<RoslynProxyTypeMap>()`. The
  association is dropped when the facade type is trimmed. Preserve it.
- **Data tables may be complete; code tables may not.** Where a complete map is
  needed, make it the data one and walk from it to a retained implementation.
- **Strings live on the compiler side.** Member names, unsupported reasons, and
  diagnostic text are resolved by the compiler from the manifest it already
  holds. Modules send ids.
- **Per-analyzer roots.** The on-machine glue roots only what the analyzer
  reaches; the projection assembly declares no module initializers or eager
  registration.

Module size and ILC time are tracked build metrics (§6.3), because a rooting
regression is invisible in behavior and obvious in bytes.

## 1.3 What already exists

*Verified against the implementation at `main`.*

ComWrappers is used for the **service interfaces**, not for Roslyn objects.
`IRoslynControlVtbl` and the per-type vtbl dispatchers are
`[GeneratedComInterface]` types marshalled through
`StrategyBasedComWrappers`; Roslyn objects themselves cross as signed 64-bit
handles into `RoslynHandleTable`, a hand-rolled slot/generation table. So
ComWrappers supplies portable vtables, dispatch thunks, and refcounting for
services — genuinely valuable, and the right call — but it does **not** supply
object identity for Roslyn objects, which is where problem 2 lives.

| Concern | Status at `main` |
|---|---|
| Portable vtables, thunks, service refcounting | **Done** — `[GeneratedComInterface]` + `StrategyBasedComWrappers` |
| Member dispatch table | **Done** — `GetVtbl(vtblId)` returns a per-type dispatcher |
| Handle staleness | **Done** — slot + generation, `LastDisposedGeneration` |
| Trimmable proxy resolution | **Done** — see §1.2 |
| Comparer identity as an enum tag | **Done** — `RoslynWellKnownObject` + `SymbolEqualityComparerEquals` |
| Out-of-band error channel | **Partly** — `CopyLastErrorUtf16` + `RoslynRemoteErrorKind`, thread-local; carries category and message but no member, analyzer, or frame context (§4.5) |
| Local vs remote ownership | **Partly** — a two-way split exists; §3.4 needs five classes |
| **Canonical handles** | **Missing** — `RoslynHandleTable.Add` allocates a fresh slot on every crossing, with no reverse map |
| **Canonical proxies** | **Missing** — no handle→proxy cache on the analyzer side |
| Everything in L3, L4, Parts 4–6 | **Missing** |

The two missing identity rows are the important ones, and they are the literal
text of problem 2: *"Returning a new compiler handle for the same object creates
the same problem one layer earlier."* `Add` is that fast path. §3.2's rule that
the reverse map "must never be bypassed by a just-allocate-a-new-slot path" is
not a caution against a hypothetical — it names the current code.

**Handle tables are per analyzer proxy.** `NativeDiagnosticAnalyzer` holds
`private readonly RoslynInterop _roslynInterop = new()`, so the 43-analyzer
NetAnalyzers module has 43 independent tables, and one `INamedTypeSymbol`
crossing to all of them produces 43 handles — times however many crossings
each. This is the concrete cost of the scoping that §3.2 removes.

Other BCL machinery worth using rather than rebuilding:

- **`ConditionalWeakTable<object, T>`** — reference-identity keyed, ignores
  `Equals` overrides, does not root the key. Exactly the compiler-side reverse
  map. Note it gives weak *keys*; the analyzer-side proxy cache wants weak
  values, so that side is `Dictionary<long, WeakReference<T>>` with a sweep, or
  in v1 a plain strong dictionary.
- **`GCHandle.Alloc(delegate)` + `[UnmanagedCallersOnly]`** — the `(fn, ctx)`
  callback pair in §3.1 is the standard NativeAOT interop idiom, not something
  to invent.
- **`ImmutableCollectionsMarshal.AsImmutableArray(T[])`** — builds an
  `ImmutableArray<T>` over a decoded array with no copy. Directly on the hottest
  allocation path in the bridge.
- **`MemoryMarshal` / `BinaryPrimitives`** for blittable encode/decode.
- **`NativeLibrary.Load` / `GetExport`** for attach. **`DependentHandle`** if
  the proxy cache ever needs key-dependent lifetime.
- **`ObjectIDGenerator`** looks relevant and is not: legacy serialization
  infrastructure, poor AOT story.

## 1.4 What to defer

Parts 3 and 4 describe an end state. Very little of it needs to exist on day
one, and building it up front means debugging an optimization and a semantics
question simultaneously — roughly how the inventory's bugs got hard to find.

The dividing line is **semantics versus speed**. Something is semantics if an
analyzer can observe the difference in its results. Everything else does the
dumb thing until a profile says otherwise. Each entry below is marked at its
point of definition, so the design can be read straight through without
tracking which parts are hypothetical.

| Optimization | v1 instead | Why deferring is safe |
|---|---|---|
| Inline attribute prefetch (§3.3) | Zero-length blob; every property round-trips | Reserved slot makes it a later contract revision |
| Inline semantic hash (§4.1) | `GetHashCode` round-trips | Correct, just chatty |
| `Lazy` chunked sequences (§3.5) | Always snapshot eagerly and fully | Removes a wire shape and the snapshot-pinning question |
| Handle release and refcounts (§3.2) | **Never release** | See below |
| Buffer arenas (§3.1) | `byte[]`, `ArrayPool` later | Allocation is not the first bottleneck |
| `SyntaxToken` cached fields (§3.5) | Bare handle, round-trip everything | Value-handle semantics are separable from caching |
| Descriptor identity caching (§4.4) | Re-serialize on every report | Reports are rare relative to symbol traffic |
| Blittable fast-path signatures | Uniform buffer path only | Drop from v1 entirely |
| Shared-page cancellation (§4.3) | Round-trip `IsCancellationRequested` | Correct, just slow |
| Striped locks (§3.2) | One lock, or `ConcurrentDictionary` | Contention is measurable when it arrives |

**Never releasing collapses more mechanism than everything else combined.** A
command-line compiler exits per build. If v1 keeps a strong list plus a
reference-identity dictionary and lets the table grow until the process ends,
then finalizers, ref-delta batches, the one-frame-lag safety argument, the weak
reverse map, and the generation counter all disappear. The condition that would force it back is a compiler
that outlives a build, and **verified: there is none.** `CscAot` is a
csc-compatible executable with no build server, so the process exits per
invocation. Measure peak table size on the largest available build, but the
structural risk is absent.

Note this interacts with the identity fix in §3.2: today `Add` allocates a slot
per crossing, so the table grows with *traffic*. Once handles are canonical it
grows with the *object graph*, which is a far lower ceiling — the identity fix
is also the memory fix.

**What must not be deferred**, because it looks like optimization and is not:
canonical handles, the three-state `Seq` tag, per-element runtime type ids,
ownership classification, diagnostic reconstruction, and structured error
records. All are observable in analyzer output.

One nuance on canonical *proxies*. Overriding `Equals`/`GetHashCode` to compare
handles gets `Dictionary` and `HashSet` working with no proxy cache, which is
most of what CA1508 needs. It does not cover consumers using reference identity
explicitly — `ConditionalWeakTable`, `ReferenceEqualityComparer`,
`object.ReferenceEquals` — and Roslyn's dataflow is exactly the code that might.
Since ComWrappers provides the cache anyway, build it; but handle-based equality
is a real fallback with a known, testable gap.

**What to profile, in order.** Analyzer workloads are dominated by symbol and
operation traversal, so the likely ranking is round trips per callback, then
sequence materialization, then table growth. Instrument the boundary with a call
counter per member from day one: nearly free, it ranks the deferral table above,
and it doubles as the coverage metric §6.3 needs anyway.

---

# Part 2: The projection model

One model, serialized, checked in — the single source of truth for the facade,
the ABI, the compiler dispatcher, the analyzer runtime, and the manifest
(problem 17). Everything in Parts 3 and 4 is a consumer of it, so its vocabulary
comes first.

**Per type:** TypeId, ownership class (§3.4), shape and ambient interfaces
(§3.3), inline attribute set (§3.3), nullability overrides (§4.2), sequence
policy (§3.5).

**Per member:** canonical id, wire signature, support status, unsupported
reason.

**Canonical member identity** is the `DocumentationCommentId` string, hashed —
not the member's name and not its declaration order. Overload misassociation and
the `GetTypeMembers` body bug in problem 8 are both artifacts of name- or
adjacency-based matching, and a canonical id makes them unrepresentable rather
than fixed case by case.

**Closure computation** (problem 1) is a fixed point from analyzer-facing roots.
A member is supported only if every type in its signature closure is supported;
otherwise it is emitted as unsupported with the reason chain that made it so.
This replaces onboarding members one at a time, which the inventory identifies
as the source of recurring failure.

Member-name exceptions are not an onboarding mechanism. Genuine Roslyn semantic
exceptions are model entries with a mandatory `reason` field, and their count is
a tracked metric — growth means the model is missing a concept.

The build-time validation the model enables is in §6.1.

---

# Part 3: The layers

Six layers, each depending only on those below. The existing implementation is
roughly L5 resting directly on L0.

```
L5  Generated member ABI          Roslyn members, one entry per canonical signature
L4  Value transport               recursive wire-type algebra
L3  Ownership                     remote / value / local / dual / facade
L2  Runtime type & proxies        TypeId, shape lattice, trimmable type map
L1  Handles & lifetime            handle table, generations, ref deltas
L0  Channel & peers               the boundary itself
```

The numbered subsections below run L0 upward, matching that stack.

## 3.1 L0 — Channel and peers

**The boundary traffics in references, not names.** A name has to be resolved,
which means a registry, a lifetime for the registry, a lookup on every call, and
an error case for an unknown name. A reference has none of that. Handles (§3.2)
are the one place a name is unavoidable, because a managed object cannot be
handed across as a pointer — it moves, and the other runtime cannot root it.
Everywhere else there is a natural reference, so use it.

**Attach exchanges vtables.** When a module loads, the two sides swap
function-pointer tables — a P/Invoke into the module's exported `Attach`,
returning the module's table and taking the compiler's:

```c
typedef struct {
    Status (*invoke)(void* ctx, uint32 memberIndex, Buffer args, Buffer* ret);
    Status (*apply_refs)(void* ctx, RefDelta* deltas, int count);
    ...
} CompilerVTable;

typedef struct {
    Status (*invoke_callback)(void* cb, Buffer args, Buffer* ret);
    Status (*shutdown)(void* ctx);
    ...
} AnalyzerVTable;
```

Each side stores the other's table and context pointer. There is no module
identifier anywhere, and the compiler's list of attached modules is a list of
tables.

**Callbacks are references too.** Registration hands over `(fn, ctx)` — a
function pointer plus an opaque context standing for the module's own delegate —
and the compiler closes over the pair in the Roslyn action it registers. Firing
is one indirect call. Returning a `callbackId` for the compiler to pass back
through a lookup would buy a registration table, an id space, and an
unknown-callback error path, all for nothing.

**Member dispatch works the same way**, and under ComWrappers the COM vtable
already is the table. `MemberId` is needed at *contract* time, since negotiation
and diagnostics must name a member; at *runtime* it is a vtable slot ordered by
the contract. An unprovided member is a null slot caught once at attach against
the module's used set, rather than a default arm reached mid-analysis. Names for
the contract, references for execution.

**`Status` may be an HRESULT, but it is never the whole error.** With a
COM-shaped boundary the HRESULT stays where the ABI wants it, and every call
additionally carries a structured error record out-of-band (§4.5), so `Faulted`,
`Unsupported`, and `Cancelled` arrive with context attached. Problem 15 was
caused by an HRESULT being the entire message, not by the HRESULT existing.

Buffers are valid only for the duration of the call; the callee copies anything
it retains. *(Arena allocation deferred — §1.4.)*

## 3.2 L1 — Handles and lifetime

**ComWrappers implements this layer** (§1.3): a handle is an `IUnknown*`, the
reverse map is the CCW cache, the proxy cache is the RCW cache. The
specification below is what the layer must guarantee, written out because the
layers above depend on those guarantees and because they are easy to lose to a
wrong flag.

**Handle** is a 64-bit value, unique for the process lifetime, with `0` as
canonical null. A generation field turns use-after-release into a defined
protocol error rather than a silent wrong answer.

Compiler side, **one table for the whole process**. Handles are not scoped to a
compilation, an analyzer, or anything else: Roslyn's object model is immutable
and identity-stable, and a global space inherits that guarantee directly rather
than approximating it.

- `ObjectTable`: index → object, plus a reverse map object → index keyed on
  reference identity. The reverse map is what makes handles canonical — the same
  object always yields the same handle, regardless of which compilation produced
  it or which analyzer asked. This is the compiler-side half of problem 2, and
  it must never be bypassed by a "just allocate a new slot" fast path.
- Objects genuinely shared across compilations — `SyntaxTree`, `SourceText`,
  `ParseOptions` — therefore keep one handle and one proxy, preserving reference
  equality Roslyn itself promises. Symbols from two different compilations are
  different objects and get different handles. Both cases fall out with no
  special casing.

Analyzer side, each module has its own `ProxyCache` — separate heaps make this
unavoidable, and it is the only place sharing genuinely cannot happen. A handle
maps to one canonical proxy for as long as any analyzer code can observe it.
Together with the reverse map this gives the property CA1508 needs: reference
equality on proxies means identity of compiler objects, so Roslyn's own
dictionaries keyed on operations work unchanged, across analyzers in a module
and across compilations.

**Frames** are call-scoped machinery only: the buffer arena, the cancellation
slot, and error-context capture. They do not own handles.

**Lifetime.** *Deferred in v1 — never release (§1.4).* When it is needed:
cross-runtime cycles cannot be collected, so this is a distributed-GC problem
and the answer is reap and batch. Each proxy holds one logical reference;
finalizers push dead handles onto a local free list, flushed as a single
`apply_refs` batch. The count is an unattributed total, not per module — which
works only because transmission is refcount-neutral and each module reports its
own deltas: it queues an `addref` the first time it materializes a proxy for a
handle and a `release` when it reaps one. Only the module knows whether it
already had a proxy, so only the module can report it. Materialize-then-reap
within one analysis cancels before it is sent. During a call the frame holds
strong references to everything transmitted, and the compiler keeps that set
alive until it processes the module's next batch; that one-frame lag is what
makes it safe for the increment to arrive after the transmission.

An `AnalysisComplete` hint at end of compilation flushes reap queues eagerly. It
is a hint, not a bracket: it invalidates nothing.

## 3.3 L2 — Runtime type and proxy construction

Every projected type has a stable TypeId. A compiler object reference on the
wire is never a bare handle:

```
Ref := { handle: u64, runtimeTypeId: u32, inlineAttrs: bytes }
```

`runtimeTypeId` is the most-derived supported shape, computed compiler-side from
the actual runtime object, not from the declared return type. This is problem 3:
a declared `ParseOptions` must still cast to `CSharpParseOptions`.

**Shape derivation, from public API only** (§1.2). Projecting one proxy per
public interface does not work, because a single Roslyn object implements a
lattice of them and casts must all succeed. Two public properties make the shape
set computable from reference assemblies:

*Leaf-disjointness.* Roslyn's analyzer-facing interface hierarchies do not
cross: nothing is both an `IMethodSymbol` and an `INamedTypeSymbol`, and no
operation is two operation kinds. A **shape** is one maximal public interface
plus its declared bases. Ambient interfaces implemented without declared
inheritance — `IEquatable<ISymbol>` is the recurring one — are declared per root
and are public facts. A genuine unrelated combination, should one exist, gets an
explicit shape entry with a stated reason; the list should stay near-empty.

*Public discriminators.* Roslyn already exposes the classification we need.
`ISymbol.Kind`, `IOperation.Kind`, and `SyntaxNode.RawKind` are public, stable,
and designed for exactly this:

```csharp
static uint Classify(ISymbol s) => s.Kind switch {
    SymbolKind.NamedType  => TypeIds.INamedTypeSymbol,
    SymbolKind.Method     => TypeIds.IMethodSymbol,
    SymbolKind.ArrayType  => TypeIds.IArrayTypeSymbol,
    ...
    _ => Degrade(s),
};
```

Pregeneratable, a jump table at runtime, and invariant across patch releases
because the discriminator space is public API. It is compiler-side, so its size
does not matter.

**Totality without enumerating internals.** The classifier is total by
construction over the public discriminator space: every enum member has an arm,
plus a default. A patch introducing a new internal type reaches an existing arm
and is invisible. A release adding a new *enum value* reaches `Degrade`, which
walks to the nearest supported public base and records the degradation.
Degradations are counted and surfaced in validation runs, so the differential
corpus is what catches a bad classification — the right instrument, since it is
the only one that sees the user's actual bits.

**Resolve derived types at the cast, not at construction.** The tempting design
is to classify compiler-side, send a most-derived `runtimeTypeId`, and construct
the most-derived proxy. It conflicts with trimming: the factory would have to
reach every proxy, rooting the projection.

The implementation already does the better thing, and it should be kept. A proxy
is created at its declared type; when analyzer code casts, the facade asks the
compiler `IsObjectType(handle, vtblId)` and resolves the implementation through
the type map. Cast-time resolution means only the casts an analyzer actually
writes retain entries — trimming and observability coincide for the same reason
they do generally: **if the analyzer can observe the difference, it referenced
the type, so the entry was retained.** A cast, an `is`, an `OfType<T>` all
reference the type; the only escape is reflection over Roslyn types, already
outside the supported set (problem 20).

What this costs is a round trip per cast, which the §1.4 counters will price. If
it proves hot, the fix is caching the answer per handle, not eager
classification.

`runtimeTypeId` on the wire therefore need not be most-derived. It is the
declared type, and the compiler-side `Classify` above exists to serve
`IsObjectType` and to give error records something specific to say.

**Inline attribute prefetch.** *Deferred — ship the slot empty (§1.4).* Each
type may declare hot fields carried inline with every ref, so that the property
accesses dominating analyzer inner loops stop being round trips, `GetHashCode`
becomes free, and `SyntaxToken`-style value handles get default semantics
without a call. Reserve the slot, not the contents: `inlineAttrs` is a
length-prefixed blob, zero-length in v1. Adding attrs to a type is then an
ordinary contract revision on that type (§6.2) rather than a format break, which
is the only reason this appears in the design at all rather than later. A first
guess at the contents, to be settled by profile:

| Type | Candidate inline attrs |
|---|---|
| `ISymbol` | `Kind`, `DeclaredAccessibility`, `IsStatic`, semantic hash |
| `IOperation` | `Kind`, `ConstantValue.HasValue` |
| `SyntaxNode` | `RawKind`, `SpanStart`, `SpanLength` |
| `SyntaxTree` | `FilePath` id |

## 3.4 L3 — Ownership

Problem 4's root cause is that ownership was implicit. It is a required field on
every projected type; the generator refuses to emit a type without one.

| Class | Meaning | Examples |
|---|---|---|
| **Remote** | Compiler-owned. Always a handle. | `Compilation`, `ISymbol`, `IOperation`, `SemanticModel`, `SyntaxTree` |
| **Value** | Snapshotted by content; structurally identical both sides. | `TextSpan`, `LinePosition`, `FileLinePositionSpan` |
| **Local** | Analyzer-created; lives in the analyzer heap. | `DiagnosticDescriptor`, `SymbolDisplayFormat`, `SyntaxAnnotation` |
| **Dual** | Either origin. | `Location`, `Diagnostic`, `SourceText` |
| **Facade** | Analyzer-side, no compiler object behind it. | `SymbolEqualityComparer.Default`, callback contexts |

**Dual** is where the inventory's "local objects executing generated remote
members without a control vtable" bug lives. A dual type is generated as a
*single sealed class* with an internal discriminator and two internal
implementations, not as two types. Casts, pattern matches, and `GetType()` then
behave, and every member dispatches on the discriminator rather than assuming
the object is remote.

**Facade** covers analyzer-side objects with no compiler object behind them.
`SymbolEqualityComparer.Default` is a plain singleton. What matters is that
`ISymbol.Equals(ISymbol, SymbolEqualityComparer)` transports the comparer's
identity as an enum tag rather than marshalling the comparer as a compiler
object, which it is not.

## 3.5 L4 — Value transport

The recursive algebra problem 5 asks for:

```
W ::= Void | Prim(p) | Enum(underlying) | String
    | Ref(TypeId)              -- §3.3 payload; handle 0 = null
    | Struct(shape)            -- field-wise, generated
    | Nullable(W)              -- present bit + W
    | Opt(W)                   -- Optional<T>: HasValue + W
    | Union(tag → W)           -- TypedConstant, SyntaxNodeOrToken, constants
    | Seq(W, kind, state)      -- state ∈ { Default, Empty, Items }
    | Lazy(handle, W)          -- handle-backed, count + chunked fetch
    | Local(TypeId, W)         -- analyzer-owned, by content
```

Signatures are generated by structural recursion over this grammar. There is no
per-member marshalling code and no member-specific collection handling, which is
what problem 7 asks for.

Load-bearing details:

- `Seq` carries a three-state tag. `default(ImmutableArray<T>)` and
  `ImmutableArray<T>.Empty` are distinguishable, observably different, and both
  occur in Roslyn's API. Two states is a bug.
- Element `runtimeTypeId` is per element, not per sequence. An
  `ImmutableArray<ISymbol>` routinely holds several concrete shapes.
- `Struct` handles genuine value types. For the hybrids — `SyntaxToken`,
  `SyntaxNodeOrToken`, `SeparatedSyntaxList<T>` — use a **value handle**: a
  struct containing a handle plus inline attrs, where `handle == 0` is exactly
  `default(T)`. Equality is structural over the handle. This gives correct
  default semantics without pretending the type is either fully opaque or fully
  snapshottable (problem 6).
- `Lazy` is *deferred* (§1.4). When needed, it covers sequences that can be
  large, such as `INamespaceSymbol.GetMembers()`; policy is declared per member,
  and the handle must pin a snapshot so repeated enumeration agrees.

## 3.6 L5 — The generated member ABI

With L0–L4 in place this layer is mechanical. Each supported member gets a
`MemberId` from its canonical signature (Part 2). Arrays and `params` arrays are
`Seq` instances like everything else, so `IMethodSymbol.Construct(ITypeSymbol[])`
needs no special work once `Seq` exists.

---

# Part 4: Cross-cutting mechanisms

## 4.1 Equality and hashing

Four distinct notions, four mechanisms (problem 9):

1. **Remote identity** — canonical proxies make this reference equality.
2. **Semantic symbol equality** — `Equals(ISymbol, SymbolEqualityComparer)`
   round-trips. *Deferred optimization (§1.4):* carrying a compiler-computed
   semantic hash inline with every symbol ref makes `GetHashCode` free and lets
   `Equals` short-circuit to `false` on hash mismatch, leaving a call only when
   hashes match and handles differ. Without it, every hash bucket probe in
   analyzer code is a round trip, so this is the first candidate when profiling.
3. **Value equality** — generated field-wise on `Struct`, handle-wise on value
   handles.
4. **Analyzer-local identity** — ordinary managed semantics.

## 4.2 Null and default

Nullability derives from three sources, in order (problem 10): explicit
annotation in the model, then attributes (`[MaybeNull]`, `[NotNullWhen]`), then
`NullableAnnotation`. Known Roslyn behaviors that metadata does not describe —
`ISymbol.ContainingSymbol` returning null at the root — are explicit annotation
entries carrying a reason, which is what distinguishes them from onboarding
hacks.

## 4.3 Callbacks, contexts, and cancellation

Registration (problem 11): the analyzer's facade pins its delegate and sends
`(fn, ctx, actionKind, kinds[], scope)`; the compiler registers a real Roslyn
action closing over the pair.

- **Nested registration** uses the compiler-side context handle for the live
  `CompilationStartAnalysisContext` — it is a compiler object, so L1 already
  names it. Registration against a context whose frame has ended is a protocol
  error with a real message, not undefined behavior.
- **End-action timing** and generated-code/concurrency configuration are
  passthrough.
- Initialization succeeding proves nothing; see §6.3.

Contexts (problem 12) are **Facade** objects holding a context handle plus
eagerly-transported hot state (compilation ref, options ref, cancellation slot).
Containing symbols, operation blocks, control-flow graphs, and parse options are
member calls on the context handle.

**Cancellation** replaces `CancellationToken.None` with a real token. v1
round-trips `IsCancellationRequested`. *Deferred (§1.4):* a shared page holding
one volatile int per frame slot, polled by the generated call preamble to cancel
a frame-local `CancellationTokenSource`, reduces the check to a load.

## 4.4 Diagnostics

Problem 14 is the consequence of transporting a *rendered* diagnostic. Don't.
Transport a payload and have the compiler build a genuine `Diagnostic`:

```
DiagnosticPayload {
  descriptor: Ref | Local(DiagnosticDescriptor),
  messageArgs: Seq(Union(prim | string | Ref)),   -- unformatted
  location: Dual(Location),
  additionalLocations: Seq(Dual(Location)),
  properties: Seq(Struct(kv)),
  effectiveSeverity, defaultSeverity, warningLevel, isSuppressed
}
```

The compiler calls `Diagnostic.Create`. `WithSeverity`, `WithLocation`, and
`WithIsSuppressed` then work because they operate on a real Roslyn diagnostic —
CA1200's disappearance was `WithSeverity` returning an unchanged object.

`LocalizableString` needs its own treatment: frequently a
`LocalizableResourceString` backed by a `ResourceManager` in the analyzer
assembly, it cannot be snapshotted without losing localization or eagerly
formatted without losing laziness. Represent it as **Local** with a
compiler-side subclass whose `ToString(IFormatProvider)` calls back into the
module.

## 4.5 Error reporting

Every failure carries (problem 15):

```
ErrorRecord {
  kind: Faulted | Unsupported | ProtocolError,
  memberId, unsupportedReasonCode,    -- ids, not strings
  analyzerTypeName, diagnosticIds[], actionKind,
  exceptionType, message, stackTrace,
  frameContext: { compilation, symbol?, operation?, node?, tree? }
}
```

The channel for this already exists — `CopyLastErrorUtf16` plus
`RoslynRemoteErrorKind`, thread-local, recreating a managed exception
analyzer-side. What it carries is a category and a message. What problem 15 needs
added is the *context*: which member, which analyzer, which action kind, and the
frame's compilation, symbol, operation, node, and tree.

**Ids, not strings** (§1.2). The module sends `memberId` and a reason code; the
compiler resolves both from the manifest. A name table in the module would be an
exhaustive table over every member, in the one place where the payoff is only a
diagnostic message. The exception is genuinely dynamic text — exception message
and stack trace.

The compiler renders this as `AD0001` with full text, or routes it to the
fallback policy. `Unsupported` must always name the specific member. Silent
omission is unrepresentable: there is no success status carrying no value.

## 4.6 Concurrency and reentrancy

Synchronous same-thread calls make reentrancy natural — nested callbacks are
nested stack frames, and frame stacks are thread-local. The handle table and
proxy caches are concurrent maps.

A static field holding a symbol is valid and the generator must not prohibit it:
with a global handle space it behaves exactly as in managed Roslyn, since the
symbol is immutable and the handle stays live while the static's proxy
references it. The residual concern is memory, not correctness — a static
accumulating proxies pins table entries for the process lifetime, the same leak
a managed analyzer would have. The bridge should make it visible: the compiler
holds each module's vtable, so under a diagnostic switch it can ask each module
to report its live proxy count and top retained types.

---

# Part 5: Generic support

Three phases, with the boundaries drawn where NativeAOT puts them.

The dividing line is canonical code sharing. Reference-type instantiations share
one compiled body, so the runtime stays type-erased and dispatches on a runtime
TypeId. Value-type instantiations each need distinct code that cannot be created
without a JIT, so every one the runtime might need must exist statically.

## Phase 1 — Non-generic

Everything in Parts 3 and 4 for non-generic types, plus arrays. Arrays are not
generics: `ITypeSymbol[]` and `params` transport is `Seq` and belongs here,
which unblocks `Construct` and action registration immediately.

**Exit:** L0–L5 complete for the non-generic closure; handle canonicality and
shape derivation proven by test; `AD0001`-free on an analyzer subset touching no
generic Roslyn API.

## Phase 2 — Reference-type substitutions

`ImmutableArray<ISymbol>`, `IEnumerable<SyntaxTree>`, `IEquatable<ISymbol>`,
`Optional<object>`, `SeparatedSyntaxList<T> where T : SyntaxNode`.

`ImmutableArray<T>` being a struct is fine — sharing keys off the *type
argument* being a reference type, not off the container being a class, so
`ImmutableArray<ISymbol>` shares its body with `ImmutableArray<object>`.

The `Seq` encoder/decoder can therefore be written once over `object` elements,
recovering element types from wire TypeIds. No per-instantiation code. Generic
*methods* over reference types — `FirstAncestorOrSelf<TNode>`, `OfType<T>` —
also fall out, provided §3.3's runtime type checks are correct.

## Phase 2.5 — Pinned struct instantiations

A carve-out, because otherwise phases 1–2 cannot run a syntax-node analyzer and
there is no end-to-end signal until phase 3.

`RegisterSyntaxNodeAction<TLanguageKindEnum>` and
`RegisterCodeBlockStartAction<TLanguageKindEnum>` are value-type substitutions,
but tractable ones: the set is exactly `{ CSharp.SyntaxKind,
VisualBasic.SyntaxKind }`, both `int`-backed, transported as `int32`. Analyzer
side, the instantiation is rooted by the analyzer's own call site, so the AOT
compiler generates it without help. Compiler side, dispatch is a two-arm switch.

The general principle, and the reason phase 3 is hard: **instantiations
originating in analyzer code are free; instantiations the runtime must conjure
from wire data are not.**

## Phase 3 — Value-type substitutions

`ImmutableArray<TypedConstant>`, `ImmutableArray<TextSpan>`,
`ImmutableArray<SyntaxToken>`, `ImmutableDictionary<string, string>`,
`Optional<T>` over structs.

The runtime can no longer be type-erased: decoding into
`ImmutableArray<TypedConstant>` requires that instantiation to exist. So:

1. The instantiation set is computed **offline** from the public Roslyn surface,
   not on the user's machine — possible because the struct instantiations that
   cross the boundary appear in the public API itself, and analyzer code can
   only introduce new ones through generic methods, where the sole value-type
   case is phase 2.5's pinned set.
2. `TypeId → { encoder, decoder, factory }` is emitted as a **trimmable type
   map** (§1.2), not a switch — an exhaustive table over every instantiation
   would root all of them into every module. No `MakeGenericType`.
3. Anything outside the set fails at **analyzer preparation time** with a named
   diagnostic, not at runtime with a missing-code exception.

Order within the phase: blittable elements first (`ImmutableArray<int>`,
`ImmutableArray<TextSpan>`), then structs with reference fields
(`KeyValuePair<string,string>`), then `TypedConstant` and `SyntaxToken`, which
depend on §3.5's value-handle and `Union` work and are what attribute-reading
analyzers need.

## Consequences

- The instantiation set is negotiated, not hashed (§6.2). Both sides need static
  code — the compiler is NativeAOT too, so its encoders are as closed-world as
  the module's decoders. Each side declares its set; adding one is additive.
- Partition the CA corpus by required phase. Each phase's exit criterion becomes
  a test run rather than a judgment call.
- `unsupportedReason` should name the phase, so triage during phases 1–2 is
  immediate.

---

# Part 6: What the build enforces

## 6.1 Generator validation

Failing the build, at generation time:

- Canonical member identity unique and stable across runs.
- ABI symmetry: every wire type has an encoder and a decoder on both sides.
- Factory coverage: every TypeId reachable as a `runtimeTypeId` has a factory in
  the pregenerated projection, and every shape's base chain terminates at a root
  a module cannot trim. Coverage is a property of the projection; which
  factories survive is a per-module trimming outcome.
- Classifier totality: every value of every public discriminator enum has an
  arm, and `Degrade` terminates at a supported base for every root. Checked
  offline against reference assemblies — no internal enumeration.
- Instantiation closure: every generic instantiation in a supported signature is
  in the closed set (phase 3) or reference-shared (phase 2).
- Unsupported-reason accuracy: no member marked supported has an unsupported
  type in its closure, and vice versa.
- Contract append-only diff: compared against the previous revision of the same
  major version, a changed wire signature on an existing MemberId fails.
- Every name-based exception has a `reason`. The count is a tracked metric.

## 6.2 Versioning

A whole-world compatibility hash would defeat assumption 3: any patch bump
changes it and invalidates every cached module. Use directional capability
negotiation instead.

**The contract** is the public projection — type ids, member ids, wire
signatures, inline attribute layouts, sequence policies, instantiation set. It
carries a contract id and a monotonic revision, and within a major version it is
**append-only**. New members and types may be added. An existing member's wire
signature never changes; if its semantics must change it gets a new MemberId and
the old is deprecated with a reason. Silent redefinition is the one thing this
model cannot tolerate, which is why the projection diff is a checked artifact
(§6.1).

Today this is a single `RoslynAbi.ManifestIdentity` hash over the whole
projection, plus `AnalyzerAbi.Version` for the authored contract, compared for
equality at attach. That is exactly the whole-world hash described above, so
this section is a real change rather than a refinement.

**Negotiation.** The module records the exact set of type, member, and
instantiation ids it references, each with the revision it was built against.
The compiler declares what it provides. The handshake validates `used ⊆
provided` with matching per-item revisions, and failure names the specific
member, not "version mismatch." A patch release that only adds members satisfies
every existing module unchanged.

**Two checkpoints.** On the user's machine the pregenerated dispatcher binds to
the resolved compiler's public surface, so divergence is detectable before
anything compiles: read the resolved assembly's public metadata — cheap, no
internals — and diff against the used set, producing a named diagnostic rather
than a compile error from generated code. The runtime handshake remains as the
guard for a cached module meeting a compiler swapped out underneath it.

**Cache key** is contract id plus a hash of the module's used set — not the
compiler's version or MVID. Two patch releases exposing the same contract are
the same cache key, and a module built under one is reused verbatim under the
other. That is the point of the whole scheme.

## 6.3 Validation

Initialization coverage is not coverage (problem 19). The gate is:

- Every analyzer in every assembly discovered and instantiated, every non-empty
  supported diagnostic explicitly enabled.
- Source corpora exercising every registration kind and every API family, not
  every analyzer — coverage is measured over *projected members exercised* and
  reported per build.
- Zero `AD0001`.
- Managed-vs-native differential: full diagnostic equivalence including
  severity, spans, additional locations, properties, and formatted messages. Not
  just ID sets, since CA1200 would have passed an ID-set check.
- Compiler output equivalence.
- A regression case for every transport bug fixed, permanently.

**Size and build time are correctness metrics here**, not performance
nice-to-haves, because a rooting regression (§1.2) shows up in neither behavior
nor diagnostics. Track native module size, ILC time, and retained type count per
analyzer. A representative small analyzer — one diagnostic, one registration —
should stay near the floor; if it tracks the size of the projection instead,
something is rooting the world. Gate on a ceiling, not a trend.

Two failure taxonomies, kept separate (problem 20): transport-unsupported
(`RAOT1xxx`) and analyzer-AOT-incompatible (`RAOT2xxx`, from independent
trim/AOT analysis over the analyzer assembly). They have different remedies and
must not collapse into one diagnostic. Both feed the fallback policy, and the
buffer-based ABI means the fallback can be a managed sidecar running the same
generated code.

---

# Traceability

| # | Problem | Mechanism |
|---|---|---|
| 1 | Transitive API closure | Part 2 fixed-point closure from roots |
| 2 | Canonical identity | §3.2 global reverse map + per-image proxy cache |
| 3 | Runtime types & polymorphism | §3.3 `runtimeTypeId`, shape lattice, trimmable factories |
| 4 | Ownership | §3.4 five-way classification, required field |
| 5 | Recursive transport | §3.5 wire-type algebra |
| 6 | Value-type semantics | §3.5 `Struct`, `Union`, value handles |
| 7 | Collections | §3.5 `Seq` three-state tag, per-element types, `Lazy` |
| 8 | Arrays & overloads | §3.5 `Seq`; Part 2 canonical signature ids |
| 9 | Equality & hashing | §4.1 four mechanisms; comparer identity as enum tag |
| 10 | Null & default | §4.2 three-source nullability with reasoned overrides |
| 11 | Callback kinds | §4.3 `(fn, ctx)` registration, context-handle scoping |
| 12 | Context state | §4.3 Facade contexts, real cancellation |
| 13 | Analyzer-created values | §3.4 Local and Dual |
| 14 | Diagnostics | §4.4 payload + compiler-side `Diagnostic.Create` |
| 15 | Error context | §4.5 structured record; no payload-free failure status |
| 16 | Lifetime & concurrency | §3.2 global space, ref deltas; §4.6 |
| 17 | Generator correctness | Part 2 single model; §6.1 build-failing passes |
| 18 | Versioning | §6.2 append-only contract, negotiation, used-set cache key |
| 19 | Validation | §6.3 differential corpus, member coverage, size ceilings |
| 20 | AOT compatibility | §6.3 separate taxonomy; sidecar fallback over same ABI |

# Open questions

*Questions 2, 5, and 7 in an earlier revision were answered by reading the
implementation and have moved into the body.*

1. **In-proc assumption.** Confirmed for the current host: analyzer modules are
   shared libraries loaded for the compiler process lifetime. If a sidecar
   fallback (§6.3) is built, its reentrancy and cancellation need rework.
2. **Compiler build cost.** Verified: `RoslynAot.Experimental.targets` publishes
   `CscAot` on the user's machine, guarded by `Inputs`/`Outputs`. The design
   assumes this stays cached across builds; if it does not, the compiler's size
   budget (assumption 6) is not as free as stated.
3. **Size of the on-machine codegen.** "Analyzer glue only" is the target. Worth
   measuring against the 43-analyzer NetAnalyzers module — if per-analyzer
   generation pulls projection work with it, the pregeneration boundary is drawn
   in the wrong place.
4. **Leaf-disjointness.** The public-only shape derivation rests on it.
   Checkable offline for *declared* interfaces, but whether a real object ever
   implements two unrelated maximal interfaces is not provable without
   internals, so the differential corpus is the actual evidence.
5. **Cast-time resolution cost.** §3.3 keeps a round trip per cast. Analyzers
   that cast in inner loops may make this the top profile entry, in which case
   the answer is a per-handle cache of `IsObjectType` results.
6. **Behavioral drift within a major version.** Negotiation catches signature
   changes but not a patch that changes what a member *returns*. Differential
   testing against each supported patch is the only defense; how many patches to
   test is a cost decision.
7. **`SyntaxToken` phase placement.** It is a value handle (§3.5) but its
   sequences are phase 3. Whether tokens are needed before phase 3 depends on
   the corpus partition.
