namespace Mulse.Ui.Models;

public sealed record ModuleSuggestionViewModel(
    string Id,
    string DisplayName,
    string Kind,
    string Description,
    bool IsRecommended);
