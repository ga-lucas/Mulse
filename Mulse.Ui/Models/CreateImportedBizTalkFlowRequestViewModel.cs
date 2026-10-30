using System.Text.Json.Serialization;

namespace Mulse.Ui.Models;

public sealed record CreateImportedBizTalkFlowRequestViewModel(
    string SourcePath,
    string SuggestedFlowId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? AdditionalSourcePaths = null);
