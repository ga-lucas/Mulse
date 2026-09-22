namespace Mulse.Modules.Abstractions;

public sealed record ModuleRecommendationProfile
{
    public ModuleRecommendationProfile(
        IReadOnlyList<ModuleDataFormat>? supportedFormats = null,
        IReadOnlyList<ModuleProtocol>? protocols = null,
        IReadOnlyList<ModuleCapability>? capabilities = null)
    {
        SupportedFormats = supportedFormats ?? [];
        Protocols = protocols ?? [];
        Capabilities = capabilities ?? [];
    }

    public IReadOnlyList<ModuleDataFormat> SupportedFormats { get; }

    public IReadOnlyList<ModuleProtocol> Protocols { get; }

    public IReadOnlyList<ModuleCapability> Capabilities { get; }
}
