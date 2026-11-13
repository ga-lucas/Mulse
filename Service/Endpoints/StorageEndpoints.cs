namespace Service.Endpoints;

public static class StorageEndpoints
{
    public static WebApplication MapStorageEndpoints(this WebApplication app)
    {
        app.MapGet("/api/workspace/storage", (RuntimeStorageSettings settings) => TypedResults.Ok(settings))
            .WithName("GetWorkspaceStorage").WithTags("Configuration")
            .WithSummary("Read effective startup storage settings without exposing connection strings or keys");
        return app;
    }
}
