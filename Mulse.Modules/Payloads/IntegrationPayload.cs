namespace Mulse.Modules.Payloads;

public sealed record IntegrationPayload(
    string Name,
    BinaryData Content,
    string ContentType,
    IReadOnlyDictionary<string, string> Metadata);
