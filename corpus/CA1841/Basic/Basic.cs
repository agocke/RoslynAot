// CA1841: Prefer Dictionary.Contains methods. Enumerating Keys/Values
// through Linq does an O(n) scan where the dictionary has an O(1) method.
using System.Collections.Generic;
using System.Linq;

public static class DictionaryContains
{
    public static bool HasKey(Dictionary<string, int> map) => map.Keys.Contains("key");

    public static bool HasValue(Dictionary<string, int> map) => map.Values.Contains(1);
}
