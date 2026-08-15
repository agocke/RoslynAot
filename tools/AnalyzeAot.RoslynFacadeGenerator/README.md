# Roslyn facade generator

This tool will generate the Roslyn facade assemblies and their corresponding
compiler-side COM interop surface. These details are intentionally documented
here rather than in the repository roadmap because they describe the design of
this generator, not the overall AnalyzeAot architecture.

The design is provisional. Roslyn's complete API surface will expose cases that
require revisiting the projection rules.

## Current status

The first implemented command validates managed assembly inputs and inventories
their public and protected API surface:

```bash
dotnet run \
  --project tools/AnalyzeAot.RoslynFacadeGenerator \
  -- inspect \
  path/to/Microsoft.CodeAnalysis.dll \
  path/to/Microsoft.CodeAnalysis.CSharp.dll
```

Generate executable facade stubs with:

```bash
dotnet run \
  --project tools/AnalyzeAot.RoslynFacadeGenerator \
  -- generate \
  --output artifacts/generated/roslyn-facade \
  path/to/Microsoft.CodeAnalysis.dll \
  path/to/Microsoft.CodeAnalysis.CSharp.dll
```

The generated types are partial. Every concrete constructor, method, operator,
and accessor currently throws `PlatformNotSupportedException`; abstract and
interface members retain declaration-only bodies. This makes unsupported calls
explicit while the direct COM projection is implemented. Binding-relevant
assembly attributes, including Roslyn friend-assembly declarations, are
preserved. GenAPI's usual `ReferenceAssemblyAttribute` is deliberately omitted
because these facades must execute.

Generated source mirrors the API structure on disk:

```text
roslyn-facade/
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
```

Each assembly receives its own root directory. Namespace components become
directories, and each top-level type receives a source file. Nested types stay
in the file containing their declaring type. Generic arity is appended only
when needed to distinguish legal same-name types such as `Name` and `Name<T>`.

## Source generation

The facade declaration generator will be based on a fork of the .NET SDK's
`Microsoft.DotNet.GenAPI` source. GenAPI already handles the difficult metadata
to C# declaration conversion, including nested and generic types, attributes,
constraints, overloads, and language syntax.

The relevant GenAPI implementation is vendored under `Upstream/` from
`dotnet/sdk` tag `v10.0.100`. Each vendored file retains its original license
header, and `Upstream/README.md` records the exact source commit.

The fork will differ from ordinary GenAPI output in two important ways:

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

## Initial projection rules

- An instance receiver becomes an opaque object handle.
- A COM operation returns an HRESULT-style status code.
- A Roslyn return value becomes an `out` parameter.
- Roslyn object arguments and results become handles.
- Primitive values and enums cross the ABI directly.
- Properties become getter and setter operations.
- Constructors become creation operations that return a new handle.
- Delegates become generated callback COM interfaces.
- Exceptions are caught on the originating side and converted to explicit
  status codes; exceptions never cross the ABI.

Generated operation names must be deterministic and unambiguous for overloads.
Their exact naming and stable identity scheme will be selected after inspecting
the real overload and generic-method surface.

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

## Planned implementation sequence

1. Generate the existing `Microsoft.CodeAnalysis` and
   `Microsoft.CodeAnalysis.CSharp` facade declarations with unsupported bodies.
2. Add the direct signature-to-COM transformation.
3. Generate facade bodies and compiler dispatch from that transformation.
4. Add marshalling support incrementally as the Roslyn API inventory requires
   it.
5. Compare generated assemblies with the input assemblies using ApiCompat and
   targeted metadata checks.

The generator should prefer deterministic mechanical projection over a
hand-maintained per-member implementation database. Explicit overrides are
reserved for APIs whose semantics cannot be represented by the general rules.
