using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Mulse.Modules.Mapping;
using Mulse.Modules.Orchestration;

namespace Mulse.Modules.BuiltIn.Augment;

/// <summary>Runs a configured, fully bound native ODX subset, not an imported-flow approval.</summary>
public sealed class OdxProgramAugmentModule(IFlowOrchestrationStateStore store, TimeProvider clock, IRenderModuleInvoker renderers) : IOrchestrationAugmentModule
{
    public const string ModuleId = "odx-program-augment";
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { MaxDepth = 256, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    public ModuleDescriptor Descriptor { get; } = new(ModuleId, "Durable ODX program", ModuleKind.OrchestrationAugment,
        "Runs a bounded native ODX subset with persisted program position, source receive correlations, AND Equals/Exists filters and timers. Unsupported source behavior blocks before effects. Does not approve imported Billing execution.",
        [
            new("programJson", "Source program JSON", "Private version-1 control-flow source model. Unsupported nodes fail before effects.", true, ModuleSettingInputKind.TextArea),
            new("referencedProgramsJson", "Referenced source models", "Private array of source models supplying exact external correlation type definitions; their services are never executed.", false, ModuleSettingInputKind.TextArea, "[]"),
            new("initialFactsJson", "Initial typed facts", "JSON object of initialized scalar/object facts. Applied only to a new instance.", false, ModuleSettingInputKind.TextArea, "{}"),
            new("correlationMetadataKey", "Instance metadata key", "Explicit case-sensitive instance key. Blank derives the instance from source correlation sets initialized by the first receive.", false, ModuleSettingInputKind.Text, "group"),
            new("messageIdMetadataKey", "Stable message ID metadata key", "A stable, unique per-source message identity, retained across fetch retries.", true, ModuleSettingInputKind.Text, "messageId"),
            new("inputPort", "Incoming logical port", "Exact ODX logical port used by input messages. No destination address is inferred.", true),
            new("inputOperation", "Incoming operation", "Exact source operation name when present on receives; blank only for models without operation metadata.", false),
            new("inputOperationMessage", "Incoming operation message", "Exact source Request/Response identity when present on receives.", false),
            new("receiveCorrelationKeysJson", "Receive correlation metadata keys", "Additional exact metadata keys pinned from the first input. Source correlation declarations are compiled separately.", false, ModuleSettingInputKind.TextArea, "[]"),
            new("delaysJson", "Explicit native timer bindings", "Array of {nodeId, seconds}; 0-31622400 seconds, pinned once at each entered wait. This is an explicit native timer, not source DateTime interpretation.", false, ModuleSettingInputKind.TextArea, "[]"),
            new("casesJson", "Dynamic map choices", "Allowlisted source map identities, renderer modules and settings. Empty array when no dynamic transforms are used.", false, ModuleSettingInputKind.TextArea, "[]"),
            new("outputMessage", "Completed output message", "Named message returned to downstream render/delivery. Blank preserves the working batch.", false),
            new("replyPort", "Request/reply port", "Optional logical request port whose Response sends are captured as native replies, not delivered remotely.", false)
        ]);

    public async Task<IntegrationBatch> AugmentAsync(FlowExecutionContext context, IntegrationBatch batch, ModuleStepDefinition step, CancellationToken cancellationToken)
    {
        var source = ModuleSettingReader.GetRequired(step.Settings, "programJson", ModuleId);
        if (source.Length > 8_000_000) throw new ArgumentException("ODX program JSON exceeds 8,000,000 characters.");
        var program = JsonSerializer.Deserialize<OdxProgram>(source, Options) ?? throw new ArgumentException("Missing ODX program.");
        var nodes = Flatten(program.Roots).Take(10001).ToArray();
        if (nodes.Length > 10000) throw new ArgumentException("ODX program exceeds 10000 nodes.");
        var entry = nodes.SingleOrDefault(node => node.ShapeType == "ServiceDeclaration") ?? throw new ArgumentException("Configure exactly one ODX service.");
        var frame = new OdxExecutionFrame(program, context, batch, step);
        var factsJson = step.Settings.GetValueOrDefault("initialFactsJson", "{}");
        if (factsJson.Length > 262144) throw new ArgumentException("Initial ODX facts exceed 262144 characters.");
        if (JsonNode.Parse(factsJson) is not JsonObject facts) throw new ArgumentException("Initial ODX facts must be an object.");
        foreach (var fact in facts) frame.Variables.Add(fact.Key, fact.Value?.DeepClone());
        var correlationField = step.Settings.GetValueOrDefault("correlationMetadataKey", "group");
        var correlation = context.OrchestrationCorrelationKey;
        OdxIncomingMessage? incoming = null;
        if (batch.Count > 0)
        {
            if (batch.Count != 1) throw new ArgumentException("Durable ODX input requires exactly one source message per execution.");
            var metadata = batch.Payloads[0].Metadata;
            if (!string.IsNullOrWhiteSpace(correlationField))
            {
                if (!OdxReceiveBindings.TryExactMetadata(batch.Payloads[0], correlationField, out var inputCorrelation) || string.IsNullOrWhiteSpace(inputCorrelation) || inputCorrelation.Length > 2048)
                    throw new ArgumentException("Input requires a nonblank ODX instance correlation value of at most 2048 characters.");
                // Store keys are case-insensitive; a digest preserves distinct source values without changing other modules.
                correlation = "odx:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(inputCorrelation)));
            }
            var messageIdKey = ModuleSettingReader.GetRequired(step.Settings, "messageIdMetadataKey", ModuleId);
            if (!metadata.TryGetValue(messageIdKey, out var messageId) || string.IsNullOrWhiteSpace(messageId)) throw new ArgumentException("Input is missing its stable ODX message ID.");
            incoming = new(messageId, ModuleSettingReader.GetRequired(step.Settings, "inputPort", ModuleId), batch, context.SourceReceipts.ToArray())
            {
                OperationName = step.Settings.GetValueOrDefault("inputOperation"), OperationMessageName = step.Settings.GetValueOrDefault("inputOperationMessage")
            };
            frame.Variables["$odx.correlation"] = JsonSerializer.SerializeToNode(metadata, Options);
        }
        var keys = ReadArray<string>(step, "receiveCorrelationKeysJson");
        if (keys.Length > 128 || keys.Any(string.IsNullOrWhiteSpace) || keys.Distinct(StringComparer.Ordinal).Count() != keys.Length)
            throw new ArgumentException("Receive correlation keys must be distinct, nonblank and limited to 128.");
        var delays = ReadArray<DelayBinding>(step, "delaysJson");
        var delayIds = nodes.Where(node => node.ShapeType == "Delay").Select(node => node.Id).ToHashSet();
        if (delays.Any(delay => delay is null || !delayIds.Contains(delay.NodeId) || delay.Seconds is < 0 or > 31622400) || delays.Select(delay => delay.NodeId).Distinct().Count() != delays.Length)
            throw new ArgumentException("Delay bindings require distinct source Delay node IDs and 0-31622400 seconds.");
        var automatic = OdxAutomaticBindings.Compile(program); var bindings = new OdxDurableBindings();
        foreach (var pair in automatic.Bindings.Activities) { bindings.Execution.Activities.Add(pair.Key, pair.Value); bindings.FrameLocalActivities.Add(pair.Key); }
        foreach (var pair in automatic.Bindings.Conditions) bindings.Execution.Conditions.Add(pair.Key, pair.Value);
        var referencesJson = step.Settings.GetValueOrDefault("referencedProgramsJson", "[]");
        if (referencesJson.Length > 8_000_000) throw new ArgumentException("Referenced ODX source models exceed 8,000,000 characters.");
        var references = JsonSerializer.Deserialize<OdxProgram[]>(referencesJson, Options) ?? throw new ArgumentException("Referenced source models must be an array.");
        if (references.Length > 32 || references.Any(model => model is null || model.Version != 1)) throw new ArgumentException("Configure at most 32 version-1 referenced source models.");
        var sourceKeys = OdxReceiveBindings.Bind(program, entry, bindings, references);
        if (batch.Count > 0 && string.IsNullOrWhiteSpace(correlationField))
        {
            if (sourceKeys.Count == 0) throw new NotSupportedException("Source-derived instance routing requires correlation sets initialized by the first receive.");
            var tuple = sourceKeys.Select(key =>
            {
                if (!OdxReceiveBindings.TryExactMetadata(batch.Payloads[0], key, out var value) || value.Length > 2048)
                    throw new ArgumentException("Input is missing a bounded source correlation property: " + key);
                return new KeyValuePair<string, string>(key, value);
            }).ToArray();
            correlation = "odx-source:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(tuple, Options))));
        }
        if (string.IsNullOrWhiteSpace(correlation)) throw new ArgumentException("A timer resume requires its saved instance correlation key.");
        var choicesJson = step.Settings.GetValueOrDefault("casesJson", "[]");
        var choices = RenderDispatchDefinition.Read(choicesJson, allowEmpty: true);
        var messages = nodes.Where(node => node.ShapeType == "MessageDeclaration").Select(node => node.Name)
            .Concat(nodes.Where(node => node.ShapeType == "Receive").Select(node => node.Properties.GetValueOrDefault("MessageName") ?? "")).ToHashSet(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            if (node.ShapeType == "Receive")
            {
                var sourceReceive = bindings.Receives[node.Id];
                bindings.Receives[node.Id] = (trigger, current) =>
                {
                    var subscription = sourceReceive(trigger, current);
                    var values = new Dictionary<string, string>(subscription.Correlation, StringComparer.Ordinal);
                    foreach (var key in keys)
                    {
                        var value = current.Variables.GetValueOrDefault("$odx.correlation")?[key]?.GetValue<string>()
                            ?? throw new InvalidOperationException("Pinned receive correlation metadata is missing: " + key);
                        if (values.TryGetValue(key, out var sourceValue) && sourceValue != value) throw new InvalidOperationException("Explicit correlation conflicts with source property " + key);
                        values[key] = value;
                    }
                    return subscription with { Correlation = values };
                };
            }
            else if (node.ShapeType == "Delay")
            {
                var delay = delays.SingleOrDefault(binding => binding.NodeId == node.Id) ?? throw new NotSupportedException($"Delay {node.Id} requires an explicit timer binding.");
                bindings.Deadlines[node.Id] = (_, _) => clock.GetUtcNow().AddSeconds(delay.Seconds);
            }
            else if (node.ShapeType == "Send" && step.Settings.TryGetValue("replyPort", out var replyPort) && !string.IsNullOrWhiteSpace(replyPort))
            {
                bindings.Execution.Activities[node.Id] = OdxMessageBindings.BindReply(node, replyPort);
                bindings.FrameLocalActivities.Add(node.Id);
            }
            else if (node.ShapeType is "MessageAssignment" or "VariableAssignment")
            {
                if (OdxMessageBindings.IsMessageCopy(node, messages))
                {
                    bindings.Execution.Activities[node.Id] = OdxMessageBindings.BindMessageCopy(node);
                    bindings.FrameLocalActivities.Add(node.Id);
                }
                else if (OdxMessageBindings.IsDynamicTransform(node))
                {
                    bindings.Execution.Activities[node.Id] = OdxMessageBindings.BindDynamicTransform(node, choices, renderers).Activity;
                    // Renderers are trusted extensible code, so retain effect journaling unless a developer proves purity.
                    bindings.FrameLocalActivities.Remove(node.Id);
                }
            }
        }
        var version = JsonSerializer.Serialize(step.Settings.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray(), Options);
        var result = await new OdxDurableExecutor(store, clock).ExecuteAsync(program, entry, bindings, frame, correlation, version, incoming, cancellationToken).ConfigureAwait(false);
        if (result.Outcome == OdxDurableOutcome.Unmatched) throw new OdxUnmatchedMessageException("Input does not match the saved receive/filter or arrived after its deadline. It was not added or acknowledged; route/reconcile it before retrying.");
        if (result.Outcome == OdxDurableOutcome.Completed && step.Settings.TryGetValue("outputMessage", out var output) && !string.IsNullOrWhiteSpace(output))
            return frame.Messages.TryGetValue(output, out var message) ? message : throw new InvalidOperationException("Completed ODX output message is uninitialized: " + output);
        return frame.Batch;
    }

    private static T[] ReadArray<T>(ModuleStepDefinition step, string key)
    {
        var json = step.Settings.GetValueOrDefault(key, "[]");
        if (json.Length > 262144) throw new ArgumentException(key + " exceeds 262144 characters.");
        return JsonSerializer.Deserialize<T[]>(json, Options) ?? throw new ArgumentException(key + " must be an array.");
    }
    private static IEnumerable<OdxNode> Flatten(IEnumerable<OdxNode> nodes, int depth = 1)
    {
        foreach (var node in nodes)
        {
            if (depth > 64) throw new ArgumentException("ODX program exceeds 64 levels.");
            yield return node;
            foreach (var child in Flatten(node.Children, depth + 1)) yield return child;
        }
    }
    private sealed record DelayBinding(int NodeId, int Seconds);
}
