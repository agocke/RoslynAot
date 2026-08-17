// CA2355: Unsafe DataSet or DataTable type found in deserializable object
// graph. Unlike CA2354 this covers every serializer, not just the
// [Serializable]/BinaryFormatter pair - here the data-contract and XML
// serializers are handed a type whose graph contains a DataTable.
using System.Data;
using System.IO;
using System.Runtime.Serialization;
using System.Xml.Serialization;

public class GraphWithDataTable
{
    public DataTable Table { get; set; }
}

public static class ContractDeserialization
{
    public static GraphWithDataTable FromDataContract(Stream stream)
    {
        var serializer = new DataContractSerializer(typeof(GraphWithDataTable));
        return (GraphWithDataTable)serializer.ReadObject(stream);
    }

    public static GraphWithDataTable FromXml(Stream stream)
    {
        var serializer = new XmlSerializer(typeof(GraphWithDataTable));
        return (GraphWithDataTable)serializer.Deserialize(stream);
    }
}
