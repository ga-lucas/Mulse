using System.Text.RegularExpressions;
using Mulse.Modules;

namespace Service;

/// <summary>
/// Resolves <c>{{config:reference}}</c> and <c>{{secret:reference}}</c> placeholder tokens - generated
/// by the BizTalk importer (see <c>BizTalkImportService</c> and <c>BizTalkDraftFlowResponse.ConfigurationRequirements</c>)
/// and usable by any hand-authored or designer-created flow - against the values stored in
/// <see cref="IRuntimeConfigurationStore"/>.
/// </summary>
public interface IConfigValueResolver
{
    /// <summary>
    /// Returns a copy of <paramref name="pipeline"/> with every setting value that matches the
    /// <c>{{config:reference}}</c> / <c>{{secret:reference}}</c> token pattern replaced by its stored
    /// value. Throws <see cref="InvalidOperationException"/> naming every unresolved reference if one
    /// or more tokens have no matching stored value, so a flow can never silently run with a literal
    /// placeholder string as a module setting.
    /// </summary>
    PipelineDefinition Resolve(PipelineDefinition pipeline);

    /// <summary>
    /// Scans every flow for <c>{{config:reference}}</c> / <c>{{secret:reference}}</c> tokens (regardless
    /// of whether they came from a BizTalk import or a hand-authored/designer-created flow) and reports
    /// where each reference is used and whether it currently has a stored value. Used to drive a
    /// "configuration follow-up" UI without requiring separate, easily-stale metadata about what a flow
    /// still needs - the placeholder syntax itself is the source of truth.
    /// </summary>
    IReadOnlyList<ConfigReferenceUsage> FindReferenceUsages(IEnumerable<PipelineDefinition> pipelines);
}

/// <summary>One place a <c>{{config:reference}}</c>/<c>{{secret:reference}}</c> token is used in a flow's settings.</summary>
public sealed record ConfigReferenceUsage(string Reference, string FlowId, string SettingPath, bool IsResolved);


public sealed partial class ConfigValueResolver(IRuntimeConfigurationStore runtimeConfigurationStore) : IConfigValueResolver
{
    [GeneratedRegex(@"^\{\{(?:config|secret):(?<reference>.+)\}\}$")]
    private static partial Regex TokenPattern();

    public PipelineDefinition Resolve(PipelineDefinition pipeline)
    {
        var configValues = runtimeConfigurationStore.GetState().ConfigValues;
        var missingReferences = new List<string>();

        var resolved = new PipelineDefinition
        {
            Id = pipeline.Id,
            Enabled = pipeline.Enabled,
            Trigger = pipeline.Trigger,
            Fetch = ResolveStep(pipeline.Fetch, configValues, missingReferences),
            Parse = ResolveStep(pipeline.Parse, configValues, missingReferences),
            Augments = pipeline.Augments.Select(step => ResolveStep(step, configValues, missingReferences)).ToList(),
            Deliveries = pipeline.Deliveries.Select(route => ResolveDelivery(route, configValues, missingReferences)).ToList(),
            Retry = pipeline.Retry
        };

        if (missingReferences.Count > 0)
        {
            var distinctReferences = missingReferences.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            throw new InvalidOperationException(
                $"Flow '{pipeline.Id}' has {distinctReferences.Length} unresolved configuration reference(s): " +
                $"{string.Join(", ", distinctReferences)}. Set these via PUT /api/config-values/{{reference}} " +
                "before enabling or running this flow.");
        }

        return resolved;
    }

    private static ModuleStepDefinition ResolveStep(ModuleStepDefinition step, IReadOnlyDictionary<string, string> configValues, List<string> missingReferences)
    {
        return new ModuleStepDefinition
        {
            Module = step.Module,
            Settings = ResolveSettings(step.Settings, configValues, missingReferences),
            Retry = step.Retry
        };
    }

    private static DeliveryRouteDefinition ResolveDelivery(DeliveryRouteDefinition route, IReadOnlyDictionary<string, string> configValues, List<string> missingReferences)
    {
        return new DeliveryRouteDefinition
        {
            Render = ResolveStep(route.Render, configValues, missingReferences),
            Deliver = ResolveStep(route.Deliver, configValues, missingReferences),
            AtomicScope = route.AtomicScope,
            Compensation = route.Compensation is null ? null : ResolveStep(route.Compensation, configValues, missingReferences)
        };
    }

    private static Dictionary<string, string> ResolveSettings(Dictionary<string, string> settings, IReadOnlyDictionary<string, string> configValues, List<string> missingReferences)
    {
        var resolved = new Dictionary<string, string>(settings, StringComparer.OrdinalIgnoreCase);

        foreach (var (key, value) in settings)
        {
            var match = TokenPattern().Match(value);
            if (!match.Success)
            {
                continue;
            }

            var reference = match.Groups["reference"].Value;
            if (configValues.TryGetValue(reference, out var resolvedValue))
            {
                resolved[key] = resolvedValue;
            }
            else
            {
                missingReferences.Add(reference);
            }
        }

        return resolved;
    }

    public IReadOnlyList<ConfigReferenceUsage> FindReferenceUsages(IEnumerable<PipelineDefinition> pipelines)
    {
        var configValues = runtimeConfigurationStore.GetState().ConfigValues;
        var usages = new List<ConfigReferenceUsage>();

        foreach (var pipeline in pipelines)
        {
            ScanStep(pipeline.Fetch, pipeline.Id, "fetch.settings", configValues, usages);
            ScanStep(pipeline.Parse, pipeline.Id, "parse.settings", configValues, usages);

            for (var i = 0; i < pipeline.Augments.Count; i++)
            {
                ScanStep(pipeline.Augments[i], pipeline.Id, $"augments[{i}].settings", configValues, usages);
            }

            for (var i = 0; i < pipeline.Deliveries.Count; i++)
            {
                var route = pipeline.Deliveries[i];
                ScanStep(route.Render, pipeline.Id, $"deliveries[{i}].render.settings", configValues, usages);
                ScanStep(route.Deliver, pipeline.Id, $"deliveries[{i}].deliver.settings", configValues, usages);
                if (route.Compensation is not null)
                {
                    ScanStep(route.Compensation, pipeline.Id, $"deliveries[{i}].compensation.settings", configValues, usages);
                }
            }
        }

        return usages;
    }

    private static void ScanStep(ModuleStepDefinition step, string flowId, string settingPathPrefix, IReadOnlyDictionary<string, string> configValues, List<ConfigReferenceUsage> usages)
    {
        foreach (var (key, value) in step.Settings)
        {
            var match = TokenPattern().Match(value);
            if (!match.Success)
            {
                continue;
            }

            var reference = match.Groups["reference"].Value;
            usages.Add(new ConfigReferenceUsage(reference, flowId, $"{settingPathPrefix}.{key}", configValues.ContainsKey(reference)));
        }
    }
}
