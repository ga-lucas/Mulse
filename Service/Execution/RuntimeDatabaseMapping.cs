using System.Text.Json;
using System.Text.Json.Serialization;
using Mulse.Persistence;

namespace Service.Execution;

internal static class RuntimeDatabaseMapping
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static WorkspaceDatabaseState ToDatabase(RuntimeMulseState state)
    {
        var documents = new List<WorkspaceDocument>();
        documents.AddRange(state.Pipelines.Select(flow => new WorkspaceDocument(WorkspaceDocumentKind.Flow,
            WorkspaceDocument.Identity(flow.Id), Serialize(flow))
        {
            OriginalId = flow.Id, Name = flow.Name, Category = flow.Category, Enabled = flow.Enabled
        }));
        documents.AddRange(state.ConfigValues.Select(pair => new WorkspaceDocument(WorkspaceDocumentKind.Configuration,
            WorkspaceDocument.Identity(pair.Key), Serialize(pair))));
        documents.AddRange(state.ManagedPackages.Select(package => new WorkspaceDocument(WorkspaceDocumentKind.ModulePackage,
            WorkspaceDocument.Identity(package.Id), Serialize(package))));
        documents.AddRange(state.OrchestrationCheckpoints.Select(checkpoint => new WorkspaceDocument(WorkspaceDocumentKind.Checkpoint,
            WorkspaceDocument.Identity(checkpoint.FlowId, checkpoint.CorrelationKey), Serialize(checkpoint))
        {
            FlowKey = WorkspaceDocument.Identity(checkpoint.FlowId)
        }));
        documents.AddRange(state.PendingRetries.Select(retry => new WorkspaceDocument(WorkspaceDocumentKind.Retry,
            WorkspaceDocument.Identity(retry.FlowId, retry.ExecutionId, retry.StageId), Serialize(retry))
        {
            FlowKey = WorkspaceDocument.Identity(retry.FlowId), ExecutionKey = WorkspaceDocument.Identity(retry.ExecutionId),
            NextAttemptUtcTicks = retry.NextAttemptAt.UtcTicks, NotificationsExhausted = retry.SourceCompletion?.NotificationsExhausted == true
        }));
        return new(Serialize(state.PluginDirectories), documents, state.MaintenanceRequested);
    }

    public static RuntimeMulseState FromDatabase(WorkspaceDatabaseState persisted)
    {
        var state = new RuntimeMulseState { PluginDirectories = Read<List<string>>(persisted.PluginDirectoriesJson), MaintenanceRequested = persisted.MaintenanceRequested };
        foreach (var document in persisted.Documents)
        {
            switch (document.Kind)
            {
                case WorkspaceDocumentKind.Flow: state.Pipelines.Add(Read<PipelineDefinition>(document.Payload)); break;
                case WorkspaceDocumentKind.Configuration:
                    var pair = Read<KeyValuePair<string, string>>(document.Payload);
                    state.ConfigValues.Add(pair.Key, pair.Value);
                    break;
                case WorkspaceDocumentKind.ModulePackage: state.ManagedPackages.Add(Read<ManagedModulePackageDefinition>(document.Payload)); break;
                case WorkspaceDocumentKind.Checkpoint: state.OrchestrationCheckpoints.Add(Read<FlowOrchestrationCheckpoint>(document.Payload)); break;
                case WorkspaceDocumentKind.Retry: state.PendingRetries.Add(Read<FlowRetryState>(document.Payload)); break;
                default: throw new InvalidOperationException("Unknown persisted workspace document kind.");
            }
        }
        var expected = ToDatabase(state).Documents.ToDictionary(row => (row.Kind, row.Key));
        if (persisted.Documents.Any(row => !expected.TryGetValue((row.Kind, row.Key), out var reconstructed)
            || row with { Payload = reconstructed.Payload } != reconstructed))
            throw new InvalidOperationException("Database record identities or indexed metadata disagree with their payloads. State was not loaded or reseeded.");
        return state;
    }

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);
    private static T Read<T>(string payload) => JsonSerializer.Deserialize<T>(payload, Json)
        ?? throw new InvalidOperationException($"Persisted '{typeof(T).Name}' cannot be null. State was not reseeded.");
}
