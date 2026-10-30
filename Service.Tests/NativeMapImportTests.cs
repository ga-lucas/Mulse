using System.Text.Json;
using System.Xml.Linq;
using Mulse.CompatibilityPack;
using Mulse.Modules.Mapping;
using Service.Mapping;

namespace Service.Tests;

public sealed class NativeMapImportTests
{
    internal const string SourceXsd = """
        <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:source" elementFormDefault="qualified">
          <xs:element name="Order"><xs:complexType><xs:sequence>
            <xs:element name="Id" type="xs:string" />
            <xs:element name="Optional" type="xs:string" minOccurs="0" />
            <xs:element name="Item" type="xs:string" minOccurs="0" maxOccurs="unbounded" />
          </xs:sequence><xs:attribute name="code" type="xs:string" use="required" /></xs:complexType></xs:element>
        </xs:schema>
        """;
    internal const string TargetXsd = """
        <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:target">
          <xs:element name="Result"><xs:complexType><xs:sequence>
            <xs:element name="Number" type="xs:string" />
            <xs:element name="Text" type="xs:string" minOccurs="0" />
          </xs:sequence><xs:attribute name="code" type="xs:string" use="required" /></xs:complexType></xs:element>
        </xs:schema>
        """;
    internal static string Path(string path) => "/*[local-name()='<Schema>']" + string.Concat(path.Split('/').Select(segment =>
        segment.StartsWith('@') ? "/@*[local-name()='" + segment[1..] + "']" : "/*[local-name()='" + segment + "']"));
    internal static string Map(string source = "Order/Id", string target = "Result/Number", string extra = "") => new XElement("mapsource",
        new XElement("SrcTree", new XAttribute("RootNode_Name", "Order")), new XElement("TrgTree", new XAttribute("RootNode_Name", "Result")),
        new XElement("Pages", new XElement("Page", new XElement("Links",
            new XElement("Link", new XAttribute("LinkFrom", Path("Order/@code")), new XAttribute("LinkTo", Path("Result/@code"))),
            new XElement("Link", new XAttribute("LinkFrom", Path(source)), new XAttribute("LinkTo", Path(target)))))),
        new XElement("TreeValues", new XElement("ConstantValues", new XElement("Value", new XAttribute("Query", Path("Result/Text")), new XAttribute("value", "<&>{'quoted'}")))))
        .ToString().Replace("</mapsource>", extra + "</mapsource>", StringComparison.Ordinal);

    [Fact]
    public void SchemaTrees_ResolveExpandedNamesAndInheritedCardinality()
    {
        var tree = XmlSchemaInspector.Analyze([SourceXsd]);
        var root = Assert.Single(tree.Roots);
        Assert.Equal("urn:source:Order", root.Name);
        Assert.Equal("/n1:Order/n1:Id", root.Fields.Single(field => field.LocalPath == "/Order/Id").Path);
        Assert.True(root.Fields.Single(field => field.LocalPath == "/Order/Item").Repeating);
        Assert.False(root.Fields.Single(field => field.LocalPath == "/Order/Optional").Required);
        Assert.True(root.Fields.Single(field => field.LocalPath == "/Order/@code").Required);
        var target = Assert.Single(XmlSchemaInspector.Analyze([TargetXsd]).Roots);
        Assert.Equal("/n1:Result/Number", target.Fields.Single(field => field.LocalPath == "/Result/Number").Path);
    }

