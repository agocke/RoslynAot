// CA2260: Use correct type parameter. IParsable<TSelf> constrains TSelf to
// the implementing type, but this class substitutes 'string' for it.
using System;

public class WrongSelfType : IParsable<string>
{
    public static string Parse(string s, IFormatProvider provider) => s;

    public static bool TryParse(string s, IFormatProvider provider, out string result)
    {
        result = s;
        return true;
    }
}
