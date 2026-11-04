namespace Mulse.Modules.Execution;

public sealed class FlowOrchestrationPayloadSnapshot
{
    public string Name { get; init; } = string.Empty;

    public string ContentBase64 { get; init; } = string.Empty;

    public string ContentType { get; init; } = "application/octet-stream";

    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public static FlowOrchestrationPayloadSnapshot Capture(IntegrationPayload payload) => new()
    {
        Name = payload.Name, ContentBase64 = Convert.ToBase64String(payload.Content.ToArray()), ContentType = payload.ContentType,
        Metadata = new Dictionary<string, string>(payload.Metadata, StringComparer.OrdinalIgnoreCase)
    };

    public IntegrationPayload Restore() => new(Name, BinaryData.FromBytes(Convert.FromBase64String(ContentBase64)), ContentType,
        new Dictionary<string, string>(Metadata, StringComparer.OrdinalIgnoreCase));
}
