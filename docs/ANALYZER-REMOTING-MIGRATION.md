# Evolving the current implementation

Companion to `ANALYZER-REMOTING-DESIGN.md`. That document describes a target
state; this one is an order of operations to reach it from what exists, without
a rewrite.

## Current state

*Verified against `main`, not inferred.*

- `[GeneratedComInterface]` + `StrategyBasedComWrappers` for service interfaces
  and per-type vtbl dispatchers, fetched by `GetVtbl(vtblId)`. Roslyn objects
  cross as signed 64-bit handles, not COM objects.
- `RoslynHandleTable`: slot + generation, stale-handle rejection, **and no
  reverse map** — `Add` allocates a fresh slot on every crossing.
- One `RoslynInterop` per `NativeDiagnosticAnalyzer`, so a 43-analyzer module
  has 43 handle tables and one symbol crossing to all of them yields 43 handles.
- Control identity is real: handles encode their owning interop, the active
  control lives in an `AsyncLocal`, and transports reject a foreign control.
- A two-way local/remote ownership split already exists in the facade runtime.
- Trimming via `TypeMapAssociation<RoslynProxyTypeMap>` per facade type, with
  derived types resolved at the cast through `IsObjectType` + the type map.
- Errors: HRESULT plus a thread-local `CopyLastErrorUtf16` /
  `RoslynRemoteErrorKind` carrying category and message, no context.
- Comparers already dispatch through `RoslynWellKnownObject` enum tags.
- Compatibility: one `ManifestIdentity` hash over the whole projection, plus
  `AnalyzerAbi.Version`, compared for equality.
- Diagnostics reported as descriptor indexes and source spans.
- Lifetime for non-disposable handles is explicitly incomplete; the ABI README
  tracks it as product work.
- `RoslynAot.Experimental.targets` publishes both the compiler and each analyzer
  module on the user's machine via `dotnet publish`, guarded by
  `Inputs`/`Outputs`. No build server anywhere: `CscAot` exits per invocation.

Nothing below throws any of that away. ComWrappers is the right substrate for
services and stays; the generator stays and gets a model underneath it; the type
map stays exactly as it is.

## Sequencing principles

1. **Legibility before correctness.** You cannot burn down a list you cannot
   see. The first step buys instrumentation, not fixes.
2. **Make the unsupported set explicit before making it smaller.** Converting
   unknown-unknowns into declared, reasoned gaps is progress even though no
   analyzer starts working. It is also what makes the remaining steps
   parallelizable.
3. **Foundations that everything encodes against go early**, because retrofitting
   them invalidates work done on top. That is the model, ownership, and the wire
   grammar — in that order.
4. **Per-type gating, not big-bang.** Every migration below can be driven by a
   flag in the model, one type at a time, with the corpus as the gate.
5. **Ratchet.** Once an analyzer passes the differential corpus it gets a
   regression test and the green list never shrinks.
6. **Do not regress trimming.** This is the one thing the current implementation
   already gets right, and several steps below are natural opportunities to
   break it. See next section.

## The trimming constraint

Analyzer modules are built per analyzer on user machines and must stay small and
fast to compile; the compiler image has no such limit (design §1.2). The
existing trimmable type map is the mechanism, and it must survive the migration.

The hazard is specific: **any exhaustive table over every TypeId, MemberId, or
instantiation roots the whole projection into every module.** A switch mapping
TypeIds to proxy constructors is the canonical case, and it is the obvious way to
write several things below.

Where the steps put pressure on it:

| Step | Hazard | Requirement |
|---|---|---|
| 2 — model | A generated registry of all members or types | Model drives generation; it does not become a runtime table |
| 4 — identity | `CreateObject` switching over every shape id | Type map; walk the shape lattice to the nearest *retained* factory |
| 5 — wire grammar | A registry of all encoders/decoders | Per-type, trimmed with the type |
| 6 — diagnostics | A member-name table for error text | Send ids; the compiler resolves text from the manifest |
| 8 — generics | A table of every struct instantiation | Type map keyed on the instantiation |

Add module size, ILC time, and retained type count to the Step 1 dashboard, with
a ceiling on a representative small analyzer. A rooting regression changes no
behavior and produces no diagnostic — bytes are the only signal, so they need to
be watched from the first step rather than the step that breaks them.

---

## Step 1 — Make failures legible

**Goal:** every failure names itself. No semantics change.

