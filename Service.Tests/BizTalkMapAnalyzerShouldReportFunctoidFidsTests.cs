using Service.BizTalkImport;

namespace Service.Tests;

/// <summary>
/// Coverage for the "map functoid" automation gap: since there's no verified, authoritative FID-to-functoid-name
/// table safe to guess from, <see cref="BizTalkMapAnalyzer"/> reports each involved functoid's numeric
/// <c>Functoid-FID</c> type identifier (and how many times it appears) instead of silently guessing a
/// translation - this gives the user a concrete, low-risk lead (look the FID up in BizTalk Mapper) rather than
/// risking a wrong translation.
/// </summary>
public class BizTalkMapAnalyzerShouldReportFunctoidFidsTests
{
    private const string MapTemplate = """
        <?xml version="1.0" encoding="utf-8"?>
        <mapsource Name="BizTalk Map" Version="2" IgnoreNamespacesForLinks="Yes">
          <SrcTree RootNode_Name="Source"><Reference Location="Sample.Schemas.Source" /></SrcTree>
          <TrgTree RootNode_Name="Target"><Reference Location="Sample.Schemas.Target" /></TrgTree>
          <Pages>
            <Page Name="Page 1">
              <Links>
                <Link LinkID="1" LinkFrom="/*[local-name()='&lt;Schema&gt;']/*[local-name()='Source']/*[local-name()='Id']" LinkTo="/*[local-name()='&lt;Schema&gt;']/*[local-name()='Target']/*[local-name()='Id']" />
                <Link LinkID="2" LinkFrom="/*[local-name()='&lt;Schema&gt;']/*[local-name()='Source']/*[local-name()='Name']" LinkTo="1" />
                <Link LinkID="3" LinkFrom="1" LinkTo="/*[local-name()='&lt;Schema&gt;']/*[local-name()='Target']/*[local-name()='Name']" />
              </Links>
              <Functoids>
                <Functoid FunctoidID="1" Functoid-FID="424"><Input-Parameters><Parameter Type="link" Value="2" /></Input-Parameters></Functoid>
                <Functoid FunctoidID="2" Functoid-FID="424"><Input-Parameters><Parameter Type="link" Value="2" /></Input-Parameters></Functoid>
                <Functoid FunctoidID="3" Functoid-FID="260"><Input-Parameters><Parameter Type="link" Value="2" /></Input-Parameters></Functoid>
              </Functoids>
            </Page>
          </Pages>
        </mapsource>
        """;

    private static string WriteBtmFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.btm");
        File.WriteAllText(path, MapTemplate);
        return path;
    }

    [Fact]
    public void ReportsFunctoidFidCounts_GroupedByType()
    {
        var path = WriteBtmFile();

        try
        {
            var result = BizTalkMapAnalyzer.Analyze(path, "SampleMap");

            Assert.NotNull(result);
            Assert.Equal(2, result!.FunctoidFidCounts[424]);
            Assert.Equal(1, result.FunctoidFidCounts[260]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void IncludesFidBreakdown_InGeneratedXsltComment()
    {
        var path = WriteBtmFile();

        try
        {
            var result = BizTalkMapAnalyzer.Analyze(path, "SampleMap");

            Assert.NotNull(result);
            Assert.NotNull(result!.GeneratedXsltSourceCode);
            Assert.Contains("FID 424 x2", result.GeneratedXsltSourceCode);
            Assert.Contains("FID 260 x1", result.GeneratedXsltSourceCode);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReturnsNull_ForUnreadableFile()
    {
        var result = BizTalkMapAnalyzer.Analyze(Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.btm"), "MissingMap");

        Assert.Null(result);
    }
}
