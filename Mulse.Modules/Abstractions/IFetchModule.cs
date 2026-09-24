namespace Mulse.Modules.Abstractions;

public interface IFetchModule : IModule
{
    /// <summary>
    /// Acquires this fetch node's raw content. <paramref name="input"/> is the merged PARSED batch of every parse
    /// node listed in the owning <see cref="FetchNodeDefinition.InputParseIds"/> (one or more parse nodes' already
    /// parsed output; each payload carries a <c>sourceId</c> metadata entry identifying which parse node produced
    /// it), or <see cref="IntegrationBatch.Empty"/> for a root fetch node. Modules that ingest from an external
    /// system only (file system, SFTP, HTTP inbound, ...) ignore it.
    /// </summary>
    Task<IntegrationBatch> FetchAsync(
        FlowExecutionContext context,
        IntegrationBatch input,
        ModuleStepDefinition step,
        CancellationToken cancellationToken);
}
