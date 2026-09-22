namespace Mulse.Modules.Abstractions;

public sealed record ModuleDescriptor
{
    public ModuleDescriptor(
        string id,
        string displayName,
        ModuleKind kind,
        string description,
        IReadOnlyList<ModuleSettingDescriptor>? settings = null,
        ModuleRecommendationProfile? recommendation = null)
    {
        Id = id;
        DisplayName = displayName;
        Kind = kind;
        Description = description;
        Settings = settings ?? [];
        Recommendation = recommendation;
    }

    public string Id { get; }

    public string DisplayName { get; }

    public ModuleKind Kind { get; }

    public string Description { get; }

    public IReadOnlyList<ModuleSettingDescriptor> Settings { get; }

    public ModuleRecommendationProfile? Recommendation { get; }
}
