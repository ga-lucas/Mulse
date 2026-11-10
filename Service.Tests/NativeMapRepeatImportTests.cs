using System.Xml.Linq;
using Mulse.CompatibilityPack;
using Service.Mapping;

namespace Service.Tests;

public sealed class NativeMapRepeatImportTests
{
    private const string SourceXsd = """
        <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:s" elementFormDefault="unqualified">
          <xs:element name="Order"><xs:complexType><xs:sequence>
            <xs:element name="Id" type="xs:string" />
            <xs:element name="Lines" nillable="true"><xs:complexType><xs:sequence>
              <xs:element name="Line" minOccurs="0" maxOccurs="unbounded"><xs:complexType><xs:sequence>
                <xs:element name="Sku" type="xs:string" />
                <xs:element name="Qty" type="xs:string" minOccurs="0" />
                <xs:element name="Tag" type="xs:string" minOccurs="0" maxOccurs="unbounded" />
                <xs:element name="Parts" minOccurs="0"><xs:complexType><xs:sequence>
                  <xs:element name="Part" type="xs:string" minOccurs="0" maxOccurs="unbounded" />
                </xs:sequence></xs:complexType></xs:element>
              </xs:sequence></xs:complexType></xs:element>
            </xs:sequence></xs:complexType></xs:element>
          </xs:sequence></xs:complexType></xs:element>
        </xs:schema>
        """;

    private const string TargetXsd = """
        <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:t" elementFormDefault="unqualified">
          <xs:element name="Result"><xs:complexType><xs:sequence>
            <xs:element name="Number" type="xs:string" />
            <xs:element name="Rows"><xs:complexType><xs:sequence>
              <xs:element name="Row" minOccurs="0" maxOccurs="unbounded"><xs:complexType><xs:sequence>
                <xs:element name="Sku" type="xs:string" />
                <xs:element name="Qty" type="xs:string" minOccurs="0" />
                <xs:element name="Tag" type="xs:string" minOccurs="0" maxOccurs="unbounded" />
                <xs:element name="Parts" minOccurs="0"><xs:complexType><xs:sequence>
                  <xs:element name="Part" type="xs:string" minOccurs="0" maxOccurs="unbounded" />
                </xs:sequence></xs:complexType></xs:element>
              </xs:sequence></xs:complexType></xs:element>
            </xs:sequence></xs:complexType></xs:element>
          </xs:sequence></xs:complexType></xs:element>
        </xs:schema>
        """;

    private static readonly string[] Standard =
    [
        "Order/Id>Result/Number", "Order/Lines>Result/Rows", "Order/Lines/Line>Result/Rows/Row",
        "Order/Lines/Line/Sku>Result/Rows/Row/Sku", "Order/Lines/Line/Qty>Result/Rows/Row/Qty",
        "Order/Lines/Line/Tag>Result/Rows/Row/Tag",
        "Order/Lines/Line/Parts>Result/Rows/Row/Parts", "Order/Lines/Line/Parts/Part>Result/Rows/Row/Parts/Part"
    ];

    private static string Map(IEnumerable<string> links, string extraAttributes = "")
    {
        var document = XDocument.Parse($"<mapsource Name='t' {extraAttributes}><SrcTree RootNode_Name='Order'/><TrgTree RootNode_Name='Result'/><Pages><Page><Links/></Page></Pages></mapsource>");
        var container = document.Descendants("Links").Single();
        var id = 1;
        foreach (var link in links)
        {
            var parts = link.Split('>');
            container.Add(new XElement("Link", new XAttribute("LinkID", id++),
                new XAttribute("LinkFrom", NativeMapImportTests.Path(parts[0])), new XAttribute("LinkTo", NativeMapImportTests.Path(parts[1]))));
        }
        return document.ToString();
    }

    private static NativeMapConversion Convert(IEnumerable<string> links, string extraAttributes = "")
        => BizTalkNativeMapConverter.Convert(Map(links, extraAttributes), [SourceXsd], [TargetXsd]);

