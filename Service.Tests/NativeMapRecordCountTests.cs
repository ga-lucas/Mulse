using System.Security;
using System.Xml.Linq;
using Mulse.CompatibilityPack;
using Mulse.Modules.Mapping;
using Service.Mapping;

namespace Service.Tests;

public sealed class NativeMapRecordCountTests
{
    private const string SourceXsd = """
        <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:count" elementFormDefault="qualified">
          <xs:element name="Order"><xs:complexType><xs:sequence>
            <xs:element name="Id" type="xs:string" minOccurs="0" />
            <xs:element name="Line" minOccurs="0" maxOccurs="unbounded"><xs:complexType><xs:sequence>
              <xs:element name="Sku" type="xs:string" />
              <xs:element name="Tag" type="xs:string" minOccurs="0" maxOccurs="unbounded" />
            </xs:sequence></xs:complexType></xs:element>
          </xs:sequence></xs:complexType></xs:element>
        </xs:schema>
        """;
    private const string TargetXsd = """
        <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:count" elementFormDefault="qualified">
          <xs:element name="Result"><xs:complexType><xs:sequence>
            <xs:element name="Total" type="xs:string" />
            <xs:element name="Row" minOccurs="0" maxOccurs="unbounded"><xs:complexType><xs:sequence>
              <xs:element name="Sku" type="xs:string" />
              <xs:element name="TagCount" type="xs:string" />
            </xs:sequence></xs:complexType></xs:element>
          </xs:sequence></xs:complexType></xs:element>
        </xs:schema>
        """;
    private const string Input = "<Order xmlns='urn:count'><Line><Sku>a</Sku><Tag>x</Tag><Tag>y</Tag></Line><Line><Sku>b</Sku></Line><Line><Sku>c</Sku><Tag>z</Tag></Line></Order>";
    private static readonly XNamespace Ns = "urn:count";

    private static string Path(string path) => NativeMapImportTests.Path(path);
    private static string L(int id, string from, string to) => $"<Link LinkID=\"{id}\" LinkFrom=\"{SecurityElement.Escape(from)}\" LinkTo=\"{SecurityElement.Escape(to)}\" />";
    private static string Wire(int id, string from, int functoid) => L(id, Path(from), functoid.ToString());
    private static string Out(int id, int functoid, string to) => L(id, functoid.ToString(), Path(to));
    private static string Direct(int id, string from, string to) => L(id, Path(from), Path(to));
    private static string Link(int link, int index) => $"<Parameter Type=\"link\" Value=\"{link}\" Guid=\"g\" linkIndex=\"{index}\" />";
    private static string Constant(string value, int index) => $"<Parameter Type=\"constant\" Value=\"{value}\" Guid=\"g\" linkIndex=\"{index}\" />";
    private static string Functoid(int id, int fid, params string[] parameters)
        => $"<Functoid FunctoidID=\"{id}\" Functoid-FID=\"{fid}\" X-Cell=\"1\" Y-Cell=\"1\"><Input-Parameters>{string.Concat(parameters)}</Input-Parameters></Functoid>";
    private static string Map(string links, string functoids)
        => $"<mapsource><SrcTree RootNode_Name=\"Order\"/><TrgTree RootNode_Name=\"Result\"/><Pages><Page Name=\"p\"><Links>{links}</Links><Functoids>{functoids}</Functoids></Page></Pages></mapsource>";

    private static NativeMapConversion Convert(string map) => BizTalkNativeMapConverter.Convert(map, [SourceXsd], [TargetXsd]);
    private static string Message(string map) => Assert.Throws<ArgumentException>(() => Convert(map)).Message;
    private static XElement Run(NativeMapConversion conversion) => XDocument.Parse(new XmlMappingEngine(conversion.MappingJson).Transform(Input)).Root!;

    private static string CountToTotal(string countedPath) => Wire(1, countedPath, 10) + Out(2, 10, "Result/Total");

    [Fact]
    public void CountOfARepeatingRecord_WritesTheNumberOfRecords()
    {
        var conversion = Convert(Map(CountToTotal("Order/Line"), Functoid(10, 322, Link(1, 0))));
        Assert.Equal("3", Run(conversion).Element(Ns + "Total")!.Value);
        Assert.Contains(conversion.Warnings, warning => warning.Contains("Record Count functoid(s) (FID 322)", StringComparison.Ordinal));
        Assert.Empty(conversion.PendingFunctions);
    }

    [Fact]
    public void CountFeedsOtherFunctoids()
    {
        var links = Wire(1, "Order/Line", 10) + L(2, "10", "11") + Out(3, 11, "Result/Total");
        var conversion = Convert(Map(links, Functoid(10, 322, Link(1, 0)) + Functoid(11, 118, Link(2, 0), Constant("10", 1))));
        Assert.Equal("13", Run(conversion).Element(Ns + "Total")!.Value);
    }

    [Fact]
    public void CountInsideALinkedRecord_CountsWithinThatRecord()
    {
        var links = CountToTotal("Order/Line") + Direct(3, "Order/Line", "Result/Row") + Direct(4, "Order/Line/Sku", "Result/Row/Sku")
            + Wire(5, "Order/Line/Tag", 11) + Out(6, 11, "Result/Row/TagCount");
        var conversion = Convert(Map(links, Functoid(10, 322, Link(1, 0)) + Functoid(11, 322, Link(5, 0))));
        var result = Run(conversion);
        Assert.Equal("3", result.Element(Ns + "Total")!.Value);
        Assert.Equal(["2", "0", "1"], result.Elements(Ns + "Row").Select(row => row.Element(Ns + "TagCount")!.Value));
    }

    [Fact]
    public void CountOfValuesBelowARepeatOutsideItsRecordLink_IsRefused()
    {
        var message = Message(Map(CountToTotal("Order/Line/Tag"), Functoid(10, 322, Link(1, 0))));
        Assert.Contains("sits below repeating", message, StringComparison.Ordinal);
        Assert.Contains("No partial native map was produced", message, StringComparison.Ordinal);
    }

    [Fact]
    public void CountInputsThatAreNotASingleSourceNode_AreRefused()
    {
        Assert.Contains("exactly one input", Message(Map(Out(2, 10, "Result/Total"), Functoid(10, 322, Constant("1", 0)))), StringComparison.Ordinal);
        Assert.Contains("exactly one input", Message(Map(Out(2, 10, "Result/Total"), Functoid(10, 322))), StringComparison.Ordinal);
        var chained = Wire(1, "Order/Line", 11) + L(2, "11", "10") + Out(3, 10, "Result/Total");
        Assert.Contains("exactly one input", Message(Map(chained, Functoid(11, 322, Link(1, 0)) + Functoid(10, 322, Link(2, 0)))), StringComparison.Ordinal);
        var two = Wire(1, "Order/Line", 10) + Wire(2, "Order/Id", 10) + Out(3, 10, "Result/Total");
        Assert.Contains("exactly one input", Message(Map(two, Functoid(10, 322, Link(1, 0), Link(2, 1)))), StringComparison.Ordinal);
    }
}
