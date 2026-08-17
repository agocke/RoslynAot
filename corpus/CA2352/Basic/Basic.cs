// CA2352: Unsafe DataSet or DataTable in serializable type can be
// vulnerable to remote code execution attacks.
using System.Data;

[System.Serializable]
public class SerializableWithDataSet
{
    public DataSet Data { get; set; }

    public DataTable Table { get; set; }
}
