// CA1870: Use a cached 'SearchValues' instance. A fresh char array is
// allocated on every call to IndexOfAny.
public static class UncachedSearchValues
{
    public static int FindAny(string value) =>
        value.IndexOfAny(new[] { 'a', 'b', 'c' });
}
