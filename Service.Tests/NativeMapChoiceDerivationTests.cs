using System.Text.Json;
using System.Xml.Linq;
using Mulse.CompatibilityPack;
using Mulse.Modules.Mapping;
using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mulse.Ui.Components.Shared;
using Mulse.Ui.Models;
using Service.BizTalkImport;
using Service.Models;

namespace Service.Tests;

public sealed class NativeMapChoiceDerivationTests : IDisposable
{
    private const string Assembly = "Acme.Partner";
    private const string Good = "Acme.Partner.Maps.OrderToResult, Acme.Partner, Version=1.0.0.0, Culture=neutral, PublicKeyToken=0123456789abcdef";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mulse-mapchoice-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void PolicyValues_AreMatchedByTypeAndAssemblyAndConvertedWithoutEmbeddedSchemas()
    {
        var project = WriteProject();
        var literals = Literals(Good, "Acme.Partner.Maps.OrderToResult,Acme.Partner", "Acme.Partner.Maps.OrderToResult, Other.Assembly, Version=1.0.0.0",
            "Acme.Partner.Maps.Missing, Acme.Partner", "Acme.Partner.Maps.Broken, Acme.Partner");

        var result = Derive(project, literals);
        var byValue = result.Report.ToDictionary(item => item.Value);

        Assert.Equal("Converted", byValue[Good].Status);
        Assert.Equal("Converted", byValue["Acme.Partner.Maps.OrderToResult,Acme.Partner"].Status);
        Assert.Equal("NoMatchingMap", byValue["Acme.Partner.Maps.OrderToResult, Other.Assembly, Version=1.0.0.0"].Status);
        Assert.Contains("Acme.Partner", byValue["Acme.Partner.Maps.OrderToResult, Other.Assembly, Version=1.0.0.0"].Detail);
        Assert.Equal("NoMatchingMap", byValue["Acme.Partner.Maps.Missing, Acme.Partner"].Status);
        Assert.Equal("ConversionFailed", byValue["Acme.Partner.Maps.Broken, Acme.Partner"].Status);
        Assert.Contains(byValue[Good].Warnings, warning => warning.Contains("Review proposal only", StringComparison.Ordinal));

        var cases = RenderDispatchDefinition.Read(result.CasesJson);
        Assert.Equal(new[] { Good, "Acme.Partner.Maps.OrderToResult,Acme.Partner" }.Order(StringComparer.Ordinal), cases.Select(entry => entry.Value).Order(StringComparer.Ordinal));
        var entry = cases.Single(candidate => candidate.Value == Good);
        Assert.Equal("xml-mapping-render", entry.Module);
        var definition = JsonSerializer.Deserialize<XmlMappingDefinition>(entry.Settings["mappingJson"], new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Empty(definition.SourceSchemas);
        Assert.Empty(definition.TargetSchemas);
        Assert.NotNull(definition.SourceRoot);
        var output = XDocument.Parse(new XmlMappingEngine(entry.Settings["mappingJson"]).Transform("<Order xmlns='urn:source' code='A'><Id>42</Id></Order>"));
        Assert.Equal("42", output.Root!.Element("Number")!.Value);
    }

    [Fact]
    public void ExplicitChoices_WinAndAreKeptByteForByte()
    {
        var project = WriteProject();
        var explicitJson = JsonSerializer.Serialize(new[] { new RenderDispatchCase { Value = Good, Module = "xml-mapping-render", Settings = new() { ["mappingJson"] = "{}" } } },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        var result = Derive(project, Literals(Good), explicitJson);

        Assert.Equal("ExplicitChoice", Assert.Single(result.Report).Status);
        Assert.Equal("{}", Assert.Single(RenderDispatchDefinition.Read(result.CasesJson)).Settings["mappingJson"]);
    }

    [Fact]
    public void SameClassInTwoMapFiles_IsAmbiguousAndGetsNoChoice()
    {
        var project = WriteProject();
        var other = Path.Combine(_directory, "Other");
        Directory.CreateDirectory(Path.Combine(other, "Maps"));
        File.WriteAllText(Path.Combine(other, "Other.btproj"), Project("Maps\\OrderToResult.btm", "OrderToResult"));
        File.Copy(Path.Combine(_directory, "Maps", "OrderToResult.btm"), Path.Combine(other, "Maps", "OrderToResult.btm"));

        var result = Derive(project, Literals(Good), projects: [project, Path.Combine(other, "Other.btproj")]);

        Assert.Equal("AmbiguousMap", Assert.Single(result.Report).Status);
        Assert.Empty(RenderDispatchDefinition.Read(result.CasesJson, allowEmpty: true));
    }

    [Fact]
    public void MapWithScriptingFunctoid_IsConvertedWithStubsAndFailsClosedAtRunTime()
    {
        var project = WriteProject();
        string P(string path) => System.Security.SecurityElement.Escape(NativeMapImportTests.Path(path));
        var map = WithReferences("<mapsource><SrcTree RootNode_Name=\"Order\" /><TrgTree RootNode_Name=\"Result\" /><Pages><Page Name=\"p\"><Links>"
            + $"<Link LinkID=\"1\" LinkFrom=\"{P("Order/@code")}\" LinkTo=\"{P("Result/@code")}\" />"
            + $"<Link LinkID=\"2\" LinkFrom=\"{P("Order/Id")}\" LinkTo=\"10\" /><Link LinkID=\"3\" LinkFrom=\"10\" LinkTo=\"{P("Result/Number")}\" />"
            + "</Links><Functoids><Functoid FunctoidID=\"10\" Functoid-FID=\"260\" X-Cell=\"1\" Y-Cell=\"1\"><Input-Parameters><Parameter Type=\"link\" Value=\"2\" Guid=\"g\" linkIndex=\"0\" /></Input-Parameters>"
            + "<ScripterCode><Script Language=\"CSharp\"><![CDATA[public string Tidy(string a) { return a.Trim(); }]]></Script></ScripterCode></Functoid></Functoids></Page></Pages></mapsource>");
        File.WriteAllText(Path.Combine(_directory, "Maps", "OrderToResult.btm"), map);

        var result = Derive(project, Literals(Good));

        var item = result.Report.Single();
        Assert.True(item.Status == "ConvertedWithStubs", item.Status + ": " + item.Detail);
        Assert.Contains("Tidy", item.Detail, StringComparison.Ordinal);
        var entry = Assert.Single(RenderDispatchDefinition.Read(result.CasesJson));
        var error = Assert.Throws<ArgumentException>(() => new XmlMappingEngine(entry.Settings["mappingJson"]));
        Assert.Contains("unimplemented stub", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MultiInputMap_ResolvesEachPartSchemaByClassNameAndReportsTheAggregateInput()
    {
        var project = WriteProject();
        File.WriteAllText(Path.Combine(_directory, "Schemas", "Extra.xsd"), """
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:extra" elementFormDefault="qualified">
              <xs:element name="Extra"><xs:complexType><xs:sequence><xs:element name="Val" type="xs:string" /></xs:sequence></xs:complexType></xs:element>
            </xs:schema>
            """);
        File.WriteAllText(project, File.ReadAllText(project).Replace("</ItemGroup>",
            "<Schema Include=\"Schemas\\Extra.xsd\"><Namespace>Acme.Partner.Schemas</Namespace><TypeName>Extra</TypeName></Schema></ItemGroup>", StringComparison.Ordinal));
        const string aggregate = "http://schemas.microsoft.com/BizTalk/2003/aggschema";
        string P(string path) => System.Security.SecurityElement.Escape(NativeMapImportTests.Path(path));
        var wrapper = $"""
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:a="urn:source" xmlns:b="urn:extra" targetNamespace="{aggregate}">
              <xs:import schemaLocation="Acme.Partner.Schemas.Order" namespace="urn:source" />
              <xs:import schemaLocation="Acme.Partner.Schemas.Extra" namespace="urn:extra" />
              <xs:element name="Root"><xs:complexType><xs:sequence>
                <xs:element name="InputMessagePart_0"><xs:complexType><xs:sequence><xs:element ref="a:Order" /></xs:sequence></xs:complexType></xs:element>
                <xs:element name="InputMessagePart_1"><xs:complexType><xs:sequence><xs:element ref="b:Extra" /></xs:sequence></xs:complexType></xs:element>
              </xs:sequence></xs:complexType></xs:element>
            </xs:schema>
            """;
        var map = "<mapsource><SrcTree RootNode_Name=\"Root\">" + wrapper + "</SrcTree><TrgTree RootNode_Name=\"Result\"><Reference Location=\"Acme.Partner.Schemas.Result\" /></TrgTree><Pages><Page Name=\"p\"><Links>"
            + $"<Link LinkID=\"1\" LinkFrom=\"{P("Root/InputMessagePart_0/Order/@code")}\" LinkTo=\"{P("Result/@code")}\" />"
            + $"<Link LinkID=\"2\" LinkFrom=\"{P("Root/InputMessagePart_1/Extra/Val")}\" LinkTo=\"{P("Result/Number")}\" />"
            + "</Links></Page></Pages></mapsource>";
        File.WriteAllText(Path.Combine(_directory, "Maps", "OrderToResult.btm"), map);

        var result = Derive(project, Literals(Good));

        var item = result.Report.Single();
        Assert.True(item.Status == "Converted", item.Status + ": " + item.Detail);
        Assert.Contains(item.Warnings, warning => warning.Contains("xml-parts-render", StringComparison.Ordinal));
        var entry = Assert.Single(RenderDispatchDefinition.Read(result.CasesJson));
        var output = XDocument.Parse(new XmlMappingEngine(entry.Settings["mappingJson"]).Transform(
            $"<Root xmlns='{aggregate}'><InputMessagePart_0 xmlns=''><Order xmlns='urn:source' code='A'><Id>1</Id></Order></InputMessagePart_0><InputMessagePart_1 xmlns=''><Extra xmlns='urn:extra'><Val>V</Val></Extra></InputMessagePart_1></Root>"));
        Assert.Equal("V", output.Root!.Element("Number")!.Value);
        Assert.Equal("A", output.Root.Attribute("code")!.Value);

        File.WriteAllText(Path.Combine(_directory, "Maps", "OrderToResult.btm"), map.Replace("Acme.Partner.Schemas.Extra", "Acme.Partner.Schemas.Absent", StringComparison.Ordinal));
        var unresolved = Derive(project, Literals(Good)).Report.Single();
        Assert.Equal("SchemaUnresolved", unresolved.Status);
        Assert.Contains("Acme.Partner.Schemas.Absent", unresolved.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void MapSchemaReferences_CanBeRelativeFilePathsToDeclaredSchemas()
    {
        var project = WriteProject();
        var map = File.ReadAllText(Path.Combine(_directory, "Maps", "OrderToResult.btm"))
            .Replace("Acme.Partner.Schemas.Order", @"..\Schemas\Order.xsd", StringComparison.Ordinal)
            .Replace("Acme.Partner.Schemas.Result", @".\..\Schemas\Result.xsd", StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(_directory, "Maps", "OrderToResult.btm"), map);

        var item = Derive(project, Literals(Good)).Report.Single();
        Assert.True(item.Status == "Converted", item.Status + ": " + item.Detail);

        File.WriteAllText(Path.Combine(_directory, "Maps", "OrderToResult.btm"), map.Replace(@"..\Schemas\Order.xsd", @"..\Schemas\Undeclared.xsd", StringComparison.Ordinal));
        Assert.Equal("SchemaUnresolved", Derive(project, Literals(Good)).Report.Single().Status);
        var catalog = BizTalkSchemaSourceCatalog.Read([project]);
        Assert.Null(catalog.ResolveFileReference(Path.Combine(_directory, "Maps"), Path.Combine(_directory, "Schemas", "Order.xsd")));
        Assert.Null(catalog.ResolveFileReference(Path.Combine(_directory, "Maps"), "https://example.test/Order.xsd"));
        Assert.NotNull(catalog.ResolveFileReference(Path.Combine(_directory, "Maps"), "../Schemas/Order.xsd"));
    }

    [Fact]
    public void SchemaImports_CanNameADeclaredSchemaClass_AndUndeclaredOnesAreReported()
    {
        var project = WriteProject();
        File.WriteAllText(Path.Combine(_directory, "Schemas", "Part.xsd"), """
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:part" elementFormDefault="qualified">
              <xs:element name="Part" type="xs:string" />
            </xs:schema>
            """);
        File.WriteAllText(project, File.ReadAllText(project).Replace("</ItemGroup>",
            "<Schema Include=\"Schemas\\Part.xsd\"><Namespace>Acme.Partner.Schemas</Namespace><TypeName>Part</TypeName></Schema></ItemGroup>", StringComparison.Ordinal));
        var order = Path.Combine(_directory, "Schemas", "Order.xsd");
        var original = File.ReadAllText(order);
        File.WriteAllText(order, original.Replace("elementFormDefault=\"qualified\">", "elementFormDefault=\"qualified\"><xs:import namespace=\"urn:part\" schemaLocation=\"Acme.Partner.Schemas.Part\" />", StringComparison.Ordinal));
        var catalog = BizTalkSchemaSourceCatalog.Read([project]);
        Assert.Equal(2, catalog.ReadSchemaDocuments(order).Count);

        File.WriteAllText(order, original.Replace("elementFormDefault=\"qualified\">", "elementFormDefault=\"qualified\"><xs:import namespace=\"urn:part\" schemaLocation=\"Product.Schemas.Absent\" />", StringComparison.Ordinal));
        Assert.Contains("not declared by any analyzed project", Assert.Throws<NotSupportedException>(() => catalog.ReadSchemaDocuments(order)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ManyValues_StopAtTheRenderDispatchCaseCapAndReportTheRest()
    {
        var project = WriteProject();
        var values = Enumerable.Range(0, 130).Select(index => $"Acme.Partner.Maps.OrderToResult, Acme.Partner, Version=1.0.{index}.0").ToArray();

        var result = Derive(project, Literals(values));

        Assert.Equal(128, result.Report.Count(item => item.Status == "Converted"));
        Assert.Equal(2, result.Report.Count(item => item.Status == "CapExceeded"));
        Assert.Equal(128, RenderDispatchDefinition.Read(result.CasesJson).Count);
    }

    [Fact]
    public void MapNameLiterals_OnlyIncludeNonEmptyMapNameStringsOfSelectedVersions()
    {
        var path = Path.Combine(_directory, "rules.xml");
        Directory.CreateDirectory(_directory);
        File.WriteAllText(path, Rules(("Billing", 1, 0, "MapName", "A.Map, Asm"), ("Billing", 1, 0, "HeaderMapName", "B.Map, Asm"), ("Billing", 1, 0, "MapName", ""),
            ("Billing", 1, 0, "GroupID", "Not.A.Map, Asm"), ("Billing", 2, 0, "MapName", "C.Map, Asm")));

        Assert.Equal(["A.Map, Asm", "B.Map, Asm", "C.Map, Asm"], BizTalkRulePolicyAnalyzer.ReadMapNameLiterals(path, null).Select(item => item.Value));
        var selected = BizTalkRulePolicyAnalyzer.ReadMapNameLiterals(path, new Dictionary<string, string> { ["Billing"] = "2.0" });
        Assert.Equal("C.Map, Asm", Assert.Single(selected).Value);
        Assert.Equal("Billing", selected[0].Policy);
    }

    [Fact]
    public async Task ImportService_ReportsDerivationAndRequiresNativeGenerationAndRuleExports()
    {
        var project = WriteProject();
        var rules = Path.Combine(_directory, "rules.xml");
        File.WriteAllText(rules, Rules(("Billing", 1, 0, "MapName", Good), ("Billing", 1, 0, "MapName", "Acme.Partner.Maps.Missing, Acme.Partner")));
        var service = new BizTalkImportService();

        var response = await service.AnalyzeAsync(new AnalyzeBizTalkSolutionRequest
        {
            SourcePath = project, AdditionalRulePaths = [rules], GenerateNativeFlow = true, DeriveNativeMapChoices = true
        }, default);

        Assert.Equal(["Converted", "NoMatchingMap"], response.DerivedMapChoices.OrderBy(item => item.Status, StringComparer.Ordinal).Select(item => item.Status));
        Assert.Single(RenderDispatchDefinition.Read(response.EffectiveMapChoicesJson!));
        Assert.Contains(response.Warnings, warning => warning.Contains("1 of 2 policy map-name values", StringComparison.Ordinal));
        await Assert.ThrowsAsync<ArgumentException>(() => service.AnalyzeAsync(new AnalyzeBizTalkSolutionRequest
        { SourcePath = project, GenerateNativeFlow = true, DeriveNativeMapChoices = true }, default));
        await Assert.ThrowsAsync<ArgumentException>(() => service.AnalyzeAsync(new AnalyzeBizTalkSolutionRequest
        { SourcePath = project, AdditionalRulePaths = [rules], DeriveNativeMapChoices = true }, default));
        var without = await service.AnalyzeAsync(new AnalyzeBizTalkSolutionRequest { SourcePath = project, AdditionalRulePaths = [rules], GenerateNativeFlow = true }, default);
        Assert.Empty(without.DerivedMapChoices);
        Assert.Null(without.EffectiveMapChoicesJson);
    }

    [Fact]
    public async Task Ui_ShowsDerivedChoicesAndSendsTheFlagOnlyWhenNativeGenerationAndRuleExportsAreSelected()
    {
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<BizTalkDerivedMapChoices>(ParameterView.FromDictionary(new Dictionary<string, object?>
        {
            ["Choices"] = new[]
            {
                new BizTalkDerivedMapChoiceViewModel("A.Map, Asm", "Converted", "A.btm", "ok", ["Billing 1.0"], ["Review proposal only"]),
                new BizTalkDerivedMapChoiceViewModel("B.Map, Asm", "NoMatchingMap", null, "No btproj Map matches", ["Billing 1.0"], [])
            },
            ["CanUse"] = true,
            ["OnUseAsExplicit"] = EventCallback.Factory.Create(new object(), () => { })
        }))).ToHtmlString());
        Assert.Contains("1 of 2 converted", html);
        Assert.Contains("NoMatchingMap", html);
        Assert.Contains("Copy converted choices", html);

        var designer = new Mulse.Ui.Components.Pages.Designer();
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var current = designer.GetType().GetMethod("CurrentBizTalkAnalysisRequest", flags)!;
        void SetField(string name, object value) => designer.GetType().GetField(name, flags)!.SetValue(designer, value);
        AnalyzeBizTalkSolutionRequestViewModel Request() => (AnalyzeBizTalkSolutionRequestViewModel)current.Invoke(designer, null)!;

        SetField("_generateNativeBizTalkFlow", true);
        Assert.False(Request().DeriveNativeMapChoices);
        SetField("_additionalRulePaths", "rules.xml");
        Assert.True(Request().DeriveNativeMapChoices);
        SetField("_deriveNativeMapChoices", false);
        Assert.False(Request().DeriveNativeMapChoices);
        SetField("_deriveNativeMapChoices", true);
        SetField("_generateNativeBizTalkFlow", false);
        Assert.False(Request().DeriveNativeMapChoices);
    }

    private const string PlainStylesheet = """
        <?xml version="1.0" encoding="utf-16"?>
        <xsl:stylesheet version="1.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:s="urn:source">
          <xsl:output method="xml" indent="no" />
          <xsl:template match="/s:Order"><Result><Number><xsl:value-of select="s:Id" /></Number></Result></xsl:template>
        </xsl:stylesheet>
        """;

    private const string ScriptStylesheet = """
        <xsl:stylesheet version="1.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:msxsl="urn:schemas-microsoft-com:xslt" xmlns:userCSharp="http://schemas.microsoft.com/BizTalk/2003/userCSharp" xmlns:s="urn:source" exclude-result-prefixes="msxsl userCSharp">
          <xsl:template match="/s:Order"><Result><Number><xsl:value-of select="userCSharp:Tidy(string(s:Id))" /></Number></Result></xsl:template>
          <msxsl:script language="C#" implements-prefix="userCSharp"><![CDATA[public string Tidy(string a) { return a.Trim(); }]]></msxsl:script>
        </xsl:stylesheet>
        """;

    private DerivedMapChoices DeriveCustomXslt(string? stylesheet, string customElement = "<CustomXSLT XsltPath=\".\\Custom.xsl\" ExtObjXmlPath=\"\" />")
    {
        var project = WriteProject();
        File.WriteAllText(Path.Combine(_directory, "Maps", "OrderToResult.btm"), "<mapsource>" + customElement + "<Pages><Page Name=\"p\"><Links /></Page></Pages></mapsource>");
        if (stylesheet is not null) File.WriteAllText(Path.Combine(_directory, "Maps", "Custom.xsl"), stylesheet, System.Text.Encoding.Unicode);
        return Derive(project, Literals(Good));
    }

    private static Task<IntegrationBatch> RunXslt(RenderDispatchCase entry, string input = "<Order xmlns='urn:source'><Id> 42 </Id></Order>")
        => new XsltRenderModule().RenderAsync(new FlowExecutionContext("custom", "run", DateTimeOffset.UtcNow),
            new IntegrationBatch([new IntegrationPayload("in.xml", BinaryData.FromString(input), "application/xml", new Dictionary<string, string>())]),
            new ModuleStepDefinition { Module = entry.Module, Settings = entry.Settings }, default);

    [Fact]
    public async Task CustomXsltMap_IsImportedAsAnEditableInlineXsltChoiceThatRuns()
    {
        var result = DeriveCustomXslt(PlainStylesheet);

        var item = result.Report.Single();
        Assert.True(item.Status == "Converted", item.Status + ": " + item.Detail);
        Assert.Contains(item.Warnings, warning => warning.Contains("grid and links are ignored", StringComparison.Ordinal));
        var entry = Assert.Single(RenderDispatchDefinition.Read(result.CasesJson));
        Assert.Equal("xslt-render", entry.Module);
        Assert.False(entry.Settings.ContainsKey("xsltPath"));
        Assert.DoesNotContain("utf-16", entry.Settings["xsltText"], StringComparison.OrdinalIgnoreCase);
        var output = Assert.Single((await RunXslt(entry)).Payloads).GetText();
        Assert.Equal("42", XDocument.Parse(output).Root!.Element("Number")!.Value.Trim());
    }

    [Fact]
    public async Task CustomXsltMapWithScriptBlocks_IsConvertedWithStubsAndFailsClosedAtRunTime()
    {
        var result = DeriveCustomXslt(ScriptStylesheet);

        var item = result.Report.Single();
        Assert.True(item.Status == "ConvertedWithStubs", item.Status + ": " + item.Detail);
        Assert.Contains("Tidy", item.Detail, StringComparison.Ordinal);
        var entry = Assert.Single(RenderDispatchDefinition.Read(result.CasesJson));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => RunXslt(entry));
        Assert.Contains("msxsl:script", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("missing", "was not found")]
    [InlineData("extobj", "ExtObjXmlPath")]
    [InlineData("noPath", "no XsltPath")]
    [InlineData("notXslt", "not an xsl:stylesheet")]
    [InlineData("v2", "not XSLT 1.0")]
    [InlineData("broken", "not well-formed")]
    public void UnusableCustomXslt_IsReportedAsConversionFailedWithoutAChoice(string problem, string expected)
    {
        var stylesheet = problem switch
        {
            "notXslt" => "<root />",
            "v2" => PlainStylesheet.Replace("<xsl:stylesheet version=\"1.0\"", "<xsl:stylesheet version=\"2.0\"", StringComparison.Ordinal),
            "broken" => "<xsl:stylesheet",
            "missing" => null,
            _ => PlainStylesheet
        };
        var element = problem switch
        {
            "extobj" => "<CustomXSLT XsltPath=\"Custom.xsl\" ExtObjXmlPath=\"Ext.xml\" />",
            "noPath" => "<CustomXSLT ExtObjXmlPath=\"\" />",
            _ => "<CustomXSLT XsltPath=\"Custom.xsl\" ExtObjXmlPath=\"\" />"
        };

        var result = DeriveCustomXslt(stylesheet, element);

        var item = result.Report.Single();
        Assert.Equal("ConversionFailed", item.Status);
        Assert.Contains(expected, item.Detail, StringComparison.Ordinal);
        Assert.Empty(RenderDispatchDefinition.Read(result.CasesJson, allowEmpty: true));
    }

    [Fact]
    public async Task XsltRender_RequiresExactlyOneOfPathOrText()
    {
        foreach (var settings in new Dictionary<string, string>[] { new(), new() { ["xsltPath"] = "a.xsl", ["xsltText"] = PlainStylesheet } })
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => RunXslt(new RenderDispatchCase { Value = "x", Module = "xslt-render", Settings = settings }));
            Assert.Contains("exactly one", error.Message, StringComparison.Ordinal);
        }
    }

    private DerivedMapChoices Derive(string project, IReadOnlyList<PolicyMapNameLiteral> literals, string? explicitJson = null, string[]? projects = null)
    {
        var paths = projects ?? [project];
        return BizTalkNativeMapChoiceDeriver.Derive(paths, literals, BizTalkSchemaSourceCatalog.Read(paths), explicitJson, default);
    }

    private static PolicyMapNameLiteral[] Literals(params string[] values) => values.Select(value => new PolicyMapNameLiteral("Billing", "1.0", "MapName", value)).ToArray();

    private static string Project(string mapInclude, string mapType) => $"""
        <Project><PropertyGroup><AssemblyName>{Assembly}</AssemblyName><RootNamespace>{Assembly}</RootNamespace></PropertyGroup>
        <ItemGroup>
          <Schema Include="Schemas\Order.xsd"><Namespace>Acme.Partner.Schemas</Namespace><TypeName>Order</TypeName></Schema>
          <Schema Include="Schemas\Result.xsd"><Namespace>Acme.Partner.Schemas</Namespace><TypeName>Result</TypeName></Schema>
          <Map Include="{mapInclude}"><Namespace>Acme.Partner.Maps</Namespace><TypeName>{mapType}</TypeName></Map>
        </ItemGroup></Project>
        """;

    private string WriteProject()
    {
        Directory.CreateDirectory(Path.Combine(_directory, "Schemas"));
        Directory.CreateDirectory(Path.Combine(_directory, "Maps"));
        File.WriteAllText(Path.Combine(_directory, "Schemas", "Order.xsd"), NativeMapImportTests.SourceXsd);
        File.WriteAllText(Path.Combine(_directory, "Schemas", "Result.xsd"), NativeMapImportTests.TargetXsd);
        File.WriteAllText(Path.Combine(_directory, "Maps", "OrderToResult.btm"), WithReferences(NativeMapImportTests.Map()));
        File.WriteAllText(Path.Combine(_directory, "Maps", "Broken.btm"), WithReferences(NativeMapImportTests.Map(extra: "<Functoids />")));
        var project = Path.Combine(_directory, "Partner.btproj");
        File.WriteAllText(project, Project("Maps\\OrderToResult.btm", "OrderToResult").Replace("</ItemGroup>",
            "<Map Include=\"Maps\\Broken.btm\"><Namespace>Acme.Partner.Maps</Namespace><TypeName>Broken</TypeName></Map></ItemGroup>", StringComparison.Ordinal));
        return project;
    }

    private static string WithReferences(string map) => map
        .Replace("<SrcTree RootNode_Name=\"Order\" />", "<SrcTree RootNode_Name=\"Order\"><Reference Location=\"Acme.Partner.Schemas.Order\" /></SrcTree>", StringComparison.Ordinal)
        .Replace("<TrgTree RootNode_Name=\"Result\" />", "<TrgTree RootNode_Name=\"Result\"><Reference Location=\"Acme.Partner.Schemas.Result\" /></TrgTree>", StringComparison.Ordinal);

    private static string Rules(params (string Policy, int Major, int Minor, string Field, string Value)[] setters)
    {
        XNamespace brl = "http://schemas.microsoft.com/businessruleslanguage/2002";
        return new XElement(brl + "brl", setters.GroupBy(setter => (setter.Policy, setter.Major, setter.Minor)).Select(group => new XElement(brl + "ruleset", new XAttribute("name", group.Key.Policy),
            new XElement(brl + "version", new XAttribute("major", group.Key.Major), new XAttribute("minor", group.Key.Minor)),
            group.Select(setter => new XElement(brl + "rule", new XAttribute("name", "r"), new XAttribute("priority", 0), new XAttribute("active", "true"),
                new XElement(brl + "then", new XElement(brl + "function", new XElement(brl + "xmldocumentmember", new XAttribute("xmldocumentref", "xml_1"),
                    new XAttribute("type", "string"), new XAttribute("sideeffects", "true"),
                    new XElement(brl + "field", $"*[local-name()='{setter.Field}' and namespace-uri()='']"),
                    new XElement(brl + "argument", new XElement(brl + "constant", new XElement(brl + "string", setter.Value))))))))))).ToString();
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
}
