using Microsoft.CodeAnalysis;

namespace RoslynAot.RoslynFacadeGenerator;

/// <summary>
/// The checks the model can make about itself before a line of source is
/// emitted. Each of these once described a class of defect that only surfaced
/// as an analyzer failing at compile time, several steps and one native build
/// removed from the generator that caused it.
/// </summary>
internal static class ProjectionValidation
{
    public static void Validate(ProjectionModel model)
    {
        var failures = new List<string>();
        ValidateCanonicalIds(model, failures);
        ValidateOverrides(model, failures);
        ValidateCollectionTransport(model, failures);
        ValidateOwnership(model, failures);
        ValidateGenericVirtualDispatch(model, failures);
        ValidateAbiSymmetry(model, failures);
        ValidateFactoryCoverage(model, failures);

        if (failures.Count > 0)
        {
            throw new InvalidOperationException(
                $"The projection model failed {failures.Count} validation " +
                "check(s):" + Environment.NewLine +
                string.Join(Environment.NewLine, failures.Take(50)) +
                (failures.Count > 50
                    ? $"{Environment.NewLine}... and {failures.Count - 50} more."
                    : string.Empty));
        }
    }

    /// <summary>
    /// The identity guarantee everything else rests on: if two members share a
    /// canonical id, every table keyed by it silently answers for the wrong one.
    /// </summary>
    private static void ValidateCanonicalIds(
        ProjectionModel model,
        List<string> failures)
    {
        foreach (IGrouping<string, ProjectedCall> group in model.Calls
            .GroupBy(call => call.CanonicalId, StringComparer.Ordinal)
            .Where(group => group.Count() > 1))
        {
            failures.Add(
                $"Canonical id '{group.Key}' is shared by " +
                $"{group.Count()} calls: " +
                string.Join(
                    ", ",
                    group.Select(call => call.CanonicalSignature)));
        }

        foreach (IGrouping<string, MemberProjection> group in model.Members
            .GroupBy(member => member.CanonicalId, StringComparer.Ordinal)
            .Where(group => group.Count() > 1))
        {
            failures.Add(
                $"Canonical id '{group.Key}' is shared by " +
                $"{group.Count()} members.");
        }
    }

    /// <summary>
    /// An override that matches nothing is the failure mode the name-matched
    /// rules had no way to report: the deviation silently stops applying when
    /// the member it targeted is renamed or its signature changes.
    /// </summary>
    private static void ValidateOverrides(
        ProjectionModel model,
        List<string> failures)
    {
        var ids = model.Calls
            .Select(call => call.CanonicalId)
            .ToHashSet(StringComparer.Ordinal);
        foreach (string id in ProjectionOverrides.Ids
            .Where(id => !ids.Contains(id))
            .OrderBy(id => id, StringComparer.Ordinal))
        {
            failures.Add($"Override '{id}' matches no projected member.");
        }

        var typeIds = model.Types
            .Select(type => type.CanonicalId)
            .ToHashSet(StringComparer.Ordinal);
        foreach (string id in ProjectionTypeOwnership.Ids
            .Where(id => !typeIds.Contains(id))
            .OrderBy(id => id, StringComparer.Ordinal))
        {
            failures.Add($"Declared ownership '{id}' matches no type.");
        }

        foreach (string id in ProjectionClosure.RootIds
            .Where(id => !typeIds.Contains(id))
            .OrderBy(id => id, StringComparer.Ordinal))
        {
            failures.Add($"Closure root '{id}' matches no type.");
        }

        var memberIds = model.Members
            .Select(member => member.CanonicalId)
            .ToHashSet(StringComparer.Ordinal);
        foreach (string id in ProjectionOverrides.FieldInitializerIds
            .Where(id => !memberIds.Contains(id))
            .OrderBy(id => id, StringComparer.Ordinal))
        {
            failures.Add($"Field initializer '{id}' matches no member.");
        }
    }

