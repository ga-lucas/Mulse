using Mulse.Billing;
using Mulse.Modules.ControlFlow;
using Mulse.Modules.Execution;
using Mulse.Modules.Payloads;
using Mulse.Modules.Pipeline;

namespace Service.Tests;

public sealed class BillingContextProjectionTests
{
    private static IntegrationBatch Input(string section, string element, string value) => new([new("billing.xml", BinaryData.FromString($"<b:Billing xmlns:b='{BillingFieldAugmentModule.BillingNamespace}'><{section}><{element}>{value}</{element}></{section}></b:Billing>"), "application/xml", new Dictionary<string, string>(StringComparer.Ordinal) { ["keep"] = "original" })]);
    private static FlowExecutionContext Context() => new("billing", "context", DateTimeOffset.UtcNow);

    [Theory]
    [InlineData("CaseID", "Details", "CaseId")]
    [InlineData("ScanListID", "Details", "ScanListId")]
    [InlineData("EncounterID", "Details", "EncounterId")]
    [InlineData("UpdateMethod", "Details", "UpdateMethod")]
    [InlineData("CaseNumber", "Details", "CaseNumber")]
    [InlineData("DepartmentID", "ConfigurationInfo", "DepartmentID")]
    [InlineData("DepartmentName", "ConfigurationInfo", "DepartmentName")]
    [InlineData("TransportType", "ConfigurationInfo", "TransportType")]
    [InlineData("TargetLocation", "ConfigurationInfo", "TargetLocation")]
    [InlineData("BatchTime", "ConfigurationInfo", "BatchTime")]
    [InlineData("BatchGroup", "ConfigurationInfo", "BatchGroup")]
    [InlineData("IsBatch", "ConfigurationInfo", "IsBatch")]
    [InlineData("GroupID", "ConfigurationInfo", "GroupID")]
    public async Task ExplicitProjectionUsesDeclaredPropertyIdentityAndLeavesSourceUnchanged(string field, string section, string element)
    {
        var input = Input(section, element, field == "IsBatch" ? " 1 " : "  exact  ");
        var source = input.Payloads[0];
        var key = "{http://Shs.SpaceTrax2.Interfaces.Core.Schemas.Properties." + (section == "Details" ? "BillingProperties" : "CoreProperties") + "}" + field;
        var output = Assert.Single((await new BillingFieldAugmentModule().AugmentAsync(Context(), input, new() { Settings = new() { ["field"] = field, ["outputIdentity"] = "SourceProperty" } }, default)).Payloads);
        var fact = new NativeMessageFact { Kind = field == "IsBatch" ? NativeMessageFactKind.MetadataBoolean : NativeMessageFactKind.Metadata, MetadataKey = key };
        Assert.Equal(field == "IsBatch" ? "true" : "  exact  ", fact.Read(new([output])).Value);
        Assert.Equal(source.Content, output.Content); Assert.Equal(source.Name, output.Name); Assert.Equal("original", output.Metadata["keep"]);
        Assert.Single(source.Metadata); Assert.Equal(2, output.Metadata.Count);
    }

    [Theory]
    [InlineData("True")]
    [InlineData("yes")]
    [InlineData("")]
    public async Task ContextBooleanProjectionRejectsInvalidLexicalValuesButCustomModeRemainsText(string text)
    {
        var module = new BillingFieldAugmentModule();
        var input = Input("ConfigurationInfo", "IsBatch", text);
        await Assert.ThrowsAsync<ArgumentException>(() => module.AugmentAsync(Context(), input, new() { Settings = new() { ["field"] = "IsBatch", ["outputIdentity"] = "SourceProperty" } }, default));
        var output = Assert.Single((await module.AugmentAsync(Context(), input, new() { Settings = new() { ["field"] = "IsBatch", ["outputKey"] = "custom" } }, default)).Payloads);
        Assert.Equal(text, output.Metadata["custom"]);
    }

    [Fact]
    public async Task CredentialFieldsUnknownIdentityAndMissingCustomKeyFailEvenForEmptyInput()
    {
        var module = new BillingFieldAugmentModule();
        foreach (var field in new[] { "UserName", "Password", "CertificateName" })
            await Assert.ThrowsAsync<ArgumentException>(() => module.AugmentAsync(Context(), IntegrationBatch.Empty, new() { Settings = new() { ["field"] = field, ["outputIdentity"] = "SourceProperty" } }, default));
        await Assert.ThrowsAsync<ArgumentException>(() => module.AugmentAsync(Context(), IntegrationBatch.Empty, new() { Settings = new() { ["field"] = "TargetLocation", ["outputIdentity"] = "Guess" } }, default));
        await Assert.ThrowsAsync<ArgumentException>(() => module.AugmentAsync(Context(), IntegrationBatch.Empty, new() { Settings = new() { ["field"] = "TargetLocation" } }, default));
    }

    }
