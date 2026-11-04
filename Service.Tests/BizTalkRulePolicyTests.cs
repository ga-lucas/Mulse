using System.Text.Json;
using System.Xml.Linq;
using Mulse.Modules.Decisions;
using Service.BizTalkImport;
using Service.Models;

namespace Service.Tests;

public sealed class BizTalkRulePolicyTests : IDisposable
{
    private static readonly XNamespace Brl = "http://schemas.microsoft.com/businessruleslanguage/2002";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mulse-rules-" + Guid.NewGuid().ToString("N"));
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Import_PreservesNumericVersionsAndAllClientRules_WithoutSelectingOne()
    {
        var versions = Analyze(Export(Policy(1, 9), Policy(1, 52), Policy(1, 10)));
        Assert.Equal(new[] { 52, 10, 9 }, versions.Select(policy => policy.MinorVersion));
        foreach (var version in versions)
        {
            Assert.Equal(2, version.RuleCount);
            Assert.Equal(2, version.ActiveRuleCount);
            var policy = XmlRuleRuntime.ReadPolicy(Assert.IsType<string>(version.ProposedPolicyJson));
            Assert.Equal(2, policy.Rules.Count);
            Assert.Equal("001", policy.Rules[0].Conditions[0].Value);
            Assert.Equal("002", policy.Rules[1].Conditions[0].Value);
            var result = XmlRuleRuntime.Evaluate(policy, Facts("001", "true"));
            Assert.Equal(new[] { "Client A" }, result.MatchedRules);
            Assert.Equal("Map.A", XDocument.Parse(result.OutputXml).Root!.Element("MapName")!.Value);
            Assert.Equal("Map.B", XDocument.Parse(XmlRuleRuntime.Evaluate(policy, Facts("002", "1")).OutputXml).Root!.Element("MapName")!.Value);
        }
    }

    [Theory]
    [InlineData("operator")]
    [InlineData("function")]
    [InlineData("fact")]
    [InlineData("configuration")]
    [InlineData("path")]
    [InlineData("dependency")]
    [InlineData("attribute")]
    public void UnsupportedPolicy_IsReportedWithoutPartialExecutableProposal(string feature)
    {
        var policy = Policy(1, 0);
        switch (feature)
        {
            case "operator": policy.Descendants(Brl + "compare").First().SetAttributeValue("operator", "notEqual"); break;
            case "function": policy.Descendants(Brl + "then").First().Add(new XElement(Brl + "execute")); break;
            case "fact": policy.Element(Brl + "bindings")!.Add(new XElement(Brl + "database")); break;
            case "configuration": policy.Element(Brl + "configuration")!.Add(new XElement(Brl + "custom")); break;
            case "path": policy.Descendants(Brl + "field").First().Value = "//GroupID"; break;
            case "dependency": foreach (var field in policy.Descendants(Brl + "then").Descendants(Brl + "field")) field.Value = Field("GroupID"); break;
            case "attribute": policy.Descendants(Brl + "compare").First().SetAttributeValue("ignoreCase", "true"); break;
        }
        var result = Assert.Single(Analyze(Export(policy)));
        Assert.Null(result.ProposedPolicyJson);
        Assert.Contains(result.Diagnostics, issue => issue.StartsWith("Native proposal unavailable:", StringComparison.Ordinal));
        Assert.Equal(2, result.RuleCount);
    }

    [Fact]
    public void DuplicateIdentityAndUnsafeXml_AreExplicitFailures()
    {
        Assert.Throws<ArgumentException>(() => Analyze(Export(Policy(1, 0), Policy(1, 0))));
        Assert.Throws<ArgumentException>(() => Analyze("<!DOCTYPE brl [<!ENTITY x 'unsafe'>]><brl xmlns='http://schemas.microsoft.com/businessruleslanguage/2002'>&x;</brl>"));
        Assert.Throws<ArgumentException>(() => Analyze("<brl/>"));
        Assert.Throws<ArgumentException>(() => Analyze("<invalid"));
    }

