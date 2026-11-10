using System.Security;
using System.Xml.Linq;
using Mulse.CompatibilityPack;
using Mulse.Modules.Abstractions;
using Mulse.Modules.BuiltIn.Render;
using Service.Mapping;

namespace Service.Tests;

public sealed class NativeMapMultiInputTests
{
    private const string AggregateNs = "http://schemas.microsoft.com/BizTalk/2003/aggschema";
    private const string HeaderXsd = """
        <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:head" elementFormDefault="qualified">
          <xs:element name="Header"><xs:complexType><xs:sequence>
            <xs:element name="Code" type="xs:string" />
          </xs:sequence></xs:complexType></xs:element>
        </xs:schema>
        """;
    private const string BodyXsd = """
        <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:body" elementFormDefault="qualified">
          <xs:element name="Order"><xs:complexType><xs:sequence>
            <xs:element name="Id" type="xs:string" />
            <xs:element name="Line" minOccurs="0" maxOccurs="unbounded"><xs:complexType><xs:sequence>
              <xs:element name="Sku" type="xs:string" />
            </xs:sequence></xs:complexType></xs:element>
          </xs:sequence></xs:complexType></xs:element>
        </xs:schema>
        """;
    private const string TargetXsd = """
        <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:out" elementFormDefault="qualified">
          <xs:element name="Result"><xs:complexType><xs:sequence>
            <xs:element name="Code" type="xs:string" />
            <xs:element name="OrderId" type="xs:string" />
            <xs:element name="Row" minOccurs="0" maxOccurs="unbounded"><xs:complexType><xs:sequence>
              <xs:element name="Sku" type="xs:string" />
            </xs:sequence></xs:complexType></xs:element>
          </xs:sequence></xs:complexType></xs:element>
        </xs:schema>
        """;
    private const string Wrapper = $"""
        <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:tns="{AggregateNs}" xmlns:ns1="urn:head" xmlns:ns2="urn:body" targetNamespace="{AggregateNs}">
          <xs:import schemaLocation="Some.Class.Header" namespace="urn:head" />
          <xs:import schemaLocation="Some.Class.Body" namespace="urn:body" />
          <xs:element name="Root"><xs:complexType><xs:sequence>
            <xs:element name="InputMessagePart_0"><xs:complexType><xs:sequence><xs:element ref="ns1:Header" /></xs:sequence></xs:complexType></xs:element>
            <xs:element name="InputMessagePart_1"><xs:complexType><xs:sequence><xs:element ref="ns2:Order" /></xs:sequence></xs:complexType></xs:element>
          </xs:sequence></xs:complexType></xs:element>
        </xs:schema>
        """;
    private static readonly string Input = $"<Root xmlns='{AggregateNs}'><InputMessagePart_0 xmlns=''><Header xmlns='urn:head'><Code>H1</Code></Header></InputMessagePart_0>"
        + "<InputMessagePart_1 xmlns=''><Order xmlns='urn:body'><Id>O9</Id><Line><Sku>a</Sku></Line><Line><Sku>b</Sku></Line></Order></InputMessagePart_1></Root>";
    private static readonly XNamespace Out = "urn:out";

    private static string Path(string path) => NativeMapImportTests.Path(path);
    private static string Direct(int id, string from, string to) => $"<Link LinkID=\"{id}\" LinkFrom=\"{SecurityElement.Escape(Path(from))}\" LinkTo=\"{SecurityElement.Escape(Path(to))}\" />";

    private static string Map(string links, string sourceTree)
        => $"<mapsource><SrcTree RootNode_Name=\"Root\">{sourceTree}</SrcTree><TrgTree RootNode_Name=\"Result\"/><Pages><Page Name=\"p\"><Links>{links}</Links></Page></Pages></mapsource>";

    private static readonly string Links = Direct(1, "Root/InputMessagePart_0/Header/Code", "Result/Code")
        + Direct(2, "Root/InputMessagePart_1/Order/Id", "Result/OrderId")
        + Direct(3, "Root/InputMessagePart_1/Order/Line", "Result/Row")
        + Direct(4, "Root/InputMessagePart_1/Order/Line/Sku", "Result/Row/Sku");

