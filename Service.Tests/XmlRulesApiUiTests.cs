using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mulse.Modules.Decisions;
using Mulse.Ui.Components.Shared;
using Mulse.Ui.Models;
using Mulse.Ui.Services;
using Service.BizTalkImport;
using Service.Endpoints;
using Service.Flows;
using Service.Middleware;
using Service.Models;

namespace Service.Tests;

public sealed class XmlRulesApiUiTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task TypedClient_PreviewsExactRuntime_AndPreservesExportsDuringImportAndDownloads()
    {
        var importer = new RecordingImporter();
        var flows = new RecordingFlows();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddProblemDetails();
        builder.Services.AddExceptionHandler<ApiExceptionHandler>();
        ApiTestValidation.Register(builder.Services);
        builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        builder.Services.AddSingleton<IBizTalkImportService>(importer);
        builder.Services.AddSingleton<IFlowDefinitionService>(flows);
        await using var app = builder.Build();
        app.UseExceptionHandler(); app.MapBizTalkMigrationEndpoints();
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var http = new HttpClient { BaseAddress = new Uri(address) };
            var client = new MulseApiClient(http);
            var policy = JsonSerializer.Serialize(Policy(), Options);
            const string sample = "<Facts><GroupID>001</GroupID><MapName/></Facts>";
            var preview = await client.PreviewXmlRulesAsync(new(policy, sample), default);
            Assert.Equal(XmlRuleRuntime.Evaluate(Policy(), sample).OutputXml, preview.OutputXml);
            Assert.Equal(new[] { "Client A" }, preview.MatchedRules);
            Assert.Null(flows.Created);
            Assert.Empty(importer.Requests);
            using var invalid = await http.PostAsJsonAsync("/api/biztalk-import/rules/preview", new XmlRulePreviewRequestViewModel(policy, "<!DOCTYPE x SYSTEM 'file:///not-read'><x/>"));
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            var analysis = await client.AnalyzeBizTalkSolutionAsync(new("source", ["binding-export.xml"], ["dependencies"], ["rules.xml"]), default);
            Assert.Single(analysis.RulePolicies);
            Assert.Single(analysis.FlowCandidates);
            var request = new CreateImportedBizTalkFlowRequestViewModel("source", "example", ["dependencies"], ["binding-export.xml"], ["rules.xml"]);
            await client.DownloadBizTalkScaffoldedModulesAsync(request, default);
            await client.DownloadBizTalkScaffoldedModuleAsync("scaffold", request, default);
            await client.ImportBizTalkDraftFlowAsync(request, default);
            Assert.Equal(4, importer.Requests.Count);
            Assert.All(importer.Requests, captured =>
            {
                Assert.Equal(new[] { "dependencies" }, captured.AdditionalSourcePaths);
                Assert.Equal(new[] { "binding-export.xml" }, captured.AdditionalBindingPaths);
                Assert.Equal(new[] { "rules.xml" }, captured.AdditionalRulePaths);
            });
            Assert.NotNull(flows.Created?.Migration);
            Assert.False(flows.Created!.Enabled);
        }
        finally { await app.StopAsync(); }
    }

    [Fact]
    public async Task VisualPolicyEditor_RoundTripsClientRules_AndDoesNotSaveSyntheticSamples()
    {
        var saved = JsonSerializer.Serialize(Policy(), Options);
        var editor = new XmlRulePolicyEditor();
        Set(editor, "PolicyJson", saved);
        Set(editor, "PolicyJsonChanged", EventCallback.Factory.Create<string>(new object(), (string value) => saved = value));
        Invoke(editor, "OnParametersSet");
        Field(editor, "_sample").SetValue(editor, "sensitive-synthetic-input");
        var policy = (XmlRulePolicyViewModel)Field(editor, "_policy").GetValue(editor)!;
        policy.Rules[0].Conditions[0].Value = "002";
        policy.Rules[0].Assignments[0].Value = "Map.B";
        await (Task)Invoke(editor, "SaveAsync")!;
        var reopened = XmlRuleRuntime.ReadPolicy(saved);
        Assert.Equal("002", reopened.Rules[0].Conditions[0].Value);
        Assert.Equal("Map.B", reopened.Rules[0].Assignments[0].Value);
        Assert.DoesNotContain("sensitive-synthetic-input", saved);
        using var http = new HttpClient { BaseAddress = new Uri("http://localhost/") };
        await using var services = new ServiceCollection().AddLogging().AddSingleton<IMulseApiClient>(new MulseApiClient(http)).BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var render = await renderer.RenderComponentAsync<XmlRulePolicyEditor>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["PolicyJson"] = saved }));
            var html = render.ToHtmlString();
            Assert.Contains("Client A", html);
            Assert.Contains("Constant guard passes", html);
            Assert.Contains("Equality conditions", html);
            Assert.Contains("Literal assignments", html);
            Assert.Contains("Preview rules without delivery", html);
            var policies = await renderer.RenderComponentAsync<BizTalkRulePolicies>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                ["Policies"] = new BizTalkRulePolicyViewModel[] { new("rules.xml", "Shared", 1, 2, 1, 1, [], saved, []) }
            }));
            var inventory = policies.ToHtmlString();
            Assert.Contains("Policy name", inventory);
            Assert.Contains("latest exported version", inventory);
            Assert.Contains("xml-rule-policy-editor", inventory);
            Assert.Contains("What do policies do?", html);
            Assert.Contains("Changing a label does not load another policy", html);
            var replacement = await renderer.RenderComponentAsync<BizTalkRulePolicies>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                ["Policies"] = new BizTalkRulePolicyViewModel[] { new("rules.xml", "Shared", 1, 2, 1, 1, [], saved, []) },
                ["ReplaceExisting"] = true
            }));
            var replacementHtml = replacement.ToHtmlString();
            Assert.Contains("Replace this node", replacementHtml);
            Assert.Contains("including local edits", replacementHtml);
            Assert.DoesNotContain("xml-rule-policy-editor", replacementHtml);
        });
    }

    [Fact]
    public async Task PolicySelection_UsesNumericLatestPerName_WithoutApplyingOrFallingBack()
    {
        var first = JsonSerializer.Serialize(Policy(), Options);
        var latestPolicy = Policy();
        latestPolicy.Version = "1.10";
        latestPolicy.Rules[0].Assignments[0].Value = "Map.Latest";
        var latest = JsonSerializer.Serialize(latestPolicy, Options);
        var applied = new List<string>();
        var picker = new BizTalkRulePolicies();
        var policies = new BizTalkRulePolicyViewModel[]
        {
            new("older.xml", "Shared", 1, 2, 1, 1, [], first, []),
            new("latest.xml", "Shared", 1, 10, 1, 1, [], latest, []),
            new("other.xml", "Other", 2, 0, 1, 1, [], null, ["Unsupported expression"])
        };
        Set(picker, "Policies", policies);
        Set(picker, "OnAddPolicy", EventCallback.Factory.Create<string>(new object(), (string value) => applied.Add(value)));
        Invoke(picker, "OnParametersSet");
        Assert.Null(Field(picker, "_draft").GetValue(picker));
        Change("SelectName", "Shared");
        Assert.Equal(latest, Field(picker, "_draft").GetValue(picker));
        Assert.Empty(applied);
        Change("SelectPolicy", "0");
        Assert.Equal(first, Field(picker, "_draft").GetValue(picker));
        Invoke(picker, "OnParametersSet");
        Assert.Equal(first, Field(picker, "_draft").GetValue(picker));
        Change("SelectName", "Other");
        Assert.Null(Field(picker, "_draft").GetValue(picker));
        Assert.Equal("2", Field(picker, "_selected").GetValue(picker));
        Change("SelectPolicy", "0");
        Assert.Null(Field(picker, "_draft").GetValue(picker));
        Assert.Empty(applied);
        Set(picker, "Policies", policies.Take(2).ToArray());
        Invoke(picker, "OnParametersSet");
        Assert.Equal("Shared", Field(picker, "_selectedName").GetValue(picker));
        Assert.Equal(latest, Field(picker, "_draft").GetValue(picker));
        Assert.Empty(applied);
        await picker.OnAddPolicy.InvokeAsync(Assert.IsType<string>(Field(picker, "_draft").GetValue(picker)));
        var pinned = Assert.Single(applied);
        var result = XmlRuleRuntime.Evaluate(XmlRuleRuntime.ReadPolicy(pinned), "<Facts><GroupID>001</GroupID><MapName/></Facts>");
        Assert.Contains("Map.Latest", result.OutputXml);
        Assert.Contains("Map.A", XmlRuleRuntime.ReadPolicy(first).Rules[0].Assignments[0].Value);
        Set(picker, "Policies", new BizTalkRulePolicyViewModel[]
        {
            policies[0], policies[1], new("unsupported.xml", "Shared", 2, 0, 1, 1, [], null, ["Unsupported expression"])
        });
        Invoke(picker, "OnParametersSet");
        Assert.Equal("2", Field(picker, "_selected").GetValue(picker));
        Assert.Null(Field(picker, "_draft").GetValue(picker));
        Assert.Equal(pinned, Assert.Single(applied));
        Assert.Equal("1.10", XmlRuleRuntime.ReadPolicy(pinned).Version);

        void Change(string method, string value) => typeof(BizTalkRulePolicies)
            .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(picker, [new ChangeEventArgs { Value = value }]);
    }

    [Fact]
    public void InvalidPolicyJson_IsPreservedForRepair_NotReplacedByEmptyRules()
    {
        var editor = new XmlRulePolicyEditor();
        const string invalid = "{\"futureProperty\":true}";
        Set(editor, "PolicyJson", invalid);
        Invoke(editor, "OnParametersSet");
        Assert.Null(Field(editor, "_policy").GetValue(editor));
        Assert.Contains("Invalid policy", (string)Field(editor, "_error").GetValue(editor)!);
        Assert.Equal(invalid, editor.PolicyJson);
    }

    private static XmlRulePolicy Policy() => new()
    {
        Name = "Shared", Version = "1.2", RootName = "Facts",
        Rules = [new() { Name = "Client A", Conditions = [new() { Name = "GroupID", Value = "001" }], Assignments = [new() { Name = "MapName", Value = "Map.A" }] }]
    };
    private static void Set(object target, string name, object value) => target.GetType().GetProperty(name)!.SetValue(target, value);
    private static FieldInfo Field(object target, string name) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static object? Invoke(object target, string name) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, null);

    private sealed class RecordingImporter : IBizTalkImportService
    {
        public List<AnalyzeBizTalkSolutionRequest> Requests { get; } = [];
        public Task<BizTalkSolutionAnalysisResponse> AnalyzeAsync(AnalyzeBizTalkSolutionRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var draft = new BizTalkDraftFlowResponse("example", false, new(PipelineTriggerMode.OnDemand, null, false),
                [new("fetch", new("file-system-fetch", new Dictionary<string, string>()), [])],
                [new("parse", new("json-parse", new Dictionary<string, string>()), ["fetch"])], [],
                [new(new("json-render", new Dictionary<string, string>()), [new(new("logging-deliver", new Dictionary<string, string>()))])], [], [],
                new("BizTalk", "source", "Example", [], [], []));
            var candidate = new BizTalkFlowCandidateResponse("example", "Example", "", "", "", [], [], [], [], [], draft)
            { ScaffoldedModules = [new("Scaffold.cs", "scaffold", "Example", "throw new NotSupportedException();")] };
            return Task.FromResult(new BizTalkSolutionAnalysisResponse("source", "Project", "Example", "root", 1, 0, 1, 0, 0, 0, [], [], [], [candidate], [])
            { RulePolicies = [new("rules.xml", "Shared", 1, 2, 1, 1, [], JsonSerializer.Serialize(Policy(), Options), [])] });
        }
    }
    private sealed class RecordingFlows : IFlowDefinitionService
    {
        public PipelineDefinition? Created { get; private set; }
        public Task<PipelineDefinition> CreateAsync(PipelineDefinition pipeline, CancellationToken cancellationToken) { Created = pipeline; return Task.FromResult(pipeline); }
        public IReadOnlyList<PipelineDefinition> GetAll() => throw new NotSupportedException();
        public PipelineDefinition GetById(string id) => throw new NotSupportedException();
        public Task<PipelineDefinition> UpdateAsync(string id, PipelineDefinition pipeline, CancellationToken ct) => throw new NotSupportedException();
        public Task<PipelineDefinition> DuplicateAsync(string id, string newId, CancellationToken ct) => throw new NotSupportedException();
        public Task DeleteAsync(string id, CancellationToken ct) => throw new NotSupportedException();
    }
}
