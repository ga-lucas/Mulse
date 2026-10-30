using System.Xml.Linq;
using Mulse.CompatibilityPack;
using Mulse.Modules.Mapping;

namespace Service.Tests;

public sealed class XmlMappingOperationTests
{
    [Theory]
    [InlineData(XmlStringOperationKind.NormalizeWhitespace, " \t a\r\n b  ", null, "a b")]
    [InlineData(XmlStringOperationKind.NormalizeWhitespace, "\u00a0 a \u00a0", null, "\u00a0 a \u00a0")]
    [InlineData(XmlStringOperationKind.UppercaseAscii, "abz é ß İ", null, "ABZ é ß İ")]
    [InlineData(XmlStringOperationKind.LowercaseAscii, "ABZ É ß İ", null, "abz É ß İ")]
    [InlineData(XmlStringOperationKind.Prefix, "value", "<&>{'\"}", "<&>{'\"}value")]
    [InlineData(XmlStringOperationKind.Suffix, "value", "<&>{'\"}", "value<&>{'\"}")]
    [InlineData(XmlStringOperationKind.Prefix, "", "prefix", "prefix")]
    [InlineData(XmlStringOperationKind.Suffix, "value", "", "value")]
    public void Operations_HaveExplicitStringSemantics(XmlStringOperationKind kind, string input, string? argument, string expected)
    {
        var engine = Engine(new() { SourcePath = "/Input/Value", TargetPath = "Value", Operations = [new() { Kind = kind, Argument = argument }] });
        var output = engine.Transform(new XElement("Input", new XElement("Value", input)).ToString(SaveOptions.DisableFormatting));
        Assert.Equal(expected, XDocument.Parse(output).Root!.Element("Value")!.Value);
    }

    [Fact]
    public void Chains_PreserveOrderAndApplyIndependentlyToElementsAttributesAndConstants()
    {
        XmlStringOperation[] operations = [new() { Kind = XmlStringOperationKind.NormalizeWhitespace }, new() { Kind = XmlStringOperationKind.Prefix, Argument = "id:" },
            new() { Kind = XmlStringOperationKind.UppercaseAscii }, new() { Kind = XmlStringOperationKind.Suffix, Argument = "x" }];
        var engine = Engine(new() { SourcePath = "/Input/Value", TargetPath = "Nested/Value", Operations = operations },
            new() { Constant = " a ", TargetPath = "@id", Operations = operations }, new() { Constant = "", TargetPath = "Other", Operations = [new() { Kind = XmlStringOperationKind.Prefix, Argument = "b" }] });
        var result = XDocument.Parse(engine.Transform("<Input><Value> a </Value></Input>"));
        Assert.Equal("ID:Ax", result.Root!.Element("Nested")!.Element("Value")!.Value);
        Assert.Equal("ID:Ax", result.Root.Attribute("id")!.Value);
        Assert.Equal("b", result.Root.Element("Other")!.Value);
    }

    [Fact]
    public void MissingValues_SelectDefaultBeforeOperationsAndOmitEntireAssignment()
    {
        XmlStringOperation[] operations = [new() { Kind = XmlStringOperationKind.Prefix, Argument = "id:" }];
        var engine = Engine(new() { SourcePath = "/Input/Value", TargetPath = "Default", MissingValueBehavior = MissingXmlValueBehavior.Default, DefaultValue = "fallback", Operations = operations },
            new XmlFieldMappingDefinition { SourcePath = "/Input/Value", TargetPath = "Omit", MissingValueBehavior = MissingXmlValueBehavior.Omit, Operations = operations });
        var missing = XDocument.Parse(engine.Transform("<Input/>"));
        Assert.Equal("id:fallback", missing.Root!.Element("Default")!.Value);
        Assert.Null(missing.Root.Element("Omit"));
        var empty = XDocument.Parse(engine.Transform("<Input><Value/></Input>"));
        Assert.Equal("id:", empty.Root!.Element("Default")!.Value);
        Assert.Equal("id:", empty.Root.Element("Omit")!.Value);
    }