    [Fact]
    public void InlineAggregateWrapper_IsAnalyzedWithThePartSchemas()
    {
        var conversion = BizTalkNativeMapConverter.Convert(Map(Links, Wrapper), [HeaderXsd, BodyXsd], [TargetXsd]);
        var result = XDocument.Parse(new XmlMappingEngine(conversion.MappingJson).Transform(Input)).Root!;
        Assert.Equal("H1", result.Element(Out + "Code")!.Value);
        Assert.Equal("O9", result.Element(Out + "OrderId")!.Value);
        Assert.Equal(["a", "b"], result.Elements(Out + "Row").Select(row => row.Element(Out + "Sku")!.Value));
        var warning = Assert.Single(conversion.Warnings, warning => warning.Contains("multi-input map", StringComparison.Ordinal));
        Assert.Contains("xml-parts-render", warning, StringComparison.Ordinal);
        Assert.Contains("InputMessagePart_0", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void ConvertedMap_KeepsTheWrapperWhenSchemasAreEmbedded()
    {
        var conversion = BizTalkNativeMapConverter.Convert(Map(Links, Wrapper), [HeaderXsd, BodyXsd], [TargetXsd]);
        Assert.Contains("InputMessagePart_1", conversion.MappingJson, StringComparison.Ordinal);
        Assert.Contains("urn:head", conversion.MappingJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PartsRenderOutput_FeedsTheConvertedMapAsDocumentedInTheWarning()
    {
        var conversion = BizTalkNativeMapConverter.Convert(Map(Links, Wrapper), [HeaderXsd, BodyXsd], [TargetXsd]);
        var step = new ModuleStepDefinition { Module = "xml-parts-render", Settings = new()
        {
            ["rootName"] = "Root", ["rootNamespace"] = AggregateNs, ["partNames"] = "InputMessagePart_0,InputMessagePart_1", ["outputName"] = "parts.xml"
        } };
        var parts = new IntegrationBatch([
            new IntegrationPayload("0.xml", BinaryData.FromString("<Header xmlns='urn:head'><Code>H1</Code></Header>"), "application/xml", new Dictionary<string, string>()),
            new IntegrationPayload("1.xml", BinaryData.FromString("<Order xmlns='urn:body'><Id>O9</Id><Line><Sku>a</Sku></Line></Order>"), "application/xml", new Dictionary<string, string>())]);
        var aggregate = Assert.Single((await new XmlPartsRenderModule().RenderAsync(new FlowExecutionContext("parts", "synthetic", DateTimeOffset.UtcNow), parts, step, default)).Payloads);
        var result = XDocument.Parse(new XmlMappingEngine(conversion.MappingJson).Transform(aggregate.GetText())).Root!;
        Assert.Equal("H1", result.Element(Out + "Code")!.Value);
        Assert.Equal("a", result.Element(Out + "Row")!.Element(Out + "Sku")!.Value);
    }

    [Fact]
    public void MissingPartSchema_IsRefused()
        => Assert.Throws<ArgumentException>(() => BizTalkNativeMapConverter.Convert(Map(Links, Wrapper), [BodyXsd], [TargetXsd]));

    [Fact]
    public void InlineSchemaThatIsNotTheAggregateWrapper_IsRefused()
    {
        var other = Wrapper.Replace(AggregateNs, "urn:other", StringComparison.Ordinal);
        var exception = Assert.Throws<ArgumentException>(() => BizTalkNativeMapConverter.Convert(Map(Links, other), [HeaderXsd, BodyXsd], [TargetXsd]));
        Assert.Contains("aggregate wrapper", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InlineSchemaBesideAReference_IsRefused()
        => Assert.Throws<ArgumentException>(() => BizTalkNativeMapConverter.Convert(Map(Links, Wrapper + "<Reference Location=\"X\" />"), [HeaderXsd, BodyXsd], [TargetXsd]));

    [Fact]
    public void InlineTargetSchema_IsStillUnsupported()
    {
        var map = Map(Links, string.Empty).Replace("<TrgTree RootNode_Name=\"Result\"/>", $"<TrgTree RootNode_Name=\"Result\">{Wrapper}</TrgTree>", StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => BizTalkNativeMapConverter.Convert(map, [HeaderXsd, BodyXsd], [TargetXsd]));
    }
}
