using System.Text.Json.Nodes;
using Mulse.SamplePlugin;

namespace Service.Tests;

public sealed class RegexReplaceTransformTests
{
    private static readonly FlowExecutionContext Context = new("regex", "test", DateTimeOffset.UtcNow);
    private static ModuleStepDefinition Step(string pattern, string replacement = "") => new()
    {
        Settings = new() { ["pattern"] = pattern, ["replacement"] = replacement }
    };

    [Fact]
    public async Task ReplacementIsLiteral_AndDoesNotInterpretCaptureSyntax()
    {
        var result = await new RegexReplaceTransformModule().InvokeAsync(Context, [JsonValue.Create("a12b")], Step("([0-9]+)", "$1"), default);
        Assert.Equal("a$1b", result!.GetValue<string>());
    }

    [Fact]
    public async Task ExactInputPatternReplacementAndOutputLimitsAreAccepted()
    {
        var module = new RegexReplaceTransformModule();
        var atInputLimit = new string('a', 1000000);
        var unchanged = await module.InvokeAsync(Context, [JsonValue.Create(atInputLimit)], Step("z"), default);
        Assert.Equal(atInputLimit, unchanged!.GetValue<string>());
        var atPatternLimit = new string('a', 4096);
        var atReplacementLimit = new string('b', 4096);
        var replaced = await module.InvokeAsync(Context, [JsonValue.Create(atPatternLimit)], Step(atPatternLimit, atReplacementLimit), default);
        Assert.Equal(atReplacementLimit, replaced!.GetValue<string>());
        var expanded = await module.InvokeAsync(Context, [JsonValue.Create(new string('a', 1000))], Step("a", new string('b', 4000)), default);
        Assert.Equal(4000000, expanded!.GetValue<string>().Length);
    }

    [Theory]
    [InlineData("input")]
    [InlineData("pattern")]
    [InlineData("replacement")]
    [InlineData("output")]
    public async Task ExceedingEachLimitFailsExplicitly(string limit)
    {
        var input = limit == "input" ? new string('a', 1000001) : limit == "output" ? new string('a', 1001) : "a";
        var pattern = limit == "pattern" ? new string('a', 4097) : "a";
        var replacement = limit == "replacement" ? new string('b', 4097) : limit == "output" ? new string('b', 4000) : "";
        await Assert.ThrowsAsync<ArgumentException>(() => new RegexReplaceTransformModule().InvokeAsync(Context, [JsonValue.Create(input)], Step(pattern, replacement), default));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[null]")]
    [InlineData("[1]")]
    [InlineData("[{}]")]
    [InlineData("[\"a\",\"b\"]")]
    public async Task InvalidInputShapesFailExplicitly(string input)
    {
        var inputs = JsonNode.Parse(input)!.AsArray().ToArray();
        await Assert.ThrowsAsync<ArgumentException>(() => new RegexReplaceTransformModule().InvokeAsync(Context, inputs, Step("a"), default));
    }

    [Fact]
    public async Task CancellationIsObserved_AndMalformedPatternsAreNotHidden()
    {
        var module = new RegexReplaceTransformModule();
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => module.InvokeAsync(Context, [JsonValue.Create("a")], Step("a"), cancellation.Token));
        await Assert.ThrowsAsync<System.Text.RegularExpressions.RegexParseException>(() => module.InvokeAsync(Context, [JsonValue.Create("a")], Step("["), default));
    }
}