    /// <summary>
    /// A collection may only cross as a copy when the copy answers
    /// <c>Contains</c> the same way the source would.
    /// </summary>
    /// <remarks>
    /// Copying preserves a collection's contents and discards its behaviour.
    /// The elements survive; the comparer and the lookup complexity do not.
    /// A <c>string[]</c> standing in for a set answers with
    /// <c>EqualityComparer&lt;string&gt;.Default</c> whatever the source used,
    /// and Roslyn's analyzer config keys compare case-insensitively through
    /// <c>CaseInsensitiveComparison</c> — so the copy returns <c>false</c>
    /// where Roslyn returns <c>true</c>, with no exception to notice.
    ///
    /// This is a worse failure mode than the ones the other checks here guard.
    /// A dead compiler or an <c>AD0001</c> announces itself; a wrong
    /// <c>Contains</c> becomes a wrong diagnostic, and the differential
    /// harness only catches it if some corpus case happens to observe it.
    ///
    /// What is and is not allowed to copy:
    /// <list type="bullet">
    /// <item><c>ImmutableArray&lt;T&gt;</c> — allowed. Its <c>Contains</c> is
    /// <c>EqualityComparer&lt;T&gt;.Default</c>, identical to the array the
    /// copy produces, so the substitution is provably faithful from the
    /// declared type alone.</item>
    /// <item><c>ICollection&lt;T&gt;</c>, <c>ISet&lt;T&gt;</c>,
    /// <c>IReadOnlySet&lt;T&gt;</c> — rejected. The declared type promises
    /// membership, so the comparer is part of the contract being projected
    /// and a copy cannot carry it. These must cross as a handle.</item>
    /// <item><c>IEnumerable&lt;T&gt;</c> — allowed, with a residual risk worth
    /// stating plainly. Roslyn's are lazy iterator sequences that implement no
    /// <c>ICollection&lt;T&gt;</c>, so <c>Enumerable.Contains</c> does the same
    /// linear default-equality scan on either side. But that deferral is real:
    /// were one of these ever backed by a set with a custom comparer, the copy
    /// would diverge and this check would not catch it. Proxying every lazy
    /// sequence to close that gap would turn a tree walk into a snapshot plus
    /// a crossing per element, which is why the line is drawn here.</item>
    /// </list>
    /// </remarks>
    private static void ValidateCollectionTransport(
        ProjectionModel model,
        List<string> failures)
    {
        foreach (ProjectedCall call in model.Calls.Where(call => call.IsSupported))
        {
            // String collections cross as a handle, so their membership is
            // answered by the collection itself and there is nothing to check.
            if (call.ReturnValue.Kind != AbiTypeKind.ObjectCollection ||
                !PromisesMembership(call.ReturnValue.SourceType))
            {
                continue;
            }

            failures.Add(
                $"Call '{call.CanonicalId}' returns " +
                $"'{call.ReturnValue.SourceType.ToDisplayString()}', whose " +
                "contract includes membership, but crosses as a copied " +
                "collection; the copy would answer Contains with ordinal " +
                "equality instead of the source's comparer. It must cross as " +
                "a handle.");
        }
    }

    private static bool PromisesMembership(ITypeSymbol type) =>
        type.OriginalDefinition is INamedTypeSymbol
        {
            Arity: 1,
            Name: "ICollection" or "ISet" or "IReadOnlySet",
            ContainingNamespace:
            {
                Name: "Generic",
                ContainingNamespace:
                {
                    Name: "Collections",
                    ContainingNamespace:
                    {
                        Name: "System",
                        ContainingNamespace.IsGlobalNamespace: true
                    }
                }
            }
        };

    /// <summary>
    /// Ownership is what says whether an instance may cross as a handle, so a
    /// type whose ownership and transport disagree is exactly the "local object
    /// executing remote members" defect, caught before it is emitted.
    /// </summary>
    private static void ValidateOwnership(
        ProjectionModel model,
        List<string> failures)
    {
        foreach (TypeProjection type in model.Types)
        {
            if (string.IsNullOrWhiteSpace(type.OwnershipReason))
            {
                failures.Add(
                    $"Type '{type.CanonicalId}' has ownership " +
                    $"'{type.Ownership}' with no reason.");
            }

            if (type.RequiresProxy &&
                !ProjectionTypeOwnership.CanCrossAsHandle(type.Ownership))
            {
                failures.Add(
                    $"Type '{type.CanonicalId}' is owned '{type.Ownership}' " +
                    "but has a proxy factory, so a compiler-side instance " +
                    "could reach the analyzer as a handle.");
            }
        }
    }

