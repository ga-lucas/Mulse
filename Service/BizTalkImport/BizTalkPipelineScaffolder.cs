using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Service.Models;

namespace Service.BizTalkImport;

internal static class BizTalkPipelineScaffolder
{
    public static BizTalkScaffoldedModuleResponse Create(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var identifier = new string(name.Take(64).Select(character => char.IsAsciiLetterOrDigit(character) ? character : '_').ToArray());
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant())))[..16];
        var className = $"Pipeline_{identifier}_{hash}";
        var moduleId = $"biztalk-pipeline-{hash.ToLowerInvariant()}";
        var (inventory, diagnostics) = BizTalkPipelineInventoryAnalyzer.Analyze(path);
        var evidence = new StringBuilder();
        evidence.AppendLine("// Source inventory only: no property values, executable component code or deployment identity.");
        evidence.AppendLine("// Review the correct native parse/augment/render stages before implementing and wiring this placeholder.");
        if (inventory is null) evidence.AppendLine("// Pipeline inventory unavailable. Inspect the blocking migration diagnostics and original source.");
        else
        {
            evidence.AppendLine($"// Direction: {inventory.Direction}; stages: {inventory.TotalStages}; components: {inventory.TotalComponents}.");
            foreach (var stage in inventory.Stages)
            {
                evidence.AppendLine("// Stage evidence: " + JsonSerializer.Serialize(new { stage.Order, stage.SourceLine, stage.CategoryId, stage.StageName }));
                foreach (var component in stage.Components)
                    evidence.AppendLine("// Component evidence: " + JsonSerializer.Serialize(component));
            }
        }
        foreach (var diagnostic in diagnostics)
            evidence.AppendLine("// Blocking inventory diagnostic: " + diagnostic.Code);
        var source = $$"""
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Mulse.Modules.Abstractions;
            using Mulse.Modules.Execution;
            using Mulse.Modules.Payloads;
            using Mulse.Modules.Pipeline;

            namespace Mulse.Migration.Generated;

            {{evidence}}
            // Implement and test every component, its configuration, ordering and context effects.
            // This placeholder is not wired automatically. Registration does not approve imported execution.
            public sealed class {{className}} : IOrchestrationAugmentModule
            {
                public ModuleDescriptor Descriptor { get; } = new(
                    {{JsonSerializer.Serialize(moduleId)}},
                    {{JsonSerializer.Serialize(name + " (unimplemented migration stub)")}},
                    ModuleKind.OrchestrationAugment,
                    "Untranslated BizTalk pipeline placeholder. Choose and implement the correct native stages before use.");

                public Task<IntegrationBatch> AugmentAsync(
                    FlowExecutionContext context,
                    IntegrationBatch batch,
                    ModuleStepDefinition step,
                    CancellationToken cancellationToken)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    throw new NotSupportedException("This BizTalk pipeline stub has not been implemented. Translate and verify all stages before use.");
                }
            }
            """;
        return new(className + ".cs", moduleId, name, source);
    }
}
