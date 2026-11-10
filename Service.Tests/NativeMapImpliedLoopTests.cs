using System.Security;
using System.Text.Json;
using System.Xml.Linq;
using Mulse.CompatibilityPack;
using Service.Mapping;

namespace Service.Tests;

public sealed class NativeMapImpliedLoopTests
{
    private const string SourceXsd = """
        <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:loop" elementFormDefault="qualified">
          <xs:element name="Order"><xs:complexType><xs:sequence>
            <xs:element name="Qty" type="xs:string" />
            <xs:element name="Line" minOccurs="0" maxOccurs="unbounded"><xs:complexType><xs:sequence>
              <xs:element name="Id" type="xs:string" />
              <xs:element name="Sub" minOccurs="0" maxOccurs="unbounded"><xs:complexType><xs:sequence>
                <xs:element name="Code" type="xs:string" />
              </xs:sequence></xs:complexType></xs:element>
            </xs:sequence></xs:complexType></xs:element>
            <xs:element name="Other" minOccurs="0" maxOccurs="unbounded"><xs:complexType><xs:sequence>
              <xs:element name="Id" type="xs:string" />
            </xs:sequence></xs:complexType></xs:element>
          </xs:sequence></xs:complexType></xs:element>
        </xs:schema>
        """;
    private const string TargetXsd = """
        <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:loop" elementFormDefault="qualified">
          <xs:element name="Result"><xs:complexType><xs:sequence>
            <xs:element name="Single" minOccurs="0"><xs:complexType><xs:sequence>
              <xs:element name="Value" type="xs:string" />
            </xs:sequence></xs:complexType></xs:element>
            <xs:element name="Row" minOccurs="0" maxOccurs="unbounded"><xs:complexType><xs:sequence>
              <xs:element name="Id" type="xs:string" minOccurs="0" />
              <xs:element name="Total" type="xs:string" minOccurs="0" />
              <xs:element name="Inner" minOccurs="0" maxOccurs="unbounded"><xs:complexType><xs:sequence>
                <xs:element name="Code" type="xs:string" />
              </xs:sequence></xs:complexType></xs:element>
            </xs:sequence></xs:complexType></xs:element>
          </xs:sequence></xs:complexType></xs:element>
        </xs:schema>
        """;
    private static readonly XNamespace Ns = "urn:loop";
    private const string Input = "<Order xmlns='urn:loop'><Qty>1</Qty>"
        + "<Line><Id>a</Id><Sub><Code>a1</Code></Sub><Sub><Code>a2</Code></Sub></Line>"
        + "<Line><Id>b</Id></Line>"
        + "<Line><Id>c</Id><Sub><Code>c1</Code></Sub></Line></Order>";

    private static string Path(string path) => NativeMapImportTests.Path(path);
    private static string L(int id, string from, string to) => $"<Link LinkID=\"{id}\" LinkFrom=\"{SecurityElement.Escape(from)}\" LinkTo=\"{SecurityElement.Escape(to)}\" />";
    private static string Direct(int id, string from, string to) => L(id, Path(from), Path(to));
    private static string Wire(int id, string from, int functoid) => L(id, Path(from), functoid.ToString());
    private static string Out(int id, int functoid, string to) => L(id, functoid.ToString(), Path(to));
    private static string Param(string type, string value, int index) => $"<Parameter Type=\"{type}\" Value=\"{SecurityElement.Escape(value)}\" Guid=\"g\" linkIndex=\"{index}\" />";
    private static string F(int id, int fid, params string[] parameters)
        => $"<Functoid FunctoidID=\"{id}\" Functoid-FID=\"{fid}\" X-Cell=\"1\" Y-Cell=\"1\"><Input-Parameters>{string.Concat(parameters)}</Input-Parameters></Functoid>";
    private static string Concat(int id, int inputLink) => F(id, 107, Param("link", inputLink.ToString(), 0), Param("constant", "-x", 1));
    private static string Map(string links, string functoids)
        => $"<mapsource><SrcTree RootNode_Name=\"Order\"/><TrgTree RootNode_Name=\"Result\"/><Pages><Page Name=\"p\"><Links>{links}</Links><Functoids>{functoids}</Functoids></Page></Pages></mapsource>";

