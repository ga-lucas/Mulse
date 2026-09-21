using Microsoft.Extensions.Logging;

namespace Mulse.Modules;

public sealed class LoggingEventModule(ILogger<LoggingEventModule> logger) : IOutputModule
{
    public ModuleDescriptor Descriptor { get; } = new(
        "logging-event",
        "Logging output",
        ModuleKind.Output,
        "Writes payload details through the application logger as a lightweight output target.");

    public Task WriteAsync(
        FlowExecutionContext context,
        IntegrationBatch batch,
        ModuleStepDefinition step,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var previewLength = ModuleSettingReader.GetInt32(step.Settings, "previewLength", 200, Descriptor.Id);

        foreach (var payload in batch.Payloads)
        {
            var preview = payload.Content.ToString();
            if (preview.Length > previewLength)
            {
                preview = preview[..previewLength];
            }

            logger.LogInformation(
                "Event module {ModuleId} dispatched payload {PayloadName} for flow {FlowId} execution {ExecutionId} ({ContentType}, {ByteLength} bytes): {Preview}",
                Descriptor.Id,
                payload.Name,
                context.FlowId,
                context.ExecutionId,
                payload.ContentType,
                payload.Content.ToMemory().Length,
                preview);
        }

        return Task.CompletedTask;
    }
}
