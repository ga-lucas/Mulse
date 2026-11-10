using System.Security;
using System.Xml.Linq;
using Mulse.CompatibilityPack;
using Mulse.Modules.Mapping;
using Service.Mapping;

namespace Service.Tests;

public sealed class NativeMapCumulativeTests
{
    private const string SourceXsd = """
        <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:cum" elementFormDefault="qualified">
          <xs:element name="Order"><xs:complexType><xs:sequence>
            <xs:element name="Id" type="xs:string" minOccurs="0" />
            <xs:element name="Line" minOccurs="0" maxOccurs="unbounded"><xs:complexType><xs:sequence>
              <xs:element name="Qty" type="xs:string" />
              <xs:element name="Price" type="xs:string" />
              <xs:element name="Kind" type="xs:string" />
              <xs:element name="Part" minOccurs="0" maxOccurs="unbounded"><xs:complexType><xs:sequence>
                <xs:element name="Weight" type="xs:string" />
              </xs:sequence></xs:complexType></xs:element>
            </xs:sequence></xs:complexType></xs:element>
          </xs:sequence></xs:complexType></xs:element>
        </xs:schema>
        """;
    private const string TargetXsd = """
        <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:cum" elementFormDefault="qualified">
          <xs:element name="Result"><xs:complexType><xs:sequence>
            <xs:element name="Total" type="xs:string" />
            <xs:element name="Row" minOccurs="0" maxOccurs="unbounded"><xs:complexType><xs:sequence>
              <xs:element name="Kind" type="xs:string" />
              <xs:element name="Low" type="xs:string" />
            </xs:sequence></xs:complexType></xs:element>
          </xs:sequence></xs:complexType></xs:element>
        </xs:schema>
        """;
    private const string Input = "<Order xmlns='urn:cum'><Line><Qty>2</Qty><Price>1.5</Price><Kind>a</Kind><Part><Weight>5</Weight></Part><Part><Weight>7</Weight></Part></Line>"
        + "<Line><Qty>3</Qty><Price>2</Price><Kind>b</Kind></Line><Line><Qty>1</Qty><Price>4</Price><Kind>a</Kind><Part><Weight>1</Weight></Part></Line></Order>";
    private static readonly XNamespace Ns = "urn:cum";

    private static string Path(string path) => NativeMapImportTests.Path(path);
    private static string L(int id, string from, string to) => $"<Link LinkID=\"{id}\" LinkFrom=\"{SecurityElement.Escape(from)}\" LinkTo=\"{SecurityElement.Escape(to)}\" />";
    private static string Wire(int id, string from, int functoid) => L(id, Path(from), functoid.ToString());
    private static string Between(int id, int from, int to) => L(id, from.ToString(), to.ToString());
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

    [Theory]
    [InlineData(324, "6")]
    [InlineData(325, "2")]
    [InlineData(326, "1")]
    [InlineData(327, "3")]
    [InlineData(328, "231")]
    public void DirectValueInput_FoldsEveryNode(int fid, string expected)
    {
        var conversion = Convert(Map(Wire(1, "Order/Line/Qty", 10) + Out(2, 10, "Result/Total"), Functoid(10, fid, Link(1, 0))));
        Assert.Equal(expected, Run(conversion).Element(Ns + "Total")!.Value);
        Assert.Contains(conversion.Warnings, warning => warning.Contains("Cumulative functoid(s)", StringComparison.Ordinal));
        Assert.Empty(conversion.PendingFunctions);
    }

    [Fact]
    public void ValueFedByAnotherFunctoid_IsFoldedPerSourceRecord()
    {
        var links = Wire(1, "Order/Line/Qty", 11) + Wire(2, "Order/Line/Price", 11) + Between(3, 11, 10) + Out(4, 10, "Result/Total");
        var conversion = Convert(Map(links, Functoid(11, 120, Link(1, 0), Link(2, 1)) + Functoid(10, 324, Link(3, 0))));
        Assert.Equal("13", Run(conversion).Element(Ns + "Total")!.Value);
    }

