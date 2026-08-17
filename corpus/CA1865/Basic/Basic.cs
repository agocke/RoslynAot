// CA1865: Use char overload. A single-character string literal passed to
// StartsWith/EndsWith with an explicit ordinal comparison.
public static class SingleCharOrdinal
{
    public static bool Starts(string value) =>
        value.StartsWith("a", System.StringComparison.Ordinal);

    public static bool Ends(string value) =>
        value.EndsWith("z", System.StringComparison.Ordinal);
}
