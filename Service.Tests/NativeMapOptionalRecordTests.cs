using System.Security;
using System.Xml.Linq;
using Mulse.CompatibilityPack;
using Service.Mapping;

namespace Service.Tests;

public sealed class NativeMapOptionalRecordTests
{
    private const string SourceXsd = """
        <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:opt" elementFormDefault="qualified">
          <xs:element name="Order"><xs:complexType><xs:sequence>
            <xs:element name="Note" type="xs:string" minOccurs="0" />
          </xs:sequence></xs:complexType></xs:element>
        </xs:schema>
        """;
    private const string TargetXsd = """
        <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:opt" elementFormDefault="qualified">
          <xs:element name="Result"><xs:complexType><xs:sequence>
            <xs:element name="Meta" minOccurs="0"><xs:complexType><xs:sequence>
              <xs:element name="Origin" type="xs:string" minOccurs="0" />
              <xs:element name="Note" type="xs:string" minOccurs="0" />
            </xs:sequence></xs:complexType></xs:element>
          </xs:sequence></xs:complexType></xs:element>
        </xs:schema>
        """;
    private static readonly XNamespace Ns = "urn:opt";

    private static string Path(string path) => NativeMapImportTests.Path(path);
    private static string Direct(int id, string from, string to)
        => $"<Link LinkID=\"{id}\" LinkFrom=\"{SecurityElement.Escape(Path(from))}\" LinkTo=\"{SecurityElement.Escape(Path(to))}\" />";
    private static string Map(string links, string constants = "")
        => $"<mapsource><SrcTree RootNode_Name=\"Order\"/><TrgTree RootNode_Name=\"Result\"/><Pages><Page Name=\"p\"><Links>{links}</Links><Functoids /></Page></Pages><TreeValues><ConstantValues>{constants}</ConstantValues></TreeValues></mapsource>";
    private static string Constant(string to, string value) => $"<Value Query=\"{SecurityElement.Escape(Path(to))}\" value=\"{value}\" />";

    private static NativeMapConversion Convert(string map) => BizTalkNativeMapConverter.Convert(map, [SourceXsd], [TargetXsd]);

    [Fact]
    public void OptionalRecord_WithAConstantBelow_IsAlwaysCreated_AndWarns()
    {
        var conversion = Convert(Map(Direct(1, "Order/Note", "Result/Meta/Note"), Constant("Result/Meta/Origin", "svc")));
        var engine = new XmlMappingEngine(conversion.MappingJson);
        var without = XDocument.Parse(engine.Transform("<Order xmlns='urn:opt'/>")).Root!;
        Assert.Equal("svc", without.Element(Ns + "Meta")!.Element(Ns + "Origin")!.Value);
        Assert.Null(without.Element(Ns + "Meta")!.Element(Ns + "Note"));
        var with = XDocument.Parse(engine.Transform("<Order xmlns='urn:opt'><Note>n</Note></Order>")).Root!;
        Assert.Equal("n", with.Element(Ns + "Meta")!.Element(Ns + "Note")!.Value);
        Assert.Contains(conversion.Warnings, warning => warning.Contains("always created", StringComparison.Ordinal));
    }

    [Fact]
    public void OptionalRecord_WithOnlyDirectLinksBelow_IsStillRefused()
        => Assert.Contains("Optional record", Assert.Throws<ArgumentException>(() => Convert(Map(Direct(1, "Order/Note", "Result/Meta/Note")))).Message);
}
