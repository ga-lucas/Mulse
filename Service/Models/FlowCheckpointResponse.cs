namespace Service.Models;

public sealed record FlowCheckpointResponse(string InstanceId, string CorrelationKey, string Bookmark,
    DateTimeOffset UpdatedAt, int PayloadCount, int PendingSourceCount, int? TargetCount, bool? BatchReady, DateTimeOffset? DeadlineUtc = null);
