// CA1851: Possible multiple enumerations of 'IEnumerable' collection. The
// sequence is walked once by Any() and again by Sum().
using System.Collections.Generic;
using System.Linq;

public static class MultipleEnumeration
{
    public static int SumOrZero(IEnumerable<int> values)
    {
        if (!values.Any())
        {
            return 0;
        }

        return values.Sum();
    }
}
