using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mulse.Persistence;

namespace Service.Endpoints;

public sealed record ProcessingBacklog(bool DurableInboxSupported, int? UnfinishedMessages, int? PendingMessages,
    int? ClaimedMessages, int? HeldMessages, int? HeldExecutions, int? UnknownDeliveries, int? InFlightDeliveries, int? MaxPendingMessages);

public static class ProcessingEndpoints
{
    public sealed record MaintenanceRequest(bool Requested);

    public static WebApplication MapProcessingEndpoints(this WebApplication app)
    {
        app.MapGet("/api/workspace/processing", (ProcessingCoordinator coordinator, HttpResponse response) =>
        {
            response.Headers.CacheControl = "no-store";
            return TypedResults.Ok(coordinator.GetStatus());
        }).WithTags("Operations");
        app.MapGet("/api/workspace/processing/backlog", async (ProcessingCoordinator coordinator, IServiceProvider services, HttpResponse response, CancellationToken token) =>
        {
            response.Headers.CacheControl = "no-store";
            var database = services.GetService<WorkspaceDatabase>();
            if (database is null) return TypedResults.Ok(new ProcessingBacklog(false, null, null, null, null, null, null, null, null));
            var capacity = services.GetRequiredService<IOptions<FlowHistoryOptions>>().Value.MaxPendingMessages;
            return TypedResults.Ok(await database.OperationsAsync(async session => new ProcessingBacklog(true,
                await session.Db.Messages.CountAsync(row => row.State == "Pending" || row.State == "Claimed" || row.State == "Held", token),
                await session.Db.Messages.CountAsync(row => row.State == "Pending", token),
                await session.Db.Messages.CountAsync(row => row.State == "Claimed", token),
                await session.Db.Messages.CountAsync(row => row.State == "Held", token),
                await session.Db.Executions.CountAsync(row => row.State == "Held", token),
                await session.Db.Deliveries.CountAsync(row => row.State == "Unknown", token),
                await session.Db.Deliveries.CountAsync(row => row.State == "InFlight", token), capacity), false, token).ConfigureAwait(false));
        }).WithTags("Operations");
        app.MapPut("/api/workspace/processing/maintenance", async (MaintenanceRequest request, ProcessingCoordinator coordinator, CancellationToken cancellationToken) =>
            TypedResults.Ok(await coordinator.SetMaintenanceAsync(request.Requested, cancellationToken).ConfigureAwait(false)))
            .WithTags("Operations");
        return app;
    }
}
