// CA1802: Use literals where appropriate. A static readonly field whose
// initializer is a compile-time constant should be const.
public static class StaticReadonlyConstant
{
    private static readonly int Answer = 42;

    public static int Get() => Answer;
}