    [Theory]
    [InlineData("999", "true")]
    [InlineData("1", "true")]
    [InlineData("001", "false")]
    public void UnmatchedFacts_AreNotSuccessShapedFallbacks(string group, string batch)
    {
        Assert.Contains("No XML rule matched", Assert.Throws<ArgumentException>(() => XmlRuleRuntime.Evaluate(Native(), Facts(group, batch))).Message);
    }

    [Fact]
    public void Conditions_AreCaseSensitive_AndInactiveRulesDoNotFire()
    {
        var policy = Native();
        policy.Rules[0].Conditions[0].Value = "ClientA";
        Assert.Throws<ArgumentException>(() => XmlRuleRuntime.Evaluate(policy, Facts("clienta", "true")));
        policy.Rules[0].Active = false;
        Assert.Throws<ArgumentException>(() => XmlRuleRuntime.Evaluate(policy, Facts("ClientA", "true")));
    }

    [Fact]
    public void Conflicts_DoNotUsePriorityOrOrderToSilentlyChooseClientOutput()
    {
        var policy = Native();
        policy.Rules[1].Conditions[0].Value = "001";
        policy.Rules[1].Priority = 100;
        Assert.Contains("conflicting", Assert.Throws<ArgumentException>(() => XmlRuleRuntime.Evaluate(policy, Facts("001", "true"))).Message);
        policy.Rules[1].Assignments[0].Value = "Map.A";
        Assert.Equal(2, XmlRuleRuntime.Evaluate(policy, Facts("001", "true")).MatchedRules.Count);
    }

    [Theory]
    [InlineData("<IntegrationRules xmlns='urn:test'><GroupID xmlns=''>001</GroupID><IsBatch xmlns=''>true</IsBatch></IntegrationRules>")]
    [InlineData("<IntegrationRules xmlns='urn:test'><GroupID xmlns=''>001</GroupID><GroupID xmlns=''>001</GroupID><IsBatch xmlns=''>true</IsBatch><MapName xmlns=''/></IntegrationRules>")]
    [InlineData("<IntegrationRules xmlns='urn:wrong'/>")]
    [InlineData("<!DOCTYPE root SYSTEM 'file:///not-read'><root/>")]
    [InlineData("<IntegrationRules xmlns='urn:test'><GroupID xmlns=''>001</GroupID><IsBatch xmlns=''>invalid</IsBatch><MapName xmlns=''/></IntegrationRules>")]
    public void InvalidFacts_AreRejected(string xml)
    {
        Assert.Throws<ArgumentException>(() => XmlRuleRuntime.Evaluate(Native(), xml));
    }

    [Fact]
    public async Task Module_PreservesSourcesAndMetadata_AndUsesSameRuntime()
    {
        var policy = Assert.IsType<string>(Assert.Single(Analyze(Export(Policy(1, 2)))).ProposedPolicyJson);
        var first = new IntegrationPayload("a.xml", BinaryData.FromString(Facts("001", "true")), "application/xml", new Dictionary<string, string> { ["sourceId"] = "facts", ["correlationId"] = "synthetic" });
        var other = new IntegrationPayload("b.xml", BinaryData.FromString("unchanged"), "application/xml", new Dictionary<string, string> { ["sourceId"] = "other" });
        var output = await new XmlRulesAugmentModule().AugmentAsync(new("test", "run", DateTimeOffset.UtcNow), new([first, other]),
            new ModuleStepDefinition { Settings = new() { ["policyJson"] = policy, ["sourceId"] = "facts" } }, default);
        Assert.Equal("synthetic", output.Payloads[0].Metadata["correlationId"]);
        Assert.Equal("1.2", output.Payloads[0].Metadata["xmlRulesVersion"]);
        Assert.Same(other, output.Payloads[1]);
        Assert.Equal(XmlRuleRuntime.Evaluate(XmlRuleRuntime.ReadPolicy(policy), first.GetText()).OutputXml, output.Payloads[0].GetText());
    }

