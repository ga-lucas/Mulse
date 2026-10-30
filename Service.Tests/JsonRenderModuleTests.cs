using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Mulse.Excel;
using Mulse.Modules.BuiltIn.Deliver;
using Mulse.Modules.BuiltIn.Render;
using Mulse.Modules.Workflow;

namespace Service.Tests;

public sealed class JsonRenderModuleTests
{
    private static readonly FlowExecutionContext Context = new("json-tests", "run", DateTimeOffset.UtcNow);

    private static IntegrationPayload Payload(string json, string source = "rows")
        => new("rows.json", BinaryData.FromString(json), "application/json", new Dictionary<string, string>
        {
            ["sourceId"] = source, ["flowId"] = "json-tests", ["label"] = source
        });

    private static ModuleStepDefinition Step(bool combine = true, bool indented = false)
        => new() { Module = "json-render", Settings = new() { ["combinePayloads"] = combine.ToString(), ["indented"] = indented.ToString() } };

    [Fact]
    public async Task Default_PreservesSeparatePayloadsAndTheirMetadata()
    {
        var input = new IntegrationBatch([Payload("{\"id\":1}", "first"), Payload("[{\"id\":2}]", "second")]);
        var output = await new JsonRenderModule().RenderAsync(Context, input, new(), CancellationToken.None);
        Assert.Equal(2, output.Count);
        Assert.Equal("first", output.Payloads[0].Metadata["sourceId"]);
        Assert.Equal("second", output.Payloads[1].Metadata["sourceId"]);
        Assert.Equal("[{\"id\":2}]", output.Payloads[1].GetText());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Combined_FlattensOnlyTopLevelArraysAndPreservesOrderAndTypes(bool indented)
    {
        var input = new IntegrationBatch([
            Payload("{\"id\":1,\"children\":[1,2]}"), Payload("[{\"id\":2},[3,4],null]"), Payload("true"), Payload("[]")]);
        var result = await new JsonRenderModule().RenderAsync(Context, input, Step(indented: indented), CancellationToken.None);
        var payload = Assert.Single(result.Payloads);
        Assert.Equal("combined.json", payload.Name);
        Assert.Equal("application/json", payload.ContentType);
        using var document = JsonDocument.Parse(payload.Content);
        var rows = document.RootElement;
        Assert.Equal(5, rows.GetArrayLength());
        Assert.Equal(1, rows[0].GetProperty("id").GetInt32());
        Assert.Equal(2, rows[0].GetProperty("children").GetArrayLength());
        Assert.Equal(2, rows[1].GetProperty("id").GetInt32());
        Assert.Equal(JsonValueKind.Array, rows[2].ValueKind);
        Assert.Equal(JsonValueKind.Null, rows[3].ValueKind);
        Assert.True(rows[4].GetBoolean());
        Assert.Equal(indented, payload.GetText().Contains('\n'));
        Assert.Equal("rows", payload.Metadata["sourceId"]);
        Assert.Equal("json-render", payload.Metadata["renderedBy"]);
    }

    [Fact]
    public async Task Combined_KeepsOnlySharedMetadataWithoutMislabelingMixedSources()
    {
        var result = await new JsonRenderModule().RenderAsync(Context,
            new([Payload("{}", "excel"), Payload("{}", "sql")]), Step(), CancellationToken.None);
        var metadata = Assert.Single(result.Payloads).Metadata;
        Assert.Equal("json-tests", metadata["flowId"]);
        Assert.Equal("Json", metadata["renderedFormat"]);
        Assert.False(metadata.ContainsKey("sourceId"));
        Assert.False(metadata.ContainsKey("label"));
    }

    [Fact]
    public async Task Combined_EmptyBatchProducesNoOutputButEmptyArrayRemainsAnArray()
    {
        var module = new JsonRenderModule();
        Assert.Empty((await module.RenderAsync(Context, IntegrationBatch.Empty, Step(), CancellationToken.None)).Payloads);
        var result = await module.RenderAsync(Context, new([Payload("[]")]), Step(), CancellationToken.None);
        Assert.Equal("[]", Assert.Single(result.Payloads).GetText());
    }

    [Fact]
    public async Task Combined_InvalidJsonFailsRatherThanReturningPartialOutput()
        => await Assert.ThrowsAnyAsync<JsonException>(() => new JsonRenderModule().RenderAsync(Context,
            new([Payload("{}"), Payload("not json")]), Step(), CancellationToken.None));

    [Fact]
    public async Task Combined_RespectsCancellation()
        => await Assert.ThrowsAsync<OperationCanceledException>(() => new JsonRenderModule().RenderAsync(Context,
            new([Payload("{}")]), Step(), new CancellationToken(true)));

    [Fact]
    public async Task ExcelRows_JoinLookupResults_AndDeliverOneJsonArrayFile()
    {
        var excelRows = new IntegrationBatch([Payload("[{\"OrgId\":\"A\",\"Amount\":10},{\"OrgId\":\"A\",\"Amount\":20},{\"OrgId\":\"B\",\"Amount\":30}]")]);
        var workbook = await new ExcelRenderModule().RenderAsync(Context, excelRows, new(), CancellationToken.None);
        var parsedExcel = await new ExcelParseModule().ParseAsync(Context, workbook, new(), CancellationToken.None);
        var excel = Assert.Single(parsedExcel.Payloads);
        var taggedExcel = excel with { Metadata = new Dictionary<string, string>(excel.Metadata) { ["sourceId"] = "primary" } };
        var lookup = Payload("[{\"OrgId\":\"A\",\"OrgName\":\"Alpha\"}]", "lookup");
        await using var services = new ServiceCollection().AddSingleton<IModuleCatalog>(new FakeExecutableModuleCatalog()).BuildServiceProvider();
        var join = new MultiSourceJoinMapAugmentModule(new ModuleInvoker(services));
        var joined = await join.AugmentAsync(Context, new([taggedExcel, lookup]), new()
        {
            Settings = new()
            {
                ["leftSourceId"] = "primary", ["rightSourceId"] = "lookup", ["joinKind"] = "Left",
                ["joinKeysJson"] = "[{\"leftPath\":\"OrgId\",\"rightPath\":\"OrgId\"}]",
                ["mappingJson"] = "[{\"sourceKind\":\"Left\",\"sourcePath\":\"OrgId\",\"targetField\":\"OrgId\"},{\"sourceKind\":\"Left\",\"sourcePath\":\"Amount\",\"targetField\":\"Amount\"},{\"sourceKind\":\"Right\",\"sourcePath\":\"OrgName\",\"targetField\":\"OrgName\"}]"
            }
        }, CancellationToken.None);
        var rendered = await new JsonRenderModule().RenderAsync(Context, joined, Step(), CancellationToken.None);
        var directory = Path.Combine(Path.GetTempPath(), "mulse-json-" + Guid.NewGuid().ToString("N"));
        try
        {
            await new FileSystemDeliverModule(NullLogger<FileSystemDeliverModule>.Instance, TimeProvider.System)
                .DeliverAsync(Context, rendered, new() { Settings = new() { ["path"] = directory, ["namingMode"] = "Fixed", ["fileName"] = "enriched.json" } }, CancellationToken.None);
            var file = Assert.Single(Directory.GetFiles(directory));
            Assert.Equal("enriched.json", Path.GetFileName(file));
            using var output = JsonDocument.Parse(await File.ReadAllTextAsync(file));
            var rows = output.RootElement;
            Assert.Equal(3, rows.GetArrayLength());
            Assert.Equal("Alpha", rows[0].GetProperty("OrgName").GetString());
            Assert.Equal("Alpha", rows[1].GetProperty("OrgName").GetString());
            Assert.Equal(20, rows[1].GetProperty("Amount").GetInt32());
            Assert.Equal("B", rows[2].GetProperty("OrgId").GetString());
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
