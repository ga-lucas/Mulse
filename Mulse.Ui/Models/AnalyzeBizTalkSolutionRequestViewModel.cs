using System.Text.Json.Serialization;

namespace Mulse.Ui.Models;

public sealed record AnalyzeBizTalkSolutionRequestViewModel(
    string SourcePath,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? AdditionalBindingPaths = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? AdditionalSourcePaths = null);
