namespace Service.Models;

/// <summary>Represents the analysis result for a sample-driven flow design session.</summary>
public sealed record FlowDesignResponse(
    DetectedDataFormat DetectedFormat,
    string? FileName,
    string Preview,
    IReadOnlyList<ModuleSuggestionResponse> InputModules,
    IReadOnlyList<ModuleSuggestionResponse> OrchestrationAugmentModules,
    IReadOnlyList<ModuleSuggestionResponse> OutputModules,
    IReadOnlyList<FieldMappingSuggestionResponse> Fields);