    [Fact]
    public void RecordAndRepeatLinks_ConvertToCorrelatedRepeatsAndBizTalkOutput()
    {
        var conversion = Convert(Standard);
        var engine = new XmlMappingEngine(conversion.MappingJson);
        var output = XDocument.Parse(engine.Transform("""
            <s:Order xmlns:s="urn:s"><Id>7</Id><Lines>
              <Line><Sku>A</Sku><Qty>2</Qty><Tag>x</Tag><Tag>y</Tag><Parts><Part>p1</Part><Part>p2</Part></Parts></Line>
              <Line><Sku>B</Sku></Line>
            </Lines></s:Order>
            """));
        var rows = output.Root!.Element("Rows")!.Elements("Row").ToArray();
        Assert.Equal(2, rows.Length);
        Assert.Equal(["A", "B"], rows.Select(row => row.Element("Sku")!.Value));
        Assert.Equal(["x", "y"], rows[0].Elements("Tag").Select(tag => tag.Value));
        Assert.Equal(["p1", "p2"], rows[0].Element("Parts")!.Elements("Part").Select(part => part.Value));
        Assert.Null(rows[1].Element("Qty"));
        Assert.Null(rows[1].Element("Parts"));
        Assert.Equal("7", output.Root.Element("Number")!.Value);
    }

    [Fact]
    public void ZeroSourceRecords_ProduceNoTargetRecordsButKeepRequiredContainers()
    {
        var output = XDocument.Parse(new XmlMappingEngine(Convert(Standard).MappingJson).Transform("<s:Order xmlns:s=\"urn:s\"><Id>1</Id><Lines/></s:Order>"));
        Assert.Empty(output.Root!.Element("Rows")!.Elements());
    }

    [Fact]
    public void NilSourceRecord_FailsClosed()
    {
        var engine = new XmlMappingEngine(Convert(Standard).MappingJson);
        Assert.Contains("xsi:nil", Assert.Throws<ArgumentException>(() => engine.Transform(
            "<s:Order xmlns:s='urn:s' xmlns:xsi='http://www.w3.org/2001/XMLSchema-instance'><Id>1</Id><Lines><Line xsi:nil='true'/></Lines></s:Order>")).Message);
    }

    [Fact]
    public void ConvertedRepeats_AreSerializedWithMappingAndStayEditable()
    {
        var map = System.Text.Json.JsonSerializer.Deserialize<Mulse.CompatibilityPack.Mapping.XmlMappingDefinition>(Convert(Standard).MappingJson,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
        Assert.Equal(5, map.Repeats.Count);
        Assert.All(map.Repeats, repeat => Assert.DoesNotContain("local-name", repeat.TargetPath));
    }

    [Theory]
    [InlineData("Order/Lines/Line/Sku>Result/Number")]
    [InlineData("Order/Lines/Line>Result/Number")]
    [InlineData("Order/Id>Result/Rows/Row")]
    public void InvalidRecordOrRepeatLinks_RejectWholeProposal(string extra)
    {
        var links = Standard.Where(link => !link.StartsWith("Order/Id>", StringComparison.Ordinal)).Append(extra);
        Assert.Throws<ArgumentException>(() => Convert(links));
    }

    [Fact]
    public void DuplicateRecordLinksAndEmptyLinkedRecords_AreRejected()
    {
        Assert.Throws<ArgumentException>(() => Convert([.. Standard, "Order/Lines/Line>Result/Rows/Row"]));
        Assert.Throws<ArgumentException>(() => Convert(["Order/Id>Result/Number", "Order/Lines>Result/Rows", "Order/Lines/Line>Result/Rows/Row"]));
    }

    [Fact]
    public void RequiredTargetWithoutALink_IsRejected()
        => Assert.Contains("not produced", Assert.Throws<ArgumentException>(() => Convert(Standard.Where(link => link != "Order/Lines/Line/Sku>Result/Rows/Row/Sku"))).Message);

    [Fact]
    public void OptionalRecordCreatedWithoutARecordLink_IsRejected()
        => Assert.Contains("Optional record", Assert.Throws<ArgumentException>(() => Convert(
            Standard.Where(link => link != "Order/Lines/Line/Parts>Result/Rows/Row/Parts"))).Message);

    [Theory]
    [InlineData("UseXSLTransform", "Undefined", true)]
    [InlineData("UseXSLTransform", "Yes", false)]
    [InlineData("PreserveSequenceOrder", "Yes", false)]
    public void MapSettingsForRepeatingMaps_AreChecked(string name, string value, bool accepted)
    {
        var attributes = $"{name}='{value}'";
        if (accepted) Assert.NotEmpty(Convert(Standard, attributes).MappingJson);
        else Assert.Throws<ArgumentException>(() => Convert(Standard, attributes));
    }
}
