using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mulse.CompatibilityPack;
using Mulse.Ui.Models;
using Mulse.Ui.Services;
using Service.Endpoints;
using Service.Middleware;

namespace Service.Tests;

public sealed class XmlMappingApiTests
{
    [Fact]
    public async Task UiPreviewClient_UsesRuntimeEngineAndSurfacesInvalidMappingsAndSamples()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddProblemDetails();
        builder.Services.AddExceptionHandler<ApiExceptionHandler>();
        ApiTestValidation.Register(builder.Services);
        builder.Services.AddSingleton<IFlowDesignService>(_ => throw new InvalidOperationException("Preview must not use flow creation or analysis."));
        await using var app = builder.Build();
        app.UseExceptionHandler();
        app.MapDesignerEndpoints();
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var http = new HttpClient { BaseAddress = new Uri(address) };
            var client = new MulseApiClient(http);
            var preview = await client.PreviewXmlMappingAsync(new(XmlMappingTests.Json(), XmlMappingTests.Sample), default);
            var module = new XmlMappingRenderModule();
            var rendered = await module.RenderAsync(new FlowExecutionContext("preview", "execution", DateTimeOffset.UtcNow),
                new IntegrationBatch([new IntegrationPayload("sample.xml", BinaryData.FromString(XmlMappingTests.Sample), "application/xml", new Dictionary<string, string>())]),
                new ModuleStepDefinition { Module = module.Descriptor.Id, Settings = new() { ["mappingJson"] = XmlMappingTests.Json() } }, default);
            Assert.Equal(Assert.Single(rendered.Payloads).GetText(), preview.OutputXml);
            Assert.Equal(new XmlMappingEngine(XmlMappingTests.Json()).Xslt, preview.Xslt);
            var operations = XmlMappingTests.Json(new Mulse.Modules.Mapping.XmlMappingDefinition
            {
                Version = 2, Fields = [new() { SourcePath = "/Input/Value", TargetPath = "Value", Operations = [new() { Kind = Mulse.Modules.Mapping.XmlStringOperationKind.UppercaseAscii }] }]
            });
            var operationPreview = await client.PreviewXmlMappingAsync(new(operations, "<Input><Value>abc</Value></Input>"), default);
            Assert.Equal(new XmlMappingEngine(operations).Transform("<Input><Value>abc</Value></Input>"), operationPreview.OutputXml);
            Assert.Contains("ABC", operationPreview.OutputXml);
            var invalidOperations = operations.Replace("UppercaseAscii", "UnknownOperation", StringComparison.Ordinal);
            Assert.Contains("Invalid XML mapping JSON", (await Assert.ThrowsAsync<InvalidOperationException>(() => client.PreviewXmlMappingAsync(new(invalidOperations, "<Input/>"), default))).Message);
            var tree = await client.AnalyzeXmlMappingSchemasAsync(new([NativeMapImportTests.SourceXsd]), default);
            Assert.Contains(tree.Roots.Single().Fields, field => field.Path == "/n1:Order/n1:Id");
            var proposal = await client.ImportNativeXmlMappingAsync(new(NativeMapImportTests.Map(), [NativeMapImportTests.SourceXsd], [NativeMapImportTests.TargetXsd]), default);
            Assert.Contains("tn1:Result", proposal.MappingJson);
            Assert.NotEmpty(proposal.Warnings);
            var conversionError = await Assert.ThrowsAsync<InvalidOperationException>(() => client.ImportNativeXmlMappingAsync(
                new(NativeMapImportTests.Map("Order/Item"), [NativeMapImportTests.SourceXsd], [NativeMapImportTests.TargetXsd]), default));
            Assert.Contains("non-repeating scalar", conversionError.Message);
            var schemaError = await Assert.ThrowsAsync<InvalidOperationException>(() => client.AnalyzeXmlMappingSchemasAsync(new(["<!DOCTYPE x SYSTEM 'file:///not-read'><x/>"]), default));
            Assert.Contains("DTD", schemaError.Message);
            var invalid = await Assert.ThrowsAsync<InvalidOperationException>(() => client.PreviewXmlMappingAsync(new(XmlMappingTests.Json(), "<wrong />"), default));
            Assert.Contains("exactly one scalar", invalid.Message);
            using var missing = await http.PostAsJsonAsync("/api/flow-designer/xml-mapping/preview", new XmlMappingPreviewRequestViewModel("", ""));
            Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
            using var unsafeXml = await http.PostAsJsonAsync("/api/flow-designer/xml-mapping/preview", new XmlMappingPreviewRequestViewModel(XmlMappingTests.Json(), "<!DOCTYPE x SYSTEM 'file:///not-read'><x/>"));
            Assert.Equal(HttpStatusCode.BadRequest, unsafeXml.StatusCode);
            Assert.Contains("DTD", await unsafeXml.Content.ReadAsStringAsync());
            using var future = await http.PostAsJsonAsync("/api/flow-designer/xml-mapping/preview", new XmlMappingPreviewRequestViewModel(XmlMappingTests.Json(XmlMappingTests.Map with { Version = 3 }), XmlMappingTests.Sample));
            Assert.Equal(HttpStatusCode.BadRequest, future.StatusCode);
        }
        finally { await app.StopAsync(); }
    }
}
