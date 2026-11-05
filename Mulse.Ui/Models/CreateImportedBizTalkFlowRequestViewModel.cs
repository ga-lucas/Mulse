using System.Text.Json.Serialization;

namespace Mulse.Ui.Models;

public sealed record CreateImportedBizTalkFlowRequestViewModel(
    string SourcePath,
    string SuggestedFlowId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? AdditionalSourcePaths = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? AdditionalBindingPaths = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? AdditionalRulePaths = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool GenerateNativeFlow = false);