    [Fact]
    public void CompleteScalarMap_ProducesEditableDefinitionAndRealRuntimeOutputInSchemaOrder()
    {
        var conversion = BizTalkNativeMapConverter.Convert(Map(), [SourceXsd], [TargetXsd]);
        var definition = JsonSerializer.Deserialize<XmlMappingDefinition>(conversion.MappingJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal("tn1:Result", definition.TargetRoot);
        Assert.Equal(3, definition.Fields.Count);
        Assert.Equal("/sn1:Order/@code", definition.Fields[0].SourcePath);
        Assert.Contains(conversion.Warnings, warning => warning.Contains("No deployed policy association", StringComparison.Ordinal));
        var output = XDocument.Parse(new XmlMappingEngine(conversion.MappingJson).Transform("<Order xmlns='urn:source' code='A&amp;B'><Id>42</Id><Item>x</Item></Order>"));
        Assert.Equal(XName.Get("Result", "urn:target"), output.Root!.Name);
        Assert.Equal("A&B", output.Root.Attribute("code")!.Value);
        Assert.Equal(new[] { "Number", "Text" }, output.Root.Elements().Select(element => element.Name.LocalName));
        Assert.Equal("42", output.Root.Element("Number")!.Value);
        Assert.Equal("<&>{'quoted'}", output.Root.Element("Text")!.Value);
        Assert.Equal(new XmlMappingEngine(conversion.MappingJson).Xslt, conversion.Xslt);
    }

    [Fact]
    public void OptionalSourceToOptionalTarget_UsesVersion2PresenceCondition()
    {
        var document = XDocument.Parse(Map("Order/Optional", "Result/Text"));
        document.Descendants("ConstantValues").Single().Remove();
        document.Descendants("Links").Single().Add(new XElement("Link",
            new XAttribute("LinkFrom", Path("Order/Id")), new XAttribute("LinkTo", Path("Result/Number"))));
        var conversion = BizTalkNativeMapConverter.Convert(document.ToString(), [SourceXsd], [TargetXsd]);
        var map = JsonSerializer.Deserialize<XmlMappingDefinition>(conversion.MappingJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(2, map.Version);
        Assert.Equal(MissingXmlValueBehavior.Omit, map.Fields.Single(field => field.TargetPath == "Text").MissingValueBehavior);
        var engine = new XmlMappingEngine(conversion.MappingJson);
        var missing = XDocument.Parse(engine.Transform("<Order xmlns='urn:source' code='x'><Id>1</Id></Order>"));
        Assert.Null(missing.Root!.Element("Text"));
        var present = XDocument.Parse(engine.Transform("<Order xmlns='urn:source' code='x'><Id>1</Id><Optional/></Order>"));
        Assert.Equal("", present.Root!.Element("Text")!.Value);
    }

    [Theory]
    [InlineData("Order/Optional")]
    [InlineData("Order/Item")]
    [InlineData("Order")]
    [InlineData("Missing/Id")]
    public void UnsupportedSourceCardinalityOrStructure_RejectsWholeProposal(string source)
        => Assert.Throws<ArgumentException>(() => BizTalkNativeMapConverter.Convert(Map(source), [SourceXsd], [TargetXsd]));

    [Theory]
    [InlineData("<Functoids><Functoid Functoid-FID='260'/></Functoids>")]
    [InlineData("<Script>not executed</Script>")]
    [InlineData("<FutureConstruct />")]
    public void UnknownOrExecutableConstructs_AreNotSilentlyDropped(string extra)
        => Assert.Throws<ArgumentException>(() => BizTalkNativeMapConverter.Convert(Map(extra: extra), [SourceXsd], [TargetXsd]));

    [Theory]
    [InlineData("CopyPIs", "Yes")]
    [InlineData("method", "text")]
    [InlineData("CustomXsltPath", "file:///not-read")]
    [InlineData("OmitXmlDeclaration", "No")]
    public void UnsupportedMapSettings_RejectProposal(string name, string value)
    {
        var document = XDocument.Parse(Map());
        document.Root!.SetAttributeValue(name, value);
        Assert.Throws<ArgumentException>(() => BizTalkNativeMapConverter.Convert(document.ToString(), [SourceXsd], [TargetXsd]));
    }

    [Fact]
    public void DuplicateMissingAndStructuredTargets_AreNotAccepted()
    {
        Assert.Throws<ArgumentException>(() => BizTalkNativeMapConverter.Convert(Map(target: "Result"), [SourceXsd], [TargetXsd]));
        var document = XDocument.Parse(Map());
        document.Descendants("Links").Single().Add(new XElement(document.Descendants("Link").Last()));
        Assert.Throws<ArgumentException>(() => BizTalkNativeMapConverter.Convert(document.ToString(), [SourceXsd], [TargetXsd]));
        document = XDocument.Parse(Map());
        document.Descendants("Link").Last().Remove();
        Assert.Throws<ArgumentException>(() => BizTalkNativeMapConverter.Convert(document.ToString(), [SourceXsd], [TargetXsd]));
    }

    [Fact]
    public void ImportedSchemas_AreResolvedOnlyFromSuppliedText()
    {
        const string wrapper = """
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:s="urn:source">
              <xs:import namespace="urn:source" schemaLocation="https://invalid.example/not-loaded.xsd" />
              <xs:element name="Wrapper"><xs:complexType><xs:sequence><xs:element ref="s:Order" /></xs:sequence></xs:complexType></xs:element>
            </xs:schema>
            """;
        var tree = XmlSchemaInspector.Analyze([wrapper, SourceXsd]);
        Assert.Contains(tree.Roots, root => root.Fields.Any(field => field.LocalPath == "/Wrapper/Order/Id"));
        Assert.Contains("Supply the imported XSD", Assert.Throws<ArgumentException>(() => XmlSchemaInspector.Analyze([wrapper])).Message);
    }

    [Fact]
    public void RecursiveChoiceAndWildcardSchemas_AreVisibleButNotAcceptedAsNativeTargetMaps()
    {
        const string schema = """
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema">
              <xs:complexType name="Node"><xs:sequence><xs:element name="Next" type="Node" minOccurs="0" /></xs:sequence></xs:complexType>
              <xs:element name="Result"><xs:complexType><xs:choice><xs:element name="Child" type="Node" /><xs:any namespace="##other" /></xs:choice></xs:complexType></xs:element>
            </xs:schema>
            """;
        var tree = XmlSchemaInspector.Analyze([schema]);
        Assert.Contains(tree.Roots.Single().Fields, field => field.UnsupportedReason?.Contains("Recursive", StringComparison.Ordinal) == true);
        Assert.Contains(tree.Roots.Single().Fields, field => field.UnsupportedReason?.Contains("Choice", StringComparison.Ordinal) == true);
        Assert.Contains(tree.Roots.Single().Fields, field => field.UnsupportedReason?.Contains("Wildcard", StringComparison.Ordinal) == true);
        Assert.Throws<ArgumentException>(() => BizTalkNativeMapConverter.Convert(Map(), [SourceXsd], [schema]));
    }

    [Theory]
    [InlineData("<!DOCTYPE x SYSTEM 'file:///not-read'><x/>")]
    [InlineData("<broken>")]
    [InlineData("<xs:schema xmlns:xs='http://www.w3.org/2001/XMLSchema'><xs:include schemaLocation='file:///not-read'/></xs:schema>")]
    public void InvalidOrExternalSchemaInput_IsRejected(string text)
        => Assert.Throws<ArgumentException>(() => XmlSchemaInspector.Analyze([text]));

    [Theory]
    [InlineData(32)]
    [InlineData(33)]
    public void CompiledSchemaTree_DepthLimitIsExactly32Levels(int depth)
    {
        XNamespace xs = "http://www.w3.org/2001/XMLSchema";
        var schema = new XElement(xs + "schema", new XAttribute(XNamespace.Xmlns + "xs", xs),
            new XElement(xs + "element", new XAttribute("name", "Root"), new XAttribute("type", "T0")));
        for (var index = 0; index < depth - 1; index++)
            schema.Add(new XElement(xs + "complexType", new XAttribute("name", "T" + index), new XElement(xs + "sequence",
                new XElement(xs + "element", new XAttribute("name", "Child"), new XAttribute("type", index == depth - 2 ? "xs:string" : "T" + (index + 1))))));
        if (depth == 32) Assert.Equal(32, XmlSchemaInspector.Analyze([schema.ToString()]).Roots.Single().Fields.Count);
        else Assert.Contains("32-level", Assert.Throws<ArgumentException>(() => XmlSchemaInspector.Analyze([schema.ToString()])).Message);
    }

    [Fact]
    public void SchemaAndMapLimitsAndCancellation_AreEnforced()
    {
        Assert.Throws<ArgumentException>(() => XmlSchemaInspector.Analyze([]));
        Assert.Throws<ArgumentException>(() => XmlSchemaInspector.Analyze(Enumerable.Repeat(SourceXsd, 17).ToArray()));
        Assert.Throws<ArgumentException>(() => XmlSchemaInspector.Analyze([new string('x', 1_000_001)]));
        Assert.Throws<ArgumentException>(() => XmlSchemaInspector.Analyze([string.Concat(Enumerable.Repeat("<x>", 70)) + string.Concat(Enumerable.Repeat("</x>", 70))]));
        Assert.Throws<ArgumentException>(() => BizTalkNativeMapConverter.Convert(new string('x', 1_000_001), [SourceXsd], [TargetXsd]));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => XmlSchemaInspector.Analyze([SourceXsd], cancelled.Token));
        Assert.Throws<OperationCanceledException>(() => BizTalkNativeMapConverter.Convert(Map(), [SourceXsd], [TargetXsd], cancelled.Token));
    }
}
