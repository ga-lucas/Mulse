using System.Text;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Mulse.Modules.BuiltIn.Augment;

namespace Service.Tests;

public sealed class XmlBatchEnvelopeTests
{
    private static IntegrationPayload Payload(string xml, string client = "A", string unique = "x") => new("input.xml", BinaryData.FromString(xml), "application/xml",
        new Dictionary<string, string> { ["rules.GroupID"] = client, ["unique"] = unique });
    private static Task<IntegrationBatch> Wrap(IntegrationBatch batch, ModuleStepDefinition? step = null, CancellationToken ct = default)
        => new XmlBatchEnvelopeAugmentModule().AugmentAsync(new("test", "run", DateTimeOffset.UtcNow), batch, step ?? new(), ct);

    [Fact]
    public async Task EnvelopePreservesOrderedDocumentRootsNamespacesAndSharedFacts_AndExposesExactPayloadCount()
    {
        var first = Payload("<?xml version='1.0'?><Invoice xmlns='urn:original'><Id>1</Id></Invoice>", unique: "first");
        var second = Payload("<Invoice xmlns='urn:original'><Id>2</Id></Invoice>", unique: "second");
        var output = Assert.Single((await Wrap(new([first, second]))).Payloads);
        Assert.Equal("batch.xml", output.Name); Assert.Equal("application/xml", output.ContentType);
        var xml = XDocument.Parse(output.GetText()).Root!; XNamespace ns = "urn:mulse:batch"; XNamespace original = "urn:original";
        Assert.Equal(ns + "Batch", xml.Name); Assert.Equal("2", xml.Element(ns + "MessageCount")!.Value);
        Assert.Equal(new[] { "1", "2" }, xml.Element(ns + "Messages")!.Elements(ns + "Message").Select(e => e.Element(original + "Invoice")!.Element(original + "Id")!.Value));
        Assert.Equal("2", output.Metadata["batch.count"]); Assert.Equal("A", output.Metadata["rules.GroupID"]); Assert.False(output.Metadata.ContainsKey("unique"));
        Assert.StartsWith("<?xml", first.GetText()); Assert.Equal("first", first.Metadata["unique"]);
    }

    [Fact]
    public async Task NamespaceAndRootAreUiSettings_AndXmlEncodingIsReadFromBytes()
    {
        var bytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("<?xml version='1.0' encoding='utf-16'?><Invoice>\u00e6</Invoice>")).ToArray();
        var payload = new IntegrationPayload("utf16.xml", BinaryData.FromBytes(bytes), "application/xml", new Dictionary<string, string>());
        var output = Assert.Single((await Wrap(new([payload]), new() { Settings = new() { ["namespace"] = "", ["rootName"] = "Invoices", ["outputFileName"] = "invoices.xml" } })).Payloads);
        var root = XDocument.Parse(output.GetText()).Root!;
        Assert.Equal("Invoices", root.Name.LocalName); Assert.Equal("", root.Name.NamespaceName); Assert.Equal("1", root.Element("MessageCount")!.Value);
        Assert.Equal("\u00e6", root.Element("Messages")!.Element("Message")!.Element("Invoice")!.Value); Assert.Equal("invoices.xml", output.Name);
    }

    [Theory]
    [InlineData("<!DOCTYPE Invoice [<!ENTITY x SYSTEM 'file:///not-read'>]><Invoice>&x;</Invoice>")]
    [InlineData("<Invoice/><Second/>")]
    [InlineData("not XML")]
    public async Task InvalidXmlAndExternalEntitiesFailInsteadOfProducingAPartialBatch(string input)
    {
        await Assert.ThrowsAsync<XmlException>(() => Wrap(new([Payload("<Invoice/>"), Payload(input)])));
    }

    [Theory]
    [InlineData("rootName", "bad:name")]
    [InlineData("outputFileName", "..\\batch.xml")]
    public async Task InvalidOutputSettingsAreExplicitErrors(string key, string value)
    {
        var step = new ModuleStepDefinition { Settings = new() { [key] = value } };
        if (key == "rootName") await Assert.ThrowsAsync<XmlException>(() => Wrap(new([Payload("<Invoice/>")]), step));
        else await Assert.ThrowsAsync<ArgumentException>(() => Wrap(new([Payload("<Invoice/>")]), step));
    }

    [Fact]
    public async Task EmptyBatchesStayEmpty_AndInputCountBytesAndCancellationAreBounded()
    {
        Assert.Empty((await Wrap(IntegrationBatch.Empty)).Payloads);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Wrap(new([Payload("<Invoice/>")]), ct: new CancellationToken(true)));
        await Assert.ThrowsAsync<ArgumentException>(() => Wrap(new(Enumerable.Repeat(Payload("<Invoice/>"), 10001).ToArray())));
        await Assert.ThrowsAsync<ArgumentException>(() => Wrap(new([new("large", BinaryData.FromBytes(new byte[16000001]), "application/xml", new Dictionary<string, string>())])));
    }

    [Fact]
    public async Task NativeCatalogExposesEnvelopeAndMetadataTimingDescriptorsForScratchBuiltFlows()
    {
        var registration = new ServiceCollection().AddLogging().AddMulsePlatform(new ConfigurationBuilder().Build(), AppContext.BaseDirectory);
        registration.AddSingleton<Microsoft.Extensions.Hosting.IHostEnvironment>(new FakeHostEnvironment());
        registration.AddSingleton<IRuntimeConfigurationStore>(new FakeRuntimeConfigurationStore(new()));
        await using var services = registration.BuildServiceProvider();
        var catalog = services.GetRequiredService<IModuleCatalog>();
        Assert.Contains(catalog.GetAll(), entry => entry.Descriptor.Id == "xml-batch-envelope-augment");
        var collector = catalog.GetAll().Single(entry => entry.Descriptor.Id == "durable-batch-augment").Descriptor;
        Assert.Contains(collector.Settings, setting => setting.Key == "batchTimeMetadataKey");
        Assert.Contains(collector.Settings, setting => setting.Key == "batchIntervalMetadataKey");
    }
}
