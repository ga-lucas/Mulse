using Service.BizTalkImport;

namespace Service.Tests;

/// <summary>
/// Regression coverage for the "duplicate decision-augment rule" gap: a BizTalk orchestration where the same
/// simple condition (e.g. <c>OutputFormat == "FLAT"</c>) appears in several distinct Decision shapes must not
/// produce byte-for-byte duplicate rules in the generated <c>decisionJson</c> - only the first occurrence of
/// each distinct conditions+actions combination should be kept.
/// </summary>
public class BizTalkOrchestrationDecisionAnalyzerShouldDedupeRulesTests
{
    private const string DesignerDataTemplate = """
        // #if __DESIGNER_DATA
        <?xml version="1.0" encoding="utf-16"?>
        <om:MetaModel MajorVersion="1" MinorVersion="0" Core="STC" xmlns:om="http://schemas.microsoft.com/BizTalk/2003/DesignerData">
          <om:Element Type="ServiceDeclaration">
            <om:Property Name="Name" Value="TestOrchestration" />
            {0}
          </om:Element>
        </om:MetaModel>
        // #endif
        """;

    private static string BuildDecisionElement(string decisionName, string branchName, string expression) => $"""
        <om:Element Type="Decision">
          <om:Property Name="Name" Value="{decisionName}" />
          <om:Element Type="DecisionBranch">
            <om:Property Name="Name" Value="{branchName}" />
            <om:Property Name="Expression" Value="{expression}" />
          </om:Element>
        </om:Element>
        """;

    private static string WriteOdxFile(string designerXml)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.odx");
        File.WriteAllText(path, string.Format(DesignerDataTemplate, designerXml));
        return path;
    }

    [Fact]
    public void MergesIdenticalRules_WhenSameConditionAppearsInMultipleDecisionShapes()
    {
        // Mirrors the real-world RequisitionBatchWithTrailerProcess.odx case: the same simple condition
        // repeated across several distinct Decision shapes (e.g. once per delivery-route branch).
        var decisions = string.Join(
            Environment.NewLine,
            Enumerable.Range(1, 5).Select(i => BuildDecisionElement("OutputFormatDecision", "Flat", "msgBatch.OutputFormat == &quot;FLAT&quot;")));
        var odxPath = WriteOdxFile(decisions);

        try
        {
            var result = BizTalkOrchestrationDecisionAnalyzer.Analyze(odxPath, "TestOrchestration", "Migrated.TestOrchestration");

            Assert.NotNull(result.GeneratedDecisionJson);
            Assert.Equal(1, CountRuleOccurrences(result.GeneratedDecisionJson!, "\"name\""));
        }
        finally
        {
            File.Delete(odxPath);
        }
    }

    [Fact]
    public void KeepsDistinctRules_WhenBranchesDifferInConditionOrAction()
    {
        var decisions = string.Join(
            Environment.NewLine,
            BuildDecisionElement("OutputFormatDecision", "Flat", "msgBatch.OutputFormat == &quot;FLAT&quot;"),
            BuildDecisionElement("OutputFormatDecision", "Xml", "msgBatch.OutputFormat == &quot;XML&quot;"));
        var odxPath = WriteOdxFile(decisions);

        try
        {
            var result = BizTalkOrchestrationDecisionAnalyzer.Analyze(odxPath, "TestOrchestration", "Migrated.TestOrchestration");

            Assert.NotNull(result.GeneratedDecisionJson);
            Assert.Equal(2, CountRuleOccurrences(result.GeneratedDecisionJson!, "\"name\""));
        }
        finally
        {
            File.Delete(odxPath);
        }
    }

    private static int CountRuleOccurrences(string json, string marker)
    {
        var count = 0;
        var index = 0;
        while ((index = json.IndexOf(marker, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            index += marker.Length;
        }

        return count;
    }
}
