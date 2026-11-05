using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mulse.Modules.Mapping;

public sealed record RenderPipelineStage(string Module, IReadOnlyDictionary<string, string> Settings);

public static class RenderPipelineDefinition
{
    public const string ModuleId = "render-pipeline";
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    public static IReadOnlyList<RenderPipelineStage> Read(string json, bool allowEmpty = false)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > 262144) throw new ArgumentException("Renderer pipeline requires stages JSON of at most 262144 characters.");
        RenderPipelineStage[] stages;
        try { stages = JsonSerializer.Deserialize<RenderPipelineStage[]>(json, Options) ?? throw new ArgumentException("Renderer pipeline must be an array, not null."); }
        catch (JsonException exception) { throw new ArgumentException("Invalid renderer pipeline stages JSON.", exception); }
        if ((!allowEmpty && stages.Length == 0) || stages.Length > 32 || stages.Any(stage => stage is null || stage.Settings is null
            || string.Equals(stage.Module, ModuleId, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Renderer pipeline requires 1-32 non-null stages (0 allowed for outbound sends), without nested pipelines.");
        var validated = RenderDispatchDefinition.Read(JsonSerializer.Serialize(stages.Select((stage, index) => new RenderDispatchCase
        {
            Value = index.ToString(CultureInfo.InvariantCulture), Module = stage.Module, Settings = new(stage.Settings, StringComparer.OrdinalIgnoreCase)
        }), Options), allowEmpty);
        return validated.Select(stage => new RenderPipelineStage(stage.Module, stage.Settings)).ToArray();
    }
}
