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
  reverse map** — `Add` allocates a fresh slot on every crossing. Proxy
  `Equals`/`GetHashCode` forward to the compiler-side object through
  `ObjectEquals`/`ObjectGetHashCode`, so value equality is correct, but handles
  and proxies are still not canonical and reference identity does not survive.
- One `RoslynInterop` per `NativeDiagnosticAnalyzer`, so a 43-analyzer module
  has 43 handle tables and one symbol crossing to all of them yields 43 handles.
- Control identity is real: handles encode their owning interop, the active
  control lives in an `AsyncLocal`, and transports reject a foreign control.
- A two-way local/remote ownership split already exists in the facade runtime.
- Trimming via `TypeMapAssociation<RoslynProxyTypeMap>` per facade type, with
  derived types resolved at the cast through `IsObjectType` + the type map.
- Errors: HRESULT plus a thread-local `CopyLastErrorUtf16` /
  `RoslynRemoteErrorKind` carrying category, message, and the failing
  control-vtbl member name — but as prose in the message, not a structured
  record.
- Every generated dispatcher increments a per-member call counter;
  `ROSLYNAOT_CALL_COUNTS` writes them out. Control-vtbl operations are not
  counted.
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

**In place since 2026-08-16.** `eng/module-baseline.json` records size and
retained counts, and `eng/measure-modules.sh` fails on any change. There is no
separate *ceiling*: the baseline is exact-match in both directions, which is
stricter, and the measured floor of 2,593,424 bytes / 2,716 types that 11
analyzers sit on is the number a ceiling would have to be expressed against.

**Narrowed to five modules on 2026-08-17.** The original baseline held all 43
single-analyzer modules, which measured a configuration that never ships — the
product unit is the analyzer *assembly*, so the whole-assembly module is the
real one — and 39 of the 43 builds re-measured a number four others already
covered. The baseline now keeps the whole-assembly module plus four singles
spanning the observed range (floor, median, dataflow, heaviest), which is what
the sensitivity argument above actually needs: a rooting regression invisible at
9 MB is legible at the floor. The five reproduced their previous numbers exactly
when the matrix was cut, and the run went from tens of minutes to under a
minute. `--all-modules` still sweeps everything as an audit.

---

## Step 1 — Make failures legible

**Status: complete (2026-08-16), except the structured `ErrorRecord` — see the
first bullet.** Measured results are recorded under this step.

**Goal:** every failure names itself. No semantics change.

- Replace HRESULT-only reporting with the structured `ErrorRecord`
  (design §4.5) carried out-of-band alongside the HRESULT. Keep the
  HRESULT; it is fine, it was just never the whole message.
  - **Partial.** The AD0001 text now names analyzer, action kind, member,
    exception, and frame, and `SetError` records the failing control-vtbl
    member — but all of it travels as a formatted string, not a record. The
    differential harness therefore parses it back out with regexes coupled to
    two repo-owned format strings. Finish this with the wire grammar in Step 5.
- Surface every failure as `AD0001` with full text — analyzer type, action kind,
  member name, exception, and frame context.
- Stand up the differential harness: run the CA corpus managed and native, diff
  diagnostics on **id, severity, span, message, properties, and additional
  locations**. An id-set comparison would have passed CA1200.
  - **Amended when built.** `ReportDiagnostic` carries descriptor, span, and
    preformatted message only, and `NativeAnalyzerDiagnostic` hardcodes
    `Properties` and `AdditionalLocations` empty, so comparing those two fields
    would not measure a bridge defect — it would restate a known ABI limit 125
    times. They are instead **declared and counted** in `report.md` as an
    explicit blind spot, and compared once Step 6 widens the ABI. The four
    fields that are compared still catch CA1200, which failed on severity.
- Add a per-member call counter at the boundary. Nearly free, and it doubles as
  the coverage metric and the profile input for every deferral decision later.
  - **Done.** See "Boundary call coverage" below.
- Record native module size, ILC time, and retained type count per corpus
  analyzer. Establishes the trimming baseline before anything can regress it.
  - **Done.** `eng/measure-modules.sh` builds the whole-assembly module plus
    four representative singles; `eng/module-baseline.json` ratchets size and
    retained counts. Times are measured and reported but deliberately kept out
    of the baseline — they are nondeterministic, and a baseline that churns
    every run stops being read. See "Per-analyzer module baseline" below.

