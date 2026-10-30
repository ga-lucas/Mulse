namespace Service.Flows;

internal static class FlowMigrationReadiness
{
    public const string BlockReason = "Imported flows cannot be enabled or run until verified translation support exists. Save this flow disabled and review its migration report. Editing configuration, diagnostics, or translation statuses does not approve execution.";

    public static void EnsureReady(PipelineDefinition pipeline)
    {
        // Import reports are snapshots, not trusted evidence that business behavior was implemented.
        if (pipeline.Migration is not null)
            throw new FlowMigrationBlockedException($"Flow '{pipeline.Id}': {BlockReason}");
    }
}

internal sealed class FlowMigrationBlockedException(string message) : InvalidOperationException(message);
