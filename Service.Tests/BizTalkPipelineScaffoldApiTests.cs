using System.IO.Compression;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mulse.Ui.Models;
using Mulse.Ui.Services;
using Service.BizTalkImport;
using Service.Endpoints;
using Service.Flows;
using Service.Middleware;

namespace Service.Tests;

public sealed class BizTalkPipelineScaffoldApiTests
{
    [Fact]
    public async Task PipelineScaffolds_AreDistinctAndDownloadableThroughTheTypedUiClientWithoutCreatingFlows()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mulse-pipeline-download-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            foreach (var subdirectory in new[] { "First", "Second" })
            {
                Directory.CreateDirectory(Path.Combine(directory, subdirectory));
                File.WriteAllText(Path.Combine(directory, subdirectory, "Send.btp"), """
                    <Document><Stages><Stage CategoryId="9d0e4107-4cce-4536-83fa-4a5040674ad6"><Components>
                      <Component><Name>Example.Assembler</Name><Properties><Property Name="Password"><Value>DO_NOT_COPY_VALUE</Value></Property></Properties></Component>
                    </Components></Stage></Stages></Document>
                    """);
            }
            var project = Path.Combine(directory, "Pipelines.btproj");
            File.WriteAllText(project, "<Project><ItemGroup><Pipeline Include='First\\Send.btp'/><Pipeline Include='Second\\Send.btp'/></ItemGroup></Project>");
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            builder.Services.AddProblemDetails();
            builder.Services.AddExceptionHandler<ApiExceptionHandler>();
            ApiTestValidation.Register(builder.Services);
            builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
            builder.Services.AddSingleton<IBizTalkImportService>(new BizTalkImportService());
            builder.Services.AddSingleton<IFlowDefinitionService>(_ => throw new InvalidOperationException("Scaffold downloads must not create or run a flow."));
            await using var app = builder.Build();
            app.UseExceptionHandler();
            app.MapBizTalkMigrationEndpoints();
            await app.StartAsync();
            try
            {
                var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
                using var http = new HttpClient { BaseAddress = new Uri(address) };
                var client = new MulseApiClient(http);
                var analysis = await client.AnalyzeBizTalkSolutionAsync(new(project), default);
                var candidate = Assert.Single(analysis.FlowCandidates);
                Assert.Equal(2, candidate.ScaffoldedModules.Count);
                Assert.Equal(2, candidate.ScaffoldedModules.Select(module => module.ModuleId).Distinct().Count());
                Assert.Equal(2, candidate.ScaffoldedModules.Select(module => module.FileName).Distinct().Count());
                Assert.False(candidate.DraftFlow.Enabled);
                var request = new CreateImportedBizTalkFlowRequestViewModel(project, candidate.SuggestedFlowId);
                foreach (var module in candidate.ScaffoldedModules)
                {
                    var download = await client.DownloadBizTalkScaffoldedModuleAsync(module.ModuleId, request, default);
                    Assert.Equal(module.FileName, download.FileName);
                    Assert.Equal(module.SourceCode, Encoding.UTF8.GetString(download.Content));
                    Assert.DoesNotContain("DO_NOT_COPY_VALUE", module.SourceCode);
                    Assert.Contains("throw new NotSupportedException", module.SourceCode);
                }
                var zip = await client.DownloadBizTalkScaffoldedModulesAsync(request, default);
                using var stream = new MemoryStream(zip.Content);
                using var archive = new ZipArchive(stream);
                Assert.Equal(2, archive.Entries.Count);
                foreach (var entry in archive.Entries)
                {
                    using var reader = new StreamReader(entry.Open());
                    Assert.Equal(candidate.ScaffoldedModules.Single(module => module.FileName == entry.FullName).SourceCode, await reader.ReadToEndAsync());
                }
                var unknown = await Assert.ThrowsAsync<InvalidOperationException>(() => client.DownloadBizTalkScaffoldedModuleAsync("missing-module", request, default));
                Assert.Contains("no scaffolded module", unknown.Message);
            }
            finally { await app.StopAsync(); }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
