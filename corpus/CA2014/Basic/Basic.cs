// CA2014: Do not use stackalloc in loops. Each iteration adds to the
// frame, so the stack grows without bound.
using System;

public static class StackallocInLoop
{
    public static int Total()
    {
        int total = 0;
        for (int i = 0; i < 10; i++)
        {
            Span<int> buffer = stackalloc int[16];
            total += buffer.Length;
        }

        return total;
    }
}
