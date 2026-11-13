namespace Service.Endpoints;

public static class HistoryEndpoints
{
    public sealed record RecoveryConfirmationRequest(string Confirmation, string Evidence);

    private static FlowJournal Journal(IServiceProvider services)
        => services.GetService<FlowJournal>() ?? throw new InvalidOperationException("Execution/message history requires a database storage engine. Legacy Json mode has no durable inbox or delivery journal.");

    private static int Limit(int? take)
        => take is null ? 50 : take is >= 1 and <= 200 ? take.Value : throw new ArgumentException("take must be between 1 and 200.");

    public static WebApplication MapHistoryEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/history").WithTags("History");
        group.MapGet("/executions", async (string? flowId, int? take, IServiceProvider services, HttpResponse response, CancellationToken cancellationToken) =>
        {
            response.Headers.CacheControl = "no-store";
            var rows = await Journal(services).ListAsync(flowId, Limit(take), cancellationToken).ConfigureAwait(false);
            return TypedResults.Ok(rows.Select(FlowJournal.Summary));
        });
        group.MapGet("/executions/{executionId}", async (string executionId, IServiceProvider services, HttpResponse response, CancellationToken cancellationToken) =>
        {
            response.Headers.CacheControl = "no-store";
            return TypedResults.Ok(await Journal(services).DetailAsync(executionId, cancellationToken).ConfigureAwait(false));
        });
        group.MapGet("/messages", async (int? take, IServiceProvider services, HttpResponse response, CancellationToken cancellationToken) =>
        {
            response.Headers.CacheControl = "no-store";
            return TypedResults.Ok(await Journal(services).MessagesAsync(Limit(take), cancellationToken).ConfigureAwait(false));
        });
        group.MapGet("/messages/{id:guid}", async (Guid id, IServiceProvider services, HttpResponse response, CancellationToken cancellationToken) =>
        {
            response.Headers.CacheControl = "no-store";
            return TypedResults.Ok(new { json = await Journal(services).ReadMessageAsync(id, cancellationToken).ConfigureAwait(false) });
        });
        group.MapPost("/executions/{executionId}/deliveries/{deliveryId:guid}/confirm-delivered", async (
            string executionId, Guid deliveryId, RecoveryConfirmationRequest request, IServiceProvider services, CancellationToken cancellationToken) =>
        {
            if (request.Confirmation != "CONFIRM DELIVERED") throw new ArgumentException("Type CONFIRM DELIVERED after independently verifying destination evidence. No external delivery will be replayed.");
            await Journal(services).ConfirmDeliveredAsync(executionId, deliveryId, request.Evidence, cancellationToken).ConfigureAwait(false);
            return TypedResults.Ok(new { confirmed = true });
        });
        group.MapPost("/executions/{executionId}/close", async (
            string executionId, RecoveryConfirmationRequest request, IServiceProvider services, CancellationToken cancellationToken) =>
        {
            if (request.Confirmation != "CLOSE WITHOUT REPLAY") throw new ArgumentException("Type CLOSE WITHOUT REPLAY to explicitly abandon held input after independent review.");
            await Journal(services).CloseHeldAsync(executionId, request.Evidence, cancellationToken).ConfigureAwait(false);
            return TypedResults.Ok(new { closed = true });
        });
        return app;
    }
}