    [Fact]
    public async Task Analyze_AttachesExportsForArbitraryProject_AndKeepsExecutionBlocked()
    {
        var path = Write("rules.xml", Export(Policy(1, 2)));
        var project = Write("Generic.btproj", "<Project><PropertyGroup><AssemblyName>Generic</AssemblyName></PropertyGroup><ItemGroup><XLang Include='Process.odx'/></ItemGroup></Project>");
        Write("Process.odx", "<?xml version='1.0'?><om:MetaModel xmlns:om='http://schemas.microsoft.com/BizTalk/2003/DesignerData'><om:Element Type='CallRules'><om:Property Name='PolicyName' Value='Any Policy'/></om:Element></om:MetaModel>");
        var result = await new BizTalkImportService().AnalyzeAsync(new AnalyzeBizTalkSolutionRequest { SourcePath = project, AdditionalRulePaths = [path, path] }, default);
        Assert.Single(result.RulePolicies);
        Assert.False(result.FlowCandidates.Single().DraftFlow.Enabled);
        Assert.NotNull(result.FlowCandidates.Single().DraftFlow.Migration);
        Assert.Single(result.FlowCandidates.Single().DraftFlow.Migration!.Artifacts, artifact => artifact.Kind == "RulePolicy");
        Assert.DoesNotContain(result.FlowCandidates.Single().DraftFlow.Augments, step => step.Module == "xml-rules-augment");
        Assert.Contains(result.Warnings, warning => warning.Contains("Select versions explicitly", StringComparison.Ordinal));
    }

    [Fact]
    public void LiteralComparisons_AreFoldedWithoutChangingSourceActiveFlags()
    {
        var source = Policy(1, 0);
        var comparison = source.Descendants(Brl + "compare").First();
        comparison.Element(Brl + "lhs")!.ReplaceNodes(Constant("boolean", "1"));
        comparison.Element(Brl + "rhs")!.ReplaceNodes(Constant("boolean", "false"));
        var policy = XmlRuleRuntime.ReadPolicy(Assert.Single(Analyze(Export(source))).ProposedPolicyJson!);
        Assert.True(policy.Rules[0].Active);
        Assert.False(policy.Rules[0].ConstantCondition);
        Assert.Throws<ArgumentException>(() => XmlRuleRuntime.Evaluate(policy, Facts("001", "true")));
        comparison.Element(Brl + "rhs")!.ReplaceNodes(Constant("boolean", "true"));
        policy = XmlRuleRuntime.ReadPolicy(Assert.Single(Analyze(Export(source))).ProposedPolicyJson!);
        Assert.True(policy.Rules[0].ConstantCondition);
        Assert.Contains("Client A", XmlRuleRuntime.Evaluate(policy, Facts("001", "true")).MatchedRules);
    }

    [Fact]
    public async Task Evaluation_IsIsolatedAcrossConcurrentClients_AndPreservesStringWhitespace()
    {
        var policy = Native();
        var results = await Task.WhenAll(Enumerable.Range(0, 100).Select(index => Task.Run(() =>
            XmlRuleRuntime.Evaluate(policy, Facts(index % 2 == 0 ? "001" : "002", "true")))));
        for (var index = 0; index < results.Length; index++)
            Assert.Equal(index % 2 == 0 ? "Map.A" : "Map.B", XDocument.Parse(results[index].OutputXml).Root!.Element("MapName")!.Value);
        var source = Policy(1, 0);
        source.Descendants(Brl + "compare").First().Element(Brl + "rhs")!.ReplaceNodes(Constant("string", " "));
        policy = XmlRuleRuntime.ReadPolicy(Assert.Single(Analyze(Export(source))).ProposedPolicyJson!);
        Assert.Equal(" ", policy.Rules[0].Conditions[0].Value);
        Assert.Contains("Client A", XmlRuleRuntime.Evaluate(policy, Facts(" ", "true")).MatchedRules);
    }

