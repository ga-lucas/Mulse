using System.Security;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Mulse.CompatibilityPack;
using Mulse.Modules.Mapping;
using Service.Mapping;

namespace Service.Tests;

public sealed class NativeMapFunctoidImportTests
{
    private const string RepeatSource = """
        <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:source" elementFormDefault="qualified">
          <xs:element name="Order"><xs:complexType><xs:sequence>
            <xs:element name="Id" type="xs:string" />
            <xs:element name="Line" minOccurs="0" maxOccurs="unbounded"><xs:complexType><xs:sequence>
              <xs:element name="Qty" type="xs:string" />
            </xs:sequence></xs:complexType></xs:element>
          </xs:sequence></xs:complexType></xs:element>
        </xs:schema>
        """;
    private const string RepeatTarget = """
        <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:target">
          <xs:element name="Result"><xs:complexType><xs:sequence>
            <xs:element name="Number" type="xs:string" />
            <xs:element name="Row" minOccurs="0" maxOccurs="unbounded"><xs:complexType><xs:sequence>
              <xs:element name="Label" type="xs:string" />
            </xs:sequence></xs:complexType></xs:element>
          </xs:sequence></xs:complexType></xs:element>
        </xs:schema>
        """;

    private static string P(string type, string value, int? index) => $"<Parameter Type=\"{type}\" Value=\"{SecurityElement.Escape(value)}\" Guid=\"g\"{(index is null ? "" : $" linkIndex=\"{index}\"")} />";
    private static string Link(string value, int index) => P("link", value, index);
    private static string Const(string value, int index) => P("constant", value, index);
    private static string F(int id, int fid, params string[] parameters)
        => $"<Functoid FunctoidID=\"{id}\" Functoid-FID=\"{fid}\" X-Cell=\"1\" Y-Cell=\"1\"><Input-Parameters>{string.Concat(parameters)}</Input-Parameters></Functoid>";
    private static string L(int id, string from, string to)
        => $"<Link LinkID=\"{id}\" LinkFrom=\"{SecurityElement.Escape(from)}\" LinkTo=\"{SecurityElement.Escape(to)}\" />";
    private static string Wire(int id, string schemaPath, int functoid) => L(id, NativeMapImportTests.Path(schemaPath), functoid.ToString());
    private static string Out(int id, int functoid, string schemaPath) => L(id, functoid.ToString(), NativeMapImportTests.Path(schemaPath));
    private static string Direct(int id, string from, string to) => L(id, NativeMapImportTests.Path(from), NativeMapImportTests.Path(to));

    private static string Build(string links, string functoids, string sourceRoot = "Order", string targetRoot = "Result", bool code = true)
        => $"<mapsource><SrcTree RootNode_Name=\"{sourceRoot}\"/><TrgTree RootNode_Name=\"{targetRoot}\"/><Pages><Page Name=\"p\"><Links>"
            + (code ? Direct(900, "Order/@code", "Result/@code") : "") + $"{links}</Links><Functoids>{functoids}</Functoids></Page></Pages></mapsource>";

    private static NativeMapConversion Convert(string map) => BizTalkNativeMapConverter.Convert(map, [NativeMapImportTests.SourceXsd], [NativeMapImportTests.TargetXsd]);

    private static XDocument Run(NativeMapConversion conversion, string xml) => XDocument.Parse(new XmlMappingEngine(conversion.MappingJson).Transform(xml));

    private static string Message(string map) => Assert.Throws<ArgumentException>(() => Convert(map)).Message;

    [Fact]
    public void ConcatAndExistence_ConvertToEditableExpressionsAndRunNatively()
    {
        var map = Build(
            Wire(1, "Order/Id", 10) + Wire(2, "Order/Optional", 10) + Out(3, 10, "Result/Number")
            + Wire(4, "Order/Optional", 20) + Out(5, 20, "Result/Text"),
            F(10, 107, Link("1", 0), Const("-", 1), Link("2", 2)) + F(20, 701, Link("4", 0)));
        var conversion = Convert(map);
        var definition = JsonSerializer.Deserialize<XmlMappingDefinition>(conversion.MappingJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var number = definition.Fields.Single(field => field.TargetPath == "Number").Expression!;
        Assert.Equal(XmlMappingFunction.StringConcat, number.Function);
        Assert.Equal(["/sn1:Order/sn1:Id", null, "/sn1:Order/sn1:Optional"], number.Arguments.Select(argument => argument.SourcePath));
        Assert.Equal("-", number.Arguments[1].Constant);
        Assert.Contains(conversion.Warnings, warning => warning.Contains("FID 107, 701", StringComparison.Ordinal));
        var absent = Run(conversion, "<Order xmlns='urn:source' code='x'><Id>42</Id></Order>").Root!;
        Assert.Equal("42-", absent.Element("Number")!.Value);
        Assert.Equal("false", absent.Element("Text")!.Value);
        var present = Run(conversion, "<Order xmlns='urn:source' code='x'><Id>42</Id><Optional>z</Optional></Order>").Root!;
        Assert.Equal("42-z", present.Element("Number")!.Value);
        Assert.Equal("true", present.Element("Text")!.Value);
        Assert.Equal(new XmlMappingEngine(conversion.MappingJson).Xslt, conversion.Xslt);
    }

    [Fact]
    public void NestedFunctoidsOrderInputsByLinkIndexNotDocumentOrder()
    {
        var map = Build(
            L(2, "11", "10") + Wire(3, "Order/Id", 11) + Out(4, 10, "Result/Number"),
            F(10, 107, Link("2", 1), Const(">", 0)) + F(11, 110, Link("3", 0)));
        var number = Run(Convert(map), "<Order xmlns='urn:source' code='x'><Id>ab</Id></Order>").Root!.Element("Number")!.Value;
        Assert.Equal(">AB", number);
    }

    [Fact]
    public void OneFunctoidFeedingSeveralTargets_AndNoInputFunctoid_Work()
    {
        var map = Build(
            Out(1, 20, "Result/Number") + Out(2, 20, "Result/Text"),
            F(20, 123));
        var conversion = Convert(map);
        var result = Run(conversion, "<Order xmlns='urn:source' code='x'/>").Root!;
        Assert.Matches(new Regex(@"^\d{4}-\d{2}-\d{2}$", RegexOptions.None, TimeSpan.FromSeconds(1)), result.Element("Number")!.Value);
        Assert.Equal(result.Element("Number")!.Value, result.Element("Text")!.Value);
    }

    [Fact]
    public void FunctoidInsideRepeat_ReadsRecordFieldsAndOutsideFieldsPerRow()
    {
        var map = Build(
            Direct(1, "Order/Line", "Result/Row") + Wire(2, "Order/Line/Qty", 10) + Wire(3, "Order/Id", 10) + Out(4, 10, "Result/Row/Label")
            + Direct(5, "Order/Id", "Result/Number"),
            F(10, 107, Link("2", 0), Const("x", 1), Link("3", 2)), code: false);
        var conversion = BizTalkNativeMapConverter.Convert(map, [RepeatSource], [RepeatTarget]);
        var rows = Run(conversion, "<Order xmlns='urn:source'><Id>7</Id><Line><Qty>1</Qty></Line><Line><Qty>2</Qty></Line></Order>").Root!.Elements("Row");
        Assert.Equal(["1x7", "2x7"], rows.Select(row => row.Element("Label")!.Value));
    }

    [Fact]
    public void FunctoidReadingRepeatingRecordFieldOutsideItsScope_IsRejected()
    {
        var map = Build(Wire(1, "Order/Line/Qty", 10) + Out(2, 10, "Result/Number"), F(10, 105, Link("1", 0)), code: false);
        Assert.Throws<ArgumentException>(() => BizTalkNativeMapConverter.Convert(map, [RepeatSource], [RepeatTarget]));
    }

    [Theory]
    [InlineData(374)]
    [InlineData(424)]
    [InlineData(474)]
    public void UnverifiedFunctoid_IsNamedAndNothingIsProduced(int fid)
    {
        var map = Build(Wire(1, "Order/Id", 10) + Out(2, 10, "Result/Number"), F(10, fid, Link("1", 0)));
        var message = Message(map);
        Assert.Contains($"FID {fid}", message, StringComparison.Ordinal);
        Assert.Contains("No partial native map", message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConnectedScriptFunctoid_IsNeverTranslated_AndNamesTheMethod()
    {
        var script = "<Functoid FunctoidID=\"10\" Functoid-FID=\"260\" X-Cell=\"1\" Y-Cell=\"1\"><Input-Parameters>" + Link("1", 0)
            + "</Input-Parameters><ScripterCode><Script Language=\"CSharp\"><![CDATA[public string FormatDate(string a) { return a; }]]></Script></ScripterCode></Functoid>";
        var message = Message(Build(Wire(1, "Order/Id", 10) + Out(2, 10, "Result/Number"), script));
        Assert.Contains("FormatDate", message, StringComparison.Ordinal);
        Assert.Contains("never auto-translated", message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnconnectedSampleScriptStub_IsIgnoredWithAWarning()
    {
        var stub = "<Functoid FunctoidID=\"30\" Functoid-FID=\"260\" X-Cell=\"1\" Y-Cell=\"1\"><Input-Parameters /><ScripterCode><Script Language=\"CSharp\"><![CDATA[//sample]]></Script></ScripterCode></Functoid>";
        var conversion = Convert(Build(Direct(1, "Order/Id", "Result/Number"), stub));
        Assert.Contains(conversion.Warnings, warning => warning.Contains("not connected", StringComparison.Ordinal));
        Assert.DoesNotContain(conversion.Warnings, warning => warning.Contains("formulas were converted", StringComparison.Ordinal));
    }

    [Fact]
    public void UnconnectedUnsupportedFunctoid_DoesNotAffectOutput_ButInlineXsltAlwaysRejects()
    {
        var conversion = Convert(Build(Direct(1, "Order/Id", "Result/Number"), F(30, 374, Const("a", 0))));
        Assert.Contains(conversion.Warnings, warning => warning.Contains("not connected", StringComparison.Ordinal));
        var xslt = "<Functoid FunctoidID=\"31\" Functoid-FID=\"260\" X-Cell=\"1\" Y-Cell=\"1\"><Input-Parameters /><ScripterCode><Script Language=\"Xslt\"><![CDATA[<x/>]]></Script></ScripterCode></Functoid>";
        Assert.Contains("inline XSLT", Message(Build(Direct(1, "Order/Id", "Result/Number"), xslt)), StringComparison.Ordinal);
    }

    [Fact]
    public void FunctoidCycle_IsRejected()
    {
        var map = Build(L(1, "11", "10") + L(2, "10", "11") + Out(3, 10, "Result/Number"),
            F(10, 110, Link("1", 0)) + F(11, 110, Link("2", 0)));
        Assert.Contains("cycle", Message(map), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(105, 2)]
    [InlineData(102, 1)]
    [InlineData(123, 1)]
    public void WrongInputCount_IsRejectedWithTheFunctoid(int fid, int inputs)
    {
        var parameters = Enumerable.Range(0, inputs).Select(index => Const("1", index)).ToArray();
        Assert.Contains($"FID {fid}", Message(Build(Out(1, 10, "Result/Number"), F(10, fid, parameters))), StringComparison.Ordinal);
    }

    [Fact]
    public void FunctoidToRecord_AndRecordOrRepeatingInputs_AreRejected()
    {
        Assert.Contains("conditional record creation", Message(Build(Out(1, 10, "Result"), F(10, 123), code: false)), StringComparison.Ordinal);
        Assert.Contains("record", Message(Build(Wire(1, "Order", 10) + Out(2, 10, "Result/Number"), F(10, 701, Link("1", 0)))), StringComparison.Ordinal);
        Assert.Contains("repeating", Message(Build(Wire(1, "Order/Item", 10) + Out(2, 10, "Result/Number"), F(10, 105, Link("1", 0)))), StringComparison.Ordinal);
    }

    [Fact]
    public void InconsistentWiring_IsRejected()
    {
        // unknown functoid, link not wired to this functoid, unaccounted link, missing linkIndex
        Assert.Contains("unknown functoid", Message(Build(Wire(1, "Order/Id", 99) + Direct(2, "Order/Id", "Result/Number"), "")), StringComparison.Ordinal);
        Assert.Contains("not wired", Message(Build(Wire(1, "Order/Id", 11) + Out(2, 10, "Result/Number"), F(10, 105, Link("1", 0)) + F(11, 105, Link("1", 0)))), StringComparison.Ordinal);
        Assert.Contains("account", Message(Build(Wire(1, "Order/Id", 10) + Wire(2, "Order/Optional", 10) + Out(3, 10, "Result/Number"), F(10, 105, Link("1", 0)))), StringComparison.Ordinal);
        Assert.Contains("input positions", Message(Build(Wire(1, "Order/Id", 10) + Out(2, 10, "Result/Number"), F(10, 105, P("link", "1", null)))), StringComparison.Ordinal);
    }

    [Fact]
    public void DuplicateFunctoidOrLinkIds_AreRejected()
    {
        Assert.Contains("more than once", Message(Build(Out(1, 10, "Result/Number"), F(10, 123) + F(10, 123))), StringComparison.Ordinal);
        Assert.Contains("more than once", Message(Build(Out(1, 10, "Result/Number") + Out(1, 10, "Result/Text"), F(10, 123))), StringComparison.Ordinal);
    }

    [Fact]
    public void ExponentialSharedFunctoidChain_IsStoppedByTheNodeLimit()
    {
        var functoids = new List<string> { F(1, 123) };
        var links = new List<string>();
        for (var id = 2; id <= 14; id++)
        {
            links.Add(L(id * 10, (id - 1).ToString(), id.ToString()));
            links.Add(L(id * 10 + 1, (id - 1).ToString(), id.ToString()));
            functoids.Add(F(id, 107, Link((id * 10).ToString(), 0), Link((id * 10 + 1).ToString(), 1)));
        }
        links.Add(Out(500, 14, "Result/Number"));
        Assert.Contains("larger than the native limit", Message(Build(string.Concat(links), string.Concat(functoids))), StringComparison.Ordinal);
    }
}
