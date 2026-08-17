# Generic virtual dispatch across the analyzer boundary

## Why this has its own document

Every other gap in the [problem inventory](ANALYZER-REMOTING-PROBLEMS.md) fails
the same way: an analyzer calls something unimplemented, the facade throws
`PlatformNotSupportedException`, the compiler reports `AD0001`, and the rest of
the compilation is unaffected.

This one terminates the compiler.

```
Process terminated. Generic virtual method pointer lookup failure.

Declaring type: Microsoft.CodeAnalysis.IOperation
Target type: RoslynAot.RoslynFacade.RoslynObjectProxy
Method name: Accept
Instantiation:
  Argument 00000000: System.Object
  Argument 00000001: ...PointsToAnalysis.PointsToAbstractValue

   at Internal.Runtime.TypeLoader.TypeLoaderEnvironment.GVMLookupForSlotWorker
   at Internal.Runtime.Dispatch.ResolveGvmDispatch
   at ...DataFlowOperationVisitor`4.VisitCore(IOperation, Object)
```

Three properties make it categorically worse than a missing member:

1. **It destroys unrelated work.** Analyzers share the compiler process. When
   `CA1508` reached this path, `CA1309` and `CA1841` — both passing — lost
   their diagnostics because the process died underneath them.
2. **It cannot be caught.** The failure is a `FailFast` inside the runtime type
   loader, below any managed frame the bridge controls. No `try`/`catch`, no
   per-analyzer guard in the current process model, contains it.
3. **It is silent until it isn't.** The facade *does* emit
   `Accept<TArgument, TResult>` with a `PlatformNotSupportedException` body. The
   member looks implemented-and-declined. It is simply never reached.

## What actually fails

`IDynamicInterfaceCastable` resolves *interface dispatch* at runtime:
`RoslynObjectProxy` claims to implement `IOperation`, and the runtime routes
calls to default interface methods on `__RoslynAotImplementation`.

Generic virtual methods do not go through that path. They resolve through a
separate GVM slot-mapping table that ILC builds ahead of time, keyed on the
concrete target type. `RoslynObjectProxy` does not statically implement
`IOperation`, so it has no entry in that table for `IOperation.Accept<,>`, and
the type loader fails fast rather than falling back.

**Confirmed by experiment, 2026-08-17.** A forty-line NativeAOT program with a
hand-written `IDynamicInterfaceCastable` proxy and one generic virtual method
reproduces it exactly. The non-generic default interface method returns
normally; the generic one fails fast — **with reference type arguments**
(`<object, string>`), canonically shared, and with the call site directly
visible to ILC in the same assembly.

So the cause is neither value-type specialization nor an instantiation ILC
could not see. `IDynamicInterfaceCastable` and GVM slot resolution are simply
disjoint mechanisms: the runtime looks for a GVM slot mapping on the concrete
target type, `RoslynObjectProxy` does not statically implement the interface,
and there is nothing to find.

## The constraint that removes the obvious workaround

Under NativeAOT there is no JIT, so every instantiation that needs distinct
machine code must exist before the program runs. Reference type arguments share
one canonical body; **value type arguments do not**. Consequently
`MakeGenericMethod` and `MakeGenericType` do not work for struct
instantiations, and any design that resolves a generic instantiation
reflectively at runtime is unavailable to us the moment a type argument is a
struct.

Roslyn's analyzer surface has struct and enum type arguments in exactly these
positions, so "look it up reflectively" is not a fallback we can keep in
reserve. Whatever the answer is, it has to be computed ahead of time.

## Size of the surface

Measured from the projection model, 2026-08-17:

| Measure | Count |
|---|---|
| Projected types | 700 |
| Types declaring a generic method | 266 |
| **Of those, dynamic-interface proxied — the hazard** | **252** |

252 is not a corner. The facade turns Roslyn's sealed syntax classes into
interfaces so they can be dynamic-proxied, so `ClassDeclarationSyntax` is a
facade *interface* declaring
`Accept<TResult>(CSharpSyntaxVisitor<TResult> visitor)`. The hazard therefore
covers substantially the whole C# syntax tree and the operation tree, and
`CSharpSyntaxVisitor<TResult>` is an ordinary thing for an analyzer to use.

Only one instantiation has been observed failing so far because only one
analyzer has reached that far. The exposure is much larger than the
observation.

## Should `IDynamicInterfaceCastable` be retired instead?

It was worth asking, because `IDynamicInterfaceCastable` is a COM-shaped
mechanism for discovering an object's interfaces at runtime, and our type graph
is fully known when the facade is generated. On the face of it we pay for late
binding we do not need.

**Measurement says no.** Benchmarked under NativeAOT on a trivial interface
member, 20M calls per configuration, best of three rounds:

| Case | ns/call |
|---|---|
| `IDynamicInterfaceCastable`, one proxy type — as deployed | **1.98** |
| `IDynamicInterfaceCastable`, two proxy types | 1.78 |
| Concrete class, monomorphic call site | 3.15 |
| Concrete class, polymorphic call site | 3.15 |

Dynamic interface dispatch is about **1.2 ns/call faster**, not slower, and
call-site polymorphism does not move either number. The likely reason is that
the single default-interface-method body inlines into the resolved dispatch
target while the concrete class keeps a real virtual call — but whatever the
cause, the direction is not in doubt.

`CachedInterfaceDispatch.RhpCidResolve_Worker` appearing in the failfast stack
is the cache-*miss* path. It is not what steady-state calls take, and reading a
cost off that stack frame was simply wrong.

For scale: the whole 34-case differential corpus makes 1,296,285 boundary calls.
The entire dispatch difference across all of it is **about 1.6 ms**, in
`IDynamicInterfaceCastable`'s favour.

**Conclusion: `IDynamicInterfaceCastable` stays.** There is no performance case
for removing it, and the capability it provides is real — a handle typed
`ISymbol` must be castable to `INamedTypeSymbol` later, which one proxy type
gives for free. A concrete-proxy design would have to solve that by
materializing the right class from a runtime shape id, walking the shape lattice
to the nearest retained factory, and that is strictly more machinery for a
slower result.

That makes the shims below the **durable** fix rather than a stopgap: generic
virtual dispatch is the one thing `IDynamicInterfaceCastable` genuinely does not
do, so it is the one thing that needs handling around it.

## The mechanism that works

`IDynamicInterfaceCastable` is half-implemented for this purpose: it carries
ordinary interface dispatch but not generic virtual dispatch. The surface that
needs generic dispatch therefore has to be **statically shimmed** onto the proxy
so ILC builds real GVM slots for it.

A second experiment confirms this works, and that the shim is far cheaper than
one-proxy-per-type. A proxy that statically implements *only* the
GVM-declaring base interface, while every other interface still arrives through
`IDynamicInterfaceCastable`:

```
plain ok                                    <- DIM, still works
other ok                                    <- DIM on a second interface
[dispatched Object/String] via IThing       <- GVM through a DIM-served interface
[dispatched Object/Int32]  via IThing       <- struct type argument
[dispatched Int32/String]  via IThing       <- struct type argument
[dispatched Object/String] via IOther       <- second DIM-served interface
[dispatched Object/String] via IBase        <- direct
exit=0
```

Three properties make this the design rather than a workaround:

- **Derived interfaces inherit the fix.** `IThing` and `IOther` were served by
  `IDynamicInterfaceCastable` and still dispatched their inherited generic
  method correctly. The shim goes on the base that *declares* the generic
  member, not on all 252 types that expose it.
- **Struct instantiations work.** ILC generates the specialized bodies because
  it can see the call sites — and it can, because the analyzer assembly is
  compiled into the same NativeAOT module as the facade. The closed world is
  automatic; no instantiation scanning, and no reliance on `MakeGenericMethod`,
  which would not work for struct arguments anyway.
- **Trimming is preserved.** Rooting a small number of declaring interfaces is
  not rooting a concrete proxy per type.

### Where the generic dispatch should land

For the `Accept` family the answer is that it should not cross the boundary at
all. Roslyn's `node.Accept(visitor)` is `visitor.VisitClassDeclaration(this)` —
the visitor is analyzer-owned, so the proxy can dispatch on its own kind to the
visitor's method entirely analyzer-side, and `TResult` never touches the wire.

That leaves genuine ABI-crossing generics — members whose type argument has to
be *represented* in the transport — as a separate and smaller problem.
Reference-erasing those, so one shared implementation serves every
instantiation, is the right treatment and belongs with the Step 8 wire work
rather than here.

### Rejected alternatives

| Option | Why not |
|---|---|
| A concrete class proxy per GVM-declaring type | Roots 252 types' surface into every module, abandoning trimming, and the shim achieves the same dispatch for a fraction of the cost |
| Remove the generic member from the facade | The analyzer's IL references it; the assembly would not bind |
| Scan for a closed instantiation set and root each one | Killed by experiment: the failing instantiation was already rooted and directly visible to ILC. Rooting is not what is missing |
| Refuse to project types declaring generic virtual methods | Withdraws the syntax tree, which is most of what analyzers do. Retained only as the narrow mitigation now in place |

## Size of the shim set

Measured 2026-08-17. The 252 hazard types collapse to **seven distinct generic
method signatures**:

| Occurrences | Signature | Declared on |
|---|---|---|
| 251 | `Accept<TResult>(CSharpSyntaxVisitor<TResult>)` | every syntax node interface |
| 1 | `Accept<TArgument, TResult>(OperationVisitor<TArgument, TResult>, TArgument)` | `IOperation` |
| 1 | `Accept<TResult>(SymbolVisitor<TResult>)` | `ISymbol` |
| 1 | `Accept<TArgument, TResult>(SymbolVisitor<TArgument, TResult>, TArgument)` | `ISymbol` |
| 1 | `CopyAnnotationsTo<T>(T?)` | `SyntaxNode` |
| 1 | `FirstAncestorOrSelf<TNode>(...)` | `SyntaxNode` |
| 1 | `FirstAncestorOrSelf<TNode, TArg>(...)` | `SyntaxNode` |

One prerequisite makes the 251 collapse to one. The facade currently
**re-declares** `Accept<TResult>` on each derived syntax node interface, because
Roslyn's classes each `override` it. In an interface a re-declaration is a new
slot that hides the base's, so a shim on `CSharpSyntaxNode` would not cover
`ClassDeclarationSyntax.Accept<TResult>`. An interface has no need to restate an
inherited member, so the generator should stop emitting overrides of generic
virtual members on derived facade interfaces — after which one shim on the
declaring type serves the whole hierarchy.

## Open questions

1. **Does the kind-based local dispatch stay trimmable?** A switch over every
   syntax kind is an exhaustive table, which is the hazard the migration plan
   names. It is only reachable when an analyzer calls `Accept`, but that needs
   confirming against the module baseline rather than assuming.
2. **What happens when a shim is missing?** A member that needs a shim and does
   not have one is a process kill, not a diagnostic. Generation should be able
   to detect a projected generic virtual member with no shim and refuse, the
   way `ProjectionValidation` already refuses other structural mistakes.
3. **Is containment worth pursuing independently?** Nothing in-process can
   catch a `FailFast`. Isolating analyzer execution is a much larger change,
   but it is the only thing that makes this class survivable rather than
   merely avoidable. Worth keeping on the list precisely because the shim
   approach is only as good as its coverage.
4. **Do other generic shapes need marshalling, not just dispatch?** Generic
   virtual *dispatch* is fixed by shimming. A generic member whose type
   argument must be **represented** in the transport is a different problem:
   `MakeGenericType`/`MakeGenericMethod` do not work for struct instantiations
   under NativeAOT, so those need reference-erasure — boxing the value so one
   shared implementation serves every instantiation — rather than a shim. That
   belongs with the Step 8 wire work.

## Current mitigation

Migration Step 3 withdrew the three `GetControlFlowGraph` members, which are
the route into the analyzer utilities' dataflow analysis and the only observed
path to a GVM call. That converts the process kill into an `AD0001` naming
`GetControlFlowGraph`, at the cost of the rules that need a control flow graph.

This is a tourniquet, not a fix. It closes the one path the corpus reaches; it
does nothing about the other 251 types, and the corpus reaching a second path
is a matter of the burn-down improving.
