using System.Xml.Linq;
using Mulse.CompatibilityPack;
using Mulse.Modules.Mapping;

namespace Service.Tests;

public sealed class XmlMappingRepeatTests
{
    private static XmlMappingDefinition Map(params XmlRepeatMappingDefinition[] repeats) => new()
    {
        Version = 2, TargetRoot = "Out",
        Fields =
        [
            new() { SourcePath = "/In/Id", TargetPath = "Id" },
            new() { SourcePath = "/In/Items/Item/@no", TargetPath = "Lines/Line/@n" },
            new() { SourcePath = "/In/Items/Item/Name", TargetPath = "Lines/Line/Name", MissingValueBehavior = MissingXmlValueBehavior.Default, DefaultValue = "?" },
            new() { SourcePath = "/In/Items/Item/Parts/Part", TargetPath = "Lines/Line/Parts/Part" },
            new() { Constant = "x", TargetPath = "Lines/Line/Kind" }
        ],
        Repeats = repeats
    };

    private static readonly XmlRepeatMappingDefinition Lines = new() { SourcePath = "/In/Items/Item", TargetPath = "Lines/Line" };
    private static readonly XmlRepeatMappingDefinition Parts = new() { SourcePath = "/In/Items/Item/Parts/Part", TargetPath = "Lines/Line/Parts/Part" };

    private static XmlMappingEngine Engine(XmlMappingDefinition map) => new(XmlMappingTests.Json(map));

    [Fact]
    public void Repeats_KeepEachSourceRecordCorrelated()
    {
        var engine = Engine(Map(Lines, Parts));
        var output = XDocument.Parse(engine.Transform(
            "<In><Id>7</Id><Items><Item no='1'><Name>a</Name><Parts><Part>p1</Part><Part>p2</Part></Parts></Item>"
            + "<Item no='2'><Parts><Part>p3</Part></Parts></Item></Items></In>")).Root!;
        var lines = output.Element("Lines")!.Elements("Line").ToList();
        Assert.Equal("7", output.Element("Id")!.Value);
        Assert.Equal(2, lines.Count);
        Assert.Equal("1", (string)lines[0].Attribute("n")!);
        Assert.Equal("a", lines[0].Element("Name")!.Value);
        Assert.Equal(["p1", "p2"], lines[0].Element("Parts")!.Elements("Part").Select(part => part.Value));
        Assert.Equal("?", lines[1].Element("Name")!.Value);
        Assert.Equal(["p3"], lines[1].Element("Parts")!.Elements("Part").Select(part => part.Value));
        Assert.All(lines, line => Assert.Equal("x", line.Element("Kind")!.Value));
    }

    [Fact]
    public void ZeroRepeats_EmitNoRecords()
    {
        var output = XDocument.Parse(Engine(Map(Lines, Parts)).Transform("<In><Id>7</Id><Items/></In>")).Root!;
        Assert.Empty(output.Descendants("Line"));
    }

    [Fact]
    public void InsideARepeat_ScalarCardinalityIsStillEnforcedPerRecord()
    {
        var engine = Engine(Map(Lines, Parts));
        Assert.Throws<ArgumentException>(() => engine.Transform(
            "<In><Id>7</Id><Items><Item no='1'><Name>a</Name><Name>b</Name></Item></Items></In>"));
        Assert.Throws<ArgumentException>(() => engine.Transform(
            "<In><Id>7</Id><Items><Item><Name>a</Name></Item></Items></In>"));
    }

    [Fact]
    public void RepeatingScalar_MapsToItself()
    {
        var map = new XmlMappingDefinition
        {
            Version = 2, TargetRoot = "Out",
            Fields = [new() { SourcePath = "/In/V", TargetPath = "Vs/V" }],
            Repeats = [new() { SourcePath = "/In/V", TargetPath = "Vs/V" }]
        };
        var output = XDocument.Parse(Engine(map).Transform("<In><V>a</V><V>b</V></In>")).Root!;
        Assert.Equal(["a", "b"], output.Element("Vs")!.Elements("V").Select(item => item.Value));
    }

    [Fact]
    public void FieldsOutsideTheRepeat_CannotReadFromInsideIt()
    {
        var map = Map(Lines, Parts) with
        {
            Fields = [.. Map(Lines, Parts).Fields, new XmlFieldMappingDefinition { SourcePath = "/In/Items/Item/Name", TargetPath = "First" }]
        };
        Assert.Contains("not below the repeat", Assert.Throws<ArgumentException>(() => Engine(map)).Message);
    }

    [Fact]
    public void NestedRepeat_CanReadFieldsOfTheEnclosingRecordAndNilRecordsFailClosed()
    {
        var map = new XmlMappingDefinition
        {
            Version = 2, TargetRoot = "Out",
            Fields =
            [
                new() { SourcePath = "/In/Items/Item/Parts/Part", TargetPath = "Lines/Line/Parts/Part/V" },
                new() { SourcePath = "/In/Items/Item/@no", TargetPath = "Lines/Line/Parts/Part/Owner" }
            ],
            Repeats = [Lines, Parts]
        };
        var engine = Engine(map);
        var output = XDocument.Parse(engine.Transform(
            "<In><Items><Item no='1'><Parts><Part>a</Part></Parts></Item><Item no='2'><Parts><Part>b</Part></Parts></Item></Items></In>")).Root!;
        Assert.Equal(["1", "2"], output.Descendants("Owner").Select(owner => owner.Value));
        Assert.Contains("xsi:nil", Assert.Throws<ArgumentException>(() => engine.Transform(
            "<In xmlns:xsi='http://www.w3.org/2001/XMLSchema-instance'><Id>1</Id><Items><Item xsi:nil='true' no='1'/></Items></In>")).Message);
    }

    [Fact]
    public void Repeats_FailClosedOnInvalidDefinitions()
    {
        Assert.Throws<ArgumentException>(() => Engine(Map(Lines, new XmlRepeatMappingDefinition { SourcePath = "/In/Other", TargetPath = "Lines/Line/Parts/Part" })));
        Assert.Throws<ArgumentException>(() => Engine(Map(Lines, Lines)));
        Assert.Throws<ArgumentException>(() => Engine(Map(new XmlRepeatMappingDefinition { SourcePath = "/In/Items/Item", TargetPath = "Nowhere/Item" })));
        Assert.Throws<ArgumentException>(() => Engine(Map(new XmlRepeatMappingDefinition { SourcePath = "/In/Items/Item/@no", TargetPath = "Lines/Line/@n" })));
        Assert.Throws<ArgumentException>(() => Engine(Map(Lines) with { Version = 1 }));
    }
}
