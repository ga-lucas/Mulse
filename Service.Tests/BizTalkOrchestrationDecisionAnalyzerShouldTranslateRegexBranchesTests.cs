using Service.BizTalkImport;

namespace Service.Tests;

/// <summary>
/// Coverage for the "regex-based decision branch" automation gap: a BizTalk Decision branch expression using
/// <c>Regex.IsMatch(path, "pattern")</c> should auto-translate into a declarative <c>decision-augment</c> rule
/// using the <c>regexMatch</c> comparison operator, instead of falling back to a scaffolded C# module.
/// </summary>
public class BizTalkOrchestrationDecisionAnalyzerShouldTranslateRegexBranchesTests
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
    public void TranslatesRegexIsMatchConjunct_IntoRegexMatchRule()
    {
        var odxPath = WriteOdxFile(BuildDecisionElement(
            "AccountIdDecision",
            "ValidFormat",
            @"Regex.IsMatch(msgBilling.AccountId, &quot;^\d{6}$&quot;)"));

        try
        {
            var result = BizTalkOrchestrationDecisionAnalyzer.Analyze(odxPath, "TestOrchestration", "Migrated.TestOrchestration");

            Assert.NotNull(result.GeneratedDecisionJson);
            Assert.Empty(result.ComplexBranches);
            Assert.Contains("\"regexMatch\"", result.GeneratedDecisionJson, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("$.AccountId", result.GeneratedDecisionJson);
            Assert.Contains(@"^\\d{6}$", result.GeneratedDecisionJson);
        }
        finally
        {
            File.Delete(odxPath);
        }
    }

    [Fact]
    public void TranslatesRegexIsMatch_CombinedWithSimpleComparisonViaAnd()
    {
        var odxPath = WriteOdxFile(BuildDecisionElement(
            "CombinedDecision",
            "Matches",
            @"Regex.IsMatch(msgBilling.AccountId, &quot;^\d+$&quot;) &amp;&amp; msgBilling.IsActive == true"));

        try
        {
            var result = BizTalkOrchestrationDecisionAnalyzer.Analyze(odxPath, "TestOrchestration", "Migrated.TestOrchestration");

            Assert.NotNull(result.GeneratedDecisionJson);
            Assert.Empty(result.ComplexBranches);
            Assert.Contains("\"regexMatch\"", result.GeneratedDecisionJson, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("\"isTrue\"", result.GeneratedDecisionJson, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(odxPath);
        }
    }

    [Fact]
    public void BailsToComplex_WhenRegexIsMatchIsNegated()
    {
        // No "NotRegexMatch" operator exists - negation must not be silently mistranslated.
        var odxPath = WriteOdxFile(BuildDecisionElement(
            "NegatedDecision",
            "NotMatching",
            @"!Regex.IsMatch(msgBilling.AccountId, &quot;^\d+$&quot;)"));

        try
        {
            var result = BizTalkOrchestrationDecisionAnalyzer.Analyze(odxPath, "TestOrchestration", "Migrated.TestOrchestration");

            Assert.Null(result.GeneratedDecisionJson);
            Assert.Single(result.ComplexBranches);
            Assert.NotNull(result.ScaffoldedModuleSourceCode);
        }
        finally
        {
            File.Delete(odxPath);
        }
    }

    [Fact]
    public void BailsToComplex_WhenExpressionHasUnrelatedMethodCall()
    {
        var odxPath = WriteOdxFile(BuildDecisionElement(
            "OtherCallDecision",
            "Branch",
            "msgBilling.AccountId.Contains(&quot;X&quot;)"));

        try
        {
            var result = BizTalkOrchestrationDecisionAnalyzer.Analyze(odxPath, "TestOrchestration", "Migrated.TestOrchestration");

            Assert.Null(result.GeneratedDecisionJson);
            Assert.Single(result.ComplexBranches);
        }
        finally
        {
            File.Delete(odxPath);
        }
    }
}
