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

    [Theory]
    [InlineData(111, "-5", "", "5")]
    [InlineData(118, "2", "3", "5")]
    [InlineData(119, "9", "4", "5")]
    [InlineData(120, "2", "3", "6")]
    [InlineData(312, "5", "5", "true")]
    [InlineData(313, "4", "5", "true")]
    [InlineData(314, "6", "5", "false")]
    [InlineData(320, "false", "true", "true")]
    [InlineData(103, "ABC", "", "abc")]
    [InlineData(108, "  x", "", "x")]
    [InlineData(109, "x  ", "", "x")]
    [InlineData(705, "false", "", "true")]
    public void BizTalkMathLogicAndTrimFunctoids_ConvertAndRun(int fid, string first, string second, string expected)
    {
        var inputs = fid is 111 or 103 or 108 or 109 or 705 ? new[] { Const(first, 0) } : [Const(first, 0), Const(second, 1)];
        var conversion = Convert(Build(Out(1, 10, "Result/Number"), F(10, fid, inputs)));
        Assert.Equal(expected, Run(conversion, "<Order xmlns='urn:source' code='x'/>").Root!.Element("Number")!.Value);
    }

    [Fact]
    public void UnverifiedFunctoids_AreListedInTheirOwnWarning()
    {
        var conversion = Convert(Build(Wire(1, "Order/Id", 10) + Out(2, 10, "Result/Number") + Wire(3, "Order/Id", 20) + Out(4, 20, "Result/Text"),
            F(10, 111, Link("1", 0)) + F(20, 108, Link("3", 0))));
        Assert.Contains(conversion.Warnings, warning => warning.StartsWith("FID 108 follow the functoid's documented behavior", StringComparison.Ordinal));
        Assert.DoesNotContain(conversion.Warnings, warning => warning.Contains("FID 111 follow", StringComparison.Ordinal));
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
    [InlineData(424)]
    [InlineData(703)]
    public void UnverifiedFunctoid_IsNamedAndNothingIsProduced(int fid)
    {
        var map = Build(Wire(1, "Order/Id", 10) + Out(2, 10, "Result/Number"), F(10, fid, Link("1", 0)));
        var message = Message(map);
        Assert.Contains($"FID {fid}", message, StringComparison.Ordinal);
        Assert.Contains("No partial native map", message, StringComparison.Ordinal);
    }

    private static string LoopMap(string links, string functoids) => Build(Direct(1, "Order/Id", "Result/Number") + links, functoids, code: false);

    private static NativeMapConversion ConvertRepeating(string map) => BizTalkNativeMapConverter.Convert(map, [RepeatSource], [RepeatTarget]);

    private const string TwoLines = "<Order xmlns='urn:source'><Id>7</Id><Line><Qty>a</Qty></Line><Line><Qty>b</Qty></Line></Order>";

    [Fact]
    public void Iteration_BecomesRecordPositionInsideALinkedRecord()
    {
        var conversion = ConvertRepeating(LoopMap(
            Direct(2, "Order/Line", "Result/Row") + Wire(3, "Order/Line", 10) + Out(4, 10, "Result/Row/Label"), F(10, 474, Link("3", 0))));
        var definition = JsonSerializer.Deserialize<XmlMappingDefinition>(conversion.MappingJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var field = Assert.Single(definition.Fields, candidate => candidate.TargetPath == "Row/Label");
        Assert.Equal(XmlMappingFunction.RecordPosition, field.Expression!.Function);
        Assert.Equal("/sn1:Order/sn1:Line", field.Expression.Arguments.Single().SourcePath);
        Assert.Contains(conversion.Warnings, warning => warning.Contains("RecordPosition", StringComparison.Ordinal));
        Assert.Equal(["1", "2"], Run(conversion, TwoLines).Root!.Elements("Row").Select(row => row.Element("Label")!.Value));
    }

    [Fact]
    public void Iteration_FeedingAFormulaWithOtherFields_Works()
    {
        var conversion = ConvertRepeating(LoopMap(
            Direct(2, "Order/Line", "Result/Row") + Wire(3, "Order/Line", 10) + L(4, "10", "11") + Wire(5, "Order/Line/Qty", 11) + Out(6, 11, "Result/Row/Label"),
            F(10, 474, Link("3", 0)) + F(11, 107, Link("4", 0), Const(":", 1), Link("5", 2))));
        Assert.Equal(["1:a", "2:b"], Run(conversion, TwoLines).Root!.Elements("Row").Select(row => row.Element("Label")!.Value));
    }

    [Fact]
    public void Iteration_OutsideARecordLinkOfItsRecord_IsRejected()
    {
        var message = Assert.Throws<ArgumentException>(() => ConvertRepeating(LoopMap(
            Direct(2, "Order/Line", "Result/Row") + Wire(3, "Order/Line", 10) + Out(4, 10, "Result/Number") + Direct(5, "Order/Line/Qty", "Result/Row/Label"),
            F(10, 474, Link("3", 0))).Replace(Direct(1, "Order/Id", "Result/Number"), string.Empty))).Message;
        Assert.Contains("not inside a record link", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Iteration_OfANonRepeatingSourceOrWithoutARecord_IsRejected()
    {
        Assert.Contains("repeating source record", Assert.Throws<ArgumentException>(() => ConvertRepeating(LoopMap(
            Wire(3, "Order/Id", 10) + Out(4, 10, "Result/Number"), F(10, 474, Link("3", 0))).Replace(Direct(1, "Order/Id", "Result/Number"), string.Empty))).Message);
        Assert.Contains("exactly one source record input", Assert.Throws<ArgumentException>(() => ConvertRepeating(LoopMap(
            Out(4, 10, "Result/Number"), F(10, 474, Const("1", 0))).Replace(Direct(1, "Order/Id", "Result/Number"), string.Empty))).Message);
    }

    [Fact]
    public void Looping_WithOneRecordInput_IsAPlainRecordLink()
    {
        var looping = ConvertRepeating(LoopMap(
            Wire(2, "Order/Line", 10) + Out(3, 10, "Result/Row") + Direct(4, "Order/Line/Qty", "Result/Row/Label"), F(10, 424, Link("2", 0))));
        var direct = ConvertRepeating(LoopMap(Direct(2, "Order/Line", "Result/Row") + Direct(4, "Order/Line/Qty", "Result/Row/Label"), string.Empty));
        Assert.Equal(direct.Xslt, looping.Xslt);
        Assert.Contains(looping.Warnings, warning => warning.Contains("Looping functoid(s) (FID 424)", StringComparison.Ordinal));
        Assert.DoesNotContain(looping.Warnings, warning => warning.Contains("not connected", StringComparison.Ordinal));
        Assert.Equal(["a", "b"], Run(looping, TwoLines).Root!.Elements("Row").Select(row => row.Element("Label")!.Value));
    }

    [Fact]
    public void Looping_WithSeveralInputsOrAScalarInput_FailsClosed()
    {
        Assert.Contains("merges 2 source records", Assert.Throws<ArgumentException>(() => ConvertRepeating(LoopMap(
            Wire(2, "Order/Line", 10) + Wire(5, "Order/Line", 10) + Out(3, 10, "Result/Row") + Direct(4, "Order/Line/Qty", "Result/Row/Label"),
            F(10, 424, Link("2", 0), Link("5", 1))))).Message);
        Assert.Contains("scalar", Assert.Throws<ArgumentException>(() => ConvertRepeating(LoopMap(
            Wire(2, "Order/Id", 10) + Out(3, 10, "Result/Row") + Direct(4, "Order/Line/Qty", "Result/Row/Label"), F(10, 424, Link("2", 0))))).Message);
    }

    private static string ValueMappingMap(int fid, string conditionConstant = "42")
        => Build(L(1, "20", "10") + Wire(2, "Order/Id", 10) + Wire(3, "Order/Id", 20) + Out(4, 10, "Result/Number"),
            F(10, fid, Link("1", 0), Link("2", 1)) + F(20, 315, Link("3", 0), Const(conditionConstant, 1)));

    [Theory]
    [InlineData(375)]
    [InlineData(374)]
    public void ValueMapping_BecomesAConditionalTarget(int fid)
    {
        var conversion = Convert(ValueMappingMap(fid));
        var definition = JsonSerializer.Deserialize<XmlMappingDefinition>(conversion.MappingJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var field = Assert.Single(definition.Fields, candidate => candidate.TargetPath == "Number");
        Assert.Equal(XmlMappingFunction.LogicalEq, field.Condition!.Function);
        Assert.Equal("/sn1:Order/sn1:Id", field.Expression!.SourcePath);
        Assert.Contains(conversion.Warnings, warning => warning.Contains($"Value Mapping functoid(s) (FID {fid})", StringComparison.Ordinal));
        Assert.Equal("42", Run(conversion, "<Order xmlns='urn:source' code='x'><Id>42</Id></Order>").Root!.Element("Number")!.Value);
        Assert.Null(Run(conversion, "<Order xmlns='urn:source' code='x'><Id>7</Id></Order>").Root!.Element("Number"));
    }

    [Fact]
    public void ValueMappingConditionFromAField_MustBeExactlyTrue()
    {
        var map = Build(Wire(1, "Order/Optional", 10) + Wire(2, "Order/Id", 10) + Out(3, 10, "Result/Number"),
            F(10, 375, Link("1", 0), Link("2", 1)));
        var conversion = Convert(map);
        Assert.Equal("5", Run(conversion, "<Order xmlns='urn:source' code='x'><Id>5</Id><Optional>true</Optional></Order>").Root!.Element("Number")!.Value);
        Assert.Null(Run(conversion, "<Order xmlns='urn:source' code='x'><Id>5</Id><Optional>yes</Optional></Order>").Root!.Element("Number"));
        Assert.Null(Run(conversion, "<Order xmlns='urn:source' code='x'><Id>5</Id></Order>").Root!.Element("Number"));
    }

    [Fact]
    public void ValueMappingFeedingAnotherFunctoid_IsRejected()
    {
        var map = Build(L(1, "20", "10") + Wire(2, "Order/Id", 10) + Wire(3, "Order/Id", 20) + L(5, "10", "30") + Out(6, 30, "Result/Number"),
            F(10, 375, Link("1", 0), Link("2", 1)) + F(20, 315, Link("3", 0), Const("1", 1)) + F(30, 105, Link("5", 0)));
        var message = Message(map);
        Assert.Contains("Value Mapping functoid 10", message, StringComparison.Ordinal);
        Assert.Contains("No partial native map", message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValueMappingWithOneInput_IsRejected()
        => Assert.Contains("FID 375", Message(Build(Wire(1, "Order/Id", 10) + Out(2, 10, "Result/Number"), F(10, 375, Link("1", 0)))), StringComparison.Ordinal);

    private static string Script(int id, string code, params string[] inputs)
        => $"<Functoid FunctoidID=\"{id}\" Functoid-FID=\"260\" X-Cell=\"1\" Y-Cell=\"1\"><Input-Parameters>{string.Concat(inputs)}</Input-Parameters><ScripterCode><Script Language=\"CSharp\"><![CDATA[{code}]]></Script></ScripterCode></Functoid>";

    private const string FormatDateCode = "// formats a date\r\npublic string FormatDate(string raw, string pattern) { switch (raw) { case \"I\": return \"In\"; case \"O\": return \"Out\"; } return DateTime.Parse(raw).ToString(\"yyyy-MM-dd\"); }";

    private static XmlMappingDefinition Definition(NativeMapConversion conversion)
        => JsonSerializer.Deserialize<XmlMappingDefinition>(conversion.MappingJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    private static NativeMapConversion ConvertFormatDate()
        => Convert(Build(Wire(1, "Order/Id", 10) + Out(3, 10, "Result/Number"),
            Script(10, FormatDateCode, Link("1", 0), Const("yyyy", 1))));

    [Fact]
    public void ConnectedScriptFunctoid_BecomesADocumentedStubAndACustomCall()
    {
        var conversion = ConvertFormatDate();
        var definition = Definition(conversion);
        var call = definition.Fields.Single(field => field.TargetPath == "Number").Expression!;
        Assert.Equal(XmlValueExpressionKind.Custom, call.Kind);
        Assert.Equal("FormatDate", call.Name);
        Assert.Equal(2, call.Arguments.Count);
        Assert.Equal("/sn1:Order/sn1:Id", call.Arguments[0].SourcePath);
        Assert.Equal("yyyy", call.Arguments[1].Constant);
        Assert.Equal(["FormatDate"], conversion.PendingFunctions);
        var function = Assert.Single(definition.CustomFunctions);
        Assert.Null(function.Body);
        Assert.Equal(["raw", "pattern"], function.Parameters.Select(parameter => parameter.Name));
        Assert.Contains("Original signature", function.Description, StringComparison.Ordinal);
        Assert.Contains("string FormatDate", function.Description, StringComparison.Ordinal);
        Assert.Contains("yyyy-MM-dd", function.Description, StringComparison.Ordinal);
        Assert.Contains("\"I\"", function.Description, StringComparison.Ordinal);
        Assert.Contains("feeds Number", Assert.Single(function.Usages), StringComparison.Ordinal);
        Assert.Contains("public string FormatDate", function.OriginalSource, StringComparison.Ordinal);
        Assert.Equal("CSharp", function.OriginalSourceLanguage);
        Assert.Contains(conversion.Warnings, warning => warning.Contains("unimplemented stubs", StringComparison.Ordinal));
    }

    [Fact]
    public void StubbedMap_FailsClosedUntilTheStubHasAFormula()
    {
        var conversion = ConvertFormatDate();
        var error = Assert.Throws<ArgumentException>(() => new XmlMappingEngine(conversion.MappingJson));
        Assert.Contains("unimplemented stub", error.Message, StringComparison.Ordinal);
        Assert.Contains("FormatDate", error.Message, StringComparison.Ordinal);

        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var json = System.Text.Json.Nodes.JsonNode.Parse(conversion.MappingJson)!;
        var body = new XmlValueExpression
        {
            Kind = XmlValueExpressionKind.Function,
            Function = XmlMappingFunction.StringUpperCase,
            Arguments = [new XmlValueExpression { Kind = XmlValueExpressionKind.Parameter, Name = "raw" }]
        };
        json["customFunctions"]![0]!["body"] = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(body, options));
        var implemented = new XmlMappingEngine(json.ToJsonString());
        var output = XDocument.Parse(implemented.Transform("<Order xmlns='urn:source' code='x'><Id>abc</Id></Order>")).Root!;
        Assert.Equal("ABC", output.Element("Number")!.Value);
    }

    [Fact]
    public void SameScriptUsedTwice_SharesOneStubWithBothUsages()
    {
        var conversion = Convert(Build(
            Wire(1, "Order/Id", 10) + Out(2, 10, "Result/Number") + Wire(3, "Order/Optional", 11) + Out(4, 11, "Result/Text"),
            Script(10, FormatDateCode, Link("1", 0), Const("a", 1)) + Script(11, FormatDateCode, Link("3", 0), Const("b", 1))));
        var function = Assert.Single(Definition(conversion).CustomFunctions);
        Assert.Equal(2, function.Usages.Count);
        Assert.Equal(["FormatDate"], conversion.PendingFunctions);
    }

    [Fact]
    public void DifferentScriptsWithTheSameName_AreSuffixed()
    {
        var other = "public string FormatDate(string raw, string pattern) { return raw + pattern; }";
        var conversion = Convert(Build(
            Wire(1, "Order/Id", 10) + Out(2, 10, "Result/Number") + Wire(3, "Order/Optional", 11) + Out(4, 11, "Result/Text"),
            Script(10, FormatDateCode, Link("1", 0), Const("a", 1)) + Script(11, other, Link("3", 0), Const("b", 1))));
        Assert.Equal(["FormatDate", "FormatDate_2"], Definition(conversion).CustomFunctions.Select(function => function.Name).Order());
    }

    [Fact]
    public void UnreadableScriptSignature_FallsBackToGenericNamesAndStillImports()
    {
        var conversion = Convert(Build(Wire(1, "Order/Id", 10) + Out(2, 10, "Result/Number"), Script(10, "this is not C#", Link("1", 0))));
        var function = Assert.Single(Definition(conversion).CustomFunctions);
        Assert.Equal("Script_10", function.Name);
        Assert.Equal(["param1"], function.Parameters.Select(parameter => parameter.Name));
        Assert.Contains("could not be read", function.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void ScriptNamedLikeABuiltInFunction_GetsASafeName()
    {
        var conversion = Convert(Build(Wire(1, "Order/Id", 10) + Out(2, 10, "Result/Number"),
            Script(10, "public string StringConcat(string a) { return a; }", Link("1", 0))));
        Assert.Equal("Script_10", Assert.Single(Definition(conversion).CustomFunctions).Name);
    }

    [Fact]
    public void HugeScript_IsTruncatedInTheStubNotRejected()
    {
        var code = "public string Big(string a) { return \"" + new string('x', 40000) + "\"; }";
        var function = Assert.Single(Definition(Convert(Build(Wire(1, "Order/Id", 10) + Out(2, 10, "Result/Number"), Script(10, code, Link("1", 0))))).CustomFunctions);
        Assert.Contains("truncated", function.OriginalSource, StringComparison.Ordinal);
        Assert.True(function.OriginalSource!.Length <= XmlMappingFunctionCatalog.MaxOriginalSourceLength);
    }

    private static string Scripts(int id, string[] inputs, params (string Language, string Code)[] scripts)
        => $"<Functoid FunctoidID=\"{id}\" Functoid-FID=\"260\" X-Cell=\"1\" Y-Cell=\"1\"><Input-Parameters>{string.Concat(inputs)}</Input-Parameters><ScripterCode>"
            + string.Concat(scripts.Select(script => $"<Script Language=\"{script.Language}\"><![CDATA[{script.Code}]]></Script>")) + "</ScripterCode></Functoid>";

    private const string SettingFragment = "<Number><xsl:value-of select=\"/*/ConfigurationInfo/AdditionalInfo/Setting[@Name='Facility']/@Value\"/></Number>";
    private const string SampleScript = "<!--Uncomment the following lines for a sample Xslt that creates a record and a child field attribute.-->\r\n<AdditionalInfo><Setting Name=\"A\" Value=\"1\" /></AdditionalInfo>";
    private const string RowTemplate = "<xsl:template name=\"BuildRow\"><xsl:param name=\"startDate\"/><xsl:for-each select=\"//Line\"><Row><Label><xsl:value-of select=\"position()\"/></Label></Row></xsl:for-each></xsl:template>";

    [Fact]
    public void ConnectedInlineXsltScript_BecomesADocumentedStubThatFailsClosed()
    {
        var conversion = Convert(Build(Out(2, 10, "Result/Number"), Scripts(10, [], ("Xslt", SettingFragment))));
        var function = Assert.Single(Definition(conversion).CustomFunctions);
        Assert.Equal("Xslt_10", function.Name);
        Assert.Equal("Xslt", function.OriginalSourceLanguage);
        Assert.Null(function.Body);
        Assert.Contains("never run", function.Description, StringComparison.Ordinal);
        Assert.Contains("Facility", function.Description, StringComparison.Ordinal);
        Assert.Contains("Number", function.Description, StringComparison.Ordinal);
        Assert.Contains("Setting[@Name='Facility']", function.OriginalSource, StringComparison.Ordinal);
        Assert.Equal(["Xslt_10"], conversion.PendingFunctions);
        Assert.Contains("unimplemented stub", Assert.Throws<ArgumentException>(() => new XmlMappingEngine(conversion.MappingJson)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void XsltCallTemplate_UsesTheTemplateAndParameterNamesAndDocumentsInputs()
    {
        var conversion = Convert(Build(Wire(1, "Order/Id", 10) + Out(2, 10, "Result/Number"),
            Scripts(10, [Link("1", 0)], ("XsltCallTemplate", RowTemplate))));
        var function = Assert.Single(Definition(conversion).CustomFunctions);
        Assert.Equal("Template_BuildRow", function.Name);
        Assert.Equal("XsltCallTemplate", function.OriginalSourceLanguage);
        Assert.Equal("startDate", Assert.Single(function.Parameters).Name);
        Assert.Contains("Named template: BuildRow", function.Description, StringComparison.Ordinal);
        Assert.Matches(@"first use: source \S*Id", function.Description);
        Assert.Contains("position of the current record", function.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void XsltBuildingARecord_IsAStubWithTheLoopingInputsDocumented_AndTheMapFailsClosed()
    {
        var conversion = ConvertRepeating(LoopMap(
            Wire(2, "Order/Id", 10) + Out(3, 10, "Result/Row") + Wire(4, "Order/Line", 11) + Out(5, 11, "Result/Row"),
            Scripts(10, [Link("2", 0)], ("XsltCallTemplate", RowTemplate)) + F(11, 424, Link("4", 0))));
        var definition = Definition(conversion);
        var function = Assert.Single(definition.CustomFunctions);
        Assert.Equal("Template_BuildRow", function.Name);
        Assert.Contains("builds a whole target record", function.Description, StringComparison.Ordinal);
        Assert.Contains("Looping functoid 11", function.Description, StringComparison.Ordinal);
        Assert.Matches(@"Looping functoid 11 creates one record for each node of its inputs: source record \S*Line", function.Description);
        Assert.Contains("feeds", Assert.Single(function.Usages), StringComparison.Ordinal);
        Assert.DoesNotContain(definition.Fields, field => field.TargetPath?.StartsWith("Row", StringComparison.Ordinal) == true);
        Assert.Empty(definition.Repeats);
        Assert.Contains(conversion.Warnings, warning => warning.Contains("were NOT converted", StringComparison.Ordinal));
        Assert.DoesNotContain(conversion.Warnings, warning => warning.Contains("not connected", StringComparison.Ordinal));
        Assert.Contains("unimplemented stub", Assert.Throws<ArgumentException>(() => new XmlMappingEngine(conversion.MappingJson)).Message, StringComparison.Ordinal);
        var preview = XDocument.Parse(new XmlMappingEngine(conversion.MappingJson, allowStubs: true).Transform(TwoLines)).Root!;
        Assert.Equal("7", preview.Element("Number")!.Value);
        Assert.Empty(preview.Elements("Row"));
    }

    [Fact]
    public void UnconnectedXslt_IsIgnoredWithAWarning_ButUnsupportedScriptLanguagesAreRejected()
    {
        var conversion = Convert(Build(Direct(1, "Order/Id", "Result/Number"), Scripts(31, [], ("Xslt", SettingFragment))));
        Assert.Contains(conversion.Warnings, warning => warning.Contains("not connected", StringComparison.Ordinal));
        Assert.Empty(Definition(conversion).CustomFunctions);
        Assert.Contains("VbNet", Message(Build(Direct(1, "Order/Id", "Result/Number"), Scripts(31, [], ("VbNet", "Public Function X() As String")))), StringComparison.Ordinal);
    }

    [Fact]
    public void SeveralScriptLanguages_DropTheDefaultTemplateAndPickByPrecedence()
    {
        var onlyOneReal = Convert(Build(Out(2, 10, "Result/Number"), Scripts(10, [], ("Xslt", SampleScript), ("CSharp", "public string Fixed() { return \"DFT\"; }"))));
        var single = Assert.Single(Definition(onlyOneReal).CustomFunctions);
        Assert.Equal("Fixed", single.Name);
        Assert.Equal("CSharp", single.OriginalSourceLanguage);
        Assert.DoesNotContain("Uncomment", single.OriginalSource, StringComparison.Ordinal);
        var both = Convert(Build(Out(2, 10, "Result/Number"), Scripts(10, [], ("Xslt", SettingFragment), ("CSharp", "public string Fixed() { return \"DFT\"; }"))));
        var chosen = Assert.Single(Definition(both).CustomFunctions);
        Assert.Equal("CSharp", chosen.OriginalSourceLanguage);
        Assert.Contains("alternate Xslt script (not chosen)", chosen.OriginalSource, StringComparison.Ordinal);
        Assert.Contains("does not name the active one", chosen.Description, StringComparison.Ordinal);
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
    public void UnconnectedUnsupportedFunctoid_DoesNotAffectOutput()
    {
        var conversion = Convert(Build(Direct(1, "Order/Id", "Result/Number"), F(30, 374, Const("a", 0))));
        Assert.Contains(conversion.Warnings, warning => warning.Contains("not connected", StringComparison.Ordinal));
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
        Assert.Contains("conditional creation of the whole message", Message(Build(Out(1, 10, "Result"), F(10, 123), code: false)), StringComparison.Ordinal);
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
