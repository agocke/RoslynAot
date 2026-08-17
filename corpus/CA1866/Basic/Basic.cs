// CA1866: Use char overload. A single-character string literal passed to
// StartsWith/EndsWith with no comparison argument.
public static class SingleCharDefaultComparison
{
    public static bool Starts(string value) => value.StartsWith("a");

    public static bool Ends(string value) => value.EndsWith("z");
}
