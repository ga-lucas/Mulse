using Mulse.CompatibilityPack;
using Mulse.Hl7;
using Mulse.Modules.Abstractions;
using Mulse.Modules.Execution;
using Mulse.Modules.Payloads;
using Mulse.Modules.Pipeline;

namespace Service.Tests;

public sealed class MigrationHelperModuleTests
{
    private const string Key = "{http://schemas.microsoft.com/BizTalk/2003/system-properties}MessageType";
    private static readonly FlowExecutionContext Ctx = new("migration", "run", DateTimeOffset.UnixEpoch);

    private static IntegrationBatch Batch(string body) => new([new("a.xml", BinaryData.FromString(body), "application/xml", new Dictionary<string, string> { ["keep"] = "1" })]);

    private static ModuleStepDefinition Step(string id, Dictionary<string, string> settings) => new() { Module = id, Settings = settings };

    [Fact]
    public async Task Hl7MessageTypePromotesRootWithDefaultNamespaceAndKey()
    {
        var module = new Hl7MessageTypeAugmentModule();
        var result = await module.AugmentAsync(Ctx, Batch("<ns0:ADT_A01 xmlns:ns0=\"urn:x\"/>"), Step(module.Descriptor.Id, []), default);
        var payload = Assert.Single(result.Payloads);
        Assert.Equal("http://microsoft.com/HealthCare/HL7/2X#ADT_A01", payload.Metadata[Key]);
        Assert.Equal("1", payload.Metadata["keep"]);
    }

    [Fact]
    public async Task Hl7MessageTypeRejectsNonXml()
    {
        var module = new Hl7MessageTypeAugmentModule();
        await Assert.ThrowsAsync<ArgumentException>(() => module.AugmentAsync(Ctx, Batch("MSH|^~"), Step(module.Descriptor.Id, []), default));
    }

    [Fact]
    public async Task SetPropertyWritesNamespacedKeyOnlyWhenAllValuesPresent()
    {
        var module = new SetContextPropertyAugmentModule();
        var full = await module.AugmentAsync(Ctx, Batch("<r/>"), Step(module.Descriptor.Id,
            new() { ["propertyName"] = "FileName", ["propertyNamespace"] = "urn:mime", ["propertyValue"] = "x.xml" }), default);
        Assert.Equal("x.xml", full.Payloads[0].Metadata["{urn:mime}FileName"]);

        var partial = await module.AugmentAsync(Ctx, Batch("<r/>"), Step(module.Descriptor.Id,
            new() { ["propertyName"] = "FileName", ["propertyNamespace"] = "", ["propertyValue"] = "x.xml" }), default);
        Assert.Single(partial.Payloads[0].Metadata);
    }

    [Fact]
    public void GenericMigrationModulesDoNotCarryBillingIdentities()
    {
        var ids = new[]
        {
            new Hl7MessageTypeAugmentModule().Descriptor.Id, new SetContextPropertyAugmentModule().Descriptor.Id,
            new FilenameTemplateTransformModule().Descriptor.Id, new FilenameTemplateAugmentModule().Descriptor.Id, new XmlRemoveNamespacesRenderModule().Descriptor.Id
        };
        Assert.All(ids, id => Assert.DoesNotContain("billing", id, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(ids.Length, ids.Distinct().Count());
    }
}
