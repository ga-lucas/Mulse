using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Mulse.Persistence;

public enum WorkspaceDocumentKind { Flow, Configuration, ModulePackage, Checkpoint, Retry }

public sealed record WorkspaceDocument(WorkspaceDocumentKind Kind, string Key, string Payload)
{
    public string OriginalId { get; init; } = "";
    public string? Name { get; init; }
    public string? Category { get; init; }
    public bool Enabled { get; init; }
    public string FlowKey { get; init; } = "";
    public string ExecutionKey { get; init; } = "";
    public long NextAttemptUtcTicks { get; init; }
    public bool NotificationsExhausted { get; init; }

    public static string Identity(params string[] parts)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
            parts.Select(part => part.ToUpperInvariant()).ToArray()))));
}

public sealed record WorkspaceDatabaseState(string PluginDirectoriesJson, IReadOnlyList<WorkspaceDocument> Documents);

public sealed record WorkspaceDatabaseSnapshot(WorkspaceDatabaseState State, bool Encrypted, string? KeyId);
