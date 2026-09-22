using Service.BizTalkImport;

namespace Service.Tests;

/// <summary>
/// Coverage for the "promoted-property routing" automation gap: scanning a custom pipeline component's C#
/// source (e.g. <c>HL7Promotions.cs</c>) for <c>context.Promote(...)</c>/<c>context.Write(...)</c> call sites
/// should recover the promoted property names/namespaces without needing to reproduce the component's actual
/// runtime logic.
/// </summary>
public class BizTalkPromotedPropertyAnalyzerTests
{
    private static string WriteCsFile(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.cs");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void ExtractsPromotedAndWrittenProperties_FromPipelineComponentSource()
    {
        var path = WriteCsFile("""
            namespace Sample.PipelineComponents;

            public class HL7Promotions
            {
                public void Execute(IPipelineContext pContext, IBaseMessage message)
                {
                    var context = message.Context;
                    context.Promote("MessageType", "http://schemas.example.com/hl7", "ADT_A01");
                    context.Write("SendingApplication", "http://schemas.example.com/hl7", "MULSE");
                }
            }
            """);

        try
        {
            var result = BizTalkPromotedPropertyAnalyzer.Analyze([path]);

            Assert.Equal(2, result.Count);
            var promoted = Assert.Single(result, static p => p.IsPromoted);
            Assert.Equal("MessageType", promoted.PropertyName);
            Assert.Equal("http://schemas.example.com/hl7", promoted.Namespace);

            var written = Assert.Single(result, static p => !p.IsPromoted);
            Assert.Equal("SendingApplication", written.PropertyName);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void DeduplicatesIdenticalCallSites_AcrossFiles()
    {
        const string source = """
            context.Promote("MessageType", "http://schemas.example.com/hl7", "ADT_A01");
            """;
        var path1 = WriteCsFile(source);
        var path2 = WriteCsFile(source);

        try
        {
            var result = BizTalkPromotedPropertyAnalyzer.Analyze([path1, path2]);

            Assert.Single(result);
        }
        finally
        {
            File.Delete(path1);
            File.Delete(path2);
        }
    }

    [Fact]
    public void ReturnsEmpty_WhenNoPromoteOrWriteCallsExist()
    {
        var path = WriteCsFile("public class NotAPipelineComponent { public void DoWork() { } }");

        try
        {
            var result = BizTalkPromotedPropertyAnalyzer.Analyze([path]);

            Assert.Empty(result);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void BuildPromotionsJson_ProducesOneEntryPerDistinctProperty_WithTodoSelectors()
    {
        var properties = BizTalkPromotedPropertyAnalyzer.Analyze([WriteAndTrack("""
            context.Promote("MessageType", "http://schemas.example.com/hl7", "ADT_A01");
            context.Write("FacilityId", "http://schemas.example.com/hl7", "FAC1");
            """, out var path)]);

        try
        {
            var json = BizTalkPromotedPropertyAnalyzer.BuildPromotionsJson(properties);

            Assert.NotNull(json);
            Assert.Contains("\"metadataKey\":\"messageType\"", json);
            Assert.Contains("\"metadataKey\":\"facilityId\"", json);
            Assert.Contains("TODO", json);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void BuildPromotionsJson_ReturnsNull_WhenNoPropertiesExtracted()
    {
        var json = BizTalkPromotedPropertyAnalyzer.BuildPromotionsJson([]);

        Assert.Null(json);
    }

    private static string WriteAndTrack(string content, out string path)
    {
        path = WriteCsFile(content);
        return path;
    }
}
