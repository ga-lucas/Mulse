using Microsoft.Extensions.Logging.Abstractions;
using Mulse.Modules;
using Mulse.Modules.BuiltIn.Deliver;

namespace Service.Tests;

public sealed class FileSystemDeliverTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mulse-deliver-" + Guid.NewGuid());
    private readonly FileSystemDeliverModule _module = new(NullLogger<FileSystemDeliverModule>.Instance, new FixedTimeProvider());

    private Task DeliverAsync(string? mode = null, string? name = null, bool overwrite = false, string? extension = null,
        string? dateFormat = null, params string[] contents)
    {
        var settings = new Dictionary<string, string> { ["path"] = _directory, ["overwriteExisting"] = overwrite.ToString() };
        if (mode is not null) settings["namingMode"] = mode;
        if (name is not null) settings["fileName"] = name;
        if (extension is not null) settings["extension"] = extension;
        if (dateFormat is not null) settings["dateFormat"] = dateFormat;
        var payloads = (contents.Length == 0 ? new[] { "payload" } : contents)
            .Select(content => new IntegrationPayload("orders.xlsx", BinaryData.FromString(content), "application/octet-stream", new Dictionary<string, string>())).ToArray();
        return _module.DeliverAsync(new("flow", "run", DateTimeOffset.UtcNow), new IntegrationBatch(payloads),
            new ModuleStepDefinition { Settings = settings }, CancellationToken.None);
    }

    [Fact]
    public async Task Default_KeepsOriginalExecutionBasedFilename()
    {
        await DeliverAsync();
        Assert.Equal("orders-run-20261029150000123-0.xlsx", Path.GetFileName(Assert.Single(Directory.GetFiles(_directory))));
    }

    [Fact]
    public async Task Numeric_SkipsExistingFilesAndContinuesAcrossBatchesWithoutOverwriting()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(Path.Combine(_directory, "orders-1.xlsx"), "existing");
        await DeliverAsync("Numeric", contents: ["two", "three"]);
        await DeliverAsync("Numeric", overwrite: true, contents: ["four"]);
        Assert.Equal("existing", await File.ReadAllTextAsync(Path.Combine(_directory, "orders-1.xlsx")));
        Assert.Equal("two", await File.ReadAllTextAsync(Path.Combine(_directory, "orders-2.xlsx")));
        Assert.Equal("three", await File.ReadAllTextAsync(Path.Combine(_directory, "orders-3.xlsx")));
        Assert.Equal("four", await File.ReadAllTextAsync(Path.Combine(_directory, "orders-4.xlsx")));
    }

    [Fact]
    public async Task Numeric_ConcurrentDeliveriesReserveDistinctNames()
    {
        await Task.WhenAll(Enumerable.Range(0, 20).Select(index => DeliverAsync("Numeric", "export.csv", contents: [index.ToString()])));
        var files = Directory.GetFiles(_directory);
        Assert.Equal(20, files.Length);
        Assert.Equal(20, (await Task.WhenAll(files.Select(file => File.ReadAllTextAsync(file)))).Distinct().Count());
        Assert.All(Enumerable.Range(1, 20), index => Assert.True(File.Exists(Path.Combine(_directory, $"export-{index}.csv"))));
    }

    [Fact]
    public async Task Random_HasDistinctHexSuffixAndPreservesExtension()
    {
        await DeliverAsync("Random", "export.xlsx", contents: ["one", "two"]);
        var files = Directory.GetFiles(_directory).Select(Path.GetFileName).ToArray();
        Assert.Equal(2, files.Distinct().Count());
        Assert.All(files, file => Assert.Matches("^export-[a-f0-9]{32}\\.xlsx$", file!));
    }

    [Fact]
    public async Task Fixed_DefaultRejectsCollisionAndOverwriteTruncatesExistingContent()
    {
        await DeliverAsync("Fixed", "report.xlsx", contents: ["long original content"]);
        await Assert.ThrowsAsync<IOException>(() => DeliverAsync("Fixed", "report.xlsx", contents: ["blocked"]));
        Assert.Equal("long original content", await File.ReadAllTextAsync(Path.Combine(_directory, "report.xlsx")));
        await DeliverAsync("Fixed", "report.xlsx", overwrite: true, contents: ["new"]);
        Assert.Equal("new", await File.ReadAllTextAsync(Path.Combine(_directory, "report.xlsx")));
    }

    [Fact]
    public async Task FixedDate_AppendsUtcDateBeforeExtensionAndHonorsCollisionPolicy()
    {
        await DeliverAsync("FixedDate", "report.xlsx", dateFormat: "yyyy-MM-dd");
        var path = Path.Combine(_directory, "report-2026-10-29.xlsx");
        Assert.True(File.Exists(path));
        await Assert.ThrowsAsync<IOException>(() => DeliverAsync("FixedDate", "report.xlsx", dateFormat: "yyyy-MM-dd"));
        await DeliverAsync("FixedDate", "report.xlsx", overwrite: true, dateFormat: "yyyy-MM-dd", contents: ["replacement"]);
        Assert.Equal("replacement", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task FixedDate_DefaultUsesTimestampBeforeInheritedExtension()
    {
        await DeliverAsync("FixedDate", "report");
        Assert.Equal("report-20261029-150000123.xlsx", Path.GetFileName(Assert.Single(Directory.GetFiles(_directory))));
    }

    [Fact]
    public async Task Fixed_MultiplePayloadsFollowExplicitOverwriteChoice()
    {
        await Assert.ThrowsAsync<IOException>(() => DeliverAsync("Fixed", "report.xlsx", contents: ["first", "second"]));
        Assert.Equal("first", await File.ReadAllTextAsync(Path.Combine(_directory, "report.xlsx")));
        await DeliverAsync("Fixed", "report.xlsx", overwrite: true, contents: ["third", "last"]);
        Assert.Equal("last", await File.ReadAllTextAsync(Path.Combine(_directory, "report.xlsx")));
    }

    [Theory]
    [InlineData("report", null, "report.xlsx")]
    [InlineData("report.csv", null, "report.csv")]
    [InlineData("report.csv", "xlsx", "report.xlsx")]
    [InlineData("report.xlsx", ".xls", "report.xls")]
    public async Task Fixed_ResolvesExtensionWithoutDoublingIt(string name, string? extension, string expected)
    {
        await DeliverAsync("Fixed", name, extension: extension);
        Assert.Equal(expected, Path.GetFileName(Assert.Single(Directory.GetFiles(_directory))));
    }

    [Theory]
    [InlineData("Unknown", null, null, null)]
    [InlineData("Fixed", null, null, null)]
    [InlineData("FixedDate", null, null, null)]
    [InlineData("Fixed", "..\\outside.xlsx", null, null)]
    [InlineData("Fixed", "../outside.xlsx", null, null)]
    [InlineData("Fixed", "C:\\outside.xlsx", null, null)]
    [InlineData("Fixed", "report.xlsx", "../escape", null)]
    [InlineData("FixedDate", "report.xlsx", null, "yyyy/MM/dd")]
    public async Task InvalidSettings_FailBeforeWriting(string mode, string? name, string? extension, string? dateFormat)
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(() => DeliverAsync(mode, name, extension: extension, dateFormat: dateFormat));
        Assert.False(Directory.Exists(_directory));
    }

    [Fact]
    public async Task CancelledDelivery_DoesNotCreateDirectory()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _module.DeliverAsync(new("flow", "run", DateTimeOffset.UtcNow),
            new IntegrationBatch([]), new ModuleStepDefinition { Settings = new() { ["path"] = _directory } }, cancellation.Token));
        Assert.False(Directory.Exists(_directory));
    }

    [Fact]
    public void Descriptor_ExposesNamingChoicesAndCollisionControls()
    {
        var naming = Assert.Single(_module.Descriptor.Settings, setting => setting.Key == "namingMode");
        Assert.Equal("Execution", naming.DefaultValue);
        Assert.Equal(["Execution", "Numeric", "Random", "Fixed", "FixedDate"], naming.Options!.Select(option => option.Value));
        Assert.Contains(_module.Descriptor.Settings, setting => setting.Key == "overwriteExisting" && setting.DefaultValue == "false");
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 29, 15, 0, 0, 123, TimeSpan.Zero);
    }
}