- Replace HRESULT-only reporting with the structured `ErrorRecord`
  (design §4.5) carried out-of-band alongside the HRESULT. Keep the
  HRESULT; it is fine, it was just never the whole message.
- Surface every failure as `AD0001` with full text — analyzer type, action kind,
  member name, exception, and frame context.
- Stand up the differential harness: run the CA corpus managed and native, diff
  diagnostics on **id, severity, span, message, properties, and additional
  locations**. An id-set comparison would have passed CA1200.
- Add a per-member call counter at the boundary. Nearly free, and it doubles as
  the coverage metric and the profile input for every deferral decision later.
- Record native module size, ILC time, and retained type count per corpus
  analyzer. Establishes the trimming baseline before anything can regress it.

**Exit:** a burn-down list — for every corpus analyzer, either "passes" or a
named member and reason. Current true pass rate known for the first time, and a
size baseline to hold the rest of the migration against.

**Closes:** 15, and the measurement half of 19.

---

## Step 2 — The projection model becomes the source of truth

**Goal:** generator bugs stop being runtime bugs.

- Introduce the model as a checked-in artifact. Per type: ownership, shape,
  nullability overrides. Per member: canonical id, wire signature, support
  status, unsupported reason.
- **Canonical member identity** from `DocumentationCommentId`, hashed. This
  alone makes overload misassociation and the `GetTypeMembers` body class of bug
  unrepresentable rather than fixed case by case.
- Migrate existing hand-onboarded members into the model. Every existing
  member-name exception becomes a model entry with a mandatory `reason` field.
  Delete the name-exception path as an onboarding mechanism.
- Compute the transitive closure from analyzer-facing roots and mark everything
  outside it unsupported **with a reason chain**. Expect the declared-unsupported
  set to be large. That is the point.
- Add the generator validation passes that can run now: canonical id uniqueness,
  ABI symmetry, factory coverage.

**Exit:** generator output is a function of the model; no member reaches the ABI
without a model entry; the unsupported set is explicit and reasoned.

**Closes:** 8, 17, most of 1.

---

## Step 3 — Ownership

**Goal:** the "local object executing remote members" crash class becomes
unrepresentable. A two-way local/remote split already exists; this widens it to
five and makes it a model field rather than a runtime convention.

- Every projected type gets a required ownership field: Remote, Value, Local,
  Dual, Facade. The generator refuses to emit a type without one.
- Rework **Dual** types (`Location`, `Diagnostic`, `SourceText`) into a single
  sealed class with an internal discriminator and two internal implementations —
  not two types. Casts and pattern matches then behave, and every member
  dispatches on the discriminator instead of assuming a handle.
- Give **Local** types (`DiagnosticDescriptor`, `SymbolDisplayFormat`,
  `SyntaxAnnotation`) real analyzer-side implementations.
- Make `SymbolEqualityComparer.Default` a plain singleton, with comparer
  identity transported as an enum tag rather than marshalled as a compiler
  object.

**Exit:** no type in the model lacks an ownership class; no local object can
reach a remote vtable.

**Closes:** 4, 13, 9.

---

## Step 4 — Identity, runtime type, and retiring control identity

**Goal:** reference equality across the boundary means object identity, and
casts to derived types work.

### Retire control identity

The inventory describes handles and proxies keyed on `(control identity,
handle)`, with comparers bound to a compiler identity. The target design has no
such scope: one global handle space for the process, with object identity doing
the work (design §3.2). This is the one structural change in the migration, so it
is worth being explicit about why, and about what changes behaviorally.

Why it goes:

1. **It breaks sharing Roslyn guarantees.** `SyntaxTree`, `SourceText`, and
   `ParseOptions` are genuinely shared across compilations. Control-scoped
   handles give one such object two handles and two proxies, breaking reference
   equality Roslyn itself promises — the mirror image of problem 2, introduced
   by the fix for problem 2.
2. **It duplicates the table**, or forces a "many analyzers, one control"
   special case. A global space has neither shape.
3. **It makes the native path stricter than managed.** An analyzer static
   caching a symbol across compilations gets a dead handle under scoping;
   managed Roslyn gives it a live, immutable symbol. Problem 19 demands
   equivalence, so this divergence is a bridge defect, not a policy.
4. **Multiple compilations stop needing special casing.** Two compilations
   produce different objects, therefore different handles. Correct for free.

What to change:

- Remove the control component from handle identity and from the proxy cache
  key. Handles become process-global.
