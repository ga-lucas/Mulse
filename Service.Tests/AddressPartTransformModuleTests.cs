using System.Text.Json.Nodes;
using Mulse.CompatibilityPack;
using Mulse.Modules.Abstractions;
using Mulse.Modules.Execution;

namespace Service.Tests;

public sealed class AddressPartTransformModuleTests
{
    private static FlowExecutionContext Context() => new("address", Guid.NewGuid().ToString("N"), new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero));
    private static async Task<string?> Run(string address, string format, string part, string? defaultPort = null)
    {
        var settings = new Dictionary<string, string> { ["format"] = format, ["part"] = part };
        if (defaultPort is not null) settings["defaultPort"] = defaultPort;
        var result = await new AddressPartTransformModule().InvokeAsync(Context(), [JsonValue.Create(address)], new() { Settings = settings }, default);
        return result?.GetValue<string>();
    }

    [Theory]
    [InlineData("ftp://ftp.example.test:2121/in/out/file.hl7", "Host", "ftp.example.test")]
    [InlineData("ftp://ftp.example.test:2121/in/out/file.hl7", "Port", "2121")]
    [InlineData("ftp://ftp.example.test:2121/in/out/file.hl7", "Directory", "/in/out")]
    [InlineData("ftp://ftp.example.test:2121/in/out/file.hl7", "FileName", "file.hl7")]
    [InlineData("ftp://ftp.example.test/file.hl7", "Directory", "/")]
    [InlineData("ftp://ftp.example.test/file.hl7", "Port", "21")]
    public async Task UrlPartsAreExtractedExactly(string address, string part, string expected)
        => Assert.Equal(expected, await Run(address, "Url", part, "21"));

    [Theory]
    [InlineData(@"C:\out\billing\file.txt", "Directory", @"C:\out\billing")]
    [InlineData(@"C:\out\billing\file.txt", "FileName", "file.txt")]
    [InlineData(@"\\server\share\file.txt", "Directory", @"\\server\share")]
    [InlineData("/var/out/file.txt", "FileName", "file.txt")]
    public async Task PathPartsSplitOnTheLastSeparator(string address, string part, string expected)
        => Assert.Equal(expected, await Run(address, "Path", part));

    [Theory]
    [InlineData("mailto:ops@example.test", "ops@example.test")]
    [InlineData("MAILTO:ops@example.test", "ops@example.test")]
    [InlineData("ops@example.test", "ops@example.test")]
    public async Task MailAddressStripsAnOptionalMailtoPrefix(string address, string expected)
        => Assert.Equal(expected, await Run(address, "Mail", "MailAddress"));

    [Theory]
    [InlineData("host/file", "Url", "Host")]
    [InlineData("ftp://host", "Url", "Host")]
    [InlineData("ftp://user@host/file", "Url", "Host")]
    [InlineData("ftp://host/dir/", "Url", "FileName")]
    [InlineData("ftp://host/file", "Url", "Port")]
    [InlineData("ftp://host:99999/file", "Url", "Port")]
    [InlineData("ftp://host:/file", "Url", "Port")]
    [InlineData("nofile", "Path", "Directory")]
    [InlineData(@"C:\dir\", "Path", "FileName")]
    [InlineData("ftp://host/file", "Url", "MailAddress")]
    [InlineData("ops@example.test", "Mail", "Host")]
    [InlineData("mailto:", "Mail", "MailAddress")]
    [InlineData("ftp://host/file", "Other", "Host")]
    public async Task InvalidOrMismatchedInputFailsExplicitly(string address, string format, string part)
        => await Assert.ThrowsAsync<ArgumentException>(() => Run(address, format, part));

    [Fact]
    public async Task InputsAndSettingsAreRequiredAndCancellationIsHonored()
    {
        var module = new AddressPartTransformModule();
        var step = new ModuleStepDefinition { Settings = new() { ["format"] = "Url", ["part"] = "Host" } };
        await Assert.ThrowsAsync<ArgumentException>(() => module.InvokeAsync(Context(), [], step, default));
        await Assert.ThrowsAsync<ArgumentException>(() => module.InvokeAsync(Context(), [JsonValue.Create(5)], step, default));
        await Assert.ThrowsAsync<ArgumentException>(() => module.InvokeAsync(Context(), [JsonValue.Create("ftp://h/f")], new(), default));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => module.InvokeAsync(Context(), [JsonValue.Create("ftp://h/f")], step, new CancellationToken(true)));
    }
}
