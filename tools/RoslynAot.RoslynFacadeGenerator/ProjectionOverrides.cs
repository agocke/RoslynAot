namespace RoslynAot.RoslynFacadeGenerator;

internal sealed record ProjectionOverride(
    ProjectionStrategy Strategy,
    string Reason);

internal static class ProjectionOverrides
{
    private static readonly IReadOnlyDictionary<string, ProjectionOverride>
        s_overrides =
            new Dictionary<string, ProjectionOverride>(
                StringComparer.Ordinal);

    public static bool TryGet(
        string canonicalSignature,
        out ProjectionOverride projectionOverride) =>
        s_overrides.TryGetValue(
            canonicalSignature,
            out projectionOverride!);
}
