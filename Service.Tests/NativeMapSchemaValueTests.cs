using System.Security;
using System.Xml.Linq;
using Mulse.CompatibilityPack;
using Service.Mapping;

namespace Service.Tests;

public sealed class NativeMapSchemaValueTests
{
    private const string SourceXsd = """
        <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:val" elementFormDefault="qualified">
          <xs:element name="Order"><xs:complexType><xs:sequence>
            <xs:element name="Note" type="xs:string" minOccurs="0" />
            <xs:element name="Item" minOccurs="0" maxOccurs="unbounded"><xs:complexType><xs:sequence>
              <xs:element name="Id" type="xs:string" />
            </xs:sequence></xs:complexType></xs:element>
          </xs:sequence></xs:complexType></xs:element>
        </xs:schema>
        """;
    private static readonly XNamespace Ns = "urn:val";

    private static string Target(string body)
        => $"""
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:val" elementFormDefault="qualified">
              <xs:element name="Result"><xs:complexType><xs:sequence>{body}</xs:sequence></xs:complexType></xs:element>
            </xs:schema>
            """;
    private static string Path(string path) => NativeMapImportTests.Path(path);
    private static string Direct(int id, string from, string to)
        => $"<Link LinkID=\"{id}\" LinkFrom=\"{SecurityElement.Escape(Path(from))}\" LinkTo=\"{SecurityElement.Escape(Path(to))}\" />";
    private static string Map(string links)
        => $"<mapsource><SrcTree RootNode_Name=\"Order\"/><TrgTree RootNode_Name=\"Result\"/><Pages><Page Name=\"p\"><Links>{links}</Links><Functoids /></Page></Pages><TreeValues><ConstantValues /></TreeValues></mapsource>";
    private static NativeMapConversion Convert(string links, string targetBody)
        => BizTalkNativeMapConverter.Convert(Map(links), [SourceXsd], [Target(targetBody)]);

    private const string Note = "<xs:element name=\"Note\" type=\"xs:string\" minOccurs=\"0\" />";

    [Fact]
    public void RequiredFixedTarget_WithoutALink_IsWrittenAsConstant_AndWarns()
    {
        var conversion = Convert(Direct(1, "Order/Note", "Result/Note"), "<xs:element name=\"Kind\" type=\"xs:string\" fixed=\"K\" />" + Note);
        var result = XDocument.Parse(new XmlMappingEngine(conversion.MappingJson).Transform("<Order xmlns='urn:val'><Note>n</Note></Order>")).Root!;
        Assert.Equal("K", result.Element(Ns + "Kind")!.Value);
        Assert.Equal("n", result.Element(Ns + "Note")!.Value);
        Assert.Contains(conversion.Warnings, warning => warning.Contains("fixed schema value", StringComparison.Ordinal));
    }

    [Fact]
    public void FixedTarget_ThatIsAlsoLinked_IsRefused()
        => Assert.Contains("default/fixed", Assert.Throws<ArgumentException>(() => Convert(Direct(1, "Order/Note", "Result/Kind"),
            "<xs:element name=\"Kind\" type=\"xs:string\" fixed=\"K\" />")).Message);

    [Fact]
    public void OptionalFixedTarget_InAnEmittedRecord_IsRefused()
        => Assert.Contains("optional fixed", Assert.Throws<ArgumentException>(() => Convert(Direct(1, "Order/Note", "Result/Note"),
            "<xs:element name=\"Flag\" type=\"xs:string\" fixed=\"F\" minOccurs=\"0\" />" + Note)).Message);

    [Fact]
    public void DefaultOnlyTarget_InAnEmittedRecord_IsRefused()
        => Assert.Throws<ArgumentException>(() => Convert(Direct(1, "Order/Note", "Result/Note"),
            "<xs:element name=\"Mode\" type=\"xs:string\" default=\"D\" minOccurs=\"0\" />" + Note));

    [Fact]
    public void FixedValue_InAnOptionalRecordThatIsNotEmitted_DoesNotBlockTheMap()
    {
        var conversion = Convert(Direct(1, "Order/Note", "Result/Note"),
            "<xs:element name=\"Meta\" minOccurs=\"0\"><xs:complexType><xs:sequence><xs:element name=\"Kind\" type=\"xs:string\" fixed=\"K\" /></xs:sequence></xs:complexType></xs:element>" + Note);
        var result = XDocument.Parse(new XmlMappingEngine(conversion.MappingJson).Transform("<Order xmlns='urn:val'><Note>n</Note></Order>")).Root!;
        Assert.Null(result.Element(Ns + "Meta"));
    }

