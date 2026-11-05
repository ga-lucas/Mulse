using System.Text.Json.Serialization;

namespace Mulse.Ui.Models;

public sealed class NativeFlowEditorModel
{
    [JsonRequired] public int Version { get; set; } = 1;
    [JsonRequired] public List<NativeFlowNodeViewModel> Nodes { get; set; } = [];
    public bool Durable { get; set; }
    public string CorrelationMetadataKey { get; set; } = "group";
    public string MessageIdMetadataKey { get; set; } = "messageId";
    public string OutputMessage { get; set; } = "";
}

public sealed class NativeFlowNodeViewModel
{
    [JsonRequired] public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "Sequence";
    public string Module { get; set; } = "";
    public Dictionary<string, string> Settings { get; set; } = [];
    public List<NativeFlowConditionViewModel> Conditions { get; set; } = [];
    public int MaxIterations { get; set; } = 100;
    public List<NativeFlowNodeViewModel> Children { get; set; } = [];
    public List<NativeFlowNodeViewModel> Otherwise { get; set; } = [];
    public string BlockReason { get; set; } = "";
    public string MessageName { get; set; } = "";
    public int DurationSeconds { get; set; }
    public string Channel { get; set; } = "";
    public List<string> CorrelationKeys { get; set; } = [];
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public NativeExpressionViewModel? Expression { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Variable { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? VariableType { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public NativeMessageFactViewModel? Target { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public NativeExpressionViewModel? NamespaceExpression { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? SourceMessage { get; set; }
}

public sealed class NativeExpressionViewModel
{
    public string Kind { get; set; } = "Literal";
    public NativeScalarViewModel? Literal { get; set; } = new();
    public string? Variable { get; set; }
    public List<NativeExpressionViewModel> Operands { get; set; } = [];
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public NativeMessageFactViewModel? Fact { get; set; }
}

public sealed class NativeMessageFactViewModel
{
    public string Kind { get; set; } = "Metadata";
    public string MessageName { get; set; } = "";
    public string MetadataKey { get; set; } = "";
    public List<NativeXmlStepViewModel> Steps { get; set; } = [];
}

public sealed class NativeXmlStepViewModel
{
    public string Name { get; set; } = "";
    public string Namespace { get; set; } = "";
    public bool Attribute { get; set; }
}

public sealed class NativeScalarViewModel
{
    public string Type { get; set; } = "String";
    public string Value { get; set; } = "";
}

public sealed class NativeFlowConditionViewModel
{
    public string Source { get; set; } = "Metadata";
    public string Path { get; set; } = "";
    public string Operator { get; set; } = "Equals";
    public string? Value { get; set; }
}
