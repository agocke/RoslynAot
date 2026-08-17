// CA1867: Use char overload. Single-character string literals passed to
// IndexOf/LastIndexOf and to the StringBuilder append surface.
using System.Text;

public static class SingleCharIndexOf
{
    public static int Find(string value) =>
        value.IndexOf("a", System.StringComparison.Ordinal);

    public static StringBuilder Append(StringBuilder builder) => builder.Append("a");
}
