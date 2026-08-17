// CA2234: Pass system uri objects instead of strings. A Uri-typed
// overload exists but the string one is called.
using System;

public static class StringInsteadOfUri
{
    public static void Fetch(string requestUri)
    {
        _ = requestUri;
    }

    public static void Fetch(Uri requestUri)
    {
        _ = requestUri;
    }

    public static void Call() => Fetch("https://example.invalid/");
}