**Exit:** a burn-down list — for every corpus analyzer, either "passes" or a
named member and reason. Current true pass rate known for the first time, and a
size baseline to hold the rest of the migration against.

**Closes:** 15, and the measurement half of 19.

### Measured result (2026-08-16)

*Superseded by the two sections below; kept because the ranking it produced is
what drove the work that followed. Current numbers are 8 pass, 26 fail, 3 not
exercised.*

First real pass rate, from `eng/differential-baseline.json` over all 37 rule IDs
in `samples/RoslynAot.CSharpNetAnalyzers.Native`: **6 pass, 27 fail, 4 not
exercised**. The four uncovered rules are individually explained in
`corpus/README.md`; each was attempted and measured rather than assumed.

The failures rank into far fewer causes than rules, which is what makes the
list actionable — fixing the top entry alone unblocks seven rules:

| Rules blocked | Cause |
|---|---|
| 7 | `IOperation.ConstantValue` |
| 6 | silent no-op — no diagnostic, no exception (see below) |
| 3 | `ISymbol.Locations` |
| 3 | `ISymbol.DeclaringSyntaxReferences` |
| 2 | `CSharpExtensions.GetDeclaredSymbol` |
| 1 each | `CompilationStartAnalysisContext.CancellationToken`, `AttributeData.ConstructorArguments`, `VariableDeclarationSyntax.Variables`, `OperationWalker` dispatch, two analyzer-internal frames |

Six rules (CA1851, CA1870, CA2352–CA2355) fail as `MissingDiagnostic`: the
native side raises no `AD0001` and reports nothing. This is a distinct failure
class from a named unimplemented member — the analyzer runs to completion and
silently produces nothing — and it is invisible to any check that only watches
for exceptions. It is the same shape as the projected-`ToString` defect fixed in
`e3bf4e1`, so it should be diagnosed before the ranked member work: a silent
wrong answer costs more than a loud missing one.

### Silent no-op class diagnosed and closed (2026-08-16)

All six were one defect: `RoslynObjectProxy` did not override `Equals` or
`GetHashCode`, so two proxies for the same Roslyn object compared unequal and
hashed differently. Handles are minted per call — `RoslynHandleTable.Add`
allocates a fresh slot every time — so the proxy's inherited reference equality
was wrong for every projected object.

That never throws; it just makes lookups miss. `InsecureDeserializationTypeDecider`
(CA2352–CA2356, CA2362) stores its dangerous types in a plain
`HashSet<ITypeSymbol>` with the default comparer, so `Contains` returned false
for `System.Data.DataSet` and the analyzer reported nothing. Analyzers that
happened to route through `SymbolEqualityComparer.Default` — which the ABI
already carried — were unaffected, which is why the class was only six rules
wide and not the whole module.

Fixed by appending `ObjectEquals` and `ObjectGetHashCode` to `IRoslynControlVtbl`
and overriding both on the proxy, the same shape as the `ToString` fix in
`e3bf4e1`: an `object` virtual that facade interfaces structurally cannot
occupy has to be remoted explicitly. **Any remaining `object` virtual is a
suspect** — that is now two of them found the same way.

Result: **8 pass, 26 fail, 3 not exercised**. `MissingDiagnostic` no longer
appears anywhere in the burn-down. CA2354 and CA2355 pass outright; CA1851,
CA1870, CA2352, and CA2353 now fail against a named member, and CA2362 moved
from `NotExercised` to a named failure because the analyzer declaring it now
runs far enough to reach one. The ranking is now:

| Rules blocked | Cause |
|---|---|
| 7 | `IOperation.ConstantValue` |
| 6 | `ISymbol.DeclaringSyntaxReferences` |
| 3 | `ISymbol.Locations` |
| 2 | `CSharpExtensions.GetDeclaredSymbol` |
| 1 each | `CompilationStartAnalysisContext.CancellationToken`, `AttributeData.ConstructorArguments`, `VariableDeclarationSyntax.Variables`, `InitializerExpressionSyntax.Expressions`, `ControlFlowGraph.Create`, `OperationWalker` dispatch, two analyzer-internal frames |

`ISymbol.DeclaringSyntaxReferences` doubled from 3 to 6 because the four
newly-unblocked analyzers reach it. The two members at the top now account for
13 of the 26 failures.

