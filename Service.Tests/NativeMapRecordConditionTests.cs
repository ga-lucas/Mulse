using System.Security;
using System.Text.Json;
using System.Xml.Linq;
using Mulse.CompatibilityPack;
using Service.Mapping;

namespace Service.Tests;

public sealed class NativeMapRecordConditionTests
{
    private const string SourceXsd = """
        <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:gate" elementFormDefault="qualified">
          <xs:element name="Order"><xs:complexType><xs:sequence>
            <xs:element name="Qty" type="xs:string" />
            <xs:element name="Line" minOccurs="0" maxOccurs="unbounded"><xs:complexType><xs:sequence>
              <xs:element name="Qty" type="xs:string" />
              <xs:element name="Id" type="xs:string" />
            </xs:sequence></xs:complexType></xs:element>
          </xs:sequence></xs:complexType></xs:element>
        </xs:schema>
        """;
    private const string TargetXsd = """
        <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:gate" elementFormDefault="qualified">
          <xs:element name="Result"><xs:complexType><xs:sequence>
            <xs:element name="Header" minOccurs="0"><xs:complexType><xs:sequence>
              <xs:element name="Id" type="xs:string" />
            </xs:sequence></xs:complexType></xs:element>
            <xs:element name="Empty" minOccurs="0"><xs:complexType><xs:sequence>
              <xs:element name="Id" type="xs:string" minOccurs="0" />
            </xs:sequence></xs:complexType></xs:element>
            <xs:element name="Row" minOccurs="0" maxOccurs="unbounded"><xs:complexType><xs:sequence>
              <xs:element name="Id" type="xs:string" />
            </xs:sequence></xs:complexType></xs:element>
          </xs:sequence></xs:complexType></xs:element>
        </xs:schema>
        """;
    private static readonly XNamespace Ns = "urn:gate";

    private static string Path(string path) => NativeMapImportTests.Path(path);
    private static string L(int id, string from, string to) => $"<Link LinkID=\"{id}\" LinkFrom=\"{SecurityElement.Escape(from)}\" LinkTo=\"{SecurityElement.Escape(to)}\" />";
    private static string Direct(int id, string from, string to) => L(id, Path(from), Path(to));
    private static string Wire(int id, string from, int functoid) => L(id, Path(from), functoid.ToString());
    private static string Out(int id, int functoid, string to) => L(id, functoid.ToString(), Path(to));
    private static string Param(string type, string value, int index) => $"<Parameter Type=\"{type}\" Value=\"{SecurityElement.Escape(value)}\" Guid=\"g\" linkIndex=\"{index}\" />";
    private static string F(int id, int fid, params string[] parameters)
        => $"<Functoid FunctoidID=\"{id}\" Functoid-FID=\"{fid}\" X-Cell=\"1\" Y-Cell=\"1\"><Input-Parameters>{string.Concat(parameters)}</Input-Parameters></Functoid>";
    private static string GreaterThanZero(int id, int inputLink) => F(id, 311, Param("link", inputLink.ToString(), 0), Param("constant", "0", 1));
    private static string Map(string links, string functoids)
        => $"<mapsource><SrcTree RootNode_Name=\"Order\"/><TrgTree RootNode_Name=\"Result\"/><Pages><Page Name=\"p\"><Links>{links}</Links><Functoids>{functoids}</Functoids></Page></Pages></mapsource>";

    private static NativeMapConversion Convert(string map) => BizTalkNativeMapConverter.Convert(map, [SourceXsd], [TargetXsd]);
    private static string Message(string map) => Assert.Throws<ArgumentException>(() => Convert(map)).Message;

    [Theory]
    [InlineData("3", true)]
    [InlineData("0", false)]
    public void LogicalFunctoidLinkedToARecord_BecomesAnEditableRecordCondition(string qty, bool written)
    {
        var conversion = Convert(Map(Direct(1, "Order/Qty", "Result/Header/Id") + Wire(2, "Order/Qty", 10) + Out(3, 10, "Result/Header"), GreaterThanZero(10, 2)));
        var definition = JsonSerializer.Deserialize<XmlMappingDefinition>(conversion.MappingJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var gate = Assert.Single(definition.RecordConditions);
        Assert.EndsWith(":Header", gate.TargetPath, StringComparison.Ordinal);
        Assert.Equal(XmlMappingFunction.LogicalGt, gate.Condition!.Function);
        Assert.Contains(conversion.Warnings, warning => warning.Contains("written only when a logical functoid", StringComparison.Ordinal));
        var output = XDocument.Parse(new XmlMappingEngine(conversion.MappingJson).Transform($"<Order xmlns='urn:gate'><Qty>{qty}</Qty></Order>")).Root!;
        Assert.Equal(written, output.Element(Ns + "Header") is not null);
    }

    [Fact]
    public void LogicalFunctoidLinkedToARepeatingRecord_IsEvaluatedPerSourceRecord()
    {
        var conversion = Convert(Map(
            Direct(1, "Order/Line", "Result/Row") + Direct(2, "Order/Line/Id", "Result/Row/Id") + Wire(3, "Order/Line/Qty", 10) + Out(4, 10, "Result/Row"),
            GreaterThanZero(10, 3)));
        var output = XDocument.Parse(new XmlMappingEngine(conversion.MappingJson).Transform(
            "<Order xmlns='urn:gate'><Qty>1</Qty><Line><Qty>1</Qty><Id>a</Id></Line><Line><Qty>0</Qty><Id>b</Id></Line><Line><Qty>7</Qty><Id>c</Id></Line></Order>")).Root!;
        Assert.Equal(["a", "c"], output.Elements(Ns + "Row").Select(row => row.Element(Ns + "Id")!.Value));
    }

    [Fact]
    public void RecordLinkedFromANonLogicalFunctoid_StaysRefused()
        => Assert.Contains("only a logical functoid", Message(Map(Direct(1, "Order/Qty", "Result/Header/Id") + Wire(2, "Order/Qty", 10) + Out(3, 10, "Result/Header"),
            F(10, 107, Param("link", "2", 0), Param("constant", "x", 1)))), StringComparison.Ordinal);

    [Fact]
    public void RecordLinkedFromTwoLogicalFunctoids_IsRefused()
        => Assert.Contains("more than one logical functoid", Message(Map(
            Direct(1, "Order/Qty", "Result/Header/Id") + Wire(2, "Order/Qty", 10) + Out(3, 10, "Result/Header") + Wire(4, "Order/Qty", 11) + Out(5, 11, "Result/Header"),
            GreaterThanZero(10, 2) + GreaterThanZero(11, 4))), StringComparison.Ordinal);

    [Fact]
    public void ConditionalRecordWithNothingBelow_IsRefused()
        => Assert.Contains("no mapped fields below it", Message(Map(Direct(1, "Order/Qty", "Result/Header/Id") + Wire(2, "Order/Qty", 10) + Out(3, 10, "Result/Empty"),
            GreaterThanZero(10, 2))), StringComparison.Ordinal);

    [Fact]
    public void ConditionReadingARepeatingFieldOutsideItsRecordLink_RangesOverThatRecord()
    {
        var conversion = Convert(Map(Direct(1, "Order/Qty", "Result/Header/Id") + Wire(2, "Order/Line/Qty", 10) + Out(3, 10, "Result/Header"), GreaterThanZero(10, 2)));
        var definition = JsonSerializer.Deserialize<XmlMappingDefinition>(conversion.MappingJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.EndsWith(":Line", Assert.Single(definition.RecordConditions).AnySourcePath, StringComparison.Ordinal);
    }
}
