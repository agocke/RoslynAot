namespace RoslynAot.RoslynFacadeGenerator;

/// <summary>
/// Which side of the boundary owns a projected type's state. The migration plan
/// requires this as a per-type model field; today only the deviations from the
/// derived default are declared, and Step 3 makes it mandatory for every type.
/// </summary>
internal enum TypeOwnership
{
    /// <summary>The compiler owns it; the analyzer holds a handle.</summary>
    Remote,

    /// <summary>A value the analyzer holds by handle to a compiler-side copy.</summary>
    Value,

    /// <summary>The analyzer owns it outright; no handle exists.</summary>
    Local,

    /// <summary>Instances of both kinds exist and share one type.</summary>
    Dual,

    /// <summary>Static surface only; no instance ever crosses.</summary>
    Facade,
}

internal sealed record TypeOwnershipEntry(
    TypeOwnership Ownership,
    string Reason);

/// <summary>
/// Declared ownership, keyed by canonical type id. This was a list of six type
/// names inside the model, which meant the distinction the whole projection
/// turns on could not be read anywhere, reported anywhere, or given a reason.
/// </summary>
internal static class ProjectionTypeOwnership
{
    private static readonly IReadOnlyDictionary<string, TypeOwnershipEntry>
        s_entries = new Dictionary<string, TypeOwnershipEntry>(
            StringComparer.Ordinal)
    {
        ["[Microsoft.CodeAnalysis]T:Microsoft.CodeAnalysis.Diagnostic"] = new(
            TypeOwnership.Dual,
            "An analyzer builds diagnostics itself and receives them from the " +
            "compiler, and the two are the same type to the analyzer."),

        ["[Microsoft.CodeAnalysis]T:Microsoft.CodeAnalysis.DiagnosticDescriptor"] = new(
            TypeOwnership.Dual,
            "Analyzers construct descriptors in static initializers, before " +
            "any compiler object exists, but descriptors also come back from " +
            "the compiler on diagnostics it owns."),

        ["[Microsoft.CodeAnalysis]T:Microsoft.CodeAnalysis.Location"] = new(
            TypeOwnership.Dual,
            "Locations are created analyzer-side from a node's span and " +
            "returned compiler-side from symbols, with no way for a caller to " +
            "tell which it holds."),

        ["[Microsoft.CodeAnalysis]T:Microsoft.CodeAnalysis.LocalizableString"] = new(
            TypeOwnership.Dual,
            "A fixed or resource string constructed analyzer-side and a " +
            "compiler-owned one are indistinguishable through the base type."),

        ["[Microsoft.CodeAnalysis]T:Microsoft.CodeAnalysis.LocalizableResourceString"] = new(
            TypeOwnership.Local,
            "Its ResourceManager and resource source type live in the analyzer " +
            "module and cannot be reached from the compiler."),

        ["[Microsoft.CodeAnalysis]T:Microsoft.CodeAnalysis.SymbolEqualityComparer"] = new(
            TypeOwnership.Local,
            "The comparer holds no compiler state; only its kind crosses, and " +
            "the comparison itself runs against symbol handles."),
    };

    public static IEnumerable<string> Ids => s_entries.Keys;

    public static bool TryGet(
        string canonicalId,
        out TypeOwnershipEntry entry) =>
        s_entries.TryGetValue(canonicalId, out entry!);
}
