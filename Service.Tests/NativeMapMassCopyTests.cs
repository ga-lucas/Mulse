using System.Security;
using System.Text.Json;
using System.Xml.Linq;
using Mulse.CompatibilityPack;
using Mulse.Modules.Mapping;
using Service.Mapping;

namespace Service.Tests;

public sealed class NativeMapMassCopyTests
{
    private const string HeaderBody = """
        <xs:element name="Id" type="xs:string" />
        <xs:element name="Note" type="xs:string" minOccurs="0" />
        <xs:element name="Party"><xs:complexType><xs:sequence>
          <xs:element name="Name" type="xs:string" />
          <xs:element name="Role" type="xs:string" minOccurs="0" />
        </xs:sequence><xs:attribute name="kind" type="xs:string" /></xs:complexType></xs:element>
        """;
    private const string Lines = """
        <xs:element name="Line" minOccurs="0" maxOccurs="unbounded"><xs:complexType><xs:sequence>
          <xs:element name="Sku" type="xs:string" />
          <xs:element name="Qty" type="xs:string" />
        </xs:sequence></xs:complexType></xs:element>
        """;
    private const string Input = "<Order xmlns='urn:copy'><Header code='H1'><Id>7</Id><Party kind='p'><Name>N</Name></Party></Header>"
        + "<Line><Sku>a</Sku><Qty>1</Qty></Line><Line><Sku>b</Sku><Qty>2</Qty></Line></Order>";

    private static string Schema(string root, string headerBody = HeaderBody, string lines = Lines, string ns = "urn:copy")
        => $"""
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="{ns}" elementFormDefault="qualified">
              <xs:element name="{root}"><xs:complexType><xs:sequence>
                <xs:element name="Header"><xs:complexType><xs:sequence>{headerBody}</xs:sequence><xs:attribute name="code" type="xs:string" /></xs:complexType></xs:element>
                {lines}
              </xs:sequence></xs:complexType></xs:element>
            </xs:schema>
            """;

    private static string Path(string path) => NativeMapImportTests.Path(path);
    private static string L(int id, string from, string to) => $"<Link LinkID=\"{id}\" LinkFrom=\"{SecurityElement.Escape(from)}\" LinkTo=\"{SecurityElement.Escape(to)}\" />";
    private static string Direct(int id, string from, string to) => L(id, Path(from), Path(to));
    private static string Wire(int id, string from, int functoid) => L(id, Path(from), functoid.ToString());
    private static string Out(int id, int functoid, string to) => L(id, functoid.ToString(), Path(to));
    private static string Copy(int functoid, int linkId, string from, string to) => Wire(linkId, from, functoid) + Out(linkId + 1, functoid, to);
    private static string MassCopy(int id, params string[] parameters)
        => $"<Functoid FunctoidID=\"{id}\" Functoid-FID=\"802\" X-Cell=\"1\" Y-Cell=\"1\"><Input-Parameters>{string.Concat(parameters)}</Input-Parameters></Functoid>";
    private static string Param(int link, int index) => $"<Parameter Type=\"link\" Value=\"{link}\" Guid=\"g\" linkIndex=\"{index}\" />";
    private static string Map(string links, string functoids)
        => $"<mapsource><SrcTree RootNode_Name=\"Order\"/><TrgTree RootNode_Name=\"Result\"/><Pages><Page Name=\"p\"><Links>{links}</Links><Functoids>{functoids}</Functoids></Page></Pages></mapsource>";

    private static NativeMapConversion Convert(string map, string? targetHeader = null, string? targetLines = null, string targetNamespace = "urn:copy", string? sourceHeader = null)
        => BizTalkNativeMapConverter.Convert(map, [Schema("Order", sourceHeader ?? HeaderBody)], [Schema("Result", targetHeader ?? HeaderBody, targetLines ?? Lines, targetNamespace)]);

    private static string Message(string map, string? targetHeader = null, string? targetLines = null, string targetNamespace = "urn:copy", string? sourceHeader = null)
        => Assert.Throws<ArgumentException>(() => Convert(map, targetHeader, targetLines, targetNamespace, sourceHeader)).Message;

