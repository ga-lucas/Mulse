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
