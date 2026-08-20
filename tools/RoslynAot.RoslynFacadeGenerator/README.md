# Roslyn facade generator

This tool will generate the Roslyn facade assemblies and their corresponding
compiler-side COM interop surface. These details are intentionally documented
here rather than in the repository roadmap because they describe the design of
this generator, not the overall RoslynAot architecture.

The design is provisional. Roslyn's complete API surface will expose cases that
require revisiting the projection rules.

## Current status

The inspect command validates managed assembly inputs and inventories their
public and protected API surface:

```bash
dotnet run \
  --project tools/RoslynAot.RoslynFacadeGenerator \
  -- inspect \
  path/to/Microsoft.CodeAnalysis.dll \
  path/to/Microsoft.CodeAnalysis.CSharp.dll
```

The generate command writes executable facade sources and their synchronized
ABI, compiler dispatch, and manifest outputs. Its input assemblies and its
`--reference` set are not free choices — they decide every generated vtbl id
and operation name — so the invocation that reproduces the checked-in tree is
given under [Regenerating the checked-in
tree](#regenerating-the-checked-in-tree) rather than sketched here.

The generated types are partial. Members covered by the initial projection
receive executable COM proxy bodies. Other concrete constructors, methods,
operators, and accessors throw `PlatformNotSupportedException` carrying the
model's reason for the member being unsupported — `Return type is unsupported:
Generic substitutions are not supported.` rather than a bare "not implemented",
so an analyzer author who hits one can tell a per-member gap from a whole class
of API without going to the manifest. Abstract and interface members retain
declaration-only bodies. Binding-relevant assembly
attributes, including Roslyn friend-assembly declarations, are preserved.
GenAPI's usual `ReferenceAssemblyAttribute` is deliberately omitted because
these facades must execute.

Coverage is whatever the model's rules currently classify as supported, and
the exact numbers are the first lines of
`Projection/Manifest/ProjectionInventory.txt` — 5,824 supported calls and
2,804 unsupported across 695 types at the time of writing. Read them there
rather than from prose here: they change with every projection rule, and the
manifest is checked in so the change is a reviewable diff. The generated
facade projects under `src/RoslynAot.GeneratedFacade*` compile the complete
emitted surface, while the ABI and compiler dispatch trees are wired into
`RoslynAot.Abi` and `CscAot`.

Generated source mirrors the API structure on disk:

```text
roslyn-facade/
  Facades/
    Runtime/
      RoslynFacadeRuntime.g.cs
    Microsoft.CodeAnalysis/
      AssemblyInfo.cs
      Microsoft/
        CodeAnalysis/
          SyntaxNode.cs
          SyntaxTree.cs
          Diagnostics/
            DiagnosticAnalyzer.cs
    Microsoft.CodeAnalysis.CSharp/
      AssemblyInfo.cs
      Microsoft/
        CodeAnalysis/
          CSharp/
            CSharpCompilation.cs
  Abi/
  Compiler/
  Manifest/
```

Each assembly receives its own root directory. Namespace components become
directories, and each top-level type receives a source file. Nested types stay
in the file containing their declaring type. Generic arity is appended only
when needed to distinguish legal same-name types such as `Name` and `Name<T>`.

## Regenerating the checked-in tree

`src/RoslynAot.Generated/Projection` is generator output that is committed, and
nothing rebuilds it during a normal build — the compiler and the analyzer both
compile the checked-in files, so the tree is the source of truth rather than a
cache of one. The generator runs when someone deliberately moves the
projection, and a change to a projection rule or a table entry owes a
regeneration in the same commit, otherwise the tables describe a tree that does
not exist.

Regenerating is reproducible, which matters for one practical reason: a regen
diff should be attributable to the change you made. If the input assemblies
drift with whatever SDK is installed, the diff also contains surface drift, and
the two cannot be told apart — see the history of issue #9 for what that costs.
`ManifestIdentity` in `Abi/RoslynControlVtbl.g.cs` is a separate mechanism and
does not help here: both sides read it out of this same tree, so it catches a
compiler and an analyzer built from *different commits*, not a tree built from
different inputs.

### The projected Roslyn

The projection is generated from the NuGet `Microsoft.CodeAnalysis.Common` and
`Microsoft.CodeAnalysis.CSharp` **5.0.0** packages. Both are named as
`PackageReference`s with `GeneratePathProperty="true"` in
`RoslynAot.RoslynFacadeGenerator.csproj`, so the version is pinned in the
project file and the regeneration command reads the paths back out rather than
hunting for assemblies. (The GenAPI fork under `Upstream/` references the same
version for its own compilation; the three references are not centrally pinned,
so moving one means moving all of them.)

That package version is the Roslyn analyzers themselves compile against — the
sample analyzer references `Microsoft.CodeAnalysis.CSharp` 5.0.0 — and it is
what gives the generated facades their `AssemblyVersion` of `5.0.0.0`, the
version an analyzer's strong-name reference asks for. The .NET SDK's own
Roslyn under `$(RoslynTargetsPath)/bincore` is deliberately not the baseline:
it is whatever the installed SDK happens to ship (5.10.0.0 today), so
generating from it would move vtbl GUIDs and operation-name hashes across most
of the tree every time the SDK moved. The compiler side still compiles the
generated dispatch against the SDK's Roslyn; the baseline surface has to stay a
subset it can satisfy.

The `net11.0` reference pack passed with `--reference` is required, not an
optimization. Without it the run fails projection validation with one
`Declared foreign type ... appears in no projected signature.` error per
declared foreign type, because nothing the projection declares as foreign
resolves.

### The invocation

Run from the repository root:

```bash
generator=tools/RoslynAot.RoslynFacadeGenerator/RoslynAot.RoslynFacadeGenerator.csproj
common="$(dotnet msbuild $generator \
  -getProperty:PkgMicrosoft_CodeAnalysis_Common)"
csharp="$(dotnet msbuild $generator \
  -getProperty:PkgMicrosoft_CodeAnalysis_CSharp)"
packs="$(dotnet msbuild src/CscAot/CscAot.csproj \
  -getProperty:NetCoreTargetingPackRoot)"
runtime="$(dotnet msbuild src/CscAot/CscAot.csproj \
  -getProperty:BundledNETCoreAppPackageVersion)"
references="$packs/Microsoft.NETCore.App.Ref/$runtime/ref/net11.0"

reference_arguments=()
for assembly in "$references"/*.dll; do
    reference_arguments+=(--reference "$assembly")
done

dotnet run \
  --project tools/RoslynAot.RoslynFacadeGenerator -c Release \
  -- generate \
  --output src/RoslynAot.Generated/Projection \
  "${reference_arguments[@]}" \
  "$common/lib/netstandard2.0/Microsoft.CodeAnalysis.dll" \
  "$csharp/lib/netstandard2.0/Microsoft.CodeAnalysis.CSharp.dll"
```

The output directory is the checked-in tree itself. Every subdirectory the
generator owns — `Facades`, `Abi`, `Compiler`, `AnalyzerRuntime`, `Manifest` —
is recreated on each run, so stale files cannot survive a regeneration, and
`git status` is the diff to review. A run takes roughly two minutes.

## Source generation

The facade declaration generator is based on a fork of the .NET SDK's
`Microsoft.DotNet.GenAPI` source. GenAPI already handles the difficult metadata
to C# declaration conversion, including nested and generic types, attributes,
constraints, overloads, and language syntax.

The relevant GenAPI implementation is vendored under `Upstream/` from
`dotnet/sdk` tag `v10.0.100`. Each vendored file retains its original license
header, and `Upstream/README.md` records the exact source commit.

The fork differs from ordinary GenAPI output in two important ways:

1. Generated declarations must be executable facade implementations rather
   than reference-assembly stubs.
2. Generating one Roslyn member must also generate its matching COM operation
   and compiler-side implementation.

## Direct COM projection

The default mapping is one public Roslyn API member to one generated COM
interop call. The facade should alter the API as little as the native ABI
allows.

For example, a property such as:

```csharp
public int RawKind { get; }
```

will receive a facade body equivalent to:

```csharp
public int RawKind
{
    get
    {
        ThrowIfFailed(
            _host.SyntaxNode_get_RawKind(_handle, out int value));
        return value;
    }
}
```

The generated compiler-side implementation will be equivalent to:

```csharp
int SyntaxNode_get_RawKind(int handle, out int value)
{
    value = Get<SyntaxNode>(handle).RawKind;
    return Success;
}
```

The generator therefore produces three synchronized outputs from the same
Roslyn symbol:

1. The facade declaration and method body.
2. The generated COM interface operation.
3. The compiler-side dispatch implementation that invokes real Roslyn.

## Emit strategy

The COM projection must be generated mechanically. Handwritten code is limited
to the transport runtime, the object table, error handling, and explicit
overrides for signatures that cannot use a general projection rule. Individual
Roslyn members are not implemented by hand.

The generator performs one symbol traversal and creates an in-memory projection
model. All generated outputs consume that same model:

```text
Roslyn assemblies
    -> GenAPI symbol traversal
    -> projection model
       -> facade source
       -> COM ABI source
       -> compiler dispatch source
       -> compatibility manifest
```

The projection model records, for every member:

- Its canonical metadata signature and containing assembly.
- Its deterministic generated operation name.
- Whether it is an instance, static, constructor, property, event, or operator
  operation.
- The ABI representation of the receiver, parameters, and result.
- The facade expression used to convert each argument to its ABI
  representation.
- The compiler expression used to resolve each ABI value to the real Roslyn
  value.
- Its generated implementation strategy or the reason it is unsupported.

The model is in-memory generator state, not a checked-in member database.
Projection behavior comes from general type and signature rules. A small
explicit override table is allowed only for semantic exceptions.

The implementation classifies ABI types compositionally, then derives member
strategies from method shape, accessibility, receiver kind, accessor kind,
static/constructor semantics, and the `IDisposable.Dispose` pattern. It does
not select strategies by Roslyn type or member name. The deterministic
`Manifest/ProjectionInventory.txt` records every classification; the initial
override table is empty.

### GenAPI integration

The vendored GenAPI emitter remains responsible for declarations. It has an
implementation callback at the point where it emits a concrete member body.
The callback receives the original Roslyn symbol and the corresponding
projection plan.

The generator must not recover member identity by parsing the emitted source
and matching C# names afterward. That would be unreliable for overloads,
operators, explicit interface implementations, nested generic types, custom
modifiers, and members whose C# spelling differs from their metadata name.

The existing source-layout pass remains a final formatting operation after
facade generation. It does not participate in ABI design.

### Generated output

One invocation emits four related trees:

```text
roslyn-facade/
  Facades/
    Runtime/
    Microsoft.CodeAnalysis/
    Microsoft.CodeAnalysis.CSharp/
  Abi/
    RoslynControlVtbl.g.cs
    ISyntaxNodeVtbl.g.cs
    ISyntaxFactoryVtbl.g.cs
    ISyntaxTokenParserVtbl.g.cs
  Compiler/
    RoslynDispatcherRegistry.g.cs
    SyntaxNodeVtblDispatcher.g.cs
    SyntaxFactoryVtblDispatcher.g.cs
    SyntaxTokenParserVtblDispatcher.g.cs
  Manifest/
    ProjectionInventory.txt
```

The generator emits one instance vtbl interface per projected facade type and
a separate type vtbl for constructors and static members. Instance vtbl
inheritance follows the facade class hierarchy when possible. `RoslynInterop`
implements only the small control vtbl. It lazily creates and caches one
generated dispatcher CCW per requested vtbl type; those dispatchers share its
handle table and error state. Roslyn objects never receive CCWs.

The generated COM interface declarations currently include support for both
wrapper directions. A future size optimization should emit compiler-side
CCW-only declarations and analyzer-side RCW-only declarations with identical
IIDs and signatures. This is intentionally deferred until after the generated
facade analyzer path is validated end to end.

The manifest records the input assembly identities, canonical operation
signatures, generated names, per-type vtbl IDs, and unsupported
reasons. The analyzer validates the manifest identity when it obtains
`IRoslynControlVtbl` from the compiler pointer.

### Handwritten runtime

The generated code depends on a small handwritten runtime:

- `RoslynInterop`, the compiler-side control object and owner of shared
  projection state.
- A compiler-side handle table with type checking, slot generations, and
  process lifetime.
- Generated per-vtbl dispatchers and a lazy dispatcher registry.
- Generated helpers that request and cache the required per-type RCWs.
- Facade helpers for HRESULT translation and proxy construction.
- Compiler helpers for exception capture and handle creation, lookup, release,
  and disposal.

Roslyn objects do not receive individual COM callable wrappers. A facade object
is an ordinary managed object containing its generated per-type vtbl and an
opaque 64-bit handle. Base and derived facade parts use the same inherited vtbl
object where possible and share one `IRoslynControlVtbl` identity. Complex
facade structs use the same representation. Only
`RoslynInterop`, generated per-vtbl dispatchers, and reverse analyzer callback
services are COM objects.

### ABI type rules

The ABI assembly does not reference Roslyn assemblies. Its signatures contain
only explicitly sized ABI values. The classification lives in
`AbiTypeKind`, and this table is its documentation:

| Roslyn signature type | ABI representation | `AbiTypeKind` |
| --- | --- | --- |
| `void` | HRESULT only | `Void` |
| Integral primitive | Same-width integral value | `Integral` |
| `bool` | 32-bit normalized integer | `Boolean` |
| Enum | 32-bit normalized integer | `Enum` |
| `string` | Caller-provided UTF-16 buffer, two-call copy | `Utf16String` |
| Reference parameter/receiver/result | 64-bit object handle | `ObjectHandle` |
| Complex value type | 64-bit value handle | `ValueHandle` |
| Nullable remote value | Zero or a 64-bit handle | `NullableHandle` |
| Reference sequence | Collection handle, counted and indexed | `ObjectCollection` |
| Reference array | Collection handle built from element handles | `ObjectArray` |
| String sequence | Collection handle, item-copied or membership-tested | `StringCollection` |
| Boxed constant | Kind tag plus a two-word payload | `ConstantUnion` |
| `Optional<object>` | Constant union with a has-value tag | `OptionalConstant` |
| Unsupported type | No operation; facade retains an unsupported body | `Unsupported` |

Delegates, generic substitutions, pointers, function pointers, and nontrivial
`ref` parameters remain unsupported; they will add projection rules rather than
per-member implementations. Types with externally visible non-const instance
fields remain unsupported until field state can be mirrored. Non-const static
facade fields receive explicit throwing initializers rather than silent default
values; constants retain their original values.

Handles are process-global: one table serves the whole compiler. It
distinguishes object and value entries, validates the expected Roslyn type,
rejects stale handles, and returns the same handle for a reference type that
has already crossed. Disposing a remote `IDisposable` atomically invalidates
its facade handle and disposes and removes the corresponding compiler entry.
Repeated disposal succeeds. For non-nullable facade value types, handle zero
resolves to `default(T)`; nullable handles continue to use zero for `null`.

### Facade templates

An instance property:

```csharp
public int RawKind
{
    get
    {
        ISyntaxNodeVtbl vtbl = __RoslynAotGetVtbl();
        IRoslynControlVtbl controlVtbl = (IRoslynControlVtbl)vtbl;
        RoslynFacadeRuntime.ThrowIfFailed(
            controlVtbl,
            vtbl.SyntaxNode_get_RawKind(
                _handle,
                out int value));
        return value;
    }
}
```

A method returning a remote reference type:

```csharp
public SyntaxTokenParser CreateParser()
{
    IExampleVtbl vtbl = __RoslynAotGetVtbl();
    IRoslynControlVtbl controlVtbl = (IRoslynControlVtbl)vtbl;
    RoslynFacadeRuntime.ThrowIfFailed(
        controlVtbl,
        vtbl.Example_CreateParser(
            _handle,
            out long result));
    return SyntaxTokenParser.__RoslynAotCreateProxy(controlVtbl, result);
}
```

A method returning a complex value type:

```csharp
public SyntaxTokenParser.Result ParseNextToken()
{
    ISyntaxTokenParserVtbl vtbl = __RoslynAotGetVtbl();
    IRoslynControlVtbl controlVtbl = (IRoslynControlVtbl)vtbl;
    RoslynFacadeRuntime.ThrowIfFailed(
        controlVtbl,
        vtbl.SyntaxTokenParser_ParseNextToken(
            _handle,
            out long result));
    return SyntaxTokenParser.Result.__RoslynAotCreateProxy(
        controlVtbl,
        result);
}
```

Static members obtain `IRoslynControlVtbl` from the facade runtime, then query
the generated type vtbl. Remote-proxy arguments contribute their handles
directly: a module talks to one compiler, so every handle it holds is already
meaningful to that control.

Constructors and static factories become creation calls. Public or
protected Roslyn constructors retain their original declaration, but their
generated body requests creation through the active type vtbl
and stores the returned handle. Internal constructors used only by the facade
accept their per-type vtbl and a handle.

Complex structs receive generated private vtbl and handle fields.
Copying the struct copies the remote value identity. `default` values are
handled according to the real API contract: default-safe Roslyn structs receive
the appropriate local behavior, while structs documented as not default-safe
fail when used.

### ABI templates

Every projected operation returns an HRESULT-style status. A Roslyn return
value becomes the final `out` parameter:

```csharp
[GeneratedComInterface]
[Guid("...")]
internal partial interface ISyntaxNodeVtbl
{
    [PreserveSig]
    int SyntaxNode_get_RawKind(
        long receiver,
        out int value);

    [PreserveSig]
}

[GeneratedComInterface]
[Guid("...")]
internal partial interface ISyntaxTokenParserVtbl
{
    [PreserveSig]
    int SyntaxTokenParser_ParseNextToken(long receiver, out long result);
}
```

Operation names are generated from a readable member name plus a stable suffix
when required for overload disambiguation. Canonical signatures, not metadata
tokens or traversal order, determine names, ordering, vtbl grouping, and vtbl
identity.

### Compiler dispatch templates

The compiler emitter generates one dispatcher class per vtbl. Each dispatcher
delegates shared handle and error operations to its owning `RoslynInterop`:

```csharp
public sealed class SyntaxNodeVtblDispatcher : ISyntaxNodeVtbl
{
    private readonly RoslynInterop _owner;

    public int SyntaxNode_get_RawKind(
    long receiver,
    out int value)
    {
        value = default;

        try
        {
            value = _owner.Objects.GetObject<SyntaxNode>(receiver).RawKind;
            return Success;
        }
        catch (Exception exception)
        {
            return _owner.SetError(exception);
        }
    }
}
```

Remote return values are inserted into the handle table:

```csharp
public int SyntaxTokenParser_ParseNextToken(
    long receiver,
    out long result)
{
    result = default;

    try
    {
        SyntaxTokenParser parser =
            _objects.GetObject<SyntaxTokenParser>(receiver);
        result = _objects.AddValue(parser.ParseNextToken());
        return Success;
    }
    catch (Exception exception)
    {
        return SetError(exception);
    }
}
```

Exceptions never escape through the unmanaged entry point. `SetError` records
enough information for the facade runtime to recreate common argument,
disposal, cancellation, and unsupported-operation exceptions. Other
exceptions become an explicit remote Roslyn exception.
Synchronous error details are stored per calling thread, and the facade
validates both the error-size query and copy operation.

### `SyntaxTokenParser` projection

`SyntaxTokenParser` follows the general rules and does not require handwritten
member implementations:

- `SyntaxFactory.CreateTokenParser` returns an object handle.
- The parser facade stores `ISyntaxTokenParserVtbl` and an object handle.
- Parse methods return value handles containing the real
  `SyntaxTokenParser.Result`.
- `Result.Token` resolves the result value, obtains the real `SyntaxToken`, and
  returns another value handle.
- `Result.ContextualKind` resolves the result and returns an enum value.
- `ResetTo` passes the parser object handle and result value handle.
- `SkipForwardTo` passes the parser handle and position directly.
- `Dispose` uses the generated disposable-object template.

Keeping the real `Result` compiler-side preserves its internal directive stack
without exposing that version-specific implementation detail in the ABI.

## Projection rules

- An instance receiver becomes an opaque object handle.
- A COM operation returns an HRESULT-style status code.
- A Roslyn return value becomes an `out` parameter.
- Roslyn object arguments and results become handles.
- Primitive values and enums cross the ABI directly.
- Properties become getter and setter calls.
- Constructors become creation calls that return a new handle.
- Delegates remain unsupported until callback COM interfaces are introduced.
- Exceptions are caught on the originating side and converted to explicit
  status codes; exceptions never cross the ABI.

Generated operation names are deterministic and unambiguous for overloads; the
stable identity scheme is the canonical id described under
[The model](#the-model).

## The model

Everything the emitters do is decided by `ProjectionModel`, and what the model
decides is written out to `Projection/Manifest/ProjectionInventory.txt` as one
greppable line per type and per call.

Read it for two things, and read the generated code for everything else. The
first is the counts in its header: when a rule change touches a thousand files,
`supported=`, `unsupported=`, `vtbls=` and `types=` are how you see the size and
direction of what you did. The second is a specific member — grep its canonical
id to see the strategy and wire signature the model chose. What the file is
*not* is a review medium. A change to the projected Roslyn version rewrites the
disambiguating hash on every overloaded operation name, so the inventory diff
can run to thousands of lines with a handful of meaningful ones in it; the
generated C# is where a reviewer who knows Roslyn should look.

There was a second manifest, `RoslynProjection.json`, holding the same model in
full. Nothing read it — not the compiler, not the analyzer runtime, not the
harnesses — and it cost 21 MB of the repository and of the shipped package. It
held two things the inventory did not, and both were moved rather than dropped:
the reason each unsupported member could not be projected, which now travels in
the exception that member throws, and the reason behind each type's ownership,
which is now the `ownershipReason=` field on the inventory's `TYPE` lines.

Members are keyed by **canonical id**: `[Assembly]M:Ns.Type.Member(Params)~Return`,
built from `DocumentationCommentId`. The assembly prefix is there because a
documentation comment id is only unique within one assembly. Overloads,
generic arity, ref-ness, and conversion return types are all already
distinguished by that form, which is what makes a table keyed on it incapable
of applying to the wrong overload.

Three tables carry the deliberate deviations, and each entry needs a reason:

| Table | Keyed by | Holds |
|---|---|---|
| `ProjectionOverrides` | member canonical id | A replaced strategy, a corrected return nullability, an analyzer-side body, or an analyzer-side field initializer |
| `ProjectionTypeOwnership` | type canonical id | Which side owns a type's state, where it differs from the derived default |
| `ProjectionClosure` | type canonical id | The analyzer-facing roots the reachability walk starts from |

### Ownership decides what may cross

Every type carries one of five ownership classes — Remote, Value, Local, Dual,
Facade — and a reason for it, either from a declared entry or from a named
derivation rule. Ownership is not a label: it is what `ProjectionTypeOwnership.
CanCrossAsHandle` answers, and that single answer gates the ABI classifier, the
proxy collector, and which types get a proxy factory at all. Before this, the
same question was asked three different ways — an `IsAnalyzerLocalClass` name
list, a hardcoded `AttributeData` metadata name, and an implicit assumption in
the classifier that every facade type was compiler-owned — and the three could
disagree without anything noticing.

The concrete consequence of getting it wrong is a **local object holding a
remote vtbl**: an analyzer-constructed instance whose members try to dispatch
over a handle that was never issued. `ProjectionValidation` now makes that
unrepresentable rather than merely unlikely.

### Declaration-only members

An abstract member has no body at the class level, so a projection that only
ever emitted class-level bodies had to declare every abstract member
unsupported. That is why a compiler-owned `Diagnostic` could not report its own
`Id`. A proxied abstract class does have somewhere to put a remoting body — the
generated `__RoslynAotProxy` override — so `CanHostFacadeBody` decides this
rather than the shape of the declaration. Protected members stay unsupported
regardless: the compiler cannot dispatch to them from outside.

`ProjectionValidation` runs on every model construction and refuses to generate
on: a duplicated canonical id; a table entry matching no member or type; a type
with no ownership reason; a type whose ownership forbids crossing that
nonetheless has a proxy factory; a supported call in zero or several vtbl slots,
or in one that disagrees with what the call records; an unsupported call
occupying a slot; two slots sharing a name within a vtbl; or a type crossing as
a handle with no proxy factory to receive it. A table entry that matches nothing
is the specific failure the older name-matched rules could not report: the
deviation simply stopped applying.

## Cases requiring explicit treatment

Direct projection will not be sufficient for every API. Expected cases include:

- Generic methods and types whose generic arguments affect ABI representation.
- Arrays, immutable collections, dictionaries, and enumerable results.
- Strings and other variable-length data.
- `ref`, `out`, pointer, function-pointer, and custom-modifier signatures.
- Delegates, events, asynchronous callbacks, and cancellation.
- Structs whose public value semantics must be preserved locally.
- Static APIs that create or query compiler-owned objects.
- APIs that expose services or implementation details unavailable under
  NativeAOT.

These cases should remain exceptions to direct projection. A member that cannot
yet be projected must retain its correct public declaration and fail with a
specific unsupported-API error rather than failing assembly or member binding.

## Implementation sequence

The ordered plan now lives in the
[analyzer remoting migration plan](../../docs/ANALYZER-REMOTING-MIGRATION.md),
which tracks it as numbered steps with dated measured results. One item never
covered there and still outstanding: comparing generated assemblies with the
input assemblies using ApiCompat and targeted metadata checks.

The generator should prefer deterministic mechanical projection over a
hand-maintained per-member implementation database. Explicit overrides are
reserved for APIs whose semantics cannot be represented by the general rules.
