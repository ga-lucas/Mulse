using Microsoft.AspNetCore.Http.HttpResults;
using Service.Models;

namespace Service.Endpoints;

public static class ModuleEndpoints
{
    public static WebApplication MapModuleEndpoints(this WebApplication app)
    {
        app.MapGet("/api/modules", Ok<ModuleResponse[]> (IModuleCatalog moduleCatalog, CancellationToken cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                var response = moduleCatalog.GetAll()
                    .Select(static module => new ModuleResponse(
                        module.Descriptor.Id,
                        module.Descriptor.DisplayName,
                        module.Descriptor.Kind,
                        module.Descriptor.Description,
                        module.PackageId,
                        module.PackageSourceKind,
                        module.AssemblyPath))
                    .ToArray();

                return TypedResults.Ok(response);
            })
            .WithName("GetModules")
            .WithSummary("List available modules")
            .WithDescription("Returns every registered source, transform, event, and storage module available to configured flows, including the package that currently supplies each module.")
            .WithTags("Modules")
            .Produces<ModuleResponse[]>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        app.MapGet("/api/module-packages", Ok<ModulePackageResponse[]> (IModuleCatalog moduleCatalog, CancellationToken cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var response = moduleCatalog.GetPackages()
                    .Select(static package => new ModulePackageResponse(
                        package.Id,
                        package.AssemblyPath,
                        package.SourceKind,
                        package.IsLoaded,
                        package.LastLoadedAt,
                        package.ModuleCount))
                    .ToArray();
                return TypedResults.Ok(response);
            })
            .WithName("GetModulePackages")
            .WithSummary("List runtime module packages")
            .WithDescription("Returns the runtime module packages that are currently loaded or managed by the service.")
            .WithTags("Modules")
            .Produces<ModulePackageResponse[]>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        app.MapGet("/api/module-packages/{packageId}", Results<Ok<ModulePackageResponse>, NotFound> (string packageId, IModuleCatalog moduleCatalog, CancellationToken cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var package = moduleCatalog.GetPackages()
                    .FirstOrDefault(candidate => string.Equals(candidate.Id, packageId, StringComparison.OrdinalIgnoreCase));

                return package is null
                    ? TypedResults.NotFound()
                    : TypedResults.Ok(new ModulePackageResponse(
                        package.Id,
                        package.AssemblyPath,
                        package.SourceKind,
                        package.IsLoaded,
                        package.LastLoadedAt,
                        package.ModuleCount));
            })
            .WithName("GetModulePackageById")
            .WithSummary("Get a runtime module package")
            .WithDescription("Returns a single runtime module package by id.")
            .WithTags("Modules")
            .Produces<ModulePackageResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound);

        app.MapPost("/api/module-packages", async Task<Created<ModulePackageResponse>> (CreateModulePackageRequest request, IModuleCatalog moduleCatalog, CancellationToken cancellationToken) =>
            {
                var package = await moduleCatalog.UpsertManagedPackageAsync(new ManagedModulePackageDefinition
                {
                    Id = request.Id,
                    AssemblyPath = request.AssemblyPath,
                    Enabled = request.Enabled
                }, cancellationToken).ConfigureAwait(false);

                return TypedResults.Created($"/api/module-packages/{package.Id}", new ModulePackageResponse(
                    package.Id,
                    package.AssemblyPath,
                    package.SourceKind,
                    package.IsLoaded,
                    package.LastLoadedAt,
                    package.ModuleCount));
            })
            .WithName("CreateModulePackage")
            .WithSummary("Create a managed runtime module package")
            .WithDescription("Registers and loads a managed runtime plugin assembly without restarting the service.")
            .WithTags("Modules")
            .Produces<ModulePackageResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        app.MapPut("/api/module-packages/{packageId}", async Task<Ok<ModulePackageResponse>> (string packageId, UpdateModulePackageRequest request, IModuleCatalog moduleCatalog, CancellationToken cancellationToken) =>
            {
                if (!string.Equals(packageId, request.Id, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException("The package id in the route must match the payload.", nameof(packageId));
                }

                var package = await moduleCatalog.UpsertManagedPackageAsync(new ManagedModulePackageDefinition
                {
                    Id = request.Id,
                    AssemblyPath = request.AssemblyPath,
                    Enabled = request.Enabled
                }, cancellationToken).ConfigureAwait(false);

                return TypedResults.Ok(new ModulePackageResponse(
                    package.Id,
                    package.AssemblyPath,
                    package.SourceKind,
                    package.IsLoaded,
                    package.LastLoadedAt,
                    package.ModuleCount));
            })
            .WithName("UpdateModulePackage")
            .WithSummary("Update a managed runtime module package")
            .WithDescription("Updates the assembly path or enabled state for a managed runtime plugin package and reloads it in place.")
            .WithTags("Modules")
            .Produces<ModulePackageResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        app.MapPost("/api/module-packages/{packageId}/reload", async Task<Ok<ModulePackageResponse>> (string packageId, IModuleCatalog moduleCatalog, CancellationToken cancellationToken) =>
            {
                var package = await moduleCatalog.ReloadPackageAsync(packageId, cancellationToken).ConfigureAwait(false);
                return TypedResults.Ok(new ModulePackageResponse(
                    package.Id,
                    package.AssemblyPath,
                    package.SourceKind,
                    package.IsLoaded,
                    package.LastLoadedAt,
                    package.ModuleCount));
            })
            .WithName("ReloadModulePackage")
            .WithSummary("Reload a runtime module package")
            .WithDescription("Reloads a runtime plugin package from disk without restarting the service.")
            .WithTags("Modules")
            .Produces<ModulePackageResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        app.MapDelete("/api/module-packages/{packageId}", async Task<NoContent> (string packageId, IModuleCatalog moduleCatalog, CancellationToken cancellationToken) =>
            {
                await moduleCatalog.RemoveManagedPackageAsync(packageId, cancellationToken).ConfigureAwait(false);
                return TypedResults.NoContent();
            })
            .WithName("DeleteModulePackage")
            .WithSummary("Delete a managed runtime module package")
            .WithDescription("Unloads a managed runtime plugin package and removes it from the runtime configuration store.")
            .WithTags("Modules")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        return app;
    }
}
