// CA1857: A constant is expected for the parameter. The callee marks the
// parameter [ConstantExpected] but the caller passes a runtime value.
using System.Diagnostics.CodeAnalysis;

public static class NonConstantArgument
{
    public static void Take([ConstantExpected] int value)
    {
        _ = value;
    }

    public static void Call(int runtimeValue) => Take(runtimeValue);
}
