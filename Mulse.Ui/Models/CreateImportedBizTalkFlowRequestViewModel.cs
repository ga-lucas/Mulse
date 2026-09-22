namespace Mulse.Ui.Models;

public sealed record CreateImportedBizTalkFlowRequestViewModel(
    string SourcePath,
    string SuggestedFlowId,
    IReadOnlyList<string>? AdditionalSourcePaths = null);
