namespace Mulse.Ui.Models;

public sealed record FlowCheckpointViewModel(string InstanceId, string CorrelationKey, string Bookmark,
    DateTimeOffset UpdatedAt, int PayloadCount, int PendingSourceCount, int? TargetCount, bool? BatchReady, DateTimeOffset? DeadlineUtc = null,
    bool Native = false, bool RecoveryPending = false, string? EffectId = null, string? EffectKind = null, string? EffectModule = null, bool RequiresEffectOutput = false);
