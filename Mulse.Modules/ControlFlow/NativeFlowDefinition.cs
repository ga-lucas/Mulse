using System.Text.Json;
using System.Text.Json.Serialization;
using Mulse.Modules.Decisions;

namespace Mulse.Modules.ControlFlow;

[JsonConverter(typeof(JsonStringEnumConverter<NativeFlowNodeKind>))]
public enum NativeFlowNodeKind { Sequence, If, ForEach, While, Augment, Render, Blocked, Wait, Receive, SaveMessage, UseMessage, Reply, Deliver, DeclareVariable, SetVariable, CreateXmlMessage, SetMessageValue, CreateXmlRoot, AppendXmlMessage }

public sealed class NativeFlowNode
{
    [JsonRequired] public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public NativeFlowNodeKind Kind { get; set; } = NativeFlowNodeKind.Sequence;
    public string Module { get; set; } = "";
    public Dictionary<string, string> Settings { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<DecisionConditionDefinition> Conditions { get; set; } = [];
    public int MaxIterations { get; set; } = 100;
    public List<NativeFlowNode> Children { get; set; } = [];
    public List<NativeFlowNode> Otherwise { get; set; } = [];
    public string BlockReason { get; set; } = "";
    public string MessageName { get; set; } = "";
    public int DurationSeconds { get; set; }
    public string Channel { get; set; } = "";
    public List<string> CorrelationKeys { get; set; } = [];
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public NativeExpression? Expression { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Variable { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public NativeScalarType? VariableType { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public NativeMessageFact? Target { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public NativeExpression? NamespaceExpression { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? SourceMessage { get; set; }
}

public sealed class NativeFlowDefinition
{
    public const string ModuleId = "native-control-flow-augment";
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 128,
        Converters = { new JsonStringEnumConverter() }
    };
    [JsonRequired] public int Version { get; set; } = 1;
    [JsonRequired] public List<NativeFlowNode> Nodes { get; set; } = [];
    public bool Durable { get; set; }
    public string CorrelationMetadataKey { get; set; } = "group";
    public string MessageIdMetadataKey { get; set; } = "messageId";
    public string OutputMessage { get; set; } = "";

    public static NativeFlowDefinition Read(string json, bool allowBlocked = false)
    {
        if (json.Length > 1_000_000) throw new ArgumentException("Native control flow exceeds 1,000,000 characters.");
        NativeFlowDefinition flow;
        try { flow = JsonSerializer.Deserialize<NativeFlowDefinition>(json, Options) ?? throw new ArgumentException("Native control flow cannot be null."); }
        catch (JsonException exception) { throw new ArgumentException("Invalid native control-flow definition.", exception); }
        if (flow.Version != 1 || flow.Nodes is null) throw new ArgumentException("Native control flow requires version 1 and a nodes array.");
        if (string.IsNullOrWhiteSpace(flow.CorrelationMetadataKey) || flow.CorrelationMetadataKey.Length > 128
            || string.IsNullOrWhiteSpace(flow.MessageIdMetadataKey) || flow.MessageIdMetadataKey.Length > 128
            || flow.OutputMessage is null || flow.OutputMessage.Length > 128 || flow.OutputMessage != flow.OutputMessage.Trim())
            throw new ArgumentException("Native durable configuration requires bounded metadata keys and output message name.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        void Validate(List<NativeFlowNode> nodes, int depth)
        {
            if (nodes.Count > 0 && depth > 32) throw new ArgumentException("Native control flow exceeds 32 nested levels.");
            foreach (var node in nodes)
            {
                if (node is null || string.IsNullOrWhiteSpace(node.Id) || node.Id.Length > 128 || !ids.Add(node.Id) || ids.Count > 1000
                    || node.Name is null || node.Module is null || node.Settings is null || node.Conditions is null || node.Children is null || node.Otherwise is null
                    || node.BlockReason is null || node.MessageName is null || node.Channel is null || node.CorrelationKeys is null
                    || !Enum.IsDefined(node.Kind) || node.Settings.Count > 128
                    || node.Settings.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null))
                    throw new ArgumentException("Native control flow requires at most 1000 nodes with unique IDs, known kinds and non-null fields/settings.");
                if (node.Kind == NativeFlowNodeKind.Blocked && (!allowBlocked || string.IsNullOrWhiteSpace(node.BlockReason)))
                    throw new NotSupportedException($"Native node '{node.Id}' is unresolved: {node.BlockReason}");
                if (node.Kind is NativeFlowNodeKind.Augment or NativeFlowNodeKind.Render or NativeFlowNodeKind.Deliver)
                {
                    if (string.IsNullOrWhiteSpace(node.Module) || node.Module.Length > 256 || node.Module != node.Module.Trim() || IsStatefulModule(node.Module))
                        throw new ArgumentException($"Native node '{node.Id}' requires a non-suspending module, not an orchestration interpreter/collector.");
                }
                else if (node.Module.Length != 0 || node.Settings.Count != 0)
                    throw new ArgumentException($"Native node '{node.Id}' cannot hide module settings on a control node.");
                var variableNode = node.Kind is NativeFlowNodeKind.DeclareVariable or NativeFlowNodeKind.SetVariable;
                var messageWrite = node.Kind is NativeFlowNodeKind.CreateXmlMessage or NativeFlowNodeKind.SetMessageValue or NativeFlowNodeKind.CreateXmlRoot;
                if ((node.Kind == NativeFlowNodeKind.CreateXmlRoot) != (node.NamespaceExpression is not null)
                    || (node.Kind == NativeFlowNodeKind.AppendXmlMessage) != (node.SourceMessage is not null)
                    || node.SourceMessage is { } source && (string.IsNullOrWhiteSpace(source) || source.Length > 128 || source != source.Trim()))
                    throw new ArgumentException($"Native node '{node.Id}' requires namespace expression only for CreateXmlRoot and a bounded source message only for AppendXmlMessage.");
                if (node.NamespaceExpression is not null) NativeExpression.Validate(node.NamespaceExpression);
                if (node.Expression is not null)
                {
                    NativeExpression.Validate(node.Expression);
                    if (!variableNode && !messageWrite && node.Kind is not (NativeFlowNodeKind.If or NativeFlowNodeKind.While) || node.Conditions.Count != 0)
                        throw new ArgumentException($"Native node '{node.Id}' has an inactive expression or mixed predicate modes.");
                }
                if (messageWrite && node.Expression is null || (node.Kind == NativeFlowNodeKind.SetMessageValue) != (node.Target is not null))
                    throw new ArgumentException($"Native node '{node.Id}' requires a value expression and an active target only for SetMessageValue.");
                if (node.Target is { } target)
                {
                    target.Validate();
                    if (target.MessageName.Length == 0 || target.Kind is not (NativeMessageFactKind.Metadata or NativeMessageFactKind.XmlString or NativeMessageFactKind.XmlInt32 or NativeMessageFactKind.XmlBoolean))
                        throw new ArgumentException($"Native node '{node.Id}' requires an explicit named metadata/typed XML scalar target.");
                }
                if (variableNode && (node.Expression is null || node.Variable is null || !NativeExpression.ValidName(node.Variable))
                    || !variableNode && node.Variable is not null
                    || (node.Kind == NativeFlowNodeKind.DeclareVariable) != node.VariableType.HasValue
                    || node.VariableType.HasValue && (node.VariableType == NativeScalarType.Null || !Enum.IsDefined(node.VariableType.Value)))
                    throw new ArgumentException($"Native node '{node.Id}' requires an explicit initializer, variable name and declaration type only where active.");
                if (node.Kind is NativeFlowNodeKind.If or NativeFlowNodeKind.While or NativeFlowNodeKind.Receive)
                {
                    if (node.Conditions.Count > 32 || (node.Kind != NativeFlowNodeKind.Receive && node.Conditions.Count == 0 && node.Expression is null) || node.Conditions.Any(condition => condition is null || !Enum.IsDefined(condition.Source)
                        || !Enum.IsDefined(condition.Operator) || condition.Operator == DecisionComparisonOperator.RegexMatch || condition.Path is null
                        || condition.Path.Length > 2048 || condition.Value?.Length > 4096
                        || (condition.Source is DecisionValueSourceKind.Metadata or DecisionValueSourceKind.Payload or DecisionValueSourceKind.Literal) && string.IsNullOrWhiteSpace(condition.Path)))
                        throw new ArgumentException($"Native node '{node.Id}' requires 1-32 valid conditions; unbounded regex is unsupported.");
                }
                else if (node.Conditions.Count != 0) throw new ArgumentException($"Native node '{node.Id}' cannot have inactive conditions.");
                if ((node.Kind is NativeFlowNodeKind.ForEach or NativeFlowNodeKind.While) && (node.MaxIterations is < 1 or > 10000))
                    throw new ArgumentException($"Native node '{node.Id}' requires an iteration limit of 1-10000.");
                if (node.Kind != NativeFlowNodeKind.If && node.Otherwise.Count > 0)
                    throw new ArgumentException($"Native node '{node.Id}' cannot have an inactive else branch.");
                if (node.Kind is not (NativeFlowNodeKind.Sequence or NativeFlowNodeKind.If or NativeFlowNodeKind.ForEach or NativeFlowNodeKind.While or NativeFlowNodeKind.Blocked)
                    && node.Children.Count > 0)
                    throw new ArgumentException($"Native leaf node '{node.Id}' cannot hide child activities.");
                var durableNode = node.Kind is NativeFlowNodeKind.Wait or NativeFlowNodeKind.Receive or NativeFlowNodeKind.SaveMessage
                    or NativeFlowNodeKind.UseMessage or NativeFlowNodeKind.Reply or NativeFlowNodeKind.Deliver or NativeFlowNodeKind.CreateXmlMessage or NativeFlowNodeKind.SetMessageValue
                    or NativeFlowNodeKind.CreateXmlRoot or NativeFlowNodeKind.AppendXmlMessage;
                if (durableNode && !flow.Durable) throw new ArgumentException($"Native node '{node.Id}' requires durable=true; transactions are not supported.");
                var namedNode = node.Kind is NativeFlowNodeKind.Receive or NativeFlowNodeKind.SaveMessage or NativeFlowNodeKind.UseMessage or NativeFlowNodeKind.Reply or NativeFlowNodeKind.CreateXmlMessage
                    or NativeFlowNodeKind.CreateXmlRoot or NativeFlowNodeKind.AppendXmlMessage;
                if (node.MessageName.Length > 128 || node.MessageName != node.MessageName.Trim()
                    || (namedNode ? string.IsNullOrWhiteSpace(node.MessageName) : node.Kind != NativeFlowNodeKind.Deliver && node.MessageName.Length != 0))
                    throw new ArgumentException($"Native node '{node.Id}' requires an active, bounded message name only on named-message activities.");
                if (node.Kind is NativeFlowNodeKind.Wait or NativeFlowNodeKind.Receive
                    ? node.DurationSeconds is < 0 or > 31622400 : node.DurationSeconds != 0)
                    throw new ArgumentException($"Native node '{node.Id}' has an invalid or inactive duration.");
                if (node.Kind == NativeFlowNodeKind.Receive)
                {
                    if (string.IsNullOrWhiteSpace(node.Channel) || node.Channel.Length > 128 || node.Channel != node.Channel.Trim()
                        || node.CorrelationKeys.Count > 128 || node.CorrelationKeys.Any(key => string.IsNullOrWhiteSpace(key) || key.Length > 128)
                        || node.CorrelationKeys.Distinct(StringComparer.Ordinal).Count() != node.CorrelationKeys.Count)
                        throw new ArgumentException($"Native receive '{node.Id}' requires an exact channel and distinct bounded correlation keys.");
                }
                else if (node.Channel.Length != 0 || node.CorrelationKeys.Count != 0)
                    throw new ArgumentException($"Native node '{node.Id}' cannot have inactive receive settings.");
                Validate(node.Children, depth + 1); Validate(node.Otherwise, depth + 1);
            }
        }
        Validate(flow.Nodes, 0);
        return flow;
    }

    public static bool IsStatefulModule(string module) => module.Equals(ModuleId, StringComparison.OrdinalIgnoreCase)
        || module.Equals("odx-program-augment", StringComparison.OrdinalIgnoreCase)
        || module.Equals("durable-batch-augment", StringComparison.OrdinalIgnoreCase)
        || module.Equals("stateful-orchestration-augment", StringComparison.OrdinalIgnoreCase);

    public IEnumerable<NativeFlowNode> AllNodes() => Flatten(Nodes);
    private static IEnumerable<NativeFlowNode> Flatten(IEnumerable<NativeFlowNode> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            foreach (var child in Flatten(node.Children)) yield return child;
            foreach (var child in Flatten(node.Otherwise)) yield return child;
        }
    }
}
