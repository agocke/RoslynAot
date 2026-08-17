// CA1032: Implement standard exception constructors. The type omits the
// (string) and (string, Exception) constructors.
public class MissingStandardConstructors : System.Exception
{
    public MissingStandardConstructors()
    {
    }
}
