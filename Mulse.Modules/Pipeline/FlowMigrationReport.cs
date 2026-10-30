using System.Text.Json.Serialization;

namespace Mulse.Modules.Pipeline;

[JsonConverter(typeof(JsonStringEnumConverter<MigrationTranslationStatus>))]
public enum MigrationTranslationStatus
{
    FullyTranslated,
    ReviewRequired,
    StubGenerated,
    Unsupported,
    ConfigurationRequired
}

[JsonConverter(typeof(JsonStringEnumConverter<MigrationDiagnosticSeverity>))]
public enum MigrationDiagnosticSeverity
{
    Information,
    Review,
    Blocking
}

/// <summary>Import-time evidence, not an execution approval or a live validation result.</summary>
public sealed record FlowMigrationReport(
    string SourceSystem,
    string SourceProjectPath,
    string? SourceOrchestration,
    IReadOnlyList<MigrationArtifactReport> Artifacts,
    IReadOnlyList<MigrationDiagnostic> Diagnostics,
    IReadOnlyList<string> Warnings)
{
    public int Version { get; init; } = 1;

    public MigrationMapInventory? MapInventory { get; init; }
}

public sealed record MigrationArtifactReport(
    string Kind,
    string SourcePath,
    MigrationTranslationStatus Status,
    IReadOnlyList<MigrationDiagnostic> Diagnostics)
{
    public OrchestrationMigrationInventory? Orchestration { get; init; }
    public PipelineMigrationInventory? Pipeline { get; init; }
}

public sealed record OrchestrationMigrationInventory(
    IReadOnlyDictionary<string, int> ShapeCounts,
    IReadOnlyList<OrchestrationShapeReport> Shapes);

public sealed record OrchestrationShapeReport(
    int Id,
    int? ParentId,
    int SourceLine,
    string ShapeType,
    string Name,
    IReadOnlyDictionary<string, string> References);

public sealed record MigrationDiagnostic(
    string Code,
    MigrationDiagnosticSeverity Severity,
    string SourcePath,
    string Message,
    string Remediation);
