using Microsoft.CodeAnalysis;

namespace RoslynAot.RoslynFacadeGenerator;

/// <summary>
/// Which side of the boundary owns a projected type's state, and therefore how
/// an instance of it is allowed to cross.
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
/// Ownership for every projected type: declared where the derivation would be
/// wrong, derived by a named rule otherwise. Both paths produce a reason, so no
/// type in the model carries an ownership class nobody can account for.
/// </summary>
/// <remarks>
/// This was six type names inside the model, which meant the distinction the
/// whole projection turns on could not be read anywhere, reported anywhere, or
/// given a reason.
/// </remarks>
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

        ["[Microsoft.CodeAnalysis]T:Microsoft.CodeAnalysis.Optional`1"] = new(
            TypeOwnership.Local,
            "A two-field value holding no compiler state. It arrives as a tag " +
            "and a payload that the analyzer side reassembles, so there is " +
            "never a compiler-side instance to take a handle to."),

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

    /// <summary>
    /// The ownership of a type, and whether it was declared above or derived.
    /// Derivation is not a placeholder for a missing declaration: for the 657
    /// types that are plainly compiler-owned it is the correct answer, and
    /// writing them out by hand would bury the six that actually differ.
    /// </summary>
    public static (TypeOwnershipEntry Entry, bool Declared) Get(
        INamedTypeSymbol type,
        string canonicalId,
        bool requiresProxy)
    {
        if (TryGet(canonicalId, out TypeOwnershipEntry declared))
        {
            return (declared, true);
        }

        if (type.IsStatic)
        {
            return (
                new TypeOwnershipEntry(
                    TypeOwnership.Facade,
                    "Derived: a static class has no instances, so only its " +
                    "type vtbl crosses."),
                false);
        }

        if (!requiresProxy)
        {
            // Compiler-owned all the same — but saying it "crosses as a
            // handle" would be a claim the model cannot back, because nothing
            // supported hands one over.
            return (
                new TypeOwnershipEntry(
                    TypeOwnership.Remote,
                    "Derived: compiler-owned, but no instance crosses today " +
                    "because every member that would hand one over is " +
                    "unsupported."),
                false);
        }

        return type.IsValueType
            ? (
                new TypeOwnershipEntry(
                    TypeOwnership.Value,
                    "Derived: a compiler-side value the analyzer reads " +
                    "through a handle to the compiler's copy."),
                false)
            : (
                new TypeOwnershipEntry(
                    TypeOwnership.Remote,
                    "Derived: the compiler constructs it and the analyzer " +
                    "only ever holds a handle."),
                false);
    }

    /// <summary>
    /// Whether an instance of the type can arrive from the compiler as a
    /// handle. This is the question the ABI classifier and the proxy collector
    /// actually need, and it belongs here rather than in either of them.
    /// </summary>
    public static bool CanCrossAsHandle(TypeOwnership ownership) =>
        ownership is
            TypeOwnership.Remote or
            TypeOwnership.Value or
            TypeOwnership.Dual;
}
