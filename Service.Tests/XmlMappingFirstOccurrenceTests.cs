using System.Xml.Linq;
using Mulse.CompatibilityPack;
using Mulse.Modules.Mapping;

namespace Service.Tests;

public sealed class XmlMappingFirstOccurrenceTests
{
    private static XmlMappingDefinition Map(MissingXmlValueBehavior behavior = MissingXmlValueBehavior.Error, string? defaultValue = null, int version = 2) => new()
    {
        Version = version, TargetRoot = "Output",
        Fields = [new() { SourcePath = "/Input/Item/Code", TargetPath = "Code", FirstOccurrence = true, MissingValueBehavior = behavior, DefaultValue = defaultValue }]
    };

    private static XmlMappingDefinition Single(XmlFieldMappingDefinition field) => new() { Version = 2, TargetRoot = "Output", Fields = [field] };

    private static XmlMappingEngine Engine(XmlMappingDefinition map) => new(XmlMappingTests.Json(map));

    [Fact]
    public void RepeatingSource_UsesTheFirstOccurrenceInDocumentOrder()
    {
        var output = XDocument.Parse(Engine(Map()).Transform("<Input><Item><Code>b</Code></Item><Item><Code>a</Code></Item></Input>")).Root!;
        Assert.Equal("b", output.Element("Code")!.Value);
    }

    [Fact]
    public void SingleOccurrence_StillMaps()
    {
        var output = XDocument.Parse(Engine(Map()).Transform("<Input><Item><Code>only</Code></Item></Input>")).Root!;
        Assert.Equal("only", output.Element("Code")!.Value);
    }

    [Fact]
    public void NoOccurrence_FailsUnderTheErrorPolicy()
    {
        var engine = Engine(Map());
        Assert.ThrowsAny<Exception>(() => engine.Transform("<Input/>"));
    }

    [Fact]
    public void NoOccurrence_IsOmittedUnderTheOmitPolicy()
    {
        var output = XDocument.Parse(Engine(Map(MissingXmlValueBehavior.Omit)).Transform("<Input/>")).Root!;
        Assert.Empty(output.Elements());
    }

    [Fact]
    public void NoOccurrence_UsesTheDefaultUnderTheDefaultPolicy()
    {
        var output = XDocument.Parse(Engine(Map(MissingXmlValueBehavior.Default, "fallback")).Transform("<Input/>")).Root!;
        Assert.Equal("fallback", output.Element("Code")!.Value);
    }

    [Fact]
    public void ComplexFirstOccurrence_StillFails()
    {
        var engine = Engine(Map());
        Assert.ThrowsAny<Exception>(() => engine.Transform("<Input><Item><Code><X>1</X></Code></Item></Input>"));
    }

    [Fact]
    public void WithoutTheFlag_ARepeatingSourceStillFails()
    {
        var engine = Engine(Single(new() { SourcePath = "/Input/Item/Code", TargetPath = "Code" }));
        Assert.ThrowsAny<Exception>(() => engine.Transform("<Input><Item><Code>a</Code></Item><Item><Code>b</Code></Item></Input>"));
    }

    [Fact]
    public void Validation_RequiresVersion2_ASourcePath_AndNoExpressionOrConstant()
    {
        Assert.Throws<ArgumentException>(() => Engine(Map(version: 1)));
        Assert.Throws<ArgumentException>(() => Engine(Single(new() { TargetPath = "Code", Constant = "x", FirstOccurrence = true })));
        Assert.Throws<ArgumentException>(() => Engine(Single(new() { TargetPath = "Code", FirstOccurrence = true })));
    }
}