    [Fact]
    public void PotentialClientConflicts_AreVisibleInAnalysisBeforePreview()
    {
        var source = Policy(1, 0);
        var comparisons = source.Descendants(Brl + "rule").Last().Descendants(Brl + "compare");
        comparisons.First().Element(Brl + "rhs")!.ReplaceNodes(Constant("string", "001"));
        var result = Assert.Single(Analyze(Export(source)));
        Assert.NotNull(result.ProposedPolicyJson);
        Assert.Contains(result.Diagnostics, issue => issue.Contains("1 active rule pair(s)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InvalidExportLists_AreRejectedBeforeSourceAccess()
    {
        foreach (var paths in new[] { new[] { "" }, Enumerable.Repeat("rules.xml", 33).ToArray() })
            await Assert.ThrowsAsync<ArgumentException>(() => new BizTalkImportService().AnalyzeAsync(new AnalyzeBizTalkSolutionRequest
            { SourcePath = "does-not-exist.btproj", AdditionalRulePaths = paths }, default));
    }

    [Fact]
    public void LimitsStrictJsonAndCancellation_AreEnforced()
    {
        Assert.Throws<ArgumentException>(() => XmlRuleRuntime.ReadPolicy("{\"unexpected\":true}"));
        Assert.Throws<ArgumentException>(() => XmlRuleRuntime.ReadPolicy(new string(' ', XmlRuleRuntime.MaxPolicyCharacters + 1)));
        Assert.Throws<ArgumentException>(() => XmlRuleRuntime.Evaluate(Native(), new string(' ', XmlRuleRuntime.MaxXmlCharacters + 1)));
        Assert.Throws<OperationCanceledException>(() => XmlRuleRuntime.Evaluate(Native(), Facts("001", "true"), new CancellationToken(true)));
        var policy = Native();
        policy.Rules[0].Assignments[0].Type = "integer";
        Assert.Throws<ArgumentException>(() => XmlRuleRuntime.Validate(policy));
        policy = Native();
        policy.Rules.Clear();
        Assert.Throws<ArgumentException>(() => XmlRuleRuntime.Validate(policy));
    }

    [Fact]
    public void StructuralAndCharacterLimits_AcceptExactBoundary_AndRejectOneOver()
    {
        var policy = Native();
        policy.Rules = Enumerable.Repeat(policy.Rules[0], 512).ToList();
        Assert.Equal(512, XmlRuleRuntime.Evaluate(policy, Facts("001", "true")).MatchedRules.Count);
        policy.Rules.Add(policy.Rules[0]);
        Assert.Throws<ArgumentException>(() => XmlRuleRuntime.Validate(policy));
        policy = Native();
        policy.Rules[0].Conditions = Enumerable.Repeat(policy.Rules[0].Conditions[0], 32).ToList();
        XmlRuleRuntime.Validate(policy);
        policy.Rules[0].Conditions.Add(policy.Rules[0].Conditions[0]);
        Assert.Throws<ArgumentException>(() => XmlRuleRuntime.Validate(policy));
        policy = Native();
        policy.Rules[0].Assignments = Enumerable.Range(0, 64).Select(index => new XmlRuleField { Name = "Target" + index }).ToList();
        XmlRuleRuntime.Validate(policy);
        policy.Rules[0].Assignments.Add(new() { Name = "Target64" });
        Assert.Throws<ArgumentException>(() => XmlRuleRuntime.Validate(policy));
        policy = Native();
        policy.Rules[0].Assignments[0].Value = "";
        var baseline = JsonSerializer.Serialize(policy, Options);
        policy.Rules[0].Assignments[0].Value = new string('x', XmlRuleRuntime.MaxPolicyCharacters - baseline.Length);
        var json = JsonSerializer.Serialize(policy, Options);
        Assert.Equal(XmlRuleRuntime.MaxPolicyCharacters, json.Length);
        XmlRuleRuntime.ReadPolicy(json);
        Assert.Throws<ArgumentException>(() => XmlRuleRuntime.ReadPolicy(json + " "));
        policy = Native();
        var facts = XDocument.Parse(Facts("001", "true"));
        var padding = new XElement("Padding", "x");
        facts.Root!.Add(padding);
        var initial = XmlRuleRuntime.Evaluate(policy, facts.ToString(SaveOptions.DisableFormatting)).OutputXml;
        padding.Value = new string('x', XmlRuleRuntime.MaxXmlCharacters - initial.Length + 1);
        Assert.Equal(XmlRuleRuntime.MaxXmlCharacters, XmlRuleRuntime.Evaluate(policy, facts.ToString(SaveOptions.DisableFormatting)).OutputXml.Length);
        padding.Value += "x";
        Assert.Throws<ArgumentException>(() => XmlRuleRuntime.Evaluate(policy, facts.ToString(SaveOptions.DisableFormatting)));
        facts = XDocument.Parse(Facts("001", "true"));
        facts.Root!.Add(Enumerable.Range(0, 4093).Select(index => new XElement("Extra" + index)));
        XmlRuleRuntime.Evaluate(policy, facts.ToString());
        facts.Root.Add(new XElement("Extra4093"));
        Assert.Throws<ArgumentException>(() => XmlRuleRuntime.Evaluate(policy, facts.ToString()));
    }

    private XmlRulePolicy Native() => XmlRuleRuntime.ReadPolicy(Assert.IsType<string>(Assert.Single(Analyze(Export(Policy(1, 0)))).ProposedPolicyJson));
    private IReadOnlyList<BizTalkRulePolicyResponse> Analyze(string text) => BizTalkRulePolicyAnalyzer.Analyze(Write("rules.xml", text));
    private string Write(string name, string text)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, text);
        return path;
    }
    private static string Facts(string group, string batch) => new XElement(XName.Get("IntegrationRules", "urn:test"),
        new XElement("GroupID", group), new XElement("IsBatch", batch), new XElement("MapName", "")).ToString();
    private static string Export(params XElement[] policies) => new XElement(Brl + "brl", policies).ToString();
    private static string Field(string name) => $"*[local-name()='{name}' and namespace-uri()='']";
    private static XElement Policy(int major, int minor) => new(Brl + "ruleset", new XAttribute("name", "Any Policy"),
        new XElement(Brl + "bindings", new XElement(Brl + "xmldocument", new XAttribute("ref", "xml_1"), new XAttribute("doctype", "Test.IntegrationRules"),
            new XElement(Brl + "selector", "/*[local-name()='IntegrationRules' and namespace-uri()='urn:test']"), new XElement(Brl + "schema", "not-loaded.xsd"))),
        new XElement(Brl + "configuration"), Rule("Client A", "001", "Map.A"), Rule("Client B", "002", "Map.B"),
        new XElement(Brl + "version", new XAttribute("major", major), new XAttribute("minor", minor)));
    private static XElement Rule(string name, string group, string map) => new(Brl + "rule", new XAttribute("name", name), new XAttribute("priority", 0), new XAttribute("active", "true"),
        new XElement(Brl + "if", new XElement(Brl + "and", Comparison("GroupID", "string", group), Comparison("IsBatch", "boolean", "true"))),
        new XElement(Brl + "then", new XElement(Brl + "function", Member("MapName", "string", true, new XElement(Brl + "argument", Constant("string", map))))));
    private static XElement Comparison(string field, string type, string value) => new(Brl + "compare", new XAttribute("operator", "equal"),
        new XElement(Brl + "lhs", new XElement(Brl + "function", Member(field, type, false))), new XElement(Brl + "rhs", Constant(type, value)));
    private static XElement Member(string field, string type, bool setter, params XElement[] children) => new(Brl + "xmldocumentmember",
        new XAttribute("xmldocumentref", "xml_1"), new XAttribute("type", type), new XAttribute("sideeffects", setter ? "true" : "false"), new XElement(Brl + "field", Field(field)), children);
    private static XElement Constant(string type, string value) => new(Brl + "constant", new XElement(Brl + type, value));
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
}
