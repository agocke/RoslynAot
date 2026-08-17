// CA2353: Unsafe DataSet or DataTable in serializable type. The type is
// serializable through a non-[Serializable] mechanism - here the
// data-contract and XML serializers.
using System.Data;
using System.Runtime.Serialization;
using System.Xml.Serialization;

[DataContract]
public class DataContractWithDataSet
{
    [DataMember]
    public DataSet Data { get; set; }
}

[XmlRoot("root")]
public class XmlSerializableWithDataTable
{
    [XmlElement("table")]
    public DataTable Table { get; set; }
}
