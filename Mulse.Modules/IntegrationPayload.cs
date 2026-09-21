namespace Mulse.Modules;

public sealed record IntegrationPayload(
    string Name,
    BinaryData Content,
    string ContentType,
    IReadOnlyDictionary<string, string> Metadata);
