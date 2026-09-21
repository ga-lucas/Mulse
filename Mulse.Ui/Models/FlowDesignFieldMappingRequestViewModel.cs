namespace Mulse.Ui.Models;

public sealed record FlowDesignFieldMappingRequestViewModel(
    string SourceKind,
    string SourcePath,
    string TargetField,
    string Condition,
    string? LiteralValue);
