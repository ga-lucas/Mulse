namespace Mulse.Modules.Execution;

public sealed class FlowOrchestrationPayloadSnapshot
{
    public string Name { get; init; } = string.Empty;

    public string ContentBase64 { get; init; } = string.Empty;

    public string ContentType { get; init; } = "application/octet-stream";

    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}
