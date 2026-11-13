namespace Mulse.Ui.Models;

public sealed record StorageMigrationStatusViewModel(bool Supported, Guid? MigrationId, string? State, string? TargetFingerprint);
public sealed record StorageMigrationRequestViewModel(string Provider, string ConnectionString, bool PrepareSchema, Guid MigrationId,
    Guid? ExpectedSourceVersion = null, string? Confirmation = null, bool BackupConfirmed = false);
public sealed record StorageTableVerificationViewModel(string Table, long Rows, string Sha256);
public sealed record StorageMigrationReportViewModel(Guid MigrationId, Guid SourceVersion, bool Completed, bool Encrypted,
    string? KeyId, IReadOnlyList<StorageTableVerificationViewModel> Tables);
