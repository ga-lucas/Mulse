using System.Security;
using System.Xml.Linq;
using Mulse.CompatibilityPack;
using Service.Mapping;

namespace Service.Tests;

public sealed class NativeMapIndexTests
{
    private const string AggregateNs = "http://schemas.microsoft.com/BizTalk/2003/aggschema";
    private const string SourceXsd = """
        <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:idx" elementFormDefault="qualified">
          <xs:element name="Order"><xs:complexType><xs:sequence>
            <xs:element name="Idx" type="xs:string" minOccurs="0" />
            <xs:element name="Case" minOccurs="0" maxOccurs="unbounded"><xs:complexType><xs:sequence>
              <xs:element name="Item" minOccurs="0" maxOccurs="unbounded"><xs:complexType><xs:sequence>
                <xs:element name="Id" type="xs:string" />
              </xs:sequence></xs:complexType></xs:element>
            </xs:sequence></xs:complexType></xs:element>
            <xs:element name="Flat" minOccurs="0" maxOccurs="unbounded"><xs:complexType><xs:sequence>
              <xs:element name="Name" type="xs:string" />
            </xs:sequence></xs:complexType></xs:element>
            <xs:element name="Single" type="xs:string" minOccurs="0" />
          </xs:sequence></xs:complexType></xs:element>
        </xs:schema>
        """;
    private const string IndexerXsd = """
        <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:indexer" elementFormDefault="qualified">
          <xs:element name="Indexer"><xs:complexType><xs:sequence>
            <xs:element name="Current" type="xs:string" />
          </xs:sequence></xs:complexType></xs:element>
        </xs:schema>
        """;
    private const string TargetXsd = """
        <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:idx" elementFormDefault="qualified">
          <xs:element name="Result"><xs:complexType><xs:sequence>
            <xs:element name="Pick" type="xs:string" />
          </xs:sequence></xs:complexType></xs:element>
        </xs:schema>
        """;
    private const string Input = "<Order xmlns='urn:idx'><Idx>2</Idx><Case><Item><Id>a1</Id></Item><Item><Id>a2</Id></Item></Case><Case><Item><Id>b1</Id></Item><Item><Id>b2</Id></Item></Case>"
        + "<Flat><Name>f1</Name></Flat><Flat><Name>f2</Name></Flat><Single>s</Single></Order>";
    private static readonly XNamespace Ns = "urn:idx";

    private static string Path(string path) => NativeMapImportTests.Path(path);
    private static string L(int id, string from, string to) => $"<Link LinkID=\"{id}\" LinkFrom=\"{SecurityElement.Escape(from)}\" LinkTo=\"{SecurityElement.Escape(to)}\" />";
    private static string Wire(int id, string from, int functoid) => L(id, Path(from), functoid.ToString());
    private static string Out(int id, int functoid, string to) => L(id, functoid.ToString(), Path(to));
    private static string Link(int link, int index) => $"<Parameter Type=\"link\" Value=\"{link}\" Guid=\"g\" linkIndex=\"{index}\" />";
    private static string Constant(string value, int index) => $"<Parameter Type=\"constant\" Value=\"{value}\" Guid=\"g\" linkIndex=\"{index}\" />";
    private static string Functoid(int id, int fid, params string[] parameters)
        => $"<Functoid FunctoidID=\"{id}\" Functoid-FID=\"{fid}\" X-Cell=\"1\" Y-Cell=\"1\"><Input-Parameters>{string.Concat(parameters)}</Input-Parameters></Functoid>";
    private static string Map(string links, string functoids, string rootName = "Order", string sourceTree = "")
        => $"<mapsource><SrcTree RootNode_Name=\"{rootName}\">{sourceTree}</SrcTree><TrgTree RootNode_Name=\"Result\"/><Pages><Page Name=\"p\"><Links>{links}</Links><Functoids>{functoids}</Functoids></Page></Pages></mapsource>";

    private static NativeMapConversion Convert(string map) => BizTalkNativeMapConverter.Convert(map, [SourceXsd], [TargetXsd]);
    private static string Message(string map) => Assert.Throws<ArgumentException>(() => Convert(map)).Message;
    private static string Pick(NativeMapConversion conversion, string input = Input)
        => XDocument.Parse(new XmlMappingEngine(conversion.MappingJson).Transform(input)).Root!.Element(Ns + "Pick")!.Value;

    private static string IdLinks(params int[] unused) => Wire(1, "Order/Case/Item/Id", 10) + Out(9, 10, "Result/Pick");

