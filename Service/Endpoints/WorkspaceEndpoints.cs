using Service.Models;

namespace Service.Endpoints;

public static class WorkspaceEndpoints
{
    public static WebApplication MapWorkspaceEndpoints(this WebApplication app)
    {
        app.MapPost("/api/workspace/backup", (CreateWorkspaceBackupRequest request, WorkspaceBackupService backups) =>
            TypedResults.File(backups.Export(request.Password), "application/octet-stream", $"mulse-workspace-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.mulsebackup"))
            .WithName("BackupWorkspace").WithTags("Workspace").WithSummary("Download a password-encrypted workspace backup");

        app.MapPost("/api/workspace/restore/preview", (RestoreWorkspaceRequest request, WorkspaceBackupService backups) =>
            TypedResults.Ok(backups.Preview(request)))
            .WithName("PreviewWorkspaceRestore").WithTags("Workspace").WithSummary("Preview a backup without changing the workspace");

        app.MapPost("/api/workspace/restore", async (RestoreWorkspaceRequest request, WorkspaceBackupService backups, CancellationToken cancellationToken) =>
            TypedResults.Ok(await backups.RestoreAsync(request, cancellationToken).ConfigureAwait(false)))
            .WithName("RestoreWorkspace").WithTags("Workspace").WithSummary("Merge or replace workspace configuration from an encrypted backup");

        app.MapPost("/api/workspace/flows/delete-all", async (DeleteAllFlowsRequest request, IRuntimeConfigurationStore store, CancellationToken cancellationToken) =>
            {
                if (request.Confirmation != "DELETE ALL FLOWS")
                {
                    throw new ArgumentException("Type DELETE ALL FLOWS to confirm deletion.");
                }
                return TypedResults.Ok(new { DeletedCount = await store.DeleteAllFlowsAsync(cancellationToken).ConfigureAwait(false) });
            })
            .WithName("DeleteAllFlows").WithTags("Workspace").WithSummary("Delete every flow and its saved execution state, preserving configuration values and packages");
        return app;
    }
}
