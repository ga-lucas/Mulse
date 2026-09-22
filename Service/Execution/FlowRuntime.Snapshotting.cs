using Mulse.Modules;

namespace Service.Execution;

public sealed partial class FlowRuntime
{
    private static Dictionary<string, IntegrationBatch> RestoreResolvedSources(
        IReadOnlyDictionary<string, List<FlowOrchestrationPayloadSnapshot>> snapshots)
    {
        var resolved = new Dictionary<string, IntegrationBatch>(StringComparer.OrdinalIgnoreCase);
        foreach (var (sourceId, payloads) in snapshots)
        {
            resolved[sourceId] = RestoreBatch(payloads);
        }

        return resolved;
    }

    private static List<FlowOrchestrationPayloadSnapshot> SnapshotBatch(IntegrationBatch batch)
        => batch.Payloads.Select(payload => new FlowOrchestrationPayloadSnapshot
        {
            Name = payload.Name,
            ContentBase64 = Convert.ToBase64String(payload.Content.ToArray()),
            ContentType = payload.ContentType,
            Metadata = new Dictionary<string, string>(payload.Metadata, StringComparer.OrdinalIgnoreCase)
        }).ToList();

    private static Dictionary<string, List<FlowOrchestrationPayloadSnapshot>> SnapshotResolvedSources(
        IReadOnlyDictionary<string, IntegrationBatch>? resolvedSources)
    {
        var snapshots = new Dictionary<string, List<FlowOrchestrationPayloadSnapshot>>(StringComparer.OrdinalIgnoreCase);
        if (resolvedSources is null)
        {
            return snapshots;
        }

        foreach (var (sourceId, batch) in resolvedSources)
        {
            snapshots[sourceId] = SnapshotBatch(batch);
        }

        return snapshots;
    }

    private static IntegrationBatch RestoreBatch(IReadOnlyList<FlowOrchestrationPayloadSnapshot> snapshots)
        => new(snapshots.Select(snapshot => new IntegrationPayload(
            snapshot.Name,
            BinaryData.FromBytes(Convert.FromBase64String(snapshot.ContentBase64)),
            snapshot.ContentType,
            new Dictionary<string, string>(snapshot.Metadata, StringComparer.OrdinalIgnoreCase))).ToArray());
}
