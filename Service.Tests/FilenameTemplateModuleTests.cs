using System.Globalization;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Mulse.CompatibilityPack;
using Mulse.Modules.Abstractions;
using Mulse.Modules.Execution;
using Mulse.Modules.Payloads;
using Mulse.Modules.Pipeline;

namespace Service.Tests;

public sealed class FilenameTemplateModuleTests
{
    private static readonly DateTimeOffset Instant = new(2026, 11, 5, 23, 45, 12, TimeSpan.Zero);
    private static readonly Guid Identity = Guid.Parse("2ae6889b-0c99-491b-898d-ebc55ef9ab1d");
    private static Dictionary<string, string> EnvironmentSettings() => new() { ["timeZone"] = "UTC", ["culture"] = "Invariant" };
    private static FlowExecutionContext Context() => new("filename", Guid.NewGuid().ToString("N"), Instant);
    private static IntegrationBatch Input(string body = "<r />") => new([new("input.xml", BinaryData.FromString(body), "application/xml", new Dictionary<string, string>
        { ["group"] = "A", ["messageId"] = "1", ["channel"] = "input", ["keep"] = "original" })]);
    private static string Expand(string template) => FilenameTemplate.Expand(template, Instant, TimeZoneInfo.Utc, CultureInfo.InvariantCulture, Identity);

    [Theory]
    [InlineData("[DATE(yyyyMMdd)]", "20261105")]
    [InlineData("[DATETIME(yyyyMMdd_HHmmss)]", "20261105_234512")]
    [InlineData("[UTCDATETIME(yyyyMMdd_HHmmss)]", "20261105_234512")]
    [InlineData("[TIME(HHmm)]_[UTCTIME(HHmm)]", "2345_2345")]
    [InlineData("[DAY(0)]_[DAY(3)]_[DAY(7)]", "THURSDAY_THU_THURSDAY")]
    [InlineData("[GUID]_[GUID]", "2ae6889b-0c99-491b-898d-ebc55ef9ab1d_2ae6889b-0c99-491b-898d-ebc55ef9ab1d")]
    [InlineData("folder\\file.txt", "folder\\file.txt")]
    [InlineData("[datetime(yyyy)]", "[datetime(yyyy)]")]
    public void FilenameMacrosHaveExplicitSourceCompatibleSemantics(string template, string expected) => Assert.Equal(expected, Expand(template));

