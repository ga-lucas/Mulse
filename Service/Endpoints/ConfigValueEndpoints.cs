using Microsoft.AspNetCore.Http.HttpResults;
using Service.Models;

namespace Service.Endpoints;

public static class ConfigValueEndpoints
{
    public static WebApplication MapConfigValueEndpoints(this WebApplication app)
    {
        app.MapGet("/api/config-values", Ok<ConfigValueResponse[]> (IRuntimeConfigurationStore runtimeConfigurationStore, CancellationToken cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var response = runtimeConfigurationStore.GetState().ConfigValues
                    .OrderBy(static entry => entry.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(static entry => new ConfigValueResponse(entry.Key, entry.Value))
                    .ToArray();
                return TypedResults.Ok(response);
            })
            .WithName("GetConfigValues")
            .WithSummary("List stored config/secret values")
            .WithDescription("Returns every stored value that resolves a {{config:reference}} or {{secret:reference}} placeholder token. Combine with a flow's configurationRequirements (from BizTalk import or the designer) to see which references still need a value before the flow can run.")
            .WithTags("Configuration")
            .Produces<ConfigValueResponse[]>(StatusCodes.Status200OK);

        app.MapGet("/api/config-values/usages", Ok<ConfigReferenceUsageResponse[]> (IFlowDefinitionService flowDefinitionService, IConfigValueResolver configValueResolver, CancellationToken cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var usages = configValueResolver.FindReferenceUsages(flowDefinitionService.GetAll())
                    .OrderBy(static usage => usage.IsResolved)
                    .ThenBy(static usage => usage.FlowId, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(static usage => usage.SettingPath, StringComparer.OrdinalIgnoreCase)
                    .Select(static usage => new ConfigReferenceUsageResponse(usage.Reference, usage.FlowId, usage.SettingPath, usage.IsResolved))
                    .ToArray();
                return TypedResults.Ok(usages);
            })
            .WithName("GetConfigValueUsages")
            .WithSummary("Find every {{config:...}}/{{secret:...}} reference used across all flows")
            .WithDescription("Scans every flow's fetch, parse, augment, and delivery settings for {{config:reference}}/{{secret:reference}} placeholder tokens and reports where each is used and whether it currently has a stored value. Unresolved references are listed first so a UI can prioritize outstanding configuration follow-up work, whether the flow came from a BizTalk import or was hand-authored.")
            .WithTags("Configuration")
            .Produces<ConfigReferenceUsageResponse[]>(StatusCodes.Status200OK);

        app.MapPut("/api/config-values/{reference}", async Task<Ok<ConfigValueResponse>> (string reference, SetConfigValueRequest request, IRuntimeConfigurationStore runtimeConfigurationStore, CancellationToken cancellationToken) =>
            {
                await runtimeConfigurationStore.SetConfigValueAsync(reference, request.Value, cancellationToken).ConfigureAwait(false);
                return TypedResults.Ok(new ConfigValueResponse(reference, request.Value));
            })
            .WithName("SetConfigValue")
            .WithSummary("Create or update a config/secret value")
            .WithDescription("Sets the value that resolves a {{config:reference}} or {{secret:reference}} placeholder token wherever it appears across any flow's settings. Values are persisted with the rest of the runtime state and benefit from the same optional at-rest encryption.")
            .WithTags("Configuration")
            .Produces<ConfigValueResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        app.MapDelete("/api/config-values/{reference}", async Task<NoContent> (string reference, IRuntimeConfigurationStore runtimeConfigurationStore, CancellationToken cancellationToken) =>
            {
                await runtimeConfigurationStore.DeleteConfigValueAsync(reference, cancellationToken).ConfigureAwait(false);
                return TypedResults.NoContent();
            })
            .WithName("DeleteConfigValue")
            .WithSummary("Delete a config/secret value")
            .WithDescription("Removes a stored config/secret value. Any flow still referencing it via {{config:reference}}/{{secret:reference}} will fail to run with a clear 'unresolved configuration reference' error until it's set again.")
            .WithTags("Configuration")
            .Produces(StatusCodes.Status204NoContent);

        return app;
    }
}