    private static XElement Run(NativeMapConversion conversion) => XDocument.Parse(new XmlMappingEngine(conversion.MappingJson).Transform(Input)).Root!;

    private static readonly XNamespace Ns = "urn:copy";

    private const string HeaderCopy = "Order/Header";

    private static string HandDrawnHeader()
        => Direct(1, "Order/Header", "Result/Header") + Direct(2, "Order/Header/@code", "Result/Header/@code") + Direct(3, "Order/Header/Id", "Result/Header/Id")
            + Direct(4, "Order/Header/Note", "Result/Header/Note") + Direct(5, "Order/Header/Party", "Result/Header/Party")
            + Direct(6, "Order/Header/Party/@kind", "Result/Header/Party/@kind") + Direct(7, "Order/Header/Party/Name", "Result/Header/Party/Name")
            + Direct(8, "Order/Header/Party/Role", "Result/Header/Party/Role");

    [Fact]
    public void MassCopyOfARecord_BecomesTheSameEditableLinksAsDrawingThemByHand()
    {
        var copied = Convert(Map(Copy(10, 100, HeaderCopy, "Result/Header"), MassCopy(10, Param(100, 0))));
        var drawn = Convert(Map(HandDrawnHeader(), string.Empty));
        Assert.Equal(drawn.Xslt, copied.Xslt);
        Assert.Equal(drawn.MappingJson, copied.MappingJson);
        Assert.Contains(copied.Warnings, warning => warning.Contains("Mass Copy functoid(s) (FID 802) were expanded into 8 explicit", StringComparison.Ordinal));
        Assert.DoesNotContain(copied.Warnings, warning => warning.Contains("not connected", StringComparison.Ordinal));
        Assert.Empty(copied.PendingFunctions);

        var header = Run(copied).Element(Ns + "Header")!;
        Assert.Equal("H1", (string?)header.Attribute("code"));
        Assert.Equal("7", header.Element(Ns + "Id")!.Value);
        Assert.Null(header.Element(Ns + "Note"));
        Assert.Equal("p", (string?)header.Element(Ns + "Party")!.Attribute("kind"));
        Assert.Equal("N", header.Element(Ns + "Party")!.Element(Ns + "Name")!.Value);
        Assert.Null(header.Element(Ns + "Party")!.Element(Ns + "Role"));
    }

    [Fact]
    public void MassCopyOfARepeatingRecord_CreatesOneTargetRecordPerSourceRecord()
    {
        var copied = Convert(Map(Copy(10, 100, HeaderCopy, "Result/Header") + Copy(11, 200, "Order/Line", "Result/Line"), MassCopy(10, Param(100, 0)) + MassCopy(11, Param(200, 0))));
        var drawn = Convert(Map(HandDrawnHeader() + Direct(20, "Order/Line", "Result/Line") + Direct(21, "Order/Line/Sku", "Result/Line/Sku") + Direct(22, "Order/Line/Qty", "Result/Line/Qty"), string.Empty));
        Assert.Equal(drawn.MappingJson, copied.MappingJson);
        Assert.Contains(copied.Warnings, warning => warning.Contains("2 Mass Copy functoid(s)", StringComparison.Ordinal));
        var lines = Run(copied).Elements(Ns + "Line").ToArray();
        Assert.Equal(["a", "b"], lines.Select(line => line.Element(Ns + "Sku")!.Value));
        Assert.Equal(["1", "2"], lines.Select(line => line.Element(Ns + "Qty")!.Value));
    }

    [Fact]
    public void MassCopy_WithOtherLinksOutsideTheCopiedRecord_StillWorks()
    {
        var conversion = Convert(Map(HandDrawnHeader() + Copy(10, 100, "Order/Line", "Result/Line"), MassCopy(10, Param(100, 0))));
        Assert.Equal(["a", "b"], Run(conversion).Elements(Ns + "Line").Select(line => line.Element(Ns + "Sku")!.Value));
    }

