# Differential analyzer harness

Runs a corpus of C# sources through both managed Roslyn and the
NativeAOT compiler host (`csc-aot`) with the same native analyzer
module, diffs the resulting diagnostics, and produces a per-rule
burn-down against a checked-in baseline (`eng/differential-baseline.json`).
This is the differential harness required by migration Step 1
("make failures legible") in `docs/ANALYZER-REMOTING-MIGRATION.md`.

Run it via `bash eng/validate-differential.sh`, or directly:

```bash
dotnet artifacts/bin/RoslynAot.DifferentialHarness/release/RoslynAot.DifferentialHarness.dll run
```

`inventory` resolves the toolchain and prints the native module's rule
catalog and the generated globalconfig without compiling anything.
`run --help` (or no verb) prints the full option list.

## How scope is derived

The comparable rule-ID set is **not hardcoded** - it comes from probing
the native module under test (`RuleInventory.ProbeNativeRuleIds`) and
reading its SARIF rule catalog, which lists every loaded analyzer's
`SupportedDiagnostics` regardless of enablement. Pointing `--native-module`
at a different module changes the corpus scope automatically.

Every discovered rule ID is forced to `warning` severity for every case
on both sides (`GlobalConfigGenerator`) - most CA rules default to a
severity Roslyn skips analyzer execution for entirely, so without this
most analyzers never call `Initialize` at all.

## What is and isn't compared

Comparison covers rule id, effective severity, span, and message - the
four fields `ReportDiagnostic` (`src/RoslynAot.Abi/AnalyzerAbi.cs`) can
transport today. `Properties`, `AdditionalLocations`, `RelatedLocations`,
suppression state, and code fixes are declared and **counted** in
`report.md`/`report.json` rather than silently ignored - they stay
uncompared until migration Step 6 widens the ABI.

## AD0001 parsing is coupled to two repo-owned strings

`AnalyzerFailureParser` extracts rule ids, analyzer type, action kind,
exception type, and the first non-runtime stack frame out of the AD0001
message text. It depends on the exact shape of:

- `src/RoslynAot.AnalyzerRuntime/AnalyzerExport.cs` (`FormatFailure`):
  `"RoslynAot analyzer '{analyzerName}' failed during {operation}:"`
- `src/CscAot/NativeDiagnosticAnalyzer.cs` (`InvokeWithHost`):
  `"Analyzer transport operation for [{diagnosticIds}] failed with 0x{result:x8}."`

If either string's shape changes, parsing degrades to a raw-text reason
with `ParseFailed = true` rather than dropping the failure - watch for
that flag at the top of `report.md` after touching either file.

## Boundary call coverage

The native compiler counts every projection dispatcher call, keyed on the
Roslyn member. Counting is unconditional and compiler-side: one
interlocked increment against a preallocated slot, negligible beside the
round trip it measures, so a zero always means "never called" rather than
"not instrumented". Overload slots are summed under one display name.

Setting `ROSLYNAOT_CALL_COUNTS=<path>` makes `csc-aot` write the counts as
JSON on exit; the harness sets it per case and aggregates. The result is
the `coverage` section of `report.json` and a table in `report.md`.

Counts are taken *before* the call is attempted, so a member that always
throws still reports as reached — coverage answers "did the corpus get
here", not "did it work". The member names match the shape
`AnalyzerFailureParser` extracts from AD0001 frames, so coverage rows and
burn-down reasons join on the same key.

Only the native side is instrumented; managed Roslyn has no boundary to
cross. Control-vtbl operations (`ObjectEquals`, `CopyObjectToStringUtf16`,
and the rest of `RoslynInterop`) are **not** counted yet — they are
hand-written rather than generated, so they carry no ordinal.

## Burn-down statuses

- **Pass**: the rule produced at least one managed diagnostic somewhere
  in the corpus, every one matched the native side, and no analyzer
  named by that rule crashed.
- **NotExercised**: no managed diagnostic for the rule anywhere in the
  corpus, and nothing crashed. Not a pass - it means the corpus doesn't
  yet exercise the rule.
- **Fail**: otherwise, with a reason. Precedence when multiple
  conditions apply: CompilerCrash > Timeout > AnalyzerException >
  MissingDiagnostic > ExtraDiagnostic > SpanMismatch > SeverityMismatch
  > MessageMismatch.

## Adding a corpus case

Add `corpus/<RuleId>/<CaseName>/<Source>.cs` (see `corpus/CA1812/Basic/`
for the minimal shape - no `case.json` needed for the common case). An
optional `case.json` next to the source can declare `rules` (defaults
to the containing `<RuleId>` directory name) and
`extraCompilerArguments` for cases that need extra references or
compiler switches. `corpus/README.md` documents the layout, the
`case.json` schema, and which rules deliberately have no case and why.

Write the case against **managed** csc first - a case only counts once
the managed side actually reports the rule, and several CA rules are
fussier to trigger than their documentation suggests. The burn-down
aggregates per rule ID across the whole corpus, so a case that trips
several rules counts for all of them.

## Per-analyzer module measurement

`bash eng/measure-modules.sh` (harness verb `modules`) builds one
NativeAOT module per analyzer plus the whole-assembly module, and records
each one's size, retained type count, and ILC time. This is the trimming
baseline migration Step 1 asks for, established before anything can
regress it.

Single-analyzer modules come from `-p:RoslynAotAnalyzers=<metadata name>`
on `samples/RoslynAot.CSharpNetAnalyzers.Native`, which forwards
`--analyzer` to the entry point generator. A filter naming an analyzer
that does not exist is an error rather than a silently smaller module,
which would otherwise read as a size win.

Retained counts come from the `.mstat` ILC emits under
`-p:IlcGenerateMstatFile=true`. `MstatReader` walks the IL of the file's
per-table methods and counts `ldtoken` rows; it walks instructions rather
than scanning bytes because a `0xD0` inside an `ldc.i4` operand would
otherwise count as a row, and it throws rather than guessing if it meets
an opcode the encoding is not supposed to contain.

**Sizes and retained counts are ratcheted; times are not.** Wall-clock
numbers are nondeterministic, and a baseline that churns on every run
stops being read — so `eng/module-baseline.json` holds only size and the
two counts, while ILC and publish times appear in `modules.md` as
information. An ILC time of zero means the incremental build skipped
`IlcCompile`, not that it was instant.

The run publishes 40+ NativeAOT modules sequentially and takes tens of
minutes, so it is deliberately not part of `validate-differential.sh`.

## Updating the baseline

After a deliberate behavior change, run with `--update-baseline` and
review the resulting diff to `eng/differential-baseline.json` like any
other change - that diff *is* the change under review.
