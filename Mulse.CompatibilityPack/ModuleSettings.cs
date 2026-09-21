namespace Mulse.CompatibilityPack;

internal static class ModuleSettings
{
    public static string GetRequired(IReadOnlyDictionary<string, string> settings, string settingName, string moduleId)
    {
        if (settings.TryGetValue(settingName, out var value) && !string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        throw new ArgumentException($"Module '{moduleId}' requires the '{settingName}' setting.", settingName);
    }

    public static string? GetOptional(IReadOnlyDictionary<string, string> settings, string settingName)
    {
        if (settings.TryGetValue(settingName, out var value) && !string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        return null;
    }

    public static bool GetBoolean(IReadOnlyDictionary<string, string> settings, string settingName, bool defaultValue, string moduleId)
    {
        if (!settings.TryGetValue(settingName, out var value) || string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        if (bool.TryParse(value, out var result))
        {
            return result;
        }

        throw new ArgumentException($"Module '{moduleId}' has an invalid boolean value for '{settingName}'.", settingName);
    }

    public static int GetInt32(IReadOnlyDictionary<string, string> settings, string settingName, int defaultValue, string moduleId)
    {
        if (!settings.TryGetValue(settingName, out var value) || string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        if (int.TryParse(value, out var result))
        {
            return result;
        }

        throw new ArgumentException($"Module '{moduleId}' has an invalid integer value for '{settingName}'.", settingName);
    }
}
