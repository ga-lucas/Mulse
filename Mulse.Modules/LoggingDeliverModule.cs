using Microsoft.Extensions.Logging;

namespace Mulse.Modules;

public sealed class LoggingDeliverModule(ILogger<LoggingDeliverModule> logger) : IDeliverModule
{
    private static readonly IReadOnlyList<ModuleSettingDescriptor> SettingDescriptors =
    [
        new("previewLength", "Preview length", "The maximum number of characters to include in the log preview.", false, ModuleSettingInputKind.Number, "200")
    ];

    private static readonly ModuleRecommendationProfile Recommendation = new(
        [ModuleDataFormat.Json, ModuleDataFormat.Xml, ModuleDataFormat.Csv, ModuleDataFormat.Text],
        capabilities: [ModuleCapability.Logging, ModuleCapability.Delivery]);

    public ModuleDescriptor Descriptor { get; } = new(
        "logging-deliver",
        "Logging deliver",
        ModuleKind.Deliver,
        "Writes rendered payload details through the application logger as a lightweight delivery target.",
        SettingDescriptors,
        Recommendation);

    public Task DeliverAsync(
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
                "Deliver module {ModuleId} dispatched payload {PayloadName} for flow {FlowId} execution {ExecutionId} ({ContentType}, {ByteLength} bytes): {Preview}",
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
