// CA1515: Consider making public types internal. The rule only applies to
// assemblies that are applications rather than libraries, so case.json
// overrides the harness's default /target:library with /target:exe.
public class PubliclyVisibleInAnApplication
{
    public static void Main()
    {
    }
}
