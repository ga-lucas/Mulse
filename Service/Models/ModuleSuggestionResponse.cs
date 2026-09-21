using Mulse.Modules;

namespace Service.Models;

/// <summary>Represents a module suggestion for a sample-driven flow design session.</summary>
public sealed record ModuleSuggestionResponse(
    string Id,
    string DisplayName,
    ModuleKind Kind,
    string Description,
    bool IsRecommended);