    [Fact]
    public void RepeatingSequenceGroupMember_IsARepeatingRecordTarget_AndWarns()
    {
        const string body = """
            <xs:sequence minOccurs="0" maxOccurs="unbounded">
              <xs:element name="Line" minOccurs="0"><xs:complexType><xs:sequence><xs:element name="Id" type="xs:string" /></xs:sequence></xs:complexType></xs:element>
            </xs:sequence>
            """;
        var conversion = Convert(Direct(1, "Order/Item", "Result/Line") + Direct(2, "Order/Item/Id", "Result/Line/Id"), body);
        var result = XDocument.Parse(new XmlMappingEngine(conversion.MappingJson).Transform(
            "<Order xmlns='urn:val'><Item><Id>a</Id></Item><Item><Id>b</Id></Item></Order>")).Root!;
        Assert.Equal(["a", "b"], result.Elements(Ns + "Line").Select(line => line.Element(Ns + "Id")!.Value));
        Assert.Contains(conversion.Warnings, warning => warning.Contains("repeating group", StringComparison.Ordinal));
    }

    [Fact]
    public void SequenceGroupSegmentsInLinkPaths_AreSkipped()
    {
        const string body = "<xs:sequence><xs:element name=\"A\" type=\"xs:string\" /></xs:sequence>";
        var to = Path("Result/A").Replace("/*[local-name()='A']", "/*[local-name()='<Sequence>' and position()='1']/*[local-name()='A']", StringComparison.Ordinal);
        var link = $"<Link LinkID=\"1\" LinkFrom=\"{SecurityElement.Escape(Path("Order/Note"))}\" LinkTo=\"{SecurityElement.Escape(to)}\" />";
        var conversion = Convert(link, body);
        var result = XDocument.Parse(new XmlMappingEngine(conversion.MappingJson).Transform("<Order xmlns='urn:val'><Note>n</Note></Order>")).Root!;
        Assert.Equal("n", result.Element(Ns + "A")!.Value);
    }

    [Fact]
    public void BareSequenceGroupSegment_IsAlsoSkipped()
    {
        const string body = "<xs:sequence><xs:element name=\"A\" type=\"xs:string\" /></xs:sequence>";
        var to = Path("Result/A").Replace("/*[local-name()='A']", "/*[local-name()='<Sequence>']/*[local-name()='A']", StringComparison.Ordinal);
        var link = $"<Link LinkID=\"1\" LinkFrom=\"{SecurityElement.Escape(Path("Order/Note"))}\" LinkTo=\"{SecurityElement.Escape(to)}\" />";
        var result = XDocument.Parse(new XmlMappingEngine(Convert(link, body).MappingJson).Transform("<Order xmlns='urn:val'><Note>n</Note></Order>")).Root!;
        Assert.Equal("n", result.Element(Ns + "A")!.Value);
    }

    [Fact]
    public void RepeatingChoiceGroup_IsStillRefused()
        => Assert.Contains("requires explicit translation", Assert.Throws<ArgumentException>(() => Convert(Direct(1, "Order/Note", "Result/Note"),
            "<xs:choice maxOccurs=\"unbounded\"><xs:element name=\"A\" type=\"xs:string\" /><xs:element name=\"B\" type=\"xs:string\" /></xs:choice>" + Note)).Message);

    [Fact]
    public void OptionalGroupWithRequiredMembers_MapsMembersAndWarnsThatPresenceIsNotEnforced()
    {
        const string body = """
            <xs:sequence minOccurs="0">
              <xs:element name="A" type="xs:string" /><xs:element name="B" type="xs:string" />
            </xs:sequence>
            """;
        var conversion = Convert(Direct(1, "Order/Note", "Result/A"), body);
        var result = XDocument.Parse(new XmlMappingEngine(conversion.MappingJson).Transform("<Order xmlns='urn:val'><Note>n</Note></Order>")).Root!;
        Assert.Equal("n", result.Element(Ns + "A")!.Value);
        Assert.Contains(conversion.Warnings, warning => warning.Contains("optional group", StringComparison.Ordinal));
    }
}
