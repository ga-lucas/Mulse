using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mulse.Modules.Orchestration;
using Mulse.Ui.Models;
using Mulse.Ui.Services;
using Service.Endpoints;
using Service.Middleware;

namespace Service.Tests;

public sealed class OdxControlFlowApiTests
{
    [Fact]
    public async Task UiClientDownloadsOnlySelectedOrchestrationSource_WithoutApprovingOrCreatingFlows()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mulse-odx-download-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            foreach (var name in new[] { "First", "Second" })
                await File.WriteAllTextAsync(Path.Combine(directory, name + ".odx"), $$"""
                    // source wrapper
                    <?xml version="1.0"?>
                    <om:MetaModel xmlns:om="http://schemas.microsoft.com/BizTalk/2003/DesignerData">
                      <om:Element Type="Module" OID="module">
                        <om:Property Name="Name" Value="Example" />
                        <om:Element Type="ServiceDeclaration" OID="{{name}}">
                          <om:Property Name="Name" Value="{{name}}" />
                          <om:Element Type="ServiceBody"><om:Element Type="While" OID="loop-{{name}}">
                            <om:Property Name="Expression" Value="private-expression-{{name}}" />
                          </om:Element></om:Element>
                        </om:Element>
                      </om:Element>
                    </om:MetaModel>
                    // generated code must not execute
                    """);
            var project = Path.Combine(directory, "Example.btproj");
            await File.WriteAllTextAsync(project, "<Project><ItemGroup><XLang Include='First.odx'/><XLang Include='Second.odx'/></ItemGroup></Project>");
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
            builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
            builder.Services.AddProblemDetails(); builder.Services.AddExceptionHandler<ApiExceptionHandler>(); ApiTestValidation.Register(builder.Services);
            builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
            builder.Services.AddSingleton<IBizTalkImportService>(new BizTalkImportService());
            builder.Services.AddSingleton<IFlowDefinitionService>(_ => throw new InvalidOperationException("Source downloads must not create or execute flows."));
            await using var app = builder.Build(); app.UseExceptionHandler(); app.MapBizTalkMigrationEndpoints(); await app.StartAsync();
            try
            {
                var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
                using var http = new HttpClient { BaseAddress = new Uri(address) }; var client = new MulseApiClient(http);
                var analysis = await client.AnalyzeBizTalkSolutionAsync(new(project), default);
                Assert.Equal(2, analysis.FlowCandidates.Count);
                foreach (var candidate in analysis.FlowCandidates)
                {
                    Assert.False(candidate.DraftFlow.Enabled);
                    Assert.NotNull(candidate.DraftFlow.Migration);
                    var name = candidate.DraftFlow.Migration.SourceOrchestration!;
                    var file = await client.DownloadBizTalkControlFlowAsync(new(project, candidate.SuggestedFlowId), default);
                    Assert.EndsWith("-control-flow.json", file.FileName); Assert.StartsWith("application/json", file.ContentType);
                    var program = JsonSerializer.Deserialize<OdxProgram>(file.Content, JsonSerializerOptions.Web)!;
                    var service = Assert.Single(Assert.Single(program.Roots).Children);
                    Assert.Equal(name, service.Name); Assert.Equal(name, service.SourceId);
                    var loop = Assert.Single(Assert.Single(service.Children).Children);
                    Assert.Equal("private-expression-" + name, loop.Properties["Expression"]);
                    var inventory = Assert.Single(candidate.DraftFlow.Migration.Artifacts, a => a.Kind == "Orchestration").Orchestration!;
                    var shape = Assert.Single(inventory.Shapes, shape => shape.Id == loop.Id);
                    Assert.Equal(shape.SourceLine, loop.SourceLine); Assert.Equal(shape.ParentId, loop.ParentId);
                    Assert.DoesNotContain("Expression", shape.References.Keys);
                }
                await Assert.ThrowsAsync<InvalidOperationException>(() => client.DownloadBizTalkControlFlowAsync(new(project, "not-a-candidate"), default));
            }
            finally { await app.StopAsync(); }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