    /// <summary>
    /// A generic method on a dynamic-interface-proxied type must not be
    /// dispatched virtually.
    /// </summary>
    /// <remarks>
    /// NativeAOT resolves a generic virtual method through a slot mapping on
    /// the concrete target type. <c>RoslynObjectProxy</c> does not statically
    /// implement the projected interfaces, so there is nothing to find and the
    /// type loader *fails fast* — killing the compiler and losing every other
    /// analyzer's diagnostics, below any frame that could catch it.
    ///
    /// The emitter seals these members so the call resolves directly to the
    /// facade body instead. This check exists because the failure mode of
    /// getting it wrong is the worst one available: not a diagnostic, not an
    /// exception, but a dead compiler. A supported generic call would mean
    /// something intends to dispatch it, which needs a statically implemented
    /// shim on the proxy first — see docs/GENERIC-VIRTUAL-DISPATCH.md.
    /// </remarks>
    private static void ValidateGenericVirtualDispatch(
        ProjectionModel model,
        List<string> failures)
    {
        foreach (ProjectedCall call in model.Calls)
        {
            if (!call.IsSupported ||
                !call.Symbol.IsGenericMethod ||
                call.Symbol.ContainingType is not INamedTypeSymbol type ||
                !model.UsesDynamicInterfaceProxy(type))
            {
                continue;
            }

            failures.Add(
                $"Generic call '{call.CanonicalId}' is supported on a " +
                "dynamic-interface proxy, which NativeAOT cannot dispatch " +
                "without a statically implemented shim; reaching it would " +
                "terminate the compiler.");
        }
    }

    /// <summary>
    /// Every supported call must occupy exactly one vtbl slot, and no vtbl slot
    /// may hold a call the ABI cannot express.
    /// </summary>
    private static void ValidateAbiSymmetry(
        ProjectionModel model,
        List<string> failures)
    {
        var slots = model.Vtbls
            .SelectMany(vtbl => vtbl.Members.Select(call => (vtbl, call)))
            .ToLookup(entry => entry.call, entry => entry.vtbl);

        foreach (ProjectedCall call in model.Calls.Where(call => call.IsSupported))
        {
            int placements = slots[call].Count();
            if (placements != 1)
            {
                failures.Add(
                    $"Supported call '{call.CanonicalId}' occupies " +
                    $"{placements} vtbl slots.");
                continue;
            }

            if (call.Vtbl is null || !ReferenceEquals(
                    call.Vtbl,
                    slots[call].Single()))
            {
                failures.Add(
                    $"Supported call '{call.CanonicalId}' is placed in " +
                    $"'{slots[call].Single().Name}' but records " +
                    $"'{call.Vtbl?.Name ?? "none"}'.");
            }
        }

        foreach (VtblProjection vtbl in model.Vtbls)
        {
            foreach (ProjectedCall call in vtbl.Members
                .Where(call => !call.IsSupported))
            {
                failures.Add(
                    $"Vtbl '{vtbl.Name}' holds unsupported call " +
                    $"'{call.CanonicalId}'.");
            }

            foreach (IGrouping<string, ProjectedCall> group in vtbl.Members
                .GroupBy(call => call.GeneratedName, StringComparer.Ordinal)
                .Where(group => group.Count() > 1))
            {
                failures.Add(
                    $"Vtbl '{vtbl.Name}' has {group.Count()} slots named " +
                    $"'{group.Key}'.");
            }
        }
    }

    /// <summary>
    /// A handle is only useful if the receiving side can build a proxy over it.
    /// Any type that crosses as a handle without a proxy would reach the
    /// analyzer as an object with no vtbl to dispatch on.
    /// </summary>
    private static void ValidateFactoryCoverage(
        ProjectionModel model,
        List<string> failures)
    {
        var reported = new HashSet<string>(StringComparer.Ordinal);
        foreach (ProjectedCall call in model.Calls.Where(call => call.IsSupported))
        {
            foreach (AbiTypePlan plan in GetHandlePlans(call))
            {
                foreach (INamedTypeSymbol type in new[]
                {
                    plan.RemoteType,
                    plan.CollectionElementType,
                }.OfType<INamedTypeSymbol>())
                {
                    if (model.RequiresProxy(type))
                    {
                        continue;
                    }

                    string name = type.ToDisplayString();
                    if (reported.Add(name))
                    {
                        failures.Add(
                            $"Type '{name}' crosses as a handle but has no " +
                            $"proxy factory; first seen on " +
                            $"'{call.CanonicalId}'.");
                    }
                }
            }
        }
    }

    private static IEnumerable<AbiTypePlan> GetHandlePlans(ProjectedCall call)
    {
        if (call.Receiver is not null)
        {
            yield return call.Receiver;
        }

        foreach (ParameterProjection parameter in call.Parameters)
        {
            yield return parameter.AbiType;
        }

        yield return call.ReturnValue;
    }
}
