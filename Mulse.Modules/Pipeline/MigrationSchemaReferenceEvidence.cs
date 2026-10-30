namespace Mulse.Modules.Pipeline;

public sealed record MigrationSchemaReferenceEvidence(
    string Reference,
    string Status,
    int TotalCandidates,
    int OmittedCount,
    IReadOnlyList<MigrationSchemaSourceCandidate> Candidates,
    int CatalogWarningCount,
    string Limitation);

public sealed record MigrationSchemaSourceCandidate(
    string SourceProjectPath,
    string SourcePath,
    string ClassName,
    string Method,
    int SourceLine,
    bool SourceExists);
