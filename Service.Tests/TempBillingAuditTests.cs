using Mulse.Modules.ControlFlow;
using Service.BizTalkImport;
using Service.Models;

namespace Service.Tests;

public sealed class TempBillingAuditTests
{
    [Fact]
    public async Task Audit()
    {
        var root = @"C:\Users\LXS0610\source\repos\SpaceTRAX Interface\Shs.SpaceTrax2.Interfaces";
        var docs = @"C:\Users\LXS0610\OneDrive - Securitas\Documents";
        var result = await new BizTalkImportService().AnalyzeAsync(new AnalyzeBizTalkSolutionRequest
        {
            SourcePath = Path.Combine(root, "Core.Process.Billing"),
            AdditionalSourcePaths = [root],
            AdditionalBindingPaths = [Path.Combine(docs, "bt bindings interfaces.xml")],
            AdditionalRulePaths = [Path.Combine(docs, "rules.xml")],
            GenerateNativeFlow = true,
        }, default);
        var lines = new List<string>();
        foreach (var candidate in result.FlowCandidates)
        {
            if (candidate.DraftFlow.Augments.FirstOrDefault(augment => augment.Settings.ContainsKey("flowJson")) is not { } native) continue;
            var json = native.Settings["flowJson"];
            var definition = NativeFlowDefinition.Read(json, allowBlocked: true);
            var blocked = definition.AllNodes().Where(node => node.Kind == NativeFlowNodeKind.Blocked).ToArray();
            lines.Add($"## {candidate.SuggestedFlowId}: blocked={blocked.Length} total={definition.AllNodes().Count()}");
            foreach (var group in blocked.GroupBy(node => node.BlockReason.Length > 160 ? node.BlockReason[..160] : node.BlockReason).OrderByDescending(group => group.Count()))
                lines.Add($"{group.Count(),4}  {group.Key}");
        }
        File.WriteAllLines(Path.Combine(Path.GetTempPath(), "billing-audit.txt"), lines);
    }
}
