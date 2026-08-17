// CA1309: Use ordinal string comparison. A non-linguistic comparison
// asks for InvariantCulture where Ordinal is meant.
public static class OrdinalComparison
{
    public static bool AreEqual(string left, string right) =>
        string.Equals(left, right, System.StringComparison.InvariantCulture);
}
