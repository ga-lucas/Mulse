namespace Mulse.Ui.Models;

public sealed record WorkspaceRestorePreviewViewModel(
    int FlowCount,
    int PackageCount,
    int ConfigValueCount,
    IReadOnlyList<string> Conflicts,
    IReadOnlyList<string> Warnings);

public sealed record RestoreWorkspaceRequestViewModel(string Content, string Password, string Mode, string? Confirmation = null);
