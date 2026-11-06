using System.Text.Json;
using System.Xml.Linq;
using System.Xml.XPath;
using Mulse.Modules.ControlFlow;
using Mulse.Modules.Payloads;
using Mulse.Ui.Models;
using Service.BizTalkImport;

namespace Service.Tests;

public sealed class NativeXmlDescendantFactTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string Config = "<b:Billing xmlns:b='urn:b'><ConfigurationInfo><AdditionalInfo><Setting Name='MSH5_1' Value='X'/><Setting Name='MSH3_1' Value='Y'/><Setting Name='MSH5_1' Value='dup'/></AdditionalInfo></ConfigurationInfo><Other xmlns='urn:other'><Setting Name='MSH5_1' Value='wrong'/></Other></b:Billing>";
    private const string Nested = "<r><A><A><B v='in'/></A><B v='out'/></A></r>";

    private static IntegrationBatch Input(string xml) => new([new("m.xml", BinaryData.FromString(xml), "application/xml", new Dictionary<string, string>())]);

    [Theory]
    [InlineData(Config, "string(//ConfigurationInfo/AdditionalInfo/Setting[@Name='MSH5_1']/@Value)", "X")]
    [InlineData(Config, "string(//ConfigurationInfo/AdditionalInfo/Setting[@Name='MSH3_1']/@Value)", "Y")]
    [InlineData(Config, "string(//ConfigurationInfo/AdditionalInfo/Setting[@Name=\"MSH3_1\"]/@Value)", "Y")]
    [InlineData(Config, "string(//ConfigurationInfo/AdditionalInfo/Setting[ @Name = 'MSH3_1' ]/@Value)", "Y")]
    [InlineData(Config, "count(//ConfigurationInfo/AdditionalInfo/Setting[@Name='MSH5_1'])", "2")]
    [InlineData(Config, "count(//Setting)", "3")]
    [InlineData(Config, "count(//ConfigurationInfo)", "1")]
    [InlineData(Config, "count(//Billing)", "0")]
    [InlineData(Config, "string(//Setting[@Name='Missing']/@Value)", "")]
    [InlineData(Config, "string(//Setting[@Name='msh5_1']/@Value)", "")]
    [InlineData(Config, "boolean(//Setting[@Name='MSH3_1'])", "true")]
    [InlineData(Config, "boolean(//Setting[@Name='nope'])", "false")]
    [InlineData(Config, "count(//*[local-name()='Setting' and namespace-uri()='urn:other'][@Name='MSH5_1'])", "1")]
    [InlineData(Config, "string(//*[local-name()='Setting' and namespace-uri()='urn:other'][@Name='MSH5_1']/@Value)", "wrong")]
    [InlineData(Nested, "string(//A/B/@v)", "in")]
    [InlineData(Nested, "count(//A/B)", "2")]
    [InlineData(Nested, "count(//A)", "2")]
    [InlineData("<A><B v='root'/></A>", "string(//A/B/@v)", "root")]
    public void DescendantAndAttributeFilterFactsMatchTheXPathEngine(string xml, string xpath, string expected)
    {
        var fact = NativeSourceXmlFactReader.Read("m", xpath);
        var actual = fact.Read(Input(xml)).Value;
        Assert.Equal(expected, actual);
        var engine = XDocument.Parse(xml, LoadOptions.PreserveWhitespace).XPathEvaluate(xpath);
        Assert.Equal(engine is bool boolean ? boolean ? "true" : "false" : Convert.ToString(engine, System.Globalization.CultureInfo.InvariantCulture), actual);
    }

    [Fact]
    public void ImportedStepsRecordTheDescendantFlagAndTheExactFilter()
    {
        var fact = NativeSourceXmlFactReader.Read("m", "string(//ConfigurationInfo/AdditionalInfo/Setting[@Name='MSH5_1']/@Value)");
        Assert.Equal([true, false, false, false], fact.Steps.Select(step => step.Descendant));
        Assert.Equal(["", "", "Name", ""], fact.Steps.Select(step => step.FilterAttribute));
        Assert.Equal("MSH5_1", fact.Steps[2].FilterValue);
        Assert.True(fact.Steps[3].Attribute);
        Assert.All(fact.Steps, step => Assert.Equal("", step.Namespace));
    }

    [Theory]
    [InlineData("count(/r//Item)")]
    [InlineData("count(//Item//Sub)")]
    [InlineData("count(//@id)")]
    [InlineData("count(//Item/@id[@x='y'])")]
    [InlineData("count(//Item[@id='a'][@x='b'])")]
    [InlineData("count(//Item[@id=a])")]
    [InlineData("count(//Item[@id='a' or @id='b'])")]
    [InlineData("count(///Item)")]
    public void OnlyTheLeadingDescendantAndSingleExactFilterAreRecognized(string xpath)
        => Assert.Throws<NotSupportedException>(() => NativeSourceXmlFactReader.Read("m", xpath));

    [Fact]
    public void FactValidationRejectsMisplacedDescendantsOrFiltersAndTypedScalarsKeepExactChildPaths()
    {
        NativeMessageFact Fact(NativeMessageFactKind kind, NativeXmlStep first, NativeXmlStep? second = null) => new() { Kind = kind, Steps = second is null ? [first] : [first, second] };
        Fact(NativeMessageFactKind.XmlText, new() { Name = "A", Descendant = true, FilterAttribute = "k", FilterValue = "" }).Validate();
        Assert.Throws<ArgumentException>(() => Fact(NativeMessageFactKind.XmlText, new() { Name = "A" }, new() { Name = "B", Descendant = true }).Validate());
        Assert.Throws<ArgumentException>(() => Fact(NativeMessageFactKind.XmlText, new() { Name = "A" }, new() { Name = "v", Attribute = true, FilterAttribute = "k" }).Validate());
        Assert.Throws<ArgumentException>(() => Fact(NativeMessageFactKind.XmlText, new() { Name = "A", FilterValue = "orphan" }).Validate());
        Assert.Throws<ArgumentException>(() => Fact(NativeMessageFactKind.XmlText, new() { Name = "A", FilterAttribute = "bad name" }).Validate());
        Assert.Throws<ArgumentException>(() => Fact(NativeMessageFactKind.XmlText, new() { Name = "A", FilterAttribute = new string('a', 257) }).Validate());
        Assert.Throws<ArgumentException>(() => Fact(NativeMessageFactKind.XmlText, new() { Name = "A", FilterAttribute = "k", FilterValue = new string('v', 2049) }).Validate());
        Assert.Throws<ArgumentException>(() => Fact(NativeMessageFactKind.XmlString, new() { Name = "A", Descendant = true }).Validate());
        Assert.Throws<ArgumentException>(() => Fact(NativeMessageFactKind.XmlInt32, new() { Name = "A", FilterAttribute = "k", FilterValue = "v" }).Validate());
        Assert.Throws<ArgumentException>(() => Fact(NativeMessageFactKind.XmlText, new() { Name = "A", FilterAttribute = null! }).Validate());
    }

    [Fact]
    public void StepsRoundTripThroughTheEditorViewModelWithoutLosingTheNewFields()
    {
        var fact = NativeSourceXmlFactReader.Read("m", "string(//ConfigurationInfo/AdditionalInfo/Setting[@Name='MSH5_1']/@Value)");
        var model = JsonSerializer.Deserialize<List<NativeXmlStepViewModel>>(JsonSerializer.Serialize(fact.Steps, Json), Json)!;
        Assert.True(model[0].Descendant); Assert.Equal("Name", model[2].FilterAttribute); Assert.Equal("MSH5_1", model[2].FilterValue);
        var back = JsonSerializer.Deserialize<List<NativeXmlStep>>(JsonSerializer.Serialize(model, Json), Json)!;
        Assert.Equal(JsonSerializer.Serialize(fact.Steps, Json), JsonSerializer.Serialize(back, Json));
    }

    [Fact]
    public void FlowsSavedBeforeTheNewFieldsExistStillReadAsExactChildPaths()
    {
        var steps = JsonSerializer.Deserialize<List<NativeXmlStep>>("""[{"name":"r","namespace":"","attribute":false}]""", Json)!;
        var fact = new NativeMessageFact { Kind = NativeMessageFactKind.XmlCount, Steps = steps };
        Assert.Equal("1", fact.Read(Input("<r/>")).Value);
        Assert.Equal("0", fact.Read(Input("<x><r/></x>")).Value);
    }
}
