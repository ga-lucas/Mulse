using System.Security;
using System.Xml.Linq;
using Mulse.CompatibilityPack;
using Mulse.Modules.Mapping;
using Service.Mapping;

namespace Service.Tests;

public sealed class NativeMapFirstOccurrenceTests
{
    private const string SourceXsd = """
        <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:first" elementFormDefault="qualified">
          <xs:element name="Order"><xs:complexType><xs:sequence>
            <xs:element name="Line" minOccurs="0" maxOccurs="unbounded"><xs:complexType><xs:sequence>
              <xs:element name="Sku" type="xs:string" />
              <xs:element name="Tag" type="xs:string" minOccurs="0" />
              <xs:element name="Part" minOccurs="0" maxOccurs="unbounded"><xs:complexType><xs:sequence>
                <xs:element name="Code" type="xs:string" />
              </xs:sequence></xs:complexType></xs:element>
            </xs:sequence></xs:complexType></xs:element>
          </xs:sequence></xs:complexType></xs:element>
        </xs:schema>
        """;
    private const string TargetXsd = """
        <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:first" elementFormDefault="qualified">
          <xs:element name="Result"><xs:complexType><xs:sequence>
            <xs:element name="FirstTag" type="xs:string" minOccurs="0" />
            <xs:element name="Row" minOccurs="0" maxOccurs="unbounded"><xs:complexType><xs:sequence>
              <xs:element name="TagValue" type="xs:string" minOccurs="0" />
              <xs:element name="Sku" type="xs:string" minOccurs="0" />
            </xs:sequence></xs:complexType></xs:element>
          </xs:sequence></xs:complexType></xs:element>
        </xs:schema>
        """;
    private const string Input = "<Order xmlns='urn:first'><Line><Sku>a</Sku></Line><Line><Sku>b</Sku><Tag>y</Tag></Line><Line><Sku>c</Sku><Tag>z</Tag></Line></Order>";
    private static readonly XNamespace Ns = "urn:first";

    private static string Path(string path) => NativeMapImportTests.Path(path);
    private static string Direct(int id, string from, string to)
        => $"<Link LinkID=\"{id}\" LinkFrom=\"{SecurityElement.Escape(Path(from))}\" LinkTo=\"{SecurityElement.Escape(Path(to))}\" />";
    private static string Map(string links)
        => $"<mapsource><SrcTree RootNode_Name=\"Order\"/><TrgTree RootNode_Name=\"Result\"/><Pages><Page Name=\"p\"><Links>{links}</Links><Functoids /></Page></Pages></mapsource>";

    private static NativeMapConversion Convert(string map) => BizTalkNativeMapConverter.Convert(map, [SourceXsd], [TargetXsd]);

    [Fact]
    public void DirectLinkBelowARepeatingRecord_WithoutARecordLink_TakesTheFirstOccurrence_AndWarns()
    {
        var conversion = Convert(Map(Direct(1, "Order/Line/Tag", "Result/FirstTag")));
        var result = XDocument.Parse(new XmlMappingEngine(conversion.MappingJson).Transform(Input)).Root!;
        Assert.Equal("y", result.Element(Ns + "FirstTag")!.Value);
        Assert.Contains(conversion.Warnings, warning => warning.Contains("only the first occurrence is mapped", StringComparison.Ordinal));
    }

    [Fact]
    public void DirectLinkBelowARepeatingRecord_WithNoOccurrence_OmitsTheTarget()
    {
        var conversion = Convert(Map(Direct(1, "Order/Line/Tag", "Result/FirstTag")));
        var result = XDocument.Parse(new XmlMappingEngine(conversion.MappingJson).Transform("<Order xmlns='urn:first'><Line><Sku>a</Sku></Line></Order>")).Root!;
        Assert.Null(result.Element(Ns + "FirstTag"));
    }

    [Fact]
    public void DirectLinkInsideARecordLink_ToAnUncoveredRepeatingLevel_IsStillRefused()
    {
        var links = Direct(1, "Order/Line", "Result/Row") + Direct(2, "Order/Line/Part/Code", "Result/Row/TagValue");
        var message = Assert.Throws<ArgumentException>(() => Convert(Map(links))).Message;
        Assert.Contains("sits below repeating", message, StringComparison.Ordinal);
        Assert.Contains("No partial native map was produced", message, StringComparison.Ordinal);
    }
}