    private static NativeMapConversion Convert(string map) => BizTalkNativeMapConverter.Convert(map, [SourceXsd], [TargetXsd]);
    private static string Message(string map) => Assert.Throws<ArgumentException>(() => Convert(map)).Message;
    private static XElement Run(NativeMapConversion conversion) => XDocument.Parse(new XmlMappingEngine(conversion.MappingJson).Transform(Input)).Root!;
    private static XmlMappingDefinition Definition(NativeMapConversion conversion)
        => JsonSerializer.Deserialize<XmlMappingDefinition>(conversion.MappingJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    [Fact]
    public void DirectLinkBelowRepeatingSource_WithoutRecordLink_InfersOneTargetRecordPerSourceRecord()
    {
        var conversion = Convert(Map(Direct(1, "Order/Line/Id", "Result/Row/Id"), ""));
        var repeat = Assert.Single(Definition(conversion).Repeats);
        Assert.EndsWith(":Line", repeat.SourcePath, StringComparison.Ordinal);
        Assert.Contains(conversion.Warnings, warning => warning.Contains("record link(s) were inferred", StringComparison.Ordinal));
        Assert.Equal(["a", "b", "c"], Run(conversion).Elements(Ns + "Row").Select(row => row.Element(Ns + "Id")!.Value));
    }

    [Fact]
    public void FunctoidInputBelowRepeatingSource_InfersTheLoop()
    {
        var conversion = Convert(Map(Wire(1, "Order/Line/Id", 10) + Out(2, 10, "Result/Row/Id"), Concat(10, 1)));
        Assert.Equal(["a-x", "b-x", "c-x"], Run(conversion).Elements(Ns + "Row").Select(row => row.Element(Ns + "Id")!.Value));
    }

    [Fact]
    public void NestedRepeatingRecords_InferNestedLoops()
    {
        var conversion = Convert(Map(Direct(1, "Order/Line/Id", "Result/Row/Id") + Direct(2, "Order/Line/Sub/Code", "Result/Row/Inner/Code"), ""));
        Assert.Equal(2, Definition(conversion).Repeats.Count);
        var rows = Run(conversion).Elements(Ns + "Row").ToList();
        Assert.Equal(3, rows.Count);
        Assert.Equal(["a1", "a2"], rows[0].Elements(Ns + "Inner").Select(inner => inner.Element(Ns + "Code")!.Value));
        Assert.Empty(rows[1].Elements(Ns + "Inner"));
        Assert.Equal(["c1"], rows[2].Elements(Ns + "Inner").Select(inner => inner.Element(Ns + "Code")!.Value));
    }

    [Fact]
    public void ExplicitRecordLink_IsNotReplacedByAnInferredOne()
    {
        var conversion = Convert(Map(Direct(1, "Order/Line", "Result/Row") + Direct(2, "Order/Line/Id", "Result/Row/Id"), ""));
        Assert.Single(Definition(conversion).Repeats);
        Assert.DoesNotContain(conversion.Warnings, warning => warning.Contains("record link(s) were inferred", StringComparison.Ordinal));
        Assert.Equal(3, Run(conversion).Elements(Ns + "Row").Count());
    }

    [Fact]
    public void FunctoidInputBelowRepeatingSource_WithoutRepeatingTarget_StaysRefused()
        => Assert.Contains("Line", Message(Map(Wire(1, "Order/Line/Id", 10) + Out(2, 10, "Result/Single/Value"), Concat(10, 1))), StringComparison.Ordinal);

    [Fact]
    public void FieldsFromUnrelatedRepeatingSources_UnderOneTargetRecord_AreRefused()
        => Assert.Contains("unrelated repeating source records", Message(Map(
            Direct(1, "Order/Line/Id", "Result/Row/Id") + Direct(2, "Order/Other/Id", "Result/Row/Total"), "")), StringComparison.Ordinal);

    [Fact]
    public void AnchorOutsideTheEnclosingRecordLink_IsRefused()
        => Assert.Contains("not inside the record link", Message(Map(
            Direct(1, "Order/Line", "Result/Row") + Direct(2, "Order/Line/Id", "Result/Row/Id") + Direct(3, "Order/Other/Id", "Result/Row/Inner/Code"), "")), StringComparison.Ordinal);
}
