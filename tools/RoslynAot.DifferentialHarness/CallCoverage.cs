using System.Text.Json;
using System.Text.Json.Serialization;

namespace RoslynAot.DifferentialHarness;

internal sealed class CoverageMember
{
    [JsonPropertyName("member")]
    public string Member { get; set; } = "";

    [JsonPropertyName("calls")]
    public long Calls { get; set; }

    [JsonPropertyName("cases")]
    public int Cases { get; set; }
}

internal sealed class CoverageReport
{
    /// <summary>
    /// Every member the projection generates a dispatcher for, whether or not
    /// the corpus reaches it. The denominator of the coverage metric.
    /// </summary>
    [JsonPropertyName("projectedMemberCount")]
    public int ProjectedMemberCount { get; set; }

    [JsonPropertyName("calledMemberCount")]
    public int CalledMemberCount { get; set; }

    [JsonPropertyName("totalCalls")]
    public long TotalCalls { get; set; }

    /// <summary>
    /// Called members only, most-called first. The never-called remainder is
    /// <see cref="ProjectedMemberCount"/> minus <see cref="CalledMemberCount"/>;
    /// listing several thousand zero rows would bury the signal.
    /// </summary>
    [JsonPropertyName("members")]
    public List<CoverageMember> Members { get; set; } = [];
}

[JsonSerializable(typeof(Dictionary<string, long>))]
internal sealed partial class CallCountJsonContext : JsonSerializerContext;

internal static class CallCoverage
{
    /// <summary>
    /// Aggregates the per-case <c>call-counts.json</c> files the native
    /// compiler writes when <c>ROSLYNAOT_CALL_COUNTS</c> is set. A case that
    /// crashed before writing one is skipped rather than counted as zero.
    /// </summary>
    public static CoverageReport Aggregate(IEnumerable<string> callCountPaths)
    {
        var calls = new Dictionary<string, long>(StringComparer.Ordinal);
        var caseCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var projectedMembers = new HashSet<string>(StringComparer.Ordinal);

        foreach (string path in callCountPaths)
        {
            Dictionary<string, long>? counts;
            try
            {
                using FileStream stream = File.OpenRead(path);
                counts = JsonSerializer.Deserialize(
                    stream,
                    CallCountJsonContext.Default.DictionaryStringInt64);
            }
            catch (FileNotFoundException)
            {
                continue;
            }
            catch (DirectoryNotFoundException)
            {
                continue;
            }

            if (counts is null)
            {
                continue;
            }

            foreach ((string member, long count) in counts)
            {
                projectedMembers.Add(member);
                if (count == 0)
                {
                    continue;
                }

                calls.TryGetValue(member, out long existing);
                calls[member] = existing + count;
                caseCounts.TryGetValue(member, out int cases);
                caseCounts[member] = cases + 1;
            }
        }

        return new CoverageReport
        {
            ProjectedMemberCount = projectedMembers.Count,
            CalledMemberCount = calls.Count,
            TotalCalls = calls.Values.Sum(),
            Members = calls
                .OrderByDescending(entry => entry.Value)
                .ThenBy(entry => entry.Key, StringComparer.Ordinal)
                .Select(entry => new CoverageMember
                {
                    Member = entry.Key,
                    Calls = entry.Value,
                    Cases = caseCounts[entry.Key],
                })
                .ToList(),
        };
    }
}
