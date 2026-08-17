// CA1805: Do not initialize unnecessarily. Both fields are explicitly
// initialized to the value the runtime already guarantees.
public class RedundantInitialization
{
    private int _count = 0;
    private string _name = null;

    public int Count => _count;

    public string Name => _name;
}
