// CA1825: Avoid zero-length array allocations. Array.Empty<T>() should be
// used instead of allocating a fresh empty array.
public static class ZeroLengthArray
{
    public static int[] Empty() => new int[0];
}
