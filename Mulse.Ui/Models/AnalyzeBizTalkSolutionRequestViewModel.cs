using System.Text.Json.Serialization;

namespace Mulse.Ui.Models;

public sealed record AnalyzeBizTalkSolutionRequestViewModel(
    string SourcePath,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? AdditionalBindingPaths = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? AdditionalSourcePaths = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? AdditionalRulePaths = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool GenerateNativeFlow = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, string>? NativePolicyVersions = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? NativeHostConfigurationPath = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? NativeConfigurationHelperPaths = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? NativeMapChoicesJson = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool DeriveNativeMapChoices = false);
