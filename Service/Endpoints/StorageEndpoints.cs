namespace Service.Endpoints;

public static class StorageEndpoints
{
    public static WebApplication MapStorageEndpoints(this WebApplication app)
    {
        app.MapGet("/api/workspace/storage", (RuntimeStorageSettings settings) => TypedResults.Ok(settings))
            .WithName("GetWorkspaceStorage").WithTags("Configuration")
            .WithSummary("Read effective startup storage settings without exposing connection strings or keys");
        app.MapGet("/api/workspace/storage/migration", (IServiceProvider services, HttpResponse response) =>
        {
            response.Headers.CacheControl = "no-store";
            return TypedResults.Ok(services.GetRequiredService<StorageMigrationService>().GetStatus());
        }).WithTags("Configuration");
        app.MapPost("/api/workspace/storage/migration/preview", async (StorageMigrationRequest request, IServiceProvider services, HttpResponse response, CancellationToken token) =>
        {
            response.Headers.CacheControl = "no-store";
            return TypedResults.Ok(await services.GetRequiredService<StorageMigrationService>().RunAsync(request, false, token).ConfigureAwait(false));
        }).WithTags("Configuration");
        app.MapPost("/api/workspace/storage/migration/execute", async (StorageMigrationRequest request, IServiceProvider services, HttpResponse response, CancellationToken token) =>
        {
            response.Headers.CacheControl = "no-store";
            return TypedResults.Ok(await services.GetRequiredService<StorageMigrationService>().RunAsync(request, true, token).ConfigureAwait(false));
        }).WithTags("Configuration");
        return app;
    }
}
