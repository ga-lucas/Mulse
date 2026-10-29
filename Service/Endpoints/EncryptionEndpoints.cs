namespace Service.Endpoints;

public static class EncryptionEndpoints
{
    public sealed record SetEncryptionRequest(bool Enabled);

    public static WebApplication MapEncryptionEndpoints(this WebApplication app)
    {
        app.MapGet("/api/workspace/encryption", (IRuntimeConfigurationStore store) => TypedResults.Ok(store.GetEncryptionStatus()))
            .WithName("GetWorkspaceEncryption").WithTags("Configuration")
            .WithSummary("Read encryption status without exposing the key");
        app.MapPut("/api/workspace/encryption", async (SetEncryptionRequest request, IRuntimeConfigurationStore store, CancellationToken cancellationToken) =>
            TypedResults.Ok(await store.SetEncryptionAsync(request.Enabled, cancellationToken).ConfigureAwait(false)))
            .WithName("SetWorkspaceEncryption").WithTags("Configuration")
            .WithSummary("Atomically change state encryption using the configured environment key");
        return app;
    }
}