    [Theory]
    [InlineData("<Input/>")]
    [InlineData("<Input><Value>a</Value><Value>b</Value></Input>")]
    [InlineData("<Input><Value><Child/></Value></Input>")]
    public void Operations_DoNotHideScalarContractFailures(string input)
        => Assert.Throws<ArgumentException>(() => Engine(new() { SourcePath = "/Input/Value", TargetPath = "Value", Operations = [new()] }).Transform(input));

    [Theory]
    [InlineData("{\"kind\":\"Unknown\"}")]
    [InlineData("{\"kind\":99}")]
    [InlineData("{\"kind\":\"Prefix\"}")]
    [InlineData("{\"kind\":\"NormalizeWhitespace\",\"argument\":\"ignored\"}")]
    [InlineData("{\"kind\":\"Suffix\",\"futureProperty\":42}")]
    [InlineData("null")]
    public void UnknownOrInvalidOperationDefinitions_FailExplicitly(string operation)
    {
        var json = "{\"version\":2,\"fields\":[{\"targetPath\":\"Value\",\"constant\":\"x\",\"operations\":[" + operation + "]}]}";
        Assert.Throws<ArgumentException>(() => new XmlMappingEngine(json));
    }

    [Fact]
    public void VersionAndBounds_AreEnforcedAtExactThresholds()
    {
        var operations = Enumerable.Repeat(new XmlStringOperation(), 16).ToArray();
        var field = new XmlFieldMappingDefinition { Constant = "a", TargetPath = "Value", Operations = operations };
        Assert.Contains("a", Engine(field).Transform("<Input/>"));
        Assert.Throws<ArgumentException>(() => Engine(field with { Operations = [.. operations, new()] }));
        Assert.Throws<ArgumentException>(() => new XmlMappingEngine(XmlMappingTests.Json(new() { Fields = [field] })));
        Assert.Throws<ArgumentException>(() => Engine(field with { Operations = null! }));
        Assert.Contains(new string('x', 4096), Engine(field with { Operations = [new() { Kind = XmlStringOperationKind.Prefix, Argument = new string('x', 4096) }] }).Transform("<Input/>"));
        Assert.Throws<ArgumentException>(() => Engine(field with { Operations = [new() { Kind = XmlStringOperationKind.Prefix, Argument = new string('x', 4097) }] }));
    }

    [Fact]
    public async Task ActualRender_UsesOperationsAndHonorsCancellation()
    {
        var json = XmlMappingTests.Json(new() { Version = 2, Fields = [new() { SourcePath = "/Input/Value", TargetPath = "Value", Operations = [new() { Kind = XmlStringOperationKind.UppercaseAscii }] }] });
        var batch = new IntegrationBatch([new IntegrationPayload("sample.xml", BinaryData.FromString("<Input><Value>abc</Value></Input>"), "application/xml", new Dictionary<string, string>())]);
        var step = new ModuleStepDefinition { Module = "xml-mapping-render", Settings = new() { ["mappingJson"] = json } };
        var module = new XmlMappingRenderModule();
        var context = new FlowExecutionContext("test", "execution", DateTimeOffset.UtcNow);
        var rendered = await module.RenderAsync(context, batch, step, default);
        Assert.Equal(new XmlMappingEngine(json).Transform(batch.Payloads.Single().GetText()), rendered.Payloads.Single().GetText());
        Assert.Contains("ABC", rendered.Payloads.Single().GetText());
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => module.RenderAsync(context, batch, step, cancelled.Token));
    }

    private static XmlMappingEngine Engine(XmlFieldMappingDefinition first, params XmlFieldMappingDefinition[] remaining)
        => new(XmlMappingTests.Json(new() { Version = 2, Fields = [first, .. remaining] }));
}
