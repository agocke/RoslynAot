// CA1856: Incorrect usage of ConstantExpected attribute. The attribute is
// applied to a parameter whose type cannot hold a constant.
using System.Diagnostics.CodeAnalysis;

public static class MisappliedConstantExpected
{
    public static void Take([ConstantExpected] object value)
    {
        _ = value;
    }
}
