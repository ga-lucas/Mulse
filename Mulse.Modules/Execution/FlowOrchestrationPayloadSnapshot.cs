using System.Text.Json.Serialization;

namespace Mulse.Modules.Execution;

public sealed class FlowOrchestrationPayloadSnapshot
{
    public string Name { get; init; } = string.Empty;

    public string ContentBase64 { get; init; } = string.Empty;

    public string ContentType { get; init; } = "application/octet-stream";

    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool PreserveMetadataCase { get; init; }

    public static FlowOrchestrationPayloadSnapshot Capture(IntegrationPayload payload) => Capture(payload, preserveMetadataCase: false);

    public static FlowOrchestrationPayloadSnapshot Capture(IntegrationPayload payload, bool preserveMetadataCase) => new()
    {
        Name = payload.Name, ContentBase64 = Convert.ToBase64String(payload.Content.ToArray()), ContentType = payload.ContentType,
        PreserveMetadataCase = preserveMetadataCase,
        Metadata = new Dictionary<string, string>(payload.Metadata, preserveMetadataCase ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase)
    };

    public IntegrationPayload Restore() => new(Name, BinaryData.FromBytes(Convert.FromBase64String(ContentBase64)), ContentType,
        new Dictionary<string, string>(Metadata, PreserveMetadataCase ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase));
}
