// CA2354: Unsafe DataSet or DataTable in deserialized object graph can be
// vulnerable to remote code execution attacks. The deserialization result
// is cast to a [Serializable] type whose graph contains a DataSet.
//
// BinaryFormatter is obsolete-as-error (SYSLIB0011); the analyzer keys on
// the API shape, so the obsoletion is suppressed rather than avoided.
#pragma warning disable SYSLIB0011

using System.Data;
using System.IO;
using System.Runtime.Serialization.Formatters.Binary;

[System.Serializable]
public class GraphWithDataSet
{
    public DataSet Data { get; set; }
}

public static class BinaryDeserialization
{
    public static GraphWithDataSet Deserialize(Stream stream)
    {
        var formatter = new BinaryFormatter();
        return (GraphWithDataSet)formatter.Deserialize(stream);
    }
}
