namespace RoslynAot.DifferentialHarness;

/// <summary>
/// A named, actionable environment problem (missing SDK component,
/// missing publish output, etc.). Always maps to exit code 3.
/// </summary>
internal sealed class HarnessEnvironmentException(string message)
    : Exception(message);
