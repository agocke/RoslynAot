// Deliberately produces no diagnostics on either side. Exercises the
// harness's happy path: both compilers run, both produce empty SARIF
// results, and every in-scope rule reports NotExercised for this case.
public sealed class Empty
{
}
