// CA1855: Prefer 'Clear' over 'Fill'. Filling a span with the default
// value is what Clear() does, more efficiently.
using System;

public static class FillWithDefault
{
    public static void ResetSpan(Span<byte> buffer) => buffer.Fill(0);

    public static void ResetArray(byte[] buffer) => Array.Fill(buffer, (byte)0);
}
