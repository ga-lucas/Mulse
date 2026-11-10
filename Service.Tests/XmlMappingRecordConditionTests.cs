using System.Xml.Linq;
using Mulse.CompatibilityPack;
using Mulse.Modules.Mapping;

namespace Service.Tests;

public sealed class XmlMappingRecordConditionTests
{
    private static XmlValueExpression Src(string path) => new() { Kind = XmlValueExpressionKind.Source, SourcePath = path };
    private static XmlValueExpression Const(string text) => new() { Kind = XmlValueExpressionKind.Constant, Constant = text };
    private static XmlValueExpression Gt(string path, string than)
        => new() { Kind = XmlValueExpressionKind.Function, Function = XmlMappingFunction.LogicalGt, Arguments = [Src(path), Const(than)] };

    private static XmlMappingDefinition Singleton(string target = "Meta", XmlValueExpression? condition = null) => new()
    {
        Version = 2, TargetRoot = "Out",
        Fields = [new() { SourcePath = "/In/A", TargetPath = "Meta/A" }],
        RecordConditions = [new() { TargetPath = target, Condition = condition ?? Gt("/In/N", "0") }]
    };

    private static XmlMappingDefinition Repeating() => new()
    {
        Version = 2, TargetRoot = "Out",
        Repeats = [new() { SourcePath = "/In/Item", TargetPath = "Row" }],
        Fields = [new() { SourcePath = "/In/Item/V", TargetPath = "Row/V" }],
        RecordConditions = [new() { TargetPath = "Row", Condition = Gt("/In/Item/Q", "0") }]
    };

    private static XDocument Run(XmlMappingDefinition map, string xml) => XDocument.Parse(new XmlMappingEngine(XmlMappingTests.Json(map)).Transform(xml));
    private static string Message(XmlMappingDefinition map) => Assert.Throws<ArgumentException>(() => new XmlMappingEngine(XmlMappingTests.Json(map))).Message;

    [Theory]
    [InlineData("1", true)]
    [InlineData("0", false)]
    public void SingletonRecord_IsWrittenOnlyWhenTheConditionIsTrue(string n, bool written)
    {
        var output = Run(Singleton(), $"<In><N>{n}</N><A>a</A></In>").Root!;
        Assert.Equal(written, output.Element("Meta") is not null);
        if (written) Assert.Equal("a", output.Element("Meta")!.Element("A")!.Value);
    }

    [Fact]
    public void SkippedRecord_DoesNotFailOnSourcesItNeverReads()
    {
        var output = Run(Singleton(), "<In><N>0</N></In>").Root!;
        Assert.Null(output.Element("Meta"));
        Assert.Contains("must select exactly one", Assert.Throws<ArgumentException>(() => Run(Singleton(), "<In><N>1</N></In>")).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RepeatingRecord_EvaluatesTheConditionOncePerSourceRecord()
    {
        var output = Run(Repeating(), "<In><Item><Q>1</Q><V>a</V></Item><Item><Q>0</Q><V>b</V></Item><Item><Q>5</Q><V>c</V></Item></In>").Root!;
        Assert.Equal(["a", "c"], output.Elements("Row").Select(row => row.Element("V")!.Value));
    }

    [Fact]
    public void RecordConditions_RequireVersion2_AndAnExistingRecordTarget()
    {
        Assert.Contains("Record conditions require version 2", Message(Singleton() with { Version = 1 }), StringComparison.Ordinal);
        Assert.Contains("no mapped fields below", Message(Singleton("Nope")), StringComparison.Ordinal);
        Assert.Contains("is a field, not a record", Message(Singleton("Meta/A")), StringComparison.Ordinal);
    }

    [Fact]
    public void RecordConditions_RejectDuplicatesMissingConditionsAndOutOfScopeSources()
    {
        var duplicate = Singleton() with { RecordConditions = [new() { TargetPath = "Meta", Condition = Const("true") }, new() { TargetPath = "Meta", Condition = Const("true") }] };
        Assert.Contains("more than one condition", Message(duplicate), StringComparison.Ordinal);
        var empty = Singleton() with { RecordConditions = [new() { TargetPath = "Meta" }] };
        Assert.Contains("need a target path and a condition", Message(empty), StringComparison.Ordinal);
        var outside = Singleton(condition: Gt("/In/Item/Q", "0")) with
        {
            Repeats = [new() { SourcePath = "/In/Item", TargetPath = "Row" }],
            Fields = [new() { SourcePath = "/In/A", TargetPath = "Meta/A" }, new() { SourcePath = "/In/Item/V", TargetPath = "Row/V" }]
        };
        Assert.Contains("/In/Item", Message(outside), StringComparison.Ordinal);
    }
}
