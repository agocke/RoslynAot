# RoslynAot native ABI

`RoslynAot.Abi` defines the private boundary between the NativeAOT compiler and
NativeAOT analyzer modules. It contains the authored analyzer transport
interfaces and the generated Roslyn projection interfaces used by both sides.

This is an implementation contract shipped with the package, not a public API
for analyzer authors. The compiler, analyzer runtime, generated facades, and
cache keys advance together when the contract changes.

## Boundary semantics

Independent NativeAOT modules do not share managed object identity. The ABI
therefore uses:

- Reference-counted `IUnknown`-style interface pointers for service objects.
- Signed 64-bit opaque handles for compiler-owned Roslyn objects and values.
- Fixed-width integers for enums, flags, lengths, and status codes.
- Caller-provided UTF-16 buffers with explicit code-unit lengths for strings.
- Descriptor indexes and source spans for analyzer diagnostic reporting.

Managed references, managed arrays, delegates, exceptions, and Roslyn objects
never cross the boundary directly. The use of `GeneratedComInterface` and
`ComWrappers` supplies portable vtables, proxies, dispatch thunks, identity, and
reference counting; it does not use COM activation or registration.

## Module and analyzer transport

Each analyzer shared library exports the versioned
`roslyn_aot_get_analyzer_module_v*` entry point. The returned
`IAnalyzerModule`:

1. Reports its ABI version.
2. Reports the number of analyzers compiled into the module.
3. Returns one `IAnalyzerTransport` interface per analyzer.

An analyzer transport exposes its supported diagnostic descriptors, accepts an
analysis-context initialization call, receives registered callbacks, and
reports diagnostics through `IAnalyzerHost`.

The compiler supplies a Roslyn control interface whenever analyzer code may
construct or inspect facade values. There is one such control per compiler
process, so every analyzer in a module reads the same object table and a
handle means the same thing to all of them.

## Strings

Strings use a two-call copy convention:

1. Call with a null buffer to query the required UTF-16 code-unit count.
2. Allocate exactly that many `char` values and call again to copy the content.

Lengths exclude a terminator, and copied buffers are not null-terminated. A
negative length is invalid except where a generated operation explicitly uses
`-1` to represent a null string.

## Handles

Roslyn handles encode:

- A slot in the process-global object table.
- A generation used to reject stale handles after disposal and slot reuse.

Handles are process-global: one table serves the whole compiler, so a reference
type that has already crossed keeps the same handle on every later crossing and
analyzer-side reference equality reflects Roslyn's own object identity.
Generated dispatchers validate the expected runtime type before invoking real
Roslyn APIs.

Disposable facade values can release their compiler-side object. General
lifetime management for all non-disposable handles is still incomplete and is
tracked as product work.

## Failures and compatibility

ABI methods return explicit status codes. Compiler-side Roslyn dispatch catches
managed exceptions, records a thread-local error category and message, and
returns a failure status. The analyzer facade queries that error and recreates
an appropriate managed exception locally.

The analyzer transport layer also converts uncaught managed exceptions into
failure results through generated COM thunks. Roslyn then reports analyzer
initialization or callback failures as analyzer diagnostics such as `AD0001`.

Compatibility is checked at two levels:

- `AnalyzerAbi.Version` protects the authored analyzer module contract.
- The generated Roslyn manifest identity protects the larger per-type
  projection contract.

Mismatched endpoints must be rejected rather than attempting partial
compatibility.
