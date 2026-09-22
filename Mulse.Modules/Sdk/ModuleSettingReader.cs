namespace Mulse.Modules.Sdk;

/// <summary>
/// Shared helpers for reading and validating <see cref="ModuleStepDefinition.Settings"/> values.
/// Public so that module packages outside <c>Mulse.Modules</c> (built-in feature packs or
/// third-party/OSS module contributions) can reuse the same setting-parsing conventions.
/// </summary>
public static class ModuleSettingReader
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

    /// <summary>
    /// Reads and parses an enum-valued setting (case-insensitive), returning <paramref name="defaultValue"/> if
    /// the setting is absent or blank. Throws <see cref="ArgumentException"/> naming the valid options if the
    /// setting is present but doesn't match a member of <typeparamref name="TEnum"/>.
    /// </summary>
    public static TEnum GetEnum<TEnum>(IReadOnlyDictionary<string, string> settings, string settingName, TEnum defaultValue, string moduleId)
        where TEnum : struct, Enum
    {
        if (!settings.TryGetValue(settingName, out var value) || string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        if (Enum.TryParse<TEnum>(value, ignoreCase: true, out var result))
        {
            return result;
        }

        var validOptions = string.Join(", ", Enum.GetNames<TEnum>());
        throw new ArgumentException(
            $"Module '{moduleId}' has an invalid value for '{settingName}': '{value}'. Valid options: {validOptions}.",
            settingName);
    }
}
