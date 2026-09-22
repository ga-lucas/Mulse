namespace Mulse.Modules;

public interface IFetchModule : IModule
{
    /// <summary>
    /// Acquires this source's raw content. <paramref name="input"/> is the merged PARSED batch of every source
    /// listed in the owning <see cref="SourceDefinition.InputSourceIds"/> (each payload carries a <c>sourceId</c>
    /// metadata entry identifying which source produced it), or <see cref="IntegrationBatch.Empty"/> for a root
    /// source. Modules that ingest from an external system only (file system, SFTP, HTTP inbound, ...) ignore it.
    /// </summary>
    Task<IntegrationBatch> FetchAsync(
        FlowExecutionContext context,
        IntegrationBatch input,
        ModuleStepDefinition step,
        CancellationToken cancellationToken);
}
