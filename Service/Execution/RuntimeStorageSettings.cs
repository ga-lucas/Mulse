namespace Service.Execution;

public sealed record RuntimeStorageSettings(
    string Provider,
    bool ConnectionConfigured,
    string? DataFile,
    bool ApplyMigrations,
    string? EncryptionKeyId,
    string KeyEnvironmentVariable,
    bool CaptureStagePayloads = false,
    int HistoryRetentionDays = 30,
    int ShutdownDrainSeconds = 90,
    int MaxConcurrentExecutions = 64,
    int MaxPendingMessages = 10000);
