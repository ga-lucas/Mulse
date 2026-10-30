using System.Text.Json;
using System.Xml.Linq;
using Mulse.CompatibilityPack;
using Mulse.Modules.Mapping;

namespace Service.Tests;

public sealed class XmlMappingMissingValueTests
{
    private static XmlMappingDefinition Map(MissingXmlValueBehavior behavior, string? defaultValue = null) => new()
    {
        Version = 2, TargetRoot = "Output",
        Fields =
        [
            new() { SourcePath = "/Input/Value", TargetPath = "Value", MissingValueBehavior = behavior, DefaultValue = defaultValue },
            new() { SourcePath = "/Input/@id", TargetPath = "@id", MissingValueBehavior = behavior, DefaultValue = defaultValue }
        ]
    };

    [Theory]
    [InlineData(MissingXmlValueBehavior.Omit, null)]
    [InlineData(MissingXmlValueBehavior.Default, "<&> literal")]
    [InlineData(MissingXmlValueBehavior.Default, "")]
    public void MissingValues_UseOnlyTheExplicitVersion2Policy(MissingXmlValueBehavior behavior, string? defaultValue)
    {
        var engine = new XmlMappingEngine(XmlMappingTests.Json(Map(behavior, defaultValue)));
        var root = XDocument.Parse(engine.Transform("<Input/>" )).Root!;
        if (behavior == MissingXmlValueBehavior.Omit)
        {
            Assert.Empty(root.Elements());
            Assert.Empty(root.Attributes());
        }
        else
        {
            Assert.Equal(defaultValue, root.Element("Value")!.Value);
            Assert.Equal(defaultValue, root.Attribute("id")!.Value);
        }
        var present = XDocument.Parse(engine.Transform("<Input id=''><Value /></Input>")).Root!;
        Assert.Equal("", present.Element("Value")!.Value);
        Assert.Equal("", present.Attribute("id")!.Value);
    }

    [Theory]
    [InlineData(MissingXmlValueBehavior.Error, null)]
    [InlineData(MissingXmlValueBehavior.Omit, null)]
    [InlineData(MissingXmlValueBehavior.Default, "fallback")]
    public void RepeatedOrComplexValues_StillFailForEveryPolicy(MissingXmlValueBehavior behavior, string? defaultValue)
    {
        var engine = new XmlMappingEngine(XmlMappingTests.Json(Map(behavior, defaultValue)));
        Assert.Throws<ArgumentException>(() => engine.Transform("<Input id='x'><Value>1</Value><Value>2</Value></Input>"));
        Assert.Throws<ArgumentException>(() => engine.Transform("<Input id='x'><Value><Child/></Value></Input>"));
    }

    [Theory]
    [InlineData(MissingXmlValueBehavior.Default, null)]
    [InlineData(MissingXmlValueBehavior.Error, "ignored")]
    [InlineData(MissingXmlValueBehavior.Omit, "ignored")]
    [InlineData((MissingXmlValueBehavior)99, null)]
    public void InvalidPolicyCombinations_AreRejected(MissingXmlValueBehavior behavior, string? defaultValue)
        => Assert.Throws<ArgumentException>(() => new XmlMappingEngine(XmlMappingTests.Json(Map(behavior, defaultValue))));

    [Fact]
    public void Version1CannotSilentlyAcquireVersion2Semantics()
    {
        Assert.Throws<ArgumentException>(() => new XmlMappingEngine(XmlMappingTests.Json(Map(MissingXmlValueBehavior.Omit) with { Version = 1 })));
        Assert.Throws<ArgumentException>(() => new XmlMappingEngine(XmlMappingTests.Json(Map(MissingXmlValueBehavior.Default, "fallback") with { Version = 1 })));
        Assert.Throws<ArgumentException>(() => new XmlMappingEngine(XmlMappingTests.Json(Map(MissingXmlValueBehavior.Error))).Transform("<Input/>"));
    }

    [Fact]
    public void ConstantsCannotCarryIgnoredMissingValuePolicies()
    {
        var field = new XmlFieldMappingDefinition { TargetPath = "Value", Constant = "value", MissingValueBehavior = MissingXmlValueBehavior.Omit };
        Assert.Throws<ArgumentException>(() => new XmlMappingEngine(XmlMappingTests.Json(new() { Version = 2, Fields = [field] })));
    }
}