- Delete control-mismatch checks and any dead-handle-on-teardown path. Under
  the v1 "never release" decision these have nothing left to do.
- Rework `SymbolEqualityComparer.Default` and `IncludeNullability` from remote
  singletons into plain analyzer-side singletons, with comparer identity
  transported as an enum tag (this overlaps Step 3's Facade work).
- Retire the per-control registration tables in favor of `(fn, ctx)` pairs
  handed over at registration.

**Behavioral change to expect:** analyzers whose statics outlive a compilation
stop throwing and start seeing live symbols from the prior compilation. That is
the managed behavior, so the corpus should get greener, not redder — but it will
change results, and the differential harness from Step 1 is what confirms the
change is in the right direction.

### Identity and runtime type

This is real work, not an audit — ComWrappers is not providing object identity
here, because Roslyn objects are handles rather than COM objects.

- **Add a reverse map to `RoslynHandleTable`.** `Add` currently allocates a fresh
  slot per crossing; it must return the existing handle for an object already in
  the table. Key on reference identity — `ConditionalWeakTable`, or a dictionary
  with `ReferenceEqualityComparer`. This is the compiler-side half of problem 2
  and the direct cause of CA1508.
- **Add a handle→proxy cache** on the analyzer side, weak-valued. The analyzer
  half of the same problem; there is currently no such cache.
- Leave cast-time type resolution alone. `IsObjectType` plus the type map is
  already the trimming-correct design, and eager most-derived construction would
  break it (design §3.3).
- Verify the `ImmutableArray`/collection paths do not re-add objects per element,
  which would defeat the reverse map on the hottest path.
- Generate shapes from the public interface hierarchy with ambient interfaces
  declared per root, so inherited interfaces like `IEquatable<ISymbol>` are on
  the proxy. Add the classifier-totality validation pass.

**Exit:** CA1508 passes. `ParseOptions` results cast to `CSharpParseOptions`.
Roslyn's own dictionaries keyed on operations work unchanged. No control
identity remains, and the 43 handle tables have become one.

**Expect a large memory improvement** as a side effect: the table stops growing
with call traffic and starts growing with the object graph.

**Closes:** 2, 3, and the scoping half of 16.

---

## Step 5 — The wire grammar

**Goal:** transport stops growing one shape at a time. This is the largest step
and the one that ends the recurring-failure pattern.

- Implement the L4 algebra — `Prim`, `Ref`, `Struct`, `Nullable`, `Opt`,
  `Union`, `Seq`, `Local` — and generate signatures by structural recursion.
- Scope to **non-generic types plus arrays**. Arrays are not generics, and they
  unblock `Construct` and action registration immediately.
- `Seq` carries the three-state tag: Default, Empty, Items. Two states is a bug;
  `default(ImmutableArray<T>)` and `ImmutableArray<T>.Empty` are observably
  different.
- Element runtime type ids are **per element**, not per sequence.
- Value types get explicit rules: `Struct` for genuine values (`TextSpan`,
  `LinePosition`), value handles for the hybrids (`SyntaxToken`,
  `SyntaxNodeOrToken`), where handle 0 is exactly `default`.
- Delete the overly broad collection classification and the per-member
  marshalling special cases it was compensating for.
- Fold in the nullability pass: annotation, then attributes, then
  `NullableAnnotation`, with reasoned overrides for known Roslyn behavior like
  `ISymbol.ContainingSymbol` at the root.

Migrate per type behind a model flag, corpus green after each.

**Exit:** no member-specific transport code remains; the unsupported set shrinks
to generics and known gaps.

**Closes:** 5, 6, 7, 10.

---

## Step 6 — Diagnostics

**Goal:** a reported diagnostic is indistinguishable from a managed one.

- Stop transporting rendered diagnostics. Send the payload — descriptor,
  **unformatted** message args, severities, locations, properties, suppression,
  warning level — and have the compiler call `Diagnostic.Create`.
- `WithSeverity`, `WithLocation`, `WithIsSuppressed` then work because they
  operate on a real Roslyn diagnostic. This is the CA1200 fix.
- `LocalizableString` becomes Local with a compiler-side subclass whose
  `ToString(IFormatProvider)` calls back into the module, preserving lazy
  formatting and localization.

**Exit:** the differential harness passes on full diagnostic detail, not just id
sets.

**Closes:** 14.

---

## Step 7 — Callbacks, contexts, cancellation

**Goal:** every registration kind actually fires and its context is usable.

- All registration kinds: compilation, syntax, semantic model, symbol,
  operation, code block, operation block, syntax tree, additional file, start,
  end.
- **Pull the language-kind generics forward to here.**
  `RegisterSyntaxNodeAction<TLanguageKindEnum>` is a value-type substitution and
  formally belongs in Step 8, but nothing works end to end without it. The set
  is exactly two `int`-backed enums; the analyzer's own call sites root the
  instantiation, and the compiler dispatches on a two-arm switch.
- Nested registration scoped by the compiler-side context handle; registration
  against an ended frame is a protocol error with a message.
- Contexts carry complete state: compilation, options, containing symbols,
  semantic models, operation blocks, control-flow graphs, trees, parse options.
- Replace `CancellationToken.None`. v1 can round-trip `IsCancellationRequested`;
  the shared flag page is a later optimization.

**Exit:** every registration kind has a corpus test that observes its callback
firing with correct arguments. Initialization success is no longer the signal.

**Closes:** 11, 12.

---

## Step 8 — Generics

Reference substitutions first (`ImmutableArray<ISymbol>`,
`IEnumerable<SyntaxTree>`, `Optional<object>`, `SeparatedSyntaxList<T>`), which
need no per-instantiation code because canonical sharing lets the runtime stay
type-erased.

Then value substitutions, offline-computed closed instantiation set, blittable
elements first (`ImmutableArray<TextSpan>`), then structs with reference fields,
then `TypedConstant` and `SyntaxToken` sequences. Anything outside the set fails
at analyzer preparation with a named diagnostic.

**Exit:** the declared-unsupported set contains no public analyzer-facing member
that the corpus reaches.

**Closes:** the rest of 1, and the transport half of 5.

---

## Step 9 — Version range

Only worth doing once the contract is stable enough to be worth freezing.

- Contract id plus per-item revisions; append-only within a major version.
- Module records its used set; compiler declares its provided set; handshake
  validates `used ⊆ provided`.
- Build-time surface probe against the resolved compiler's public metadata, so
  divergence is a named diagnostic rather than a generated-code compile error.
- Cache key is contract id plus used-set — not the compiler's version or MVID.
  This is the step where multiple patch releases of one major start working.

**Closes:** 18.

---

## Step 10 — Compatibility policy

- Independent trim/AOT analysis of analyzer assemblies, with a diagnostic
  taxonomy separate from transport failures.
- Fallback policy wired to both taxonomies.

**Closes:** 20.

---

## Deliberate non-steps

Not deferred by accident:

| | Why |
|---|---|
| Handle release, refcount tuning | Let ComWrappers do whatever it does. Measure table growth; only act if the compiler outlives a build. |
| Inline attribute prefetch | Reserve the empty slot in Step 5's `Ref` payload; fill it when the Step 1 counters say which round trips hurt. |
| Lazy chunked sequences | Snapshot everything. Revisit if a large-namespace case shows up in the profile. |
| Member pointer table | The COM vtable already is one. |
| Sidecar fallback transport | Only if the fallback policy in Step 10 needs it. |

## The gate

From Step 1 onward, CI enforces: zero `AD0001` on the green list, the green list
never shrinks, every fixed transport bug has a permanent regression case, member
coverage is reported per build, and analyzer module size and ILC time stay under
their ceilings. The green list growing is the only
progress metric that means anything here — an inventory of fixes does not, which
is the lesson the original document is recording.

## Risks

1. **Step 5 is the big one.** It touches every member. Per-type gating and a
   green list that cannot shrink are the mitigations; without them this is where
   a rewrite spiral starts.
2. **Step 2 makes things look worse.** Pass rates may drop as silently-wrong
   members become declared-unsupported. That is the instrument working, but it
   needs saying in advance to whoever reads the dashboard.
3. **Leaf-disjointness (Step 4)** is an assumption about Roslyn's public design.
   Checkable for declared interfaces, but only the corpus proves it for real
   objects.
4. **Retiring control identity (Step 4) changes results**, not just internals.
   Cross-compilation analyzer statics behave differently afterward. Expect to
   re-baseline, and read the diff rather than assuming it.
5. **Behavioral drift (Step 9)** passes negotiation silently. Differential
   testing against each supported patch is the only defense.
6. **Trimming regressions are silent.** No behavior change, no diagnostic, and
   they are easy to introduce while doing something else correctly. The size
   ceiling from Step 1 is the only thing that catches them.
