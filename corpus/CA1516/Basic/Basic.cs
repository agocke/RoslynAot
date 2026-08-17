// CA1516: Use cross-platform intrinsics. Sse.Add has a Vector128.Add
// equivalent that works on every architecture.
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

public static class PlatformSpecificIntrinsic
{
    public static Vector128<float> Add(Vector128<float> left, Vector128<float> right) =>
        Sse.Add(left, right);
}
