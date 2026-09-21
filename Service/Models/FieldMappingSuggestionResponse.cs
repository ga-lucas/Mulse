namespace Service.Models;

/// <summary>Represents a suggested mapping for a field discovered in sample data.</summary>
public sealed record FieldMappingSuggestionResponse(
    string SourcePath,
    string SampleValue,
    FieldMappingPurpose RecommendedPurpose,
    string SuggestedTargetField);
