using System.Text.Json;
using System.Text.Json.Serialization;

namespace RoslynAot.Csc;

/// <summary>
/// Writes the per-member boundary call counts collected by
/// <see cref="RoslynCallCounters"/> when <c>ROSLYNAOT_CALL_COUNTS</c> names an
/// output path. The differential harness sets it per case; it is otherwise
/// inert, so ordinary compilations pay only the counting itself.
/// </summary>
internal static class RoslynCallCountReport
{
    public const string PathVariable = "ROSLYNAOT_CALL_COUNTS";

    public static void WriteIfRequested()
    {
        string? path = Environment.GetEnvironmentVariable(PathVariable);
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        long[] counts = RoslynCallCounters.Snapshot();

        // Overloads share a display name, so slots are summed rather than
        // emitted per slot. Every projected member appears, including the
        // never-called ones: an absent row would be indistinguishable from a
        // member the generator never projected at all. Sorted so the file is
        // deterministic across runs.
        //
        // A null name is a retired slot, kept only so the slots after it stay
        // put. Emitting one would do the opposite of the rule above and report
        // a member the projection no longer has.
        var members = new SortedDictionary<string, long>(StringComparer.Ordinal);
        for (int index = 0; index < counts.Length; index++)
        {
            if (RoslynCallCounters.MemberNames[index] is not string name)
            {
                continue;
            }

            members.TryGetValue(name, out long existing);
            members[name] = existing + counts[index];
        }

        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(
            path,
            JsonSerializer.Serialize(
                members,
                RoslynCallCountJsonContext.Default.SortedDictionaryStringInt64));
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(SortedDictionary<string, long>))]
internal sealed partial class RoslynCallCountJsonContext : JsonSerializerContext;
