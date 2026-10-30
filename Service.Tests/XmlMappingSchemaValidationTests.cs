using System.Text.Json;
using System.Xml.Linq;
using Mulse.CompatibilityPack;
using Mulse.Modules.Mapping;

namespace Service.Tests;

public sealed class XmlMappingSchemaValidationTests
{
    [Fact]
    public void ConvertedMapping_RetainsExactSchemaTextAndEnforcesBothSides()
    {
        var conversion = Service.Mapping.BizTalkNativeMapConverter.Convert(NativeMapImportTests.Map(), [NativeMapImportTests.SourceXsd], [NativeMapImportTests.TargetXsd]);
        var map = JsonSerializer.Deserialize<XmlMappingDefinition>(conversion.MappingJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(2, map.Version);
        Assert.Equal(NativeMapImportTests.SourceXsd, Assert.Single(map.SourceSchemas));
        Assert.Equal(NativeMapImportTests.TargetXsd, Assert.Single(map.TargetSchemas));
        var engine = new XmlMappingEngine(conversion.MappingJson);
        Assert.Contains("Source XSD validation failed", Assert.Throws<ArgumentException>(() => engine.Transform("<Order xmlns='urn:source'><Id>1</Id></Order>")).Message);
        Assert.Contains("Source XSD validation failed", Assert.Throws<ArgumentException>(() => engine.Transform("<Order xmlns='urn:wrong' code='x'><Id>1</Id></Order>")).Message);
        var numeric = map with { TargetSchemas = [NativeMapImportTests.TargetXsd.Replace("name=\"Number\" type=\"xs:string\"", "name=\"Number\" type=\"xs:int\"", StringComparison.Ordinal)] };
        Assert.Contains("Target XSD validation failed", Assert.Throws<ArgumentException>(() => new XmlMappingEngine(XmlMappingTests.Json(numeric)).Transform("<Order xmlns='urn:source' code='x'><Id>not a number</Id></Order>")).Message);
    }

    [Fact]
    public void OptionalPoliciesCannotBypassTargetSchemaRequirements()
    {
        var map = new XmlMappingDefinition
        {
            Version = 2, TargetRoot = "t:Result", Namespaces = new() { ["t"] = "urn:target" }, TargetSchemas = [NativeMapImportTests.TargetXsd],
            Fields = [new() { SourcePath = "/Input/Number", TargetPath = "Number", MissingValueBehavior = MissingXmlValueBehavior.Omit }]
        };
        Assert.Contains("Target XSD validation failed", Assert.Throws<ArgumentException>(() => new XmlMappingEngine(XmlMappingTests.Json(map)).Transform("<Input/>" )).Message);
    }

    [Theory]
    [InlineData("<!DOCTYPE schema SYSTEM 'file:///not-read'><schema/>")]
    [InlineData("<xs:schema xmlns:xs='http://www.w3.org/2001/XMLSchema'><xs:include schemaLocation='https://invalid.example/not-loaded'/></xs:schema>")]
    public void PersistedSchemas_CannotLoadExternalResources(string schema)
    {
        var map = XmlMappingTests.Map with { Version = 2, TargetSchemas = [schema] };
        Assert.Throws<ArgumentException>(() => new XmlMappingEngine(XmlMappingTests.Json(map)));
    }

    [Fact]
    public async Task RenderModule_RejectsInvalidOutputBeforeReturningPayloads()
    {
        var conversion = Service.Mapping.BizTalkNativeMapConverter.Convert(NativeMapImportTests.Map(), [NativeMapImportTests.SourceXsd], [NativeMapImportTests.TargetXsd]);
        var module = new XmlMappingRenderModule();
        var batch = new IntegrationBatch([new IntegrationPayload("test.xml", BinaryData.FromString("<Wrong/>"), "application/xml", new Dictionary<string, string>())]);
        await Assert.ThrowsAsync<ArgumentException>(() => module.RenderAsync(new FlowExecutionContext("test", "execution", DateTimeOffset.UtcNow), batch,
            new ModuleStepDefinition { Module = module.Descriptor.Id, Settings = new() { ["mappingJson"] = conversion.MappingJson } }, default));
    }

    [Fact]
    public void ConstantOnlyConversion_EnforcesSelectedSourceRootEvenWithMultipleGlobalSchemas()
    {
        var document = XDocument.Parse(NativeMapImportTests.Map());
        document.Descendants("Links").Single().Remove();
        var constants = document.Descendants("ConstantValues").Single();
        foreach (var target in new[] { "Result/Number", "Result/@code" })
            constants.Add(new XElement("Value", new XAttribute("Query", NativeMapImportTests.Path(target)), new XAttribute("value", "1")));
        var schema = NativeMapImportTests.SourceXsd.Replace("</xs:schema>", "<xs:element name='Other' type='xs:string'/></xs:schema>", StringComparison.Ordinal);
        var conversion = Service.Mapping.BizTalkNativeMapConverter.Convert(document.ToString(), [schema], [NativeMapImportTests.TargetXsd]);
        var engine = new XmlMappingEngine(conversion.MappingJson);
        Assert.Contains("source root", Assert.Throws<ArgumentException>(() => engine.Transform("<Other xmlns='urn:source'/>" )).Message);
        Assert.Contains("Number", engine.Transform("<Order xmlns='urn:source' code='x'><Id>1</Id></Order>"));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("s:Order[1]")]
    [InlineData("missing:Order")]
    public void RequiredSourceRoot_RejectsExpressionsAndUndeclaredPrefixes(string root)
        => Assert.Throws<ArgumentException>(() => new XmlMappingEngine(XmlMappingTests.Json(XmlMappingTests.Map with { Version = 2, SourceRoot = root })));

    [Fact]
    public void NillableTarget_IsNotClaimedAsCompleteScalarTranslation()
    {
        var target = NativeMapImportTests.TargetXsd.Replace("name=\"Number\"", "name=\"Number\" nillable=\"true\"", StringComparison.Ordinal);
        Assert.Contains("Nillable", Assert.Throws<ArgumentException>(() => Service.Mapping.BizTalkNativeMapConverter.Convert(
            NativeMapImportTests.Map(), [NativeMapImportTests.SourceXsd], [target])).Message);
    }

    [Theory]
    [InlineData(1_000_001)]
    [InlineData(2_000_001)]
    public void XmlFileInspection_BoundsCharactersAndBytesBeforeParsing(int length)
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, new string('x', length));
            Assert.Throws<ArgumentException>(() => XmlMappingSchemas.ReadXmlFile(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void XmlFileInspection_HandlesBomAndPreservesSourceLines()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "<root>\n<child/>\n</root>", new System.Text.UTF8Encoding(true));
            var document = XmlMappingSchemas.ReadXmlFile(path);
            Assert.Equal(2, ((System.Xml.IXmlLineInfo)document.Root!.Element("child")!).LineNumber);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Version1DefinitionsCannotAcquireSchemaValidationImplicitly()
    {
        Assert.Throws<ArgumentException>(() => new XmlMappingEngine(XmlMappingTests.Json(XmlMappingTests.Map with { SourceSchemas = [NativeMapImportTests.SourceXsd] })));
        Assert.Throws<ArgumentException>(() => new XmlMappingEngine(XmlMappingTests.Json(XmlMappingTests.Map with { SourceRoot = "Input" })));
    }
}
