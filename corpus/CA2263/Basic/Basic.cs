// CA2263: Prefer generic overload when type is known. The type argument is
// a compile-time constant typeof, so Enum.Parse<T> applies.
using System;

public static class NonGenericWhenTypeKnown
{
    public static object Parse(string value) => Enum.Parse(typeof(DayOfWeek), value);
}
