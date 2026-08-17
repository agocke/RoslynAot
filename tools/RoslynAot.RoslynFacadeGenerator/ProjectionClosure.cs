using Microsoft.CodeAnalysis;

namespace RoslynAot.RoslynFacadeGenerator;

/// <summary>
/// The set of types an analyzer can actually reach, and how it reaches them.
/// </summary>
/// <remarks>
/// Most of the projected surface exists because Roslyn's assemblies are public,
/// not because an analyzer can get to it: command-line parsing, emit, generator
/// drivers, and workspace-adjacent APIs are all projected today and none of them
/// are reachable from a <c>DiagnosticAnalyzer</c>. Naming that set is what turns
/// "unknown, probably broken" into "declared unsupported, with a reason".
/// </remarks>
internal static class ProjectionClosure
{
    /// <summary>
    /// Where an analyzer starts. Instance types are reached by traversal, but a
    /// static class has no instance to arrive on, so every static entry point
    /// an analyzer calls has to be declared here.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> s_roots =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["[Microsoft.CodeAnalysis]T:Microsoft.CodeAnalysis.Diagnostics.DiagnosticAnalyzer"] =
                "The analyzer base type: every analysis begins on one.",
            ["[Microsoft.CodeAnalysis]T:Microsoft.CodeAnalysis.Diagnostics.DiagnosticSuppressor"] =
                "The suppressor base type, registered the same way as an analyzer.",
            ["[Microsoft.CodeAnalysis]T:Microsoft.CodeAnalysis.Diagnostics.AnalysisContext"] =
                "The context handed to Initialize, from which every other " +
                "context and callback argument descends.",
            ["[Microsoft.CodeAnalysis]T:Microsoft.CodeAnalysis.Diagnostic"] =
                "Constructed by the analyzer, so it is an entry point rather " +
                "than something traversal arrives at.",
            ["[Microsoft.CodeAnalysis]T:Microsoft.CodeAnalysis.DiagnosticDescriptor"] =
                "Constructed in analyzer static initializers, before any " +
                "context exists.",
            ["[Microsoft.CodeAnalysis]T:Microsoft.CodeAnalysis.Location"] =
                "Constructed by the analyzer when reporting.",
            ["[Microsoft.CodeAnalysis]T:Microsoft.CodeAnalysis.LocalizableResourceString"] =
                "Constructed in analyzer static initializers for descriptor text.",
            ["[Microsoft.CodeAnalysis]T:Microsoft.CodeAnalysis.SymbolEqualityComparer"] =
                "A static singleton analyzers pass to every symbol-keyed " +
                "collection they build.",
            ["[Microsoft.CodeAnalysis]T:Microsoft.CodeAnalysis.SymbolDisplayFormat"] =
                "A static format catalog analyzers pass to ToDisplayString.",
            ["[Microsoft.CodeAnalysis.CSharp]T:Microsoft.CodeAnalysis.CSharpExtensions"] =
                "Static language-specific accessors over the language-agnostic " +
                "symbol and syntax surface.",
            ["[Microsoft.CodeAnalysis.CSharp]T:Microsoft.CodeAnalysis.CSharp.CSharpExtensions"] =
                "Static language-specific accessors over the language-agnostic " +
                "symbol and syntax surface.",
            ["[Microsoft.CodeAnalysis.CSharp]T:Microsoft.CodeAnalysis.CSharp.SyntaxFacts"] =
                "Static syntax predicates analyzers call without holding a node.",
            ["[Microsoft.CodeAnalysis]T:Microsoft.CodeAnalysis.FlowAnalysis.ControlFlowGraph"] =
                "Created by static factory from an operation or block.",
        };

    /// <summary>
    /// Marks each type reachable or not, recording the edge it was reached by so
    /// an unsupported member can explain itself as a chain rather than a bare
    /// "not supported".
    /// </summary>
    public static void Compute(
        IReadOnlyList<TypeProjection> types,
        IReadOnlyList<ProjectedCall> calls)
    {
        var byType = types.ToDictionary(
            type => type.Symbol,
            SymbolEqualityComparer.Default);
        // Every call, not only the supported ones: reachability describes what
        // an analyzer can get to, and must not shrink because an unrelated
        // member is unsupported today.
        var callsByContainingType = calls
            .ToLookup<ProjectedCall, ISymbol>(
                call => call.Symbol.ContainingType,
                SymbolEqualityComparer.Default);

        // A static class has no instance for traversal to arrive on, so an
        // extension surface over a reachable type would otherwise fall out of
        // the closure entirely — ModelExtensions.GetDeclaredSymbol among them.
        var staticEntryPoints =
            new Dictionary<ISymbol, List<TypeProjection>>(
                SymbolEqualityComparer.Default);
        foreach (TypeProjection type in types.Where(type => type.Symbol.IsStatic))
        {
            foreach (INamedTypeSymbol referenced in callsByContainingType[type.Symbol]
                .SelectMany(GetReferencedTypes)
                .Distinct<INamedTypeSymbol>(SymbolEqualityComparer.Default))
            {
                if (!staticEntryPoints.TryGetValue(
                        referenced,
                        out List<TypeProjection>? holders))
                {
                    holders = [];
                    staticEntryPoints.Add(referenced, holders);
                }

                holders.Add(type);
            }
        }

        var queue = new Queue<TypeProjection>();
        foreach (TypeProjection type in types)
        {
            if (s_roots.TryGetValue(type.CanonicalId, out string? reason))
            {
                type.ReachedBy = reason;
                queue.Enqueue(type);
            }
        }

        while (queue.Count > 0)
        {
            TypeProjection current = queue.Dequeue();
            IEnumerable<(INamedTypeSymbol, string)> neighbors = GetNeighbors(
                current,
                types,
                callsByContainingType);
            if (staticEntryPoints.TryGetValue(
                    current.Symbol,
                    out List<TypeProjection>? entryPoints))
            {
                neighbors = neighbors.Concat(
                    entryPoints.Select(entry => (
                        entry.Symbol,
                        $"static entry point over {current.CanonicalId}")));
            }

            foreach ((INamedTypeSymbol next, string edge) in neighbors)
            {
                if (!byType.TryGetValue(next, out TypeProjection? projection) ||
                    projection.ReachedBy is not null)
                {
                    continue;
                }

                projection.ReachedBy = edge;
                queue.Enqueue(projection);
            }
        }
    }

    private static IEnumerable<(INamedTypeSymbol Type, string Edge)>
        GetNeighbors(
            TypeProjection type,
            IReadOnlyList<TypeProjection> types,
            ILookup<ISymbol, ProjectedCall> callsByContainingType)
    {
        // A proxy answering as a derived type still has to answer everything
        // its bases and interfaces declare.
        for (INamedTypeSymbol? baseType = type.Symbol.BaseType;
             baseType is not null;
             baseType = baseType.BaseType)
        {
            yield return (baseType, $"base of {type.CanonicalId}");
        }

        foreach (INamedTypeSymbol @interface in type.Symbol.AllInterfaces)
        {
            yield return (@interface, $"implemented by {type.CanonicalId}");
        }

        // A member typed as the base can hand back any derived type, so the
        // whole subtree is reachable through it.
        foreach (TypeProjection candidate in types)
        {
            if (!ReferenceEquals(candidate, type) &&
                DerivesFrom(candidate.Symbol, type.Symbol))
            {
                yield return (
                    candidate.Symbol,
                    $"derives from {type.CanonicalId}");
            }
        }

        foreach (ProjectedCall call in callsByContainingType[type.Symbol])
        {
            foreach (INamedTypeSymbol referenced in GetReferencedTypes(call))
            {
                yield return (referenced, $"used by {call.CanonicalId}");
            }
        }
    }

    /// <summary>
    /// Every type a caller of this member can end up holding. Deliberately read
    /// off the C# signature rather than the ABI plan: a delegate parameter has
    /// no ABI plan today, but <c>RegisterSyntaxNodeAction</c> is exactly how an
    /// analyzer reaches <c>SyntaxNodeAnalysisContext</c>.
    /// </summary>
    private static IEnumerable<INamedTypeSymbol> GetReferencedTypes(
        ProjectedCall call)
    {
        foreach (IParameterSymbol parameter in call.Symbol.Parameters)
        {
            foreach (INamedTypeSymbol type in Expand(parameter.Type))
            {
                yield return type;
            }
        }

        foreach (INamedTypeSymbol type in Expand(call.Symbol.ReturnType))
        {
            yield return type;
        }
    }

    private static IEnumerable<INamedTypeSymbol> Expand(ITypeSymbol type)
    {
        switch (type)
        {
            case IArrayTypeSymbol array:
                foreach (INamedTypeSymbol element in Expand(array.ElementType))
                {
                    yield return element;
                }

                break;
            case INamedTypeSymbol named:
                yield return named;
                foreach (ITypeSymbol argument in named.TypeArguments)
                {
                    foreach (INamedTypeSymbol nested in Expand(argument))
                    {
                        yield return nested;
                    }
                }

                break;
        }
    }

    private static bool DerivesFrom(
        INamedTypeSymbol candidate,
        INamedTypeSymbol type)
    {
        for (INamedTypeSymbol? current = candidate;
             current is not null;
             current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current, type))
            {
                return true;
            }
        }

        return candidate.AllInterfaces.Any(
            @interface =>
                SymbolEqualityComparer.Default.Equals(@interface, type));
    }

    public static IEnumerable<string> RootIds => s_roots.Keys;
}