    [Fact]
    public void ConditionalConcatenation_ThroughAValueMapping_FoldsOnlyMatchingRecords()
    {
        var links = Wire(1, "Order/Line/Kind", 11) + Between(2, 11, 12) + Between(3, 12, 10) + Out(4, 10, "Result/Total");
        var functoids = Functoid(11, 315, Link(1, 0), Constant("a", 1)) + Functoid(12, 374, Link(2, 0), Constant("x", 1)) + Functoid(10, 328, Link(3, 0));
        var conversion = Convert(Map(links, functoids));
        Assert.Equal("xx", Run(conversion).Element(Ns + "Total")!.Value);
    }

    [Fact]
    public void ConditionalSum_FoldsTheValueOfMatchingRecords()
    {
        var links = Wire(1, "Order/Line/Kind", 11) + Wire(5, "Order/Line/Qty", 12) + Between(2, 11, 12) + Between(3, 12, 10) + Out(4, 10, "Result/Total");
        var functoids = Functoid(11, 315, Link(1, 0), Constant("a", 1)) + Functoid(12, 374, Link(2, 0), Link(5, 1)) + Functoid(10, 324, Link(3, 0));
        Assert.Equal("3", Run(Convert(Map(links, functoids))).Element(Ns + "Total")!.Value);
    }

    [Fact]
    public void ScopedToTheLinkedRecord_FoldsWithinEachRecord()
    {
        var links = Direct(1, "Order/Line", "Result/Row") + Direct(2, "Order/Line/Kind", "Result/Row/Kind")
            + Wire(3, "Order/Line/Part/Weight", 10) + Wire(4, "Order/Line", 10) + Out(5, 10, "Result/Row/Low") + Direct(9, "Order/Id", "Result/Total");
        var conversion = Convert(Map(links, Functoid(10, 326, Link(3, 0), Link(4, 1))));
        Assert.Equal(["5", string.Empty, "1"], Run(conversion).Elements(Ns + "Row").Select(row => row.Element(Ns + "Low")!.Value));
    }

    [Fact]
    public void ScopeThatIsARepeatingRecord_NeedsItsRecordLink()
    {
        var links = Wire(3, "Order/Line/Part/Weight", 10) + Wire(4, "Order/Line", 10) + Out(5, 10, "Result/Total");
        var message = Message(Map(links, Functoid(10, 326, Link(3, 0), Link(4, 1))));
        Assert.Contains("not inside a record link", message, StringComparison.Ordinal);
    }

    [Fact]
    public void ScopeThatDoesNotContainTheValue_IsRefused()
    {
        var links = Wire(3, "Order/Line/Qty", 10) + Wire(4, "Order/Id", 10) + Out(5, 10, "Result/Total");
        Assert.Contains("does not contain", Message(Map(links, Functoid(10, 326, Link(3, 0), Link(4, 1)))), StringComparison.Ordinal);
    }

    [Fact]
    public void RecordConstantAndMultiAnchorInputs_AreRefused()
    {
        Assert.Contains("record or otherwise unsupported", Message(Map(Wire(1, "Order/Line", 10) + Out(2, 10, "Result/Total"), Functoid(10, 324, Link(1, 0)))), StringComparison.Ordinal);
        Assert.Contains("constant input", Message(Map(Out(2, 10, "Result/Total"), Functoid(10, 324, Constant("1", 0)))), StringComparison.Ordinal);
        Assert.Contains("0 input(s)", Message(Map(Out(2, 10, "Result/Total"), Functoid(10, 324))), StringComparison.Ordinal);
        var links = Wire(1, "Order/Line/Qty", 11) + Wire(2, "Order/Line/Part/Weight", 11) + Between(3, 11, 10) + Out(4, 10, "Result/Total");
        Assert.Contains("different repeating records", Message(Map(links, Functoid(11, 118, Link(1, 0), Link(2, 1)) + Functoid(10, 324, Link(3, 0)))), StringComparison.Ordinal);
    }

    [Fact]
    public void FoldInsideItsOwnRecordLink_IsRefusedByTheEngine()
    {
        var links = Direct(1, "Order/Line", "Result/Row") + Direct(2, "Order/Line/Kind", "Result/Row/Kind")
            + Wire(3, "Order/Line/Qty", 11) + Wire(4, "Order/Line/Price", 11) + Between(5, 11, 10) + Out(6, 10, "Result/Row/Low") + Direct(9, "Order/Id", "Result/Total");
        Assert.Contains("fold a different path", Message(Map(links, Functoid(11, 120, Link(3, 0), Link(4, 1)) + Functoid(10, 324, Link(5, 0)))), StringComparison.Ordinal);
    }
}
