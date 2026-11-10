using System.Security;
using System.Text.Json;
using System.Xml.Linq;
using Mulse.CompatibilityPack;
using Mulse.CompatibilityPack.Mapping;
using Service.Mapping;

namespace Service.Tests;

// A logical functoid fed by a repeating source list (BizTalk "settings" style switches) that is linked to a target record the schema allows once.
public sealed class NativeMapRangedGateTests
{
    private const string SourceXsd = """
        <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:gate" elementFormDefault="qualified">
          <xs:element name="Order"><xs:complexType><xs:sequence>
            <xs:element name="Id" type="xs:string" />
            <xs:element name="Setting" minOccurs="0" maxOccurs="unbounded"><xs:complexType><xs:sequence>
              <xs:element name="Qty" type="xs:string" />
            </xs:sequence></xs:complexType></xs:element>
            <xs:element name="Wrap" minOccurs="0"><xs:complexType><xs:sequence>
              <xs:element name="Item" minOccurs="0" maxOccurs="unbounded"><xs:complexType><xs:sequence>
                <xs:element name="Id" type="xs:string" />
              </xs:sequence></xs:complexType></xs:element>
            </xs:sequence></xs:complexType></xs:element>
          </xs:sequence></xs:complexType></xs:element>
        </xs:schema>
        """;
    private const string TargetXsd = """
        <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:gate" elementFormDefault="qualified">
          <xs:element name="Result"><xs:complexType><xs:sequence>
            <xs:element name="Flag" minOccurs="0"><xs:complexType><xs:sequence>
              <xs:element name="Item" minOccurs="0" maxOccurs="unbounded"><xs:complexType><xs:sequence>
                <xs:element name="Id" type="xs:string" />
              </xs:sequence></xs:complexType></xs:element>
            </xs:sequence></xs:complexType></xs:element>
            <xs:element name="Row" minOccurs="0" maxOccurs="unbounded"><xs:complexType><xs:sequence>
              <xs:element name="Id" type="xs:string" />
            </xs:sequence></xs:complexType></xs:element>
          </xs:sequence></xs:complexType></xs:element>
        </xs:schema>
        """;
    private static readonly XNamespace Ns = "urn:gate";

    private static string Path(string path) => NativeMapImportTests.Path(path);
    private static string L(int id, string from, string to) => $"<Link LinkID=\"{id}\" LinkFrom=\"{SecurityElement.Escape(from)}\" LinkTo=\"{SecurityElement.Escape(to)}\" />";
    private static string Direct(int id, string from, string to) => L(id, Path(from), Path(to));
    private static string Wire(int id, string from, int functoid) => L(id, Path(from), functoid.ToString());
    private static string Out(int id, int functoid, string to) => L(id, functoid.ToString(), Path(to));
    private static string Param(string type, string value, int index) => $"<Parameter Type=\"{type}\" Value=\"{SecurityElement.Escape(value)}\" Guid=\"g\" linkIndex=\"{index}\" />";
    private static string GreaterThanZero(int id, int inputLink)
        => $"<Functoid FunctoidID=\"{id}\" Functoid-FID=\"311\" X-Cell=\"1\" Y-Cell=\"1\"><Input-Parameters>{Param("link", inputLink.ToString(), 0)}{Param("constant", "0", 1)}</Input-Parameters></Functoid>";
    private static string Map(string links, string functoids)
        => $"<mapsource><SrcTree RootNode_Name=\"Order\"/><TrgTree RootNode_Name=\"Result\"/><Pages><Page Name=\"p\"><Links>{links}</Links><Functoids>{functoids}</Functoids></Page></Pages></mapsource>";

    private static NativeMapConversion Convert(string map) => BizTalkNativeMapConverter.Convert(map, [SourceXsd], [TargetXsd]);

    private static string GatedFlagMap()
        => Map(Direct(1, "Order/Wrap", "Result/Flag") + Direct(2, "Order/Wrap/Item", "Result/Flag/Item") + Direct(3, "Order/Wrap/Item/Id", "Result/Flag/Item/Id")
            + Wire(4, "Order/Setting/Qty", 10) + Out(5, 10, "Result/Flag"), GreaterThanZero(10, 4));

    private static XElement Run(NativeMapConversion conversion, string settings, string wrap = "<Wrap><Item><Id>a</Id></Item></Wrap>")
        => XDocument.Parse(new XmlMappingEngine(conversion.MappingJson).Transform($"<Order xmlns='urn:gate'><Id>1</Id>{settings}{wrap}</Order>")).Root!;

