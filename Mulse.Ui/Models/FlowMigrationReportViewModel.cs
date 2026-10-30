namespace Mulse.Ui.Models;

public sealed record FlowMigrationReportViewModel(
    string SourceSystem,
    string SourceProjectPath,
    string? SourceOrchestration,
    IReadOnlyList<MigrationArtifactReportViewModel> Artifacts,
    IReadOnlyList<MigrationDiagnosticViewModel> Diagnostics,
    IReadOnlyList<string> Warnings)
{
    public int Version { get; init; } = 1;
}

public sealed record MigrationArtifactReportViewModel(
    string Kind,
    string SourcePath,
    string Status,
    IReadOnlyList<MigrationDiagnosticViewModel> Diagnostics);

public sealed record MigrationDiagnosticViewModel(
    string Code,
    string Severity,
    string SourcePath,
    string Message,
    string Remediation);
