using System.Text.Json.Nodes;

namespace Mulse.Modules.Decisions;

public sealed class DecisionAugmentModule : IOrchestrationAugmentModule
{
    private const string DecisionJsonSetting = "decisionJson";
    private static readonly IReadOnlyList<ModuleSettingDescriptor> SettingDescriptors =
    [
        new(DecisionJsonSetting, "Decision rules", "A JSON array of decision rules. Each rule can test payload JSON, metadata, payload name, or content type and then set metadata, update JSON fields, rename payloads, or drop payloads.", true, ModuleSettingInputKind.TextArea,
            "[{\"name\":\"route-high-priority\",\"conditions\":[{\"source\":\"Payload\",\"path\":\"$.priority\",\"operator\":\"Equals\",\"value\":\"High\"}],\"actions\":[{\"kind\":\"SetMetadata\",\"target\":\"targetRoute\",\"value\":\"priority\"}]}]"),
        new("stopAfterFirstMatch", "Stop after first match", "Stops evaluating more rules for a payload after the first matching rule is applied.", false, ModuleSettingInputKind.Boolean, "true")
    ];

    private static readonly ModuleRecommendationProfile Recommendation = new(
        [ModuleDataFormat.Json],
        capabilities: [ModuleCapability.Transformation, ModuleCapability.Decision]);

    public ModuleDescriptor Descriptor { get; } = new(
        "decision-augment",
        "Decision augment",
        ModuleKind.OrchestrationAugment,
        "Evaluates programmable rules against payload content and metadata so downstream parse, augment, render, and deliver steps can branch declaratively.",
        SettingDescriptors,
        Recommendation);

    public Task<IntegrationBatch> AugmentAsync(
        FlowExecutionContext context,
        IntegrationBatch batch,
        ModuleStepDefinition step,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var decisionJson = ModuleSettingReader.GetRequired(step.Settings, DecisionJsonSetting, Descriptor.Id);
        var stopAfterFirstMatch = ModuleSettingReader.GetBoolean(step.Settings, "stopAfterFirstMatch", defaultValue: true, Descriptor.Id);
        var rules = DecisionRuntime.ParseRules(decisionJson, Descriptor.Id);
        var transformedPayloads = new List<IntegrationPayload>(batch.Payloads.Count);

        foreach (var payload in batch.Payloads)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = new DecisionPayloadState(payload);

            foreach (var rule in rules)
            {
                if (!DecisionRuntime.MatchesAll(state.BuildPayload(), rule.Conditions, Descriptor.Id))
                {
                    continue;
                }

                foreach (var action in rule.Actions)
                {
                    ApplyAction(state, action);
                    if (state.DropPayload)
                    {
                        break;
                    }
                }

                if (state.DropPayload || stopAfterFirstMatch)
                {
                    break;
                }
            }

            if (!state.DropPayload)
            {
                transformedPayloads.Add(state.BuildPayload());
            }
        }

        return Task.FromResult(new IntegrationBatch(transformedPayloads));
    }

    private void ApplyAction(DecisionPayloadState state, DecisionActionDefinition action)
    {
        switch (action.Kind)
        {
            case DecisionActionKind.SetMetadata:
                state.SetMetadata(action.Target, DecisionRuntime.ResolveActionValue(state.BuildPayload(), action, Descriptor.Id));
                break;
            case DecisionActionKind.RemoveMetadata:
                state.RemoveMetadata(action.Target);
                break;
            case DecisionActionKind.SetField:
                state.SetField(action.Target, DecisionRuntime.ResolveActionNode(state.BuildPayload(), action, Descriptor.Id), Descriptor.Id);
                break;
            case DecisionActionKind.RemoveField:
                state.RemoveField(action.Target, Descriptor.Id);
                break;
            case DecisionActionKind.RenamePayload:
                state.RenamePayload(DecisionRuntime.ResolveActionValue(state.BuildPayload(), action, Descriptor.Id));
                break;
            case DecisionActionKind.SetContentType:
                state.SetContentType(DecisionRuntime.ResolveActionValue(state.BuildPayload(), action, Descriptor.Id));
                break;
            case DecisionActionKind.DropPayload:
                state.MarkDropped();
                break;
        }
    }

    private sealed class DecisionPayloadState
    {
        private readonly IntegrationPayload _originalPayload;
        private readonly Dictionary<string, string> _metadata;
        private JsonNode? _jsonNode;
        private bool _jsonDirty;

        public DecisionPayloadState(IntegrationPayload payload)
        {
            _originalPayload = payload;
            _metadata = new Dictionary<string, string>(payload.Metadata, StringComparer.OrdinalIgnoreCase);
            Name = payload.Name;
            ContentType = payload.ContentType;
        }

        public string Name { get; private set; }

        public string ContentType { get; private set; }

        public bool DropPayload { get; private set; }

        public IntegrationPayload BuildPayload()
        {
            var content = _jsonDirty
                ? BinaryData.FromString(_jsonNode?.ToJsonString() ?? string.Empty)
                : _originalPayload.Content;

            return new IntegrationPayload(Name, content, ContentType, _metadata);
        }

        public void SetMetadata(string key, string? value)
        {
            if (string.IsNullOrWhiteSpace(key) || value is null)
            {
                return;
            }

            _metadata[key] = value;
        }

        public void RemoveMetadata(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return;
            }

            _metadata.Remove(key);
        }

        public void SetField(string targetPath, JsonNode? value, string moduleId)
        {
            if (string.IsNullOrWhiteSpace(targetPath) || value is null)
            {
                return;
            }

            var root = EnsureJsonObject(moduleId);
            JsonPayloadNavigator.SetNode(root, targetPath, value);
            _jsonDirty = true;
            ContentType = "application/json";
        }

        public void RemoveField(string targetPath, string moduleId)
        {
            if (string.IsNullOrWhiteSpace(targetPath))
            {
                return;
            }

            var root = EnsureJsonObject(moduleId);
            JsonPayloadNavigator.RemoveNode(root, targetPath);
            _jsonDirty = true;
            ContentType = "application/json";
        }

        public void RenamePayload(string? name)
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                Name = name;
            }
        }

        public void SetContentType(string? contentType)
        {
            if (!string.IsNullOrWhiteSpace(contentType))
            {
                ContentType = contentType;
            }
        }

        public void MarkDropped()
        {
            DropPayload = true;
        }

        private JsonObject EnsureJsonObject(string moduleId)
        {
            _jsonNode ??= JsonPayloadNavigator.Parse(_originalPayload.Content, moduleId, _originalPayload.Name);
            if (_jsonNode is JsonObject jsonObject)
            {
                return jsonObject;
            }

            throw new InvalidOperationException($"Module '{moduleId}' can only set or remove fields on JSON object payloads.");
        }
    }
}
