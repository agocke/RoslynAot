// CA1508: Avoid dead conditional code. The second null test can never be
// true - a dataflow-based rule, so this case also exercises the
// FlowAnalysis surface of the projected API.
public static class DeadConditional
{
    public static int Classify(string value)
    {
        if (value is null)
        {
            return 0;
        }

        if (value is null)
        {
            return 1;
        }

        return 2;
    }
}