### Boundary call coverage (2026-08-16)

The per-member call counter is in. Counting is compiler-side and
unconditional — one interlocked increment per dispatcher call — so a zero is
always "never called", never "not instrumented". `ROSLYNAOT_CALL_COUNTS`
makes `csc-aot` write the counts; the harness sets it per case and aggregates
into `report.json`.

First measurement over the 34-case corpus: **158 of 5303 projected members are
reached, across 1,294,513 calls.** The distribution is far more lopsided than
the failure ranking:

| Share of all calls | Member |
|---|---|
| 68.8% | `IAssemblySymbol.NamespaceNames` |
| 6.7% | `ISymbol.Name` |
| 6.5% | `IMethodSymbol.Parameters` |
| 5.3% | `IAssemblySymbol.GetTypeByMetadataName` |
| 4.2% | `IParameterSymbol.Type` |

`IAssemblySymbol.NamespaceNames` alone is over two thirds of all boundary
traffic, and it returns a string collection on every call — `WellKnownTypeProvider`
uses it to pre-filter metadata name lookups. That is one member, in one
consumer, and it dominates everything else combined. It is the first thing any
performance work should look at, and it was invisible before this counter.

Two caveats on the number. Control-vtbl operations are not counted — they are
hand-written rather than generated, so `ObjectEquals` and `ObjectGetHashCode`
traffic from the identity fix above does not appear here. And 158 of 5303 is a
statement about what this corpus reaches, not about what the projection needs:
the denominator is every member the generator emits a dispatcher for.

### Per-analyzer module baseline (2026-08-16)

`eng/measure-modules.sh` built one NativeAOT module per analyzer — 43 of them,
plus the whole-assembly module — via a new `--analyzer` filter on the entry
point generator, and records size, retained type count, and ILC time.
`eng/module-baseline.json` ratchets the first three; times stay out of it,
because they are nondeterministic and a baseline that churns every run stops
being read. Two independent full runs produced byte-identical sizes and counts.

The findings below come from that full 44-module sweep. It is what justified
narrowing the *baseline* to five modules the next day — once you know eleven
analyzers sit exactly on the floor and one analyzer is two-thirds of the
assembly, rebuilding all 43 every time buys nothing. `--all-modules` reproduces
this sweep on demand.

| Module | Size | Retained types | ILC ms |
|---|---|---|---|
| all 43 analyzers | 9,384,848 | 20,738 | 12,511 |
| `CSharpAvoidMultipleEnumerationsAnalyzer` | 6,154,528 | 15,524 | 9,027 |
| `CSharpAvoidDeadConditionalCode` | 5,422,112 | 13,406 | 7,994 |
| … 11 analyzers at the floor | 2,593,376 | 2,716 | ~2,500 |

Three things fall out of this immediately.

**There is a hard floor of 2,593,376 bytes / 2,716 types**, and eleven analyzers
sit exactly on it. That is the shared cost — facade, analyzer runtime, and the
Roslyn surface every module links regardless — so those eleven analyzers
contribute nothing measurable of their own. Any per-analyzer size target has to
be stated net of this floor, and lowering the floor helps every module at once.

**One analyzer is 65.6% of the full module by itself.**
`CSharpAvoidMultipleEnumerations` alone is 6.15 MB of the 9.38 MB total, and it
and `CSharpAvoidDeadConditionalCode` are the only two that break 5 MB. Both are
dataflow analyzers, which is also where the projection's remaining failures
cluster. Cost and difficulty are concentrated in the same place.

**Modules share almost everything.** The 43 single-analyzer modules sum to
151.8 MB against a combined module of 9.4 MB — a 16× difference. Per-analyzer
modules are a measurement device, not a shipping strategy; whatever ships has to
amortize the floor across analyzers.

Building this also surfaced a live defect in the module project.
`GenerateAnalyzerEntryPoint` was `Inputs`/`Outputs` incremental on file
timestamps, which cannot see the analyzer filter — so the first ordinary
publish after any filtered build silently reused the previous filter's entry
point and produced a module containing the wrong analyzers. It was caught
because the differential harness then reported an empty rule catalog, but
nothing would have caught a wrong-but-non-empty module. The target now always
runs the generator, which writes only when the content changes.

---

## Step 2 — The projection model becomes the source of truth

**Status: complete (2026-08-17), except withdrawing the unreachable set — see
the closure bullet.**

