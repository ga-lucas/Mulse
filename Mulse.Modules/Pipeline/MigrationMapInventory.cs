namespace Mulse.Modules.Pipeline;

public sealed record MigrationMapInventory(
    int TotalDiscovered,
    int OmittedCount,
    IReadOnlyList<MigrationMapCandidate> Candidates)
{
    public IReadOnlyList<string> SchemaCatalogWarnings { get; init; } = [];
}

public sealed record MigrationMapCandidate(
    string Name,
    string SourceProjectPath,
    string SourcePath,
    string SourceSchemaReference,
    string TargetSchemaReference,
    MigrationTranslationStatus Status,
    int DirectLinkCount,
    int ConstantValueCount,
    int UnsupportedLinkCount,
    IReadOnlyDictionary<int, int> FunctoidFidCounts,
    string Limitation)
{
    public MapScriptInventory? ScriptInventory { get; init; }
    public MigrationSchemaReferenceEvidence? SourceSchema { get; init; }
    public MigrationSchemaReferenceEvidence? TargetSchema { get; init; }
}