    [Fact]
    public void GateFedByARepeatingList_BecomesAnEditableAnyOfCondition()
    {
        var conversion = Convert(GatedFlagMap());
        var definition = JsonSerializer.Deserialize<XmlMappingDefinition>(conversion.MappingJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var gate = Assert.Single(definition.RecordConditions);
        Assert.EndsWith(":Setting", gate.AnySourcePath, StringComparison.Ordinal);
        Assert.Contains(conversion.Warnings, warning => warning.Contains("switched on by a functoid fed from a repeating source list", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("<Setting><Qty>0</Qty></Setting>", false)]
    [InlineData("<Setting><Qty>0</Qty></Setting><Setting><Qty>5</Qty></Setting>", true)]
    public void RecordIsWrittenOnceWhenAnyListEntryPasses(string settings, bool written)
    {
        var output = Run(Convert(GatedFlagMap()), settings);
        Assert.Equal(written, output.Element(Ns + "Flag") is not null);
        if (written) Assert.Equal(["a"], output.Element(Ns + "Flag")!.Elements(Ns + "Item").Select(item => item.Element(Ns + "Id")!.Value));
    }

    [Fact]
    public void SeveralPassingEntries_StopTheRunInsteadOfWritingTheRecordTwice()
    {
        var conversion = Convert(GatedFlagMap());
        var failure = Assert.ThrowsAny<Exception>(() => Run(conversion, "<Setting><Qty>1</Qty></Setting><Setting><Qty>2</Qty></Setting>"));
        Assert.Contains("more than one", failure.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void RecordLinkFromAnAbsentSingleRecord_DoesNotSuppressASwitchedOnRecord()
    {
        // BizTalk's compiled output writes the switched-on record first and loops over the linked source record inside it, so an absent Wrap leaves an empty record.
        var output = Run(Convert(GatedFlagMap()), "<Setting><Qty>1</Qty></Setting>", wrap: string.Empty);
        var flag = Assert.IsType<XElement>(output.Element(Ns + "Flag"));
        Assert.Empty(flag.Elements());
    }

    [Fact]
    public void GateFedByAListOnARepeatingTargetRecord_StaysRefused()
    {
        var map = Map(Direct(1, "Order/Id", "Result/Row/Id") + Wire(2, "Order/Setting/Qty", 10) + Out(3, 10, "Result/Row"), GreaterThanZero(10, 2));
        Assert.Contains("no record link enclosing", Assert.Throws<ArgumentException>(() => Convert(map)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ScriptedSwitch_FailsClosedUntilItsBodyIsSuppliedInTheMap()
    {
        const string Code = "public bool Switch(string name, string value) { if (name == \"SendFlag\" && value == \"1\") return true; return false; }";
        var script = $"<Functoid FunctoidID=\"20\" Functoid-FID=\"260\" X-Cell=\"1\" Y-Cell=\"1\"><Input-Parameters>{Param("link", "4", 0)}{Param("link", "6", 1)}</Input-Parameters>"
            + $"<ScripterCode><Script Language=\"CSharp\"><![CDATA[{Code}]]></Script></ScripterCode></Functoid>";
        var equals = $"<Functoid FunctoidID=\"21\" Functoid-FID=\"315\" X-Cell=\"1\" Y-Cell=\"1\"><Input-Parameters>{Param("link", "7", 0)}{Param("constant", "true", 1)}</Input-Parameters></Functoid>";
        var conversion = Convert(Map(Direct(1, "Order/Wrap", "Result/Flag") + Direct(2, "Order/Wrap/Item", "Result/Flag/Item") + Direct(3, "Order/Wrap/Item/Id", "Result/Flag/Item/Id")
            + Wire(4, "Order/Setting/Qty", 20) + Wire(6, "Order/Setting/Qty", 20) + L(7, "20", "21") + Out(5, 21, "Result/Flag"), script + equals));
        var definition = JsonSerializer.Deserialize<XmlMappingDefinition>(conversion.MappingJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var function = Assert.Single(definition.CustomFunctions);
        Assert.Null(function.Body);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Assert.ThrowsAny<Exception>(() => Run(conversion, "<Setting><Qty>1</Qty></Setting>"));

        // What a person (or the UI's formula box) writes for the original script: both inputs are compared with the switch's name/value.
        var body = new XmlValueExpression
        {
            Kind = XmlValueExpressionKind.Function, Function = XmlMappingFunction.Conditional,
            Arguments =
            [
                new() { Kind = XmlValueExpressionKind.Function, Function = XmlMappingFunction.LogicalAnd, Arguments =
                    [
                        new() { Kind = XmlValueExpressionKind.Function, Function = XmlMappingFunction.LogicalEq, Arguments = [new() { Kind = XmlValueExpressionKind.Parameter, Name = function.Parameters[0].Name }, new() { Kind = XmlValueExpressionKind.Constant, Constant = "1" }] },
                        new() { Kind = XmlValueExpressionKind.Function, Function = XmlMappingFunction.LogicalEq, Arguments = [new() { Kind = XmlValueExpressionKind.Parameter, Name = function.Parameters[1].Name }, new() { Kind = XmlValueExpressionKind.Constant, Constant = "1" }] }
                    ] },
                new() { Kind = XmlValueExpressionKind.Constant, Constant = "true" },
                new() { Kind = XmlValueExpressionKind.Constant, Constant = "false" }
            ]
        };
        var implemented = definition with { CustomFunctions = [function with { Body = body }] };
        var json = JsonSerializer.Serialize(implemented, options);
        Assert.NotNull(Run(conversion with { MappingJson = json }, "<Setting><Qty>1</Qty></Setting>").Element(Ns + "Flag"));
        Assert.Null(Run(conversion with { MappingJson = json }, "<Setting><Qty>0</Qty></Setting>").Element(Ns + "Flag"));
    }

    [Fact]
    public void AnySourcePath_MustNamePathsTheEngineKnows()
    {
        var conversion = Convert(GatedFlagMap());
        var definition = JsonSerializer.Deserialize<XmlMappingDefinition>(conversion.MappingJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var broken = definition with { RecordConditions = [definition.RecordConditions[0] with { AnySourcePath = "Setting" }] };
        Assert.Throws<ArgumentException>(() => new XmlMappingEngine(JsonSerializer.Serialize(broken, new JsonSerializerOptions(JsonSerializerDefaults.Web))));
    }
}
