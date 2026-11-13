namespace Mulse.Ui.Models;

public sealed record ProcessingStatusViewModel(string State, bool MaintenanceRequested, int ActiveExecutions,
    int ActiveAdmissions, bool ShutdownRequested, string? Error);
