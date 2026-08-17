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
        ValidateOwnership(model, failures);
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
