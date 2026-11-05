using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mulse.Modules.Mapping;

public sealed class RenderDispatchCase
{
    public string Value { get; set; } = "";
    public string Module { get; set; } = "";
    public Dictionary<string, string> Settings { get; set; } = [];
}

public static class RenderDispatchDefinition
{
    public const string ModuleId = "render-dispatch";
    public const string AugmentModuleId = "map-dispatch-augment";
    public static IReadOnlyList<RenderDispatchCase> Read(string json) => Read(json, allowEmpty: false);
    public static IReadOnlyList<RenderDispatchCase> Read(string json, bool allowEmpty)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > 262144) throw new ArgumentException("Render dispatch requires casesJson of at most 262144 characters.");
        RenderDispatchCase[] cases;
        try
        {
            cases = JsonSerializer.Deserialize<RenderDispatchCase[]>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web)
                { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow }) ?? throw new ArgumentException("Render dispatch cases must be an array, not null.");
        }
        catch (JsonException ex) { throw new ArgumentException("Invalid render dispatch casesJson.", ex); }
        if ((!allowEmpty && cases.Length == 0) || cases.Length > 128) throw new ArgumentException($"Render dispatch requires {(allowEmpty ? 0 : 1)} to 128 cases.");
        var values = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in cases)
        {
            if (entry is null || string.IsNullOrWhiteSpace(entry.Value) || entry.Value.Length > 2048 || !values.Add(entry.Value)
                || string.IsNullOrWhiteSpace(entry.Module) || entry.Module.Length > 256 || entry.Module != entry.Module.Trim()
                || string.Equals(entry.Module, ModuleId, StringComparison.OrdinalIgnoreCase)
                || entry.Settings is null || entry.Settings.Count > 128 || entry.Settings.Any(setting => string.IsNullOrWhiteSpace(setting.Key) || setting.Value is null))
                throw new ArgumentException("Render dispatch cases require unique nonblank selection values, nonblank renderer IDs (not render-dispatch), and non-null settings.");
        }
        return cases;
    }
}
