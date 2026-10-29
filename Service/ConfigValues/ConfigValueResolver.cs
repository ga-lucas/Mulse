using System.Text.RegularExpressions;
using Mulse.Modules;

namespace Service.ConfigValues;

/// <summary>
/// Resolves <c>{{config:reference}}</c> and <c>{{secret:reference}}</c> placeholder tokens - generated
/// by the BizTalk importer (see <c>BizTalkImportService</c> and <c>BizTalkDraftFlowResponse.ConfigurationRequirements</c>)
/// and usable by any hand-authored or designer-created flow - against the values stored in
/// <see cref="IRuntimeConfigurationStore"/>.
/// </summary>
public interface IConfigValueResolver
{
    /// <summary>
    /// Returns a copy of <paramref name="pipeline"/> with each whole-value or embedded
    /// <c>{{config:reference}}</c> / <c>{{secret:reference}}</c> token replaced by its stored
    /// value in a single pass, without recursively expanding stored values. Throws
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
    [GeneratedRegex(@"\{\{(?:config|secret):(?<reference>[^{}\r\n]+)\}\}")]
    internal static partial Regex TokenPattern();

    public PipelineDefinition Resolve(PipelineDefinition pipeline)
    {
        var configValues = runtimeConfigurationStore.GetState().ConfigValues;
        var missingReferences = new List<string>();

        var resolved = new PipelineDefinition
        {
            Id = pipeline.Id,
            Name = pipeline.Name,
            Category = pipeline.Category,
            Enabled = pipeline.Enabled,
            Trigger = pipeline.Trigger,
            Fetches = pipeline.Fetches.Select(fetch => ResolveFetchNode(fetch, configValues, missingReferences)).ToList(),
            Parses = pipeline.Parses.Select(parse => ResolveParseNode(parse, configValues, missingReferences)).ToList(),
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

    private static FetchNodeDefinition ResolveFetchNode(FetchNodeDefinition fetch, IReadOnlyDictionary<string, string> configValues, List<string> missingReferences)
    {
        return new FetchNodeDefinition
        {
            Id = fetch.Id,
            Fetch = ResolveStep(fetch.Fetch, configValues, missingReferences),
            InputParseIds = fetch.InputParseIds.ToList()
        };
    }

    private static ParseNodeDefinition ResolveParseNode(ParseNodeDefinition parse, IReadOnlyDictionary<string, string> configValues, List<string> missingReferences)
    {
        return new ParseNodeDefinition
        {
            Id = parse.Id,
            Parse = ResolveStep(parse.Parse, configValues, missingReferences),
            FetchNodeIds = parse.FetchNodeIds.ToList()
        };
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
            Delivers = route.Delivers.Select(action => new DeliveryActionDefinition
            {
                Deliver = ResolveStep(action.Deliver, configValues, missingReferences),
                Compensation = action.Compensation is null ? null : ResolveStep(action.Compensation, configValues, missingReferences)
            }).ToList(),
            AtomicScope = route.AtomicScope
        };
    }

    private static Dictionary<string, string> ResolveSettings(Dictionary<string, string> settings, IReadOnlyDictionary<string, string> configValues, List<string> missingReferences)
    {
        var resolved = new Dictionary<string, string>(settings, StringComparer.OrdinalIgnoreCase);

        foreach (var (key, value) in settings)
        {
            resolved[key] = Substitute(value, configValues, missingReferences.Add);
        }

        return resolved;
    }

    internal static string Substitute(string value, IReadOnlyDictionary<string, string>? configValues, Action<string> onMissing)
        => TokenPattern().Replace(value, match =>
        {
            var reference = match.Groups["reference"].Value;
            if (configValues is not null && configValues.TryGetValue(reference, out var storedValue))
            {
                return storedValue;
            }

            onMissing(reference);
            return match.Value;
        });

    public IReadOnlyList<ConfigReferenceUsage> FindReferenceUsages(IEnumerable<PipelineDefinition> pipelines)
    {
        var configValues = runtimeConfigurationStore.GetState().ConfigValues;
        var usages = new List<ConfigReferenceUsage>();

        foreach (var pipeline in pipelines)
        {
            for (var i = 0; i < pipeline.Fetches.Count; i++)
            {
                ScanStep(pipeline.Fetches[i].Fetch, pipeline.Id, $"fetches[{i}].fetch.settings", configValues, usages);
            }

            for (var i = 0; i < pipeline.Parses.Count; i++)
            {
                ScanStep(pipeline.Parses[i].Parse, pipeline.Id, $"parses[{i}].parse.settings", configValues, usages);
            }

            for (var i = 0; i < pipeline.Augments.Count; i++)
            {
                ScanStep(pipeline.Augments[i], pipeline.Id, $"augments[{i}].settings", configValues, usages);
            }

            for (var i = 0; i < pipeline.Deliveries.Count; i++)
            {
                var route = pipeline.Deliveries[i];
                ScanStep(route.Render, pipeline.Id, $"deliveries[{i}].render.settings", configValues, usages);
                for (var j = 0; j < route.Delivers.Count; j++)
                {
                    var action = route.Delivers[j];
                    ScanStep(action.Deliver, pipeline.Id, $"deliveries[{i}].delivers[{j}].deliver.settings", configValues, usages);
                    if (action.Compensation is not null)
                    {
                        ScanStep(action.Compensation, pipeline.Id, $"deliveries[{i}].delivers[{j}].compensation.settings", configValues, usages);
                    }
                }
            }
        }

        return usages;
    }

    private static void ScanStep(ModuleStepDefinition step, string flowId, string settingPathPrefix, IReadOnlyDictionary<string, string> configValues, List<ConfigReferenceUsage> usages)
    {
        foreach (var (key, value) in step.Settings)
        {
            var references = TokenPattern().Matches(value).Select(match => match.Groups["reference"].Value)
                .Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var reference in references)
            {
                usages.Add(new ConfigReferenceUsage(reference, flowId, $"{settingPathPrefix}.{key}", configValues.ContainsKey(reference)));
            }
        }
    }
}
