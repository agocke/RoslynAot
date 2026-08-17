// CA1845: Use span-based 'string.Concat'. Concatenating the result of
// Substring allocates an intermediate string.
public static class SubstringConcat
{
    public static string TrimFirstAndAppend(string value) =>
        value.Substring(1) + "suffix";
}
