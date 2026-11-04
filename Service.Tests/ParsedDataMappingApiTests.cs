using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mulse.Excel;
using Mulse.Modules.Abstractions;
using Mulse.Modules.BuiltIn.Parse;
using Mulse.Modules.BuiltIn.Transform;
using Mulse.Modules.Mapping;
using Mulse.Ui.Models;
using Mulse.Ui.Services;
using Service.Endpoints;
using Service.Middleware;

namespace Service.Tests;

public sealed class ParsedDataMappingApiTests
{
    [Fact]
    public async Task DiscoveryAndPreview_ParseSuppliedSamplesOnlyAndShareRuntimeSemantics()
    {
        var catalog = new SampleCatalog();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddProblemDetails();
        builder.Services.AddExceptionHandler<ApiExceptionHandler>();
        ApiTestValidation.Register(builder.Services);
        builder.Services.AddSingleton<IModuleCatalog>(catalog);
        builder.Services.AddSingleton<IModuleInvoker, ModuleInvoker>();
        builder.Services.AddSingleton<IFlowDesignService>(_ => throw new InvalidOperationException("Sample mapping must not create flows or run source fetching."));
        await using var app = builder.Build();
        app.UseExceptionHandler(); app.MapDesignerEndpoints();
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var http = new HttpClient { BaseAddress = new Uri(address) };
            var client = new MulseApiClient(http);
            var data = await client.InspectParsedDataAsync(new("[{\"id\":\"001\"},{\"id\":\"002\",\"extra\":true}]"), default);
            Assert.Contains("/extra", data.Fields);
            Assert.Equal(0, catalog.ParserCalls);
            var json = ParsedDataMappingTests.MapJson(new ParsedDataFieldMap { SourcePath = "/id", TargetPath = "/identifier" });
            var preview = await client.PreviewParsedDataMappingAsync(new(json, data.ParsedData), default);
            Assert.Equal(ParsedDataMapping.Map(json, data.ParsedData), preview.OutputJson);
            Assert.Equal(0, catalog.ParserCalls);
            var parsed = await client.InspectParsedDataAsync(new("[{\"id\":1}]", ParserModule: "json-parse"), default);
            Assert.Equal(1, parsed.RecordCount);
            Assert.Equal(1, catalog.Releases);
            var workbook = await new ExcelRenderModule().RenderAsync(ParsedDataMappingTests.Context,
                new([ParsedDataMappingTests.Payload("[{\"OrderId\":\"001\",\"Amount\":12.5}]")]),
                new ModuleStepDefinition { Settings = new() { ["sheetName"] = "Orders" } }, default);
            var sheet = await client.InspectParsedDataAsync(new("", ParserModule: "excel-parse", ParserSettings: new() { ["sheetName"] = "Orders" },
                BinaryContent: Convert.ToBase64String(workbook.Payloads[0].Content.ToArray()), FileName: "orders.xlsx"), default);
            Assert.Contains("/OrderId", sheet.Fields);
            Assert.Contains("001", sheet.ParsedData);
            Assert.Equal(2, catalog.Releases);
            Assert.Equal("Orders", catalog.LastSettings!["sheetName"]);
            var multi = await client.InspectParsedDataAsync(new("supplied sample", ParserModule: "multiple"), default);
            Assert.Equal(2, multi.Samples!.Count);
            Assert.Contains("/later", multi.Fields);
            Assert.Equal(2, multi.RecordCount);
            Assert.Equal("[{\"identifier\":2}]", (await client.PreviewParsedDataMappingAsync(new(json, multi.Samples[1]), default)).OutputJson);
            using var invalid = await http.PostAsJsonAsync("/api/flow-designer/parsed-mapping/preview", new ParsedDataPreviewRequestViewModel(json, "{}"));
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            Assert.Contains("missing", await invalid.Content.ReadAsStringAsync());
            using var dtd = await http.PostAsJsonAsync("/api/flow-designer/parsed-mapping/inspect", new ParsedDataSampleRequestViewModel("<!DOCTYPE x SYSTEM 'file:///not-read'><x/>"));
            Assert.Equal(HttpStatusCode.BadRequest, dtd.StatusCode);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.InspectParsedDataAsync(new("", ParserModule: "excel-parse", BinaryContent: "not base64"), default));
            Assert.Contains("base64", error.Message);
            var transformMap = ParsedDataMappingTests.MapJson(new ParsedDataFieldMap
            {
                Kind = "Transform", TargetPath = "/value", TransformModule = "concat-transform", TransformSettings = new() { ["separator"] = "-" },
                TransformInputs = [new() { SourcePath = "/id" }, new() { Kind = "Constant", ConstantJson = "\"mapped\"" }]
            });
            var parserCalls = catalog.ParserCalls;
            var releases = catalog.Releases;
            var transformed = await client.PreviewParsedDataMappingAsync(new(transformMap, data.ParsedData), default);
            Assert.Equal("[{\"value\":\"001-mapped\"},{\"value\":\"002-mapped\"}]", transformed.OutputJson);
            Assert.Equal(2, catalog.TransformCalls);
            Assert.Equal(releases + 2, catalog.Releases);
            Assert.Equal(parserCalls, catalog.ParserCalls);
            using var invalidTransform = await http.PostAsJsonAsync("/api/flow-designer/parsed-mapping/preview", new ParsedDataPreviewRequestViewModel(transformMap, "{}"));
            Assert.Equal(HttpStatusCode.BadRequest, invalidTransform.StatusCode);
            Assert.Equal(2, catalog.TransformCalls);
            var nested = ParsedDataMappingTests.MapJson(new ParsedDataFieldMap
            {
                Kind = "Array", SourcePath = "/items", TargetPath = "/rows", Fields = [new()
                {
                    SourcePath = "/name", TargetPath = "/label", Conditions = [new() { SourcePath = "/active", Operator = "Equals", ValueJson = "true" }],
                    Otherwise = "Constant", OtherwiseJson = "\"skipped\"",
                    TransformChain = [new()
                    {
                        Module = "concat-transform", Settings = new() { ["separator"] = "-" },
                        Inputs = [new() { Kind = "Previous" }, new() { SourceScope = "Root", SourcePath = "/id" }]
                    }, new() { Module = "concat-transform" }]
                }]
            });
            const string nestedInput = """{"id":"001","items":[{"name":"Ada","active":true},{"active":false}]}""";
            var nestedPreview = await client.PreviewParsedDataMappingAsync(new(nested, nestedInput), default);
            Assert.Equal("""[{"rows":[{"label":"Ada-001"},{"label":"skipped"}]}]""", nestedPreview.OutputJson);
            Assert.Equal(4, catalog.TransformCalls);
            Assert.Equal(releases + 4, catalog.Releases);
            Assert.Equal(parserCalls, catalog.ParserCalls);
            using var invalidArray = await http.PostAsJsonAsync("/api/flow-designer/parsed-mapping/preview", new ParsedDataPreviewRequestViewModel(nested, "{\"items\":1}"));
            Assert.Equal(HttpStatusCode.BadRequest, invalidArray.StatusCode);
            Assert.Contains("must be an array", await invalidArray.Content.ReadAsStringAsync());
        }
        finally { await app.StopAsync(); }
    }

    private sealed class SampleCatalog : IModuleCatalog
    {
        public int ParserCalls, TransformCalls, Releases;
        public Dictionary<string, string>? LastSettings;
        public ValueTask<ModuleLease<IParseModule>> LeaseParseAsync(string moduleId, CancellationToken cancellationToken)
        {
            IParseModule module = moduleId switch { "json-parse" => new JsonParseModule(), "excel-parse" => new ExcelParseModule(), "multiple" => new MultipleParser(), _ => throw new KeyNotFoundException(moduleId) };
            ParserCalls++;
            return ValueTask.FromResult(new ModuleLease<IParseModule>(new RecordingParser(module, this),
                new("sample", RuntimePackageSourceKind.BuiltIn, "", module.Descriptor), () => { Releases++; return ValueTask.CompletedTask; }));
        }
        private sealed class MultipleParser : IParseModule
        {
            public ModuleDescriptor Descriptor { get; } = new("multiple", "Multiple sample documents", ModuleKind.Parse, "Test parser");
            public Task<IntegrationBatch> ParseAsync(FlowExecutionContext context, IntegrationBatch batch, ModuleStepDefinition step, CancellationToken cancellationToken)
                => Task.FromResult(new IntegrationBatch([ParsedDataMappingTests.Payload("{\"id\":1}"), ParsedDataMappingTests.Payload("{\"id\":2,\"later\":true}")]));
        }
        private sealed class RecordingParser(IParseModule inner, SampleCatalog owner) : IParseModule
        {
            public ModuleDescriptor Descriptor => inner.Descriptor;
            public Task<IntegrationBatch> ParseAsync(FlowExecutionContext context, IntegrationBatch batch, ModuleStepDefinition step, CancellationToken cancellationToken)
            { owner.LastSettings = step.Settings; return inner.ParseAsync(context, batch, step, cancellationToken); }
        }
        public IReadOnlyList<ModuleCatalogEntry> GetAll() => throw new NotSupportedException();
        public IReadOnlyList<ModulePackageInfo> GetPackages() => throw new NotSupportedException();
        public IDisposable PinFlowModules(PipelineDefinition flow) => throw new NotSupportedException();
        public ValueTask<ModuleLease<IFetchModule>> LeaseFetchAsync(string id, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<ModuleLease<IOrchestrationAugmentModule>> LeaseOrchestrationAugmentAsync(string id, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<ModuleLease<IRenderModule>> LeaseRenderAsync(string id, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<ModuleLease<IDeliverModule>> LeaseDeliverAsync(string id, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<ModuleLease<ITransformModule>> LeaseTransformAsync(string id, CancellationToken ct)
        {
            if (id != "concat-transform") throw new KeyNotFoundException(id);
            TransformCalls++;
            var module = new ConcatTransformModule();
            return ValueTask.FromResult(new ModuleLease<ITransformModule>(module,
                new("sample", RuntimePackageSourceKind.BuiltIn, "", module.Descriptor), () => { Releases++; return ValueTask.CompletedTask; }));
        }
        public Task SynchronizeAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<ModulePackageInfo> UpsertManagedPackageAsync(ManagedModulePackageDefinition package, CancellationToken ct) => throw new NotSupportedException();
        public Task<ModulePackageInfo> ReloadPackageAsync(string id, CancellationToken ct) => throw new NotSupportedException();
        public Task RemoveManagedPackageAsync(string id, CancellationToken ct) => throw new NotSupportedException();
    }
}
