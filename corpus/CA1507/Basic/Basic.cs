// CA1507: Use nameof to express symbol names. The literal "value"
// matches a parameter name in scope.
public static class LiteralParameterName
{
    public static void Validate(string value)
    {
        if (value is null)
        {
            throw new System.ArgumentNullException("value");
        }
    }
}