**Goal:** generator bugs stop being runtime bugs.

- Introduce the model as a checked-in artifact. Per type: ownership, shape,
  nullability overrides. Per member: canonical id, wire signature, support
  status, unsupported reason.
  - **Done.** `RoslynProjection.json` gained a `types` section (663 entries:
    shape, ownership, reachability, proxy kind, vtbls) and every member and call
    gained `canonicalId`; calls also carry `wireSignature`.
    `ProjectionInventory.txt` carries the same as one line per type and per
    call. Both were already checked in; what changed is that they now record
    the model rather than a summary of the emitters' behavior.
- **Canonical member identity** from `DocumentationCommentId`, hashed. This
  alone makes overload misassociation and the `GetTypeMembers` body class of bug
  unrepresentable rather than fixed case by case.
  - **Done**, unhashed. `CanonicalSignatureBuilder.GetCanonicalId` returns
    `[Assembly]M:Ns.Type.Member(Params)~Return` — the assembly prefix because a
    documentation comment id is only unique within one assembly. It is not
    hashed: hashing buys nothing while the id never leaves the generator, and a
    readable key is what makes the override tables reviewable. Hash it when it
    reaches the wire in Step 5.
- Migrate existing hand-onboarded members into the model. Every existing
  member-name exception becomes a model entry with a mandatory `reason` field.
  Delete the name-exception path as an onboarding mechanism.
  - **Done.** 57 entries in `ProjectionOverrides`, each with a reason, plus 6
    type-ownership entries in `ProjectionTypeOwnership` replacing the six
    hardcoded type names inside the model. `AnalyzerLocalFacadeEmitter`'s
    ~400 lines of `method.Name == "..."` matching are gone; it now looks the
    call up by canonical id. Regenerating produced a **byte-identical** tree
    apart from the manifest and the identity constant, which is what says the
    migration moved the rules rather than rewriting them.
- Compute the transitive closure from analyzer-facing roots and mark everything
  outside it unsupported **with a reason chain**. Expect the declared-unsupported
  set to be large. That is the point.
  - **Computed and reported; not yet withdrawn.** `ProjectionClosure` walks
    from 13 declared roots and records the edge each type was reached by, and
    the model carries `reachable` / `reachedBy`. See the measured result below.
- Add the generator validation passes that can run now: canonical id uniqueness,
  ABI symmetry, factory coverage.
  - **Done.** `ProjectionValidation` runs on every model construction and fails
    generation: canonical id uniqueness across calls and members; every
    override, ownership entry, and closure root matching a real member or type;
    every supported call occupying exactly one vtbl slot that agrees with what
    the call records; no unsupported call in a vtbl; no duplicate slot names
    within a vtbl; and every type crossing as a handle having a proxy factory.

**Exit:** generator output is a function of the model; no member reaches the ABI
without a model entry; the unsupported set is explicit and reasoned.

**Closes:** 8, 17, most of 1.

### Measured result (2026-08-17)

663 types, 5,696 supported calls, 3,034 unsupported, 57 overrides, 713 vtbls.

The closure result contradicts what this step predicted. **609 of 663 types are
reachable from the analyzer-facing roots, leaving 54 unreachable types and 462
supported calls — 8% of the ABI, not the large set expected.** The unreachable
set is coherent: source generators and their driver, command-line parsing,
`RuleSet`, analyzer references and loaders, `CompilationWithAnalyzers`, and the
diagnostic formatters. Nothing an analyzer holds leads to any of it.

Two corrections were needed before that number meant anything, and both are
worth recording because they are the shape of mistake this analysis invites:

- The first closure missed **every analysis context type**, because the only
  edge to `SyntaxNodeAnalysisContext` runs through the `Action<T>` parameter of
  `RegisterSyntaxNodeAction`, and a delegate has no ABI plan to traverse.
  Reachability is now read off the C# signature, not the projected one — it
  describes what an analyzer can reach, not what the ABI carries today.
- Static classes have no instance for traversal to arrive on, so extension
  surfaces fell out entirely, `ModelExtensions.GetDeclaredSymbol` among them —
  a member the burn-down already names as blocking two rules. A static class is
  now reachable when any of its members mentions a reachable type, which
  over-approximates deliberately: keeping a member that is never called costs
  bytes, withdrawing one an analyzer calls costs a diagnostic.

