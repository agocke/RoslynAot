// CA1311: Specify a culture or use an invariant version. ToUpper() with
// no culture argument is culture-sensitive by default.
public static class CultureSensitiveCasing
{
    public static string Shout(string value) => value.ToUpper();
}
