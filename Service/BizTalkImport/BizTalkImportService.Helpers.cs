using System.Text.RegularExpressions;
using System.Xml.Linq;
using Mulse.Modules;
using Service.Models;

namespace Service.BizTalkImport;

public sealed partial class BizTalkImportService
{
    private static void AddArtifacts(XDocument document, string projectDirectory, string rootDirectory, string itemName, string artifactKind, ICollection<BizTalkArtifactResponse> artifacts)
    {
        foreach (var includePath in document.Descendants()
            .Where(element => string.Equals(element.Name.LocalName, itemName, StringComparison.Ordinal))
            .Select(static element => element.Attribute("Include")?.Value)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var fullPath = Path.GetFullPath(Path.Combine(projectDirectory, includePath!));
            artifacts.Add(new BizTalkArtifactResponse(
                artifactKind,
                Path.GetFileName(includePath) ?? Path.GetFileName(fullPath),
                Path.GetRelativePath(rootDirectory, fullPath),
                fullPath));
        }
    }

    private static string? GetProperty(XDocument document, string propertyName)
    {
        return document.Descendants()
            .FirstOrDefault(element => string.Equals(element.Name.LocalName, propertyName, StringComparison.Ordinal))
            ?.Value;
    }

    private static string NormalizeReferenceName(string? includeValue, string? explicitName)
    {
        var name = !string.IsNullOrWhiteSpace(explicitName)
            ? explicitName
            : includeValue?.Split(',')[0];
        return name?.Trim() ?? string.Empty;
    }

    private static string CreateMigrationApproach(string suggestedModuleKind, string assemblyName)
    {
        return suggestedModuleKind switch
        {
            "Fetch" => $"Rewrite '{assemblyName}' as a native fetch module so source transport and polling remain first-class Mulse stages.",
            "Parse" => $"Rewrite '{assemblyName}' as a native parse module so schema or message normalization is explicit and reusable.",
            "Render" => $"Rewrite '{assemblyName}' as a native render module so outbound document generation is separated from delivery.",
            "Deliver" => $"Rewrite '{assemblyName}' as a native deliver module so transport concerns stay isolated from rendering logic.",
            _ => $"Rewrite '{assemblyName}' as a native orchestration augment module unless deeper analysis shows it belongs in another stage."
        };
    }

    private static string CreateFlowId(string projectName)
    {
        var normalized = string.Concat(projectName
            .ToLowerInvariant()
            .Select(static character => char.IsLetterOrDigit(character) ? character : '-'));
        var compact = MultipleDashesPattern.Replace(normalized, "-").Trim('-');
        return string.IsNullOrWhiteSpace(compact) ? "biztalk-imported-flow" : $"{compact}-flow";
    }

    private static string CreateSlug(string value)
    {
        var normalized = string.Concat(value
            .ToLowerInvariant()
            .Select(static character => char.IsLetterOrDigit(character) ? character : '-'));
        var compact = MultipleDashesPattern.Replace(normalized, "-").Trim('-');
        return string.IsNullOrWhiteSpace(compact) ? "migration-item" : compact;
    }

    private static string NormalizeArtifactName(string value)
        => string.Concat(value.Where(char.IsLetterOrDigit));

    private static string AddRequirement(
        List<BizTalkSettingRequirementResponse>? requirements,
        string referencePrefix,
        string settingPath,
        string kind,
        string description,
        bool appliedToDraft,
        string? actualValue = null)
    {
        var referenceName = $"{CreateSlug(referencePrefix)}.{CreateSlug(settingPath)}";
        var placeholder = $"{{{{{kind.ToLowerInvariant()}:{referenceName}}}}}";

        if (requirements is not null && !requirements.Any(requirement => string.Equals(requirement.SettingPath, settingPath, StringComparison.OrdinalIgnoreCase) && string.Equals(requirement.Kind, kind, StringComparison.OrdinalIgnoreCase)))
        {
            requirements.Add(new BizTalkSettingRequirementResponse(kind, settingPath, referenceName, placeholder, description, appliedToDraft));
        }

        return appliedToDraft || string.IsNullOrWhiteSpace(actualValue)
            ? placeholder
            : actualValue;
    }

    private static string ResolveConfigValue(
        List<BizTalkSettingRequirementResponse>? requirements,
        string referencePrefix,
        string settingPath,
        string? actualValue,
        string description,
        bool includeConfigPlaceholders)
    {
        if (string.IsNullOrWhiteSpace(actualValue))
        {
            return AddRequirement(requirements, referencePrefix, settingPath, "Config", description, appliedToDraft: true);
        }

        _ = AddRequirement(requirements, referencePrefix, settingPath, "Config", description, appliedToDraft: false, actualValue: actualValue);
        return includeConfigPlaceholders ? actualValue : actualValue;
    }

    private static string? TryGetTransportProperty(IReadOnlyDictionary<string, string> properties, string name)
        => properties.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    private static string ExtractHost(string address)
        => Uri.TryCreate(address, UriKind.Absolute, out var uri) ? uri.Host : string.Empty;

    private static bool LooksLikeFilePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return uri.IsFile || string.Equals(uri.Scheme, Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase);
        }

        return value.Contains('\\', StringComparison.Ordinal)
            || Regex.IsMatch(value, "^[A-Za-z]:[\\/]");
    }

    private static BizTalkProjectResponse CreateDefaultProjectProfile()
        => new("ImportedBinding", string.Empty, 0, 0, 1, 0, [], []);
}