**Withdrawing the unreachable set is deferred.** At 8% it is a trimming
optimization rather than the legibility win the step was after, and it is the
one change here that alters behavior — everything else regenerated
byte-identical. It should land with the module matrix as its measurement — and
specifically with an `--all-modules` sweep, since a trimming change is exactly
the case where the four representatives need re-confirming — not folded into a
model refactor.

---

## Step 3 — Ownership

**Goal:** the "local object executing remote members" crash class becomes
unrepresentable. A two-way local/remote split already exists; this widens it to
five and makes it a model field rather than a runtime convention.

- Every projected type gets a required ownership field: Remote, Value, Local,
  Dual, Facade. The generator refuses to emit a type without one.
  - **Done.** All 700 types carry an ownership class and a reason, from a
    declared entry or from a named derivation rule, and `ProjectionValidation`
    fails generation on a type without one. The count rose from 663 because
    `Types` was previously seeded only from proxies and vtbls, which made every
    analyzer-local type — and every type whose members are all unsupported —
    invisible to the model that was supposed to describe them.
- Rework **Dual** types (`Location`, `Diagnostic`, `SourceText`) into a single
  sealed class with an internal discriminator and two internal implementations —
  not two types. Casts and pattern matches then behave, and every member
  dispatches on the discriminator instead of assuming a handle.
  - **Done for `Location` and `Diagnostic`.** Each had a hand-written
    analyzer-local subclass alongside the generated proxy; both are gone. There
    is now one runtime type per Dual type, the discriminator is the absence of
    a control vtbl, and each member's local branch is a declared override with
    a reason rather than an override in a second class.
  - `DiagnosticDescriptor` already had this shape and was left alone.
    `LocalizableString` **deliberately keeps more than one type**: it is public
    and abstract in Roslyn with a protected constructor, so analyzers may
    subclass it, and collapsing it would change the API rather than the
    transport. `SourceText` is not currently projected.
- Give **Local** types (`DiagnosticDescriptor`, `SymbolDisplayFormat`,
  `SyntaxAnnotation`) real analyzer-side implementations.
  - **Not done, and the measurement says not to.** `SymbolDisplayFormat` and
    `SyntaxAnnotation` are compiler-owned today and work that way; formatting a
    symbol is the compiler's job and a local reimplementation would have to
    reproduce it exactly. No rule in the burn-down names either. Revisit when
    one does.
- Make `SymbolEqualityComparer.Default` a plain singleton, with comparer
  identity transported as an enum tag rather than marshalled as a compiler
  object.
  - **Done.** The enum tag existed already; what was missing is that the type
    still had a proxy factory, so a compiler-side comparer could arrive as a
    handle regardless. Ownership now forbids that, and the dead
    `ISymbolEqualityComparerVtbl` and `ILocalizableResourceStringVtbl` were
    dropped from the ABI.

**Exit:** no type in the model lacks an ownership class; no local object can
reach a remote vtable.

**Closes:** 4, 13, 9.

### Measured result (2026-08-17)

Ownership stopped being a label and became the single predicate that decides
transport. `ProjectionTypeOwnership.CanCrossAsHandle` is now consulted by the
ABI classifier, the proxy collector, and proxy-factory emission; before, the
same question was answered three incompatible ways — an `IsAnalyzerLocalClass`
name list, a hardcoded `"Microsoft.CodeAnalysis.AttributeData"` metadata name in
the collection classifier, and an unstated assumption in the classifier that
every facade type was compiler-owned. The new validation pass found two real
contradictions on its first run: `LocalizableResourceString` and
`SymbolEqualityComparer` were declared analyzer-local and had proxy factories
anyway.

**The larger find was not in the plan.** Abstract members were declared
unsupported by a single rule — *"Declaration-only members have no facade
implementation body"* — because a class-level body is the only place the
emitter knew to put one. A proxied abstract class does have another: its
generated proxy's override. The rule was therefore wrong rather than
conservative, and its cost was **227 unconditional
`PlatformNotSupportedException` bodies across 41 proxied abstract classes**. A
compiler-owned `Diagnostic` could not report its own `Id`, `Severity`,
`Location`, or `Descriptor`; a compiler-owned `Location` had no `Kind`. Routing
proxy overrides through the model gave 82 of them real remoting bodies.
Protected members stay unsupported: the compiler cannot dispatch to them.