    [Fact]
    public void FilenameUsesExplicitZoneCultureAndSourceSecondsEpoch()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("SyntheticPlus2", TimeSpan.FromHours(2), "SyntheticPlus2", "SyntheticPlus2");
        Assert.Equal("20261106_0145_20261105_2345", FilenameTemplate.Expand("[DATETIME(yyyyMMdd_HHmm)]_[UTCDATETIME(yyyyMMdd_HHmm)]", Instant, zone, CultureInfo.InvariantCulture, Identity));
        Assert.Equal(Instant.UtcDateTime.Ticks.ToString(CultureInfo.InvariantCulture), Expand("[TICKS]"));
        Assert.Equal((Instant.UtcDateTime - new DateTime(2001, 1, 1)).TotalSeconds.ToString("0", CultureInfo.InvariantCulture), Expand("[SECONDS]"));
        Assert.Equal("novembre", FilenameTemplate.Expand("[DATE(MMMM)]", Instant, TimeZoneInfo.Utc, CultureInfo.GetCultureInfo("fr-FR"), Identity));
    }

    [Theory]
    [InlineData("[DAY(no)]")]
    [InlineData("[DAY(-1)]")]
    [InlineData("[DATE(yyyy/MM/dd)]")]
    [InlineData("[GUID(bad)]")]
    [InlineData("[DATE(yyyy)")]
    public void InvalidKnownFilenameTokensFailInsteadOfProducingSuccessShapedPlaceholders(string template)
        => Assert.Throws<ArgumentException>(() => Expand(template));

    [Fact]
    public async Task FilenameBoundariesInputTypesAndCancellationFailExplicitly()
    {
        Assert.Equal(4096, Expand(new string('x', 4096)).Length);
        Assert.Throws<ArgumentException>(() => Expand(new string('x', 4097)));
        Assert.Throws<ArgumentException>(() => Expand(new string('x', 4090) + "[GUID]"));
        var module = new FilenameTemplateTransformModule();
        await Assert.ThrowsAsync<ArgumentException>(() => module.InvokeAsync(Context(), [JsonValue.Create(12)], new() { Settings = EnvironmentSettings() }, default));
        await Assert.ThrowsAsync<ArgumentException>(() => module.InvokeAsync(Context(), [], new() { Settings = EnvironmentSettings() }, default));
        await Assert.ThrowsAsync<ArgumentException>(() => module.InvokeAsync(Context(), [JsonValue.Create("[DATE(yyyy)]")], new(), default));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => module.InvokeAsync(Context(), [JsonValue.Create("x")], new(), new CancellationToken(true)));
    }

    [Fact]
    public async Task FilenameAugmentPreservesContentAndRequiresTheExactMetadataKey()
    {
        var settings = EnvironmentSettings(); settings["source"] = "Metadata"; settings["template"] = "source"; settings["outputKey"] = "target";
        var payload = Input().Payloads[0];
        var metadata = new Dictionary<string, string>(payload.Metadata) { ["source"] = "file_[DATE(yyyyMMdd)].xml" };
        var original = payload with { Metadata = metadata };
        var module = new FilenameTemplateAugmentModule();
        var result = Assert.Single((await module.AugmentAsync(Context(), new([original]), new() { Settings = settings }, default)).Payloads);
        Assert.Equal("file_20261105.xml", result.Metadata["target"]); Assert.Equal(original.Content, result.Content); Assert.Equal(original.Name, result.Name);
        Assert.False(original.Metadata.ContainsKey("target"));
        settings["template"] = "Source";
        await Assert.ThrowsAsync<ArgumentException>(() => module.AugmentAsync(Context(), new([original]), new() { Settings = settings }, default));
    }

    [Fact]
    public async Task NamespaceRemovalPreservesValuesNodesAndMetadataButRejectsCollisions()
    {
        var module = new XmlRemoveNamespacesRenderModule();
        const string xml = "<?before keep?><a:r xmlns:a='urn:a' xmlns:b='urn:b' b:id='7'> before <b:i><![CDATA[value]]></b:i><!--keep--> after </a:r>";
        var input = Input(xml);
        var output = Assert.Single((await module.RenderAsync(Context(), input, new(), default)).Payloads);
        var document = XDocument.Parse(output.GetText(), LoadOptions.PreserveWhitespace);
        Assert.Equal("r", document.Root!.Name); Assert.Equal("7", document.Root.Attribute("id")!.Value);
        Assert.Equal(" before value after ", document.Root.Value);
        Assert.Contains("<?before keep?>", output.GetText()); Assert.Contains("<![CDATA[value]]>", output.GetText()); Assert.Contains("<!--keep-->", output.GetText());
        Assert.All(document.Descendants(), element => Assert.Empty(element.Name.NamespaceName));
        Assert.Equal("original", output.Metadata["keep"]); Assert.Equal(xml, input.Payloads[0].GetText());
        await Assert.ThrowsAsync<ArgumentException>(() => module.RenderAsync(Context(), Input("<r xmlns:a='urn:a' id='a' a:id='b'/>"), new(), default));
        await Assert.ThrowsAsync<ArgumentException>(() => module.RenderAsync(Context(), Input("<!DOCTYPE r [<!ENTITY x 'unsafe'>]><r>&x;</r>"), new(), default));
    }

    [Fact]
    public async Task EmptyBatchesStillValidateFilenameConfigurationAndModulesHonorCancellation()
    {
        var module = new FilenameTemplateAugmentModule();
        var settings = EnvironmentSettings(); settings["source"] = "Literal"; settings["template"] = "file.txt"; settings["outputKey"] = "target";
        Assert.Empty((await module.AugmentAsync(Context(), IntegrationBatch.Empty, new() { Settings = settings }, default)).Payloads);
        settings["culture"] = "not a culture";
        await Assert.ThrowsAsync<CultureNotFoundException>(() => module.AugmentAsync(Context(), IntegrationBatch.Empty, new() { Settings = settings }, default));
        settings["culture"] = "Invariant"; settings["timeZone"] = "Mulse-not-an-installed-zone";
        await Assert.ThrowsAsync<TimeZoneNotFoundException>(() => module.AugmentAsync(Context(), IntegrationBatch.Empty, new() { Settings = settings }, default));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => module.AugmentAsync(Context(), IntegrationBatch.Empty, new(), new CancellationToken(true)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new XmlRemoveNamespacesRenderModule().RenderAsync(Context(), IntegrationBatch.Empty, new(), new CancellationToken(true)));
    }
}
