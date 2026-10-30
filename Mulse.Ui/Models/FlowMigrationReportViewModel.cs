namespace Mulse.Ui.Models;

public sealed record FlowMigrationReportViewModel(
    string SourceSystem,
    string SourceProjectPath,
    string? SourceOrchestration,
    IReadOnlyList<MigrationArtifactReportViewModel> Artifacts,
    IReadOnlyList<MigrationDiagnosticViewModel> Diagnostics,
    IReadOnlyList<string> Warnings)
{
    public const string ExecutionBlockReason = "Imported flows cannot be enabled or run until verified translation support exists. Save this flow disabled and review its migration report.";

    public int Version { get; init; } = 1;

    public MigrationMapInventoryViewModel? MapInventory { get; init; }
}

public sealed record MigrationMapInventoryViewModel(
    int TotalDiscovered,
    int OmittedCount,
    IReadOnlyList<MigrationMapCandidateViewModel> Candidates)
{
    public IReadOnlyList<string> SchemaCatalogWarnings { get; init; } = [];
}

public sealed record MigrationMapCandidateViewModel(
    string Name,
    string SourceProjectPath,
    string SourcePath,
    string SourceSchemaReference,
    string TargetSchemaReference,
    string Status,
    int DirectLinkCount,
    int ConstantValueCount,
    int UnsupportedLinkCount,
    IReadOnlyDictionary<int, int> FunctoidFidCounts,
    string Limitation)
{
    public MapScriptInventoryViewModel? ScriptInventory { get; init; }
    public MigrationSchemaReferenceEvidenceViewModel? SourceSchema { get; init; }
    public MigrationSchemaReferenceEvidenceViewModel? TargetSchema { get; init; }
}

public sealed record MigrationSchemaReferenceEvidenceViewModel(string Reference, string Status, int TotalCandidates, int OmittedCount,
    IReadOnlyList<MigrationSchemaSourceCandidateViewModel> Candidates, int CatalogWarningCount, string Limitation);
public sealed record MigrationSchemaSourceCandidateViewModel(string SourceProjectPath, string SourcePath, string ClassName, string Method, int SourceLine, bool SourceExists);

public sealed record MapScriptInventoryViewModel(int TotalScripts, int OmittedCount, IReadOnlyList<MapScriptEvidenceViewModel> Scripts);
public sealed record MapScriptEvidenceViewModel(int SourceLine, string FunctoidId, string Language, int CharacterCount, string Sha256, IReadOnlyList<string> BizTalkApiReferences);

public sealed record MigrationArtifactReportViewModel(
    string Kind,
    string SourcePath,
    string Status,
    IReadOnlyList<MigrationDiagnosticViewModel> Diagnostics)
{
    public OrchestrationMigrationInventoryViewModel? Orchestration { get; init; }
    public PipelineMigrationInventoryViewModel? Pipeline { get; init; }
}

public sealed record PipelineMigrationInventoryViewModel(string Direction, int TotalStages, int OmittedStageCount, int TotalComponents,
    int OmittedComponentCount, IReadOnlyList<PipelineStageEvidenceViewModel> Stages);
public sealed record PipelineStageEvidenceViewModel(int Order, int SourceLine, string CategoryId, string StageName, IReadOnlyList<PipelineComponentEvidenceViewModel> Components);
public sealed record PipelineComponentEvidenceViewModel(int Order, int SourceLine, string ClassName, string Version, int TotalProperties, int OmittedPropertyCount, IReadOnlyList<string> PropertyNames);

public sealed record OrchestrationMigrationInventoryViewModel(
    IReadOnlyDictionary<string, int> ShapeCounts,
    IReadOnlyList<OrchestrationShapeViewModel> Shapes);

public sealed record OrchestrationShapeViewModel(
    int Id,
    int? ParentId,
    int SourceLine,
    string ShapeType,
    string Name,
    IReadOnlyDictionary<string, string> References);

public sealed record MigrationDiagnosticViewModel(
    string Code,
    string Severity,
    string SourcePath,
    string Message,
    string Remediation);
