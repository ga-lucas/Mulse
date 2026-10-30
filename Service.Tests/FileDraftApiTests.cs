using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mulse.Ui.Models;
using Mulse.Ui.Services;
using Service.Endpoints;
using Service.Middleware;

namespace Service.Tests;

public sealed class FileDraftApiTests
{
    [Fact]
    public async Task UiClient_AnalyzesBinaryWorkbookThroughValidatedEndpointWithoutLoadingPlugins()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddProblemDetails();
        builder.Services.AddExceptionHandler<ApiExceptionHandler>();
        ApiTestValidation.Register(builder.Services);
        builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        var catalog = FakeModuleCatalog.WithModules(("json-parse", ModuleKind.Parse));
        builder.Services.AddSingleton<IFlowDesignService>(new FlowDesignService(catalog, new FakeFlowDefinitionService()));
        await using var app = builder.Build();
        app.UseExceptionHandler();
        app.MapDesignerEndpoints();
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var http = new HttpClient { BaseAddress = new Uri(address) };
            var client = new MulseApiClient(http);
            using var stream = new MemoryStream();
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                zip.CreateEntry("xl/workbook.xml");
                zip.CreateEntry("[Content_Types].xml");
            }
            var request = new FlowDesignAnalysisRequestViewModel("sample.xlsx", "application/octet-stream", "", Convert.ToBase64String(stream.ToArray()));
            var response = await client.AnalyzeFlowDesignAsync(request, CancellationToken.None);
            Assert.Equal("Excel", response.DetectedFormat);
            Assert.Contains("Load Mulse.Excel", response.Preview);
            Assert.DoesNotContain(response.ParseModules, module => module.IsRecommended);
            using var invalid = await http.PostAsJsonAsync("/api/flow-designer/analyze", request with { BinaryContent = "invalid base64" });
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            var text = await client.AnalyzeFlowDesignAsync(new("sample.json", "application/json", "{\"id\":42}"), CancellationToken.None);
            Assert.Equal("Json", text.DetectedFormat);
        }
        finally { await app.StopAsync(); }
    }
}
