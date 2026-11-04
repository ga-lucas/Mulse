using System.Text;
using Mulse.SamplePlugin;

namespace Service.Tests;

public sealed class TextBatchRenderTests
{
    private static readonly FlowExecutionContext Context = new("batch", "test", DateTimeOffset.UtcNow);
    private static IntegrationPayload Payload(string text, string request = "same") => new("input.txt", BinaryData.FromString(text), "text/plain",
        new Dictionary<string, string> { ["group"] = "A", ["requestId"] = request });

    [Fact]
    public async Task CombinesLiteralHeaderBodiesAndTrailer_KeepsOnlySharedMetadata_WithoutMutatingInputs()
    {
        var first = Payload("one", "1"); var second = Payload("two", "2");
        var module = new TextBatchRenderModule();
        var result = Assert.Single((await module.RenderAsync(Context, new([first, second]), new() { Settings = new()
        {
            ["header"] = "HEADER\r\n", ["separator"] = "|", ["trailer"] = "\r\nTRAILER", ["outputFileName"] = "client.txt"
        } }, default)).Payloads);
        Assert.Equal("HEADER\r\none|two\r\nTRAILER", result.GetText());
        Assert.Equal("client.txt", result.Name);
        Assert.Equal("text/plain; charset=utf-8", result.ContentType);
        Assert.Equal("A", result.Metadata["group"]);
        Assert.Equal("2", result.Metadata["batch.count"]);
        Assert.False(result.Metadata.ContainsKey("requestId"));
        Assert.Equal("one", first.GetText()); Assert.Equal("1", first.Metadata["requestId"]);
        Assert.False(first.Metadata.ContainsKey("batch.count"));
        var repeated = Assert.Single((await module.RenderAsync(Context, new([first, first]), new(), default)).Payloads);
        Assert.Equal("one\none", repeated.GetText());
        Assert.Equal("input.batch.txt", repeated.Name);
        Assert.Empty((await module.RenderAsync(Context, IntegrationBatch.Empty, new(), default)).Payloads);
    }

    [Fact]
    public async Task ExactOutputLimitIncludingHeaderSeparatorAndTrailerIsAccepted_ThenOneMoreCharacterFails()
    {
        var module = new TextBatchRenderModule();
        var step = new ModuleStepDefinition { Settings = new() { ["header"] = "H", ["separator"] = "|", ["trailer"] = "T" } };
        var text = new string('a', 3999996);
        var output = Assert.Single((await module.RenderAsync(Context, new([Payload(text), Payload("b")]), step, default)).Payloads);
        Assert.Equal(4000000, output.GetText().Length);
        await Assert.ThrowsAsync<ArgumentException>(() => module.RenderAsync(Context, new([Payload(text), Payload("bb")]), step, default));
        step.Settings["header"] = new string('h', 65536);
        step.Settings["trailer"] = new string('t', 65536);
        step.Settings["separator"] = new string('s', 65536);
        Assert.Equal(196610, Assert.Single((await module.RenderAsync(Context, new([Payload("a"), Payload("b")]), step, default)).Payloads).GetText().Length);
    }

    [Theory]
    [InlineData("header")]
    [InlineData("separator")]
    [InlineData("trailer")]
    public async Task LiteralSettingLimitsFailExplicitly(string key)
        => await Assert.ThrowsAsync<ArgumentException>(() => new TextBatchRenderModule().RenderAsync(Context, new([Payload("a")]), new()
        {
            Settings = new() { [key] = new string('x', 65537) }
        }, default));

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData(".")]
    [InlineData("..")]
    public async Task OutputNameMustBeASingleFilename(string name)
        => await Assert.ThrowsAsync<ArgumentException>(() => new TextBatchRenderModule().RenderAsync(Context, new([Payload("a")]), new()
        {
            Settings = new() { ["outputFileName"] = name }
        }, default));

    [Fact]
    public async Task InvalidUtf8AndCancellationAreNotSilentlyReplaced()
    {
        var invalid = Payload("") with { Content = BinaryData.FromBytes(new byte[] { 0xc3, 0x28 }) };
        await Assert.ThrowsAsync<DecoderFallbackException>(() => new TextBatchRenderModule().RenderAsync(Context, new([invalid]), new(), default));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new TextBatchRenderModule().RenderAsync(Context, new([Payload("a")]), new(), new CancellationToken(true)));
    }
}
