namespace RoslynAot.DifferentialHarness;

/// <summary>
/// Best-effort semver-like ordering for the dotted, optionally
/// prerelease-tagged version strings <c>dotnet --list-sdks</c> and the
/// NuGet pack directories on disk use. A plain lexical sort ranks any
/// "-preview.N" build after a same-prefix stable release once the
/// stable release is one character shorter, which silently picks a
/// preview SDK the day a final release ships.
/// </summary>
internal readonly struct DottedVersion : IComparable<DottedVersion>
{
    private readonly string[] _release;
    private readonly string[]? _prerelease;

    public DottedVersion(string text)
    {
        Text = text;
        int dash = text.IndexOf('-');
        string releasePart = dash < 0 ? text : text[..dash];
        _release = releasePart.Split('.');
        _prerelease = dash < 0 ? null : text[(dash + 1)..].Split('.');
    }

    public string Text { get; }

    public int CompareTo(DottedVersion other)
    {
        int releaseCompare = CompareSegments(_release, other._release);
        if (releaseCompare != 0)
        {
            return releaseCompare;
        }

        if (_prerelease is null && other._prerelease is null)
        {
            return 0;
        }

        if (_prerelease is null)
        {
            return 1;
        }

        if (other._prerelease is null)
        {
            return -1;
        }

        return CompareSegments(_prerelease, other._prerelease);
    }

    private static int CompareSegments(string[] left, string[] right)
    {
        int length = Math.Max(left.Length, right.Length);
        for (int index = 0; index < length; index++)
        {
            string a = index < left.Length ? left[index] : "0";
            string b = index < right.Length ? right[index] : "0";
            int segmentCompare = int.TryParse(a, out int ai) &&
                int.TryParse(b, out int bi)
                ? ai.CompareTo(bi)
                : string.CompareOrdinal(a, b);
            if (segmentCompare != 0)
            {
                return segmentCompare;
            }
        }

        return 0;
    }

    public override string ToString() => Text;
}