    [Fact]
    public void Index_WithAConstantAndASourceIndex_SelectsThatOccurrenceOfEachLevel()
    {
        var links = IdLinks() + Wire(2, "Order/Idx", 10);
        var conversion = Convert(Map(links, Functoid(10, 323, Link(1, 0), Constant("2", 1), Link(2, 2))));
        Assert.Equal("b2", Pick(conversion));
        Assert.Contains(conversion.Warnings, warning => warning.Contains("Index functoid(s) (FID 323)", StringComparison.Ordinal));
        Assert.Empty(conversion.PendingFunctions);
    }

    [Fact]
    public void Index_OutOfRange_WritesAnEmptyValue()
    {
        var conversion = Convert(Map(IdLinks(), Functoid(10, 323, Link(1, 0), Constant("5", 1), Constant("1", 2))));
        Assert.Equal(string.Empty, Pick(conversion));
    }

    [Fact]
    public void Index_FedByAnotherFunctoid_IsEvaluated()
    {
        var links = IdLinks() + Wire(2, "Order/Idx", 11) + L(3, "11", "10");
        var conversion = Convert(Map(links, Functoid(10, 323, Link(1, 0), Constant("1", 1), Link(3, 2)) + Functoid(11, 118, Link(2, 0), Constant("-1", 1))));
        Assert.Equal("a1", Pick(conversion));
    }

    [Fact]
    public void Index_OverASingleRepeatingRecord_NeedsOneIndex()
    {
        var links = Wire(1, "Order/Flat/Name", 10) + Out(9, 10, "Result/Pick");
        Assert.Equal("f2", Pick(Convert(Map(links, Functoid(10, 323, Link(1, 0), Constant("2", 1))))));
    }

    [Fact]
    public void Index_WhenTheIndexCountDoesNotMatchTheRepeatingLevels_IsRefused()
    {
        var message = Message(Map(IdLinks(), Functoid(10, 323, Link(1, 0), Constant("1", 1))));
        Assert.Contains("2 repeating level", message, StringComparison.Ordinal);
        Assert.Contains("No partial native map was produced", message, StringComparison.Ordinal);
        var none = Wire(1, "Order/Single", 10) + Out(9, 10, "Result/Pick");
        Assert.Contains("0 repeating level", Message(Map(none, Functoid(10, 323, Link(1, 0), Constant("1", 1)))), StringComparison.Ordinal);
    }

    [Fact]
    public void Index_WithUnsupportedInputs_IsRefused()
    {
        Assert.Contains("at least one index", Message(Map(IdLinks(), Functoid(10, 323, Link(1, 0)))), StringComparison.Ordinal);
        Assert.Contains("directly from the source message", Message(Map(Out(9, 10, "Result/Pick"), Functoid(10, 323, Constant("a", 0), Constant("1", 1)))), StringComparison.Ordinal);
        var repeatingIndex = IdLinks() + Wire(2, "Order/Flat/Name", 10);
        Assert.Contains("sits below repeating", Message(Map(repeatingIndex, Functoid(10, 323, Link(1, 0), Constant("1", 1), Link(2, 2)))), StringComparison.Ordinal);
    }

    [Fact]
    public void Index_TakesItsValueFromOnePartAndItsIndexFromAnotherPartOfAMultiInputMap()
    {
        const string wrapper = $"""
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:a="urn:idx" xmlns:b="urn:indexer" targetNamespace="{AggregateNs}">
              <xs:import schemaLocation="X.Order" namespace="urn:idx" /><xs:import schemaLocation="X.Indexer" namespace="urn:indexer" />
              <xs:element name="Root"><xs:complexType><xs:sequence>
                <xs:element name="InputMessagePart_0"><xs:complexType><xs:sequence><xs:element ref="a:Order" /></xs:sequence></xs:complexType></xs:element>
                <xs:element name="InputMessagePart_1"><xs:complexType><xs:sequence><xs:element ref="b:Indexer" /></xs:sequence></xs:complexType></xs:element>
              </xs:sequence></xs:complexType></xs:element>
            </xs:schema>
            """;
        var links = Wire(1, "Root/InputMessagePart_0/Order/Case/Item/Id", 10) + Wire(2, "Root/InputMessagePart_1/Indexer/Current", 10) + Out(9, 10, "Result/Pick");
        var map = Map(links, Functoid(10, 323, Link(1, 0), Constant("2", 1), Link(2, 2)), "Root", wrapper);
        var conversion = BizTalkNativeMapConverter.Convert(map, [SourceXsd, IndexerXsd], [TargetXsd]);
        var aggregate = $"<Root xmlns='{AggregateNs}'><InputMessagePart_0 xmlns=''>{Input}</InputMessagePart_0><InputMessagePart_1 xmlns=''><Indexer xmlns='urn:indexer'><Current>1</Current></Indexer></InputMessagePart_1></Root>";
        Assert.Equal("b1", Pick(conversion, aggregate));
        Assert.Contains(conversion.Warnings, warning => warning.Contains("multi-input map", StringComparison.Ordinal));
    }
}