    [Fact]
    public void UnmappedOptionalRecord_WithRequiredChildren_DoesNotDemandItsChildren()
    {
        const string optionalLine = """
            <xs:element name="Line" minOccurs="0"><xs:complexType><xs:sequence>
              <xs:element name="Sku" type="xs:string" />
              <xs:element name="Qty" type="xs:string" />
            </xs:sequence></xs:complexType></xs:element>
            """;
        var conversion = Convert(Map(HandDrawnHeader(), string.Empty), targetLines: optionalLine);
        var result = Run(conversion);
        Assert.NotNull(result.Element(Ns + "Header"));
        Assert.Null(result.Element(Ns + "Line"));
    }

    [Fact]
    public void ConvertedLinksAreOrdinaryMappingRows()
    {
        var conversion = Convert(Map(Copy(10, 100, HeaderCopy, "Result/Header"), MassCopy(10, Param(100, 0))));
        var definition = JsonSerializer.Deserialize<XmlMappingDefinition>(conversion.MappingJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Contains(definition.Fields, field => field.TargetPath == "tn1:Header/tn1:Id" && field.SourcePath == "/sn1:Order/sn1:Header/sn1:Id");
        Assert.Contains(definition.Repeats, repeat => repeat.TargetPath == "tn1:Header/tn1:Party");
        Assert.Empty(definition.CustomFunctions);
    }

    [Fact]
    public void UnconnectedMassCopy_IsIgnoredWithTheUsualWarning()
    {
        var conversion = Convert(Map(HandDrawnHeader(), MassCopy(10)));
        Assert.Contains(conversion.Warnings, warning => warning.Contains("not connected", StringComparison.Ordinal));
        Assert.DoesNotContain(conversion.Warnings, warning => warning.Contains("Mass Copy functoid(s)", StringComparison.Ordinal));
    }

    private const string TargetWithoutNote = """
        <xs:element name="Id" type="xs:string" />
        <xs:element name="Party"><xs:complexType><xs:sequence>
          <xs:element name="Name" type="xs:string" />
          <xs:element name="Role" type="xs:string" minOccurs="0" />
        </xs:sequence><xs:attribute name="kind" type="xs:string" /></xs:complexType></xs:element>
        """;
    private const string TargetNoteIsARecord = """
        <xs:element name="Id" type="xs:string" />
        <xs:element name="Note" minOccurs="0"><xs:complexType><xs:sequence><xs:element name="X" type="xs:string" /></xs:sequence></xs:complexType></xs:element>
        <xs:element name="Party"><xs:complexType><xs:sequence>
          <xs:element name="Name" type="xs:string" />
          <xs:element name="Role" type="xs:string" minOccurs="0" />
        </xs:sequence><xs:attribute name="kind" type="xs:string" /></xs:complexType></xs:element>
        """;
    private const string TargetOutOfOrder = """
        <xs:element name="Note" type="xs:string" minOccurs="0" />
        <xs:element name="Id" type="xs:string" />
        <xs:element name="Party"><xs:complexType><xs:sequence>
          <xs:element name="Name" type="xs:string" />
          <xs:element name="Role" type="xs:string" minOccurs="0" />
        </xs:sequence><xs:attribute name="kind" type="xs:string" /></xs:complexType></xs:element>
        """;
    private const string TargetSingleLine = """
        <xs:element name="Line" minOccurs="0"><xs:complexType><xs:sequence>
          <xs:element name="Sku" type="xs:string" />
          <xs:element name="Qty" type="xs:string" />
        </xs:sequence></xs:complexType></xs:element>
        """;
    private const string SourceWithWildcard = HeaderBody + "<xs:any minOccurs=\"0\" processContents=\"skip\" />";

    private static string HeaderMap() => Map(Copy(10, 100, HeaderCopy, "Result/Header"), MassCopy(10, Param(100, 0)));

    [Fact]
    public void SourceNodesTheTargetDoesNotDeclare_AreRefusedRatherThanDropped()
    {
        var message = Message(HeaderMap(), targetHeader: TargetWithoutNote);
        Assert.Contains("does not declare", message, StringComparison.Ordinal);
        Assert.Contains("Note", message, StringComparison.Ordinal);
        Assert.Contains("No partial native map", message, StringComparison.Ordinal);
    }

    [Fact]
    public void SameLocalNamesInADifferentNamespace_AreNotTheSameNodes()
        => Assert.Contains("does not declare", Message(HeaderMap(), targetNamespace: "urn:other"), StringComparison.Ordinal);

    [Fact]
    public void ValueCopiedOntoARecord_IsRefused()
        => Assert.Contains("a value", Message(HeaderMap(), targetHeader: TargetNoteIsARecord), StringComparison.Ordinal);

    [Fact]
    public void DifferentOrder_IsRefusedBecauseMassCopyKeepsTheSourceOrder()
        => Assert.Contains("different order", Message(HeaderMap(), targetHeader: TargetOutOfOrder), StringComparison.Ordinal);

    [Fact]
    public void RepeatingOnOneSideOnly_IsRefused()
        => Assert.Contains("only one of them repeats",
            Message(Map(Copy(10, 100, "Order/Line", "Result/Line"), MassCopy(10, Param(100, 0))), targetLines: TargetSingleLine), StringComparison.Ordinal);

    [Fact]
    public void SourceWildcards_CannotBeCopiedNatively()
        => Assert.Contains("cannot reproduce", Message(HeaderMap(), sourceHeader: SourceWithWildcard), StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void AnotherLinkIntoTheCopiedRecord_IsRefusedBecauseBizTalkWouldEmitBoth()
    {
        var message = Message(Map(Copy(10, 100, HeaderCopy, "Result/Header") + Direct(1, "Order/Header/Id", "Result/Header/Id"), MassCopy(10, Param(100, 0))));
        Assert.Contains("also by another link", message, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoMassCopiesIntoTheSameRecord_AreRefused()
        => Assert.Contains("also by another link", Message(Map(
            Copy(10, 100, HeaderCopy, "Result/Header") + Copy(11, 200, HeaderCopy, "Result/Header"), MassCopy(10, Param(100, 0)) + MassCopy(11, Param(200, 0)))), StringComparison.Ordinal);

    [Fact]
    public void InputsThatAreNotASingleSourceRecord_AreRefused()
    {
        Assert.Contains("works on records", Message(Map(Copy(10, 100, "Order/Header/Id", "Result/Header"), MassCopy(10, Param(100, 0)))), StringComparison.Ordinal);
        Assert.Contains("exactly one source record input", Message(Map(Out(101, 10, "Result/Header"), MassCopy(10))), StringComparison.Ordinal);
        Assert.Contains("exactly one source record input", Message(Map(
            Wire(100, HeaderCopy, 10) + Wire(102, "Order/Line", 10) + Out(101, 10, "Result/Header"), MassCopy(10, Param(100, 0), Param(102, 1)))), StringComparison.Ordinal);
        Assert.Contains("constant input", Message(Map(
            Wire(100, HeaderCopy, 10) + Out(101, 10, "Result/Header"),
            "<Functoid FunctoidID=\"10\" Functoid-FID=\"802\" X-Cell=\"1\" Y-Cell=\"1\"><Input-Parameters><Parameter Type=\"constant\" Value=\"x\" Guid=\"g\" linkIndex=\"0\" /></Input-Parameters></Functoid>")), StringComparison.Ordinal);
        Assert.Contains("fed by another functoid", Message(Map(
            L(100, "11", "10") + Out(101, 10, "Result/Header"),
            MassCopy(10, Param(100, 0)) + "<Functoid FunctoidID=\"11\" Functoid-FID=\"107\" X-Cell=\"1\" Y-Cell=\"1\"><Input-Parameters><Parameter Type=\"constant\" Value=\"a\" Guid=\"g\" linkIndex=\"0\" /></Input-Parameters></Functoid>")), StringComparison.Ordinal);
    }

    [Fact]
    public void MassCopyOfAWholeMessage_IsRefused()
        => Assert.Contains("whole message root", Message(Map(Copy(10, 100, "Order", "Result"), MassCopy(10, Param(100, 0)))), StringComparison.Ordinal);
}
