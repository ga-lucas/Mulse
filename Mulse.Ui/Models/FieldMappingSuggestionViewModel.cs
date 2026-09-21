namespace Mulse.Ui.Models;

public sealed record FieldMappingSuggestionViewModel(
    string SourcePath,
    string SampleValue,
    string RecommendedPurpose,
    string SuggestedTargetField);