Two more consequences fell out of ownership being asked properly:

- `ImmutableArray<T>` return support asked whether `T` was a *dynamic-interface*
  proxy candidate, which excluded every class with a generated proxy subclass —
  hence the `AttributeData` name exception readmitting one of them by hand.
  Asking instead whether `T` can be proxied at all made `ISymbol.Locations` and
  `ISymbol.DeclaringSyntaxReferences` supported for the first time. Those are
  the members behind **9 of the 26 failing rules**.
- `Location.None` is now a shared analyzer-side singleton, so the value an
  analyzer reads and the one its own diagnostics default to are the same
  object. This does **not** close the `Location.None` item in problem 14: the
  report path still sends `SourceSpan.Start`/`Length` unconditionally, so an
  unlocated diagnostic still arrives as `(1,1)`. The wire needs a way to say
  "unlocated", which is Step 6.

Model totals moved from 5,696 supported / 3,034 unsupported / 663 types to
5,823 / 2,907 / 700, with overrides from 57 to 78.

### The regression this step found (2026-08-17)

Making `ISymbol.Locations` work let `CA1508` get past its old failure and into
the analyzer utilities' dataflow analysis for the first time — where it hit
`IOperation.Accept<TArgument, TResult>` and NativeAOT **terminated the compiler
process**. Six corpus cases crashed identically, and two rules that had been
passing, `CA1309` and `CA1841`, went to `CompilerCrash` purely because they
shared a process with an analyzer that reached it.

Nothing about the crash was caused by this step: `IOperation`'s projection is
byte-identical before and after, and both `GetControlFlowGraph` overloads were
already supported. The path was always fatal; no analyzer had reached it. This
is the clearest argument yet for the differential harness — the defect was two
Pass rules away from shipping silently, and it is the only failure class in the
inventory that destroys *other* analyzers' results.

Recorded as problem 21 and mitigated by withdrawing the three
`GetControlFlowGraph` members, so an analyzer that wants a control flow graph
gets an `AD0001` naming the member instead of killing the build.

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
- **Memoize immutable collection-valued members on the canonical proxy.** Roslyn
  returns the same cached object on every get; the facade re-crosses and
  re-materializes. Measured on the corpus, `IAssemblySymbol.NamespaceNames`
  alone is 68.8% of all boundary traffic — 890,664 calls, because
  `WellKnownTypeProvider` prefilters every metadata-name lookup against all 177
  referenced assemblies' namespace sets, and each get costs `1 + 2N` crossings
  through `ReadStringCollection`. Memoizing per proxy takes that to 177. Which
  members are safe to memoize is a model field, so this half lands after Step 2
  even though the identity work above does not need to wait.

**Note on the exit criterion below.** CA1508's live failure is now
`ISymbol.Locations`, not identity, so it will not verify this step until that
member is projected. Use a reference-identity probe and the rules currently
failing on `DeclaringSyntaxReferences` as the signal instead.

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

**Generic virtual dispatch has been separated out and half-solved.** A generic
virtual method on a proxied type cannot be dispatched through
`IDynamicInterfaceCastable`; NativeAOT's type loader *fails fast* rather than
throwing, killing the compiler and every other analyzer's diagnostics. That is
no longer reachable: these members are emitted `sealed`, so they are
non-virtual, resolve directly to the facade body, and raise a catchable
exception instead. `GetControlFlowGraph`, withdrawn as a tourniquet in Step 3,
is restored.

What remains for this step is making them *work*. Two distinct pieces:

- **Dispatch** needs a statically implemented shim on the proxy for ILC to build
  real GVM slots against. Seven signatures cover the whole surface, and the
  mechanism is verified for reference and struct instantiations alike.
- **Marshalling** is the separate problem: a generic member whose type argument
  must be *represented* in the transport cannot be resolved reflectively —
  `MakeGenericMethod` and `MakeGenericType` do not work for struct
  instantiations under NativeAOT — so those need reference-erasure, boxing the
  value so one shared implementation serves every instantiation.

See [generic virtual dispatch](GENERIC-VIRTUAL-DISPATCH.md).

**Exit:** the declared-unsupported set contains no public analyzer-facing member
that the corpus reaches, and no reachable member can terminate the compiler.

**Closes:** the rest of 1, the transport half of 5, and the rest of 21.

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
