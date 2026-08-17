// CA2020: Prevent behavioral change. Starting with .NET 7 the built-in
// IntPtr '+' operator throws on overflow in a checked context, where the
// old user-defined operator silently wrapped.
using System;

public static class IntPtrArithmetic
{
    public static IntPtr Add(IntPtr pointer, int offset) => checked(pointer + offset);
}
