using Microsoft.Extensions.Logging;
using Mulse.Modules;

namespace Mulse.SamplePlugin;

/// <summary>
/// Demonstrates <see cref="TypedDeliverModule{TIn}"/>: deserializes a <see cref="SamplePluginTypedAugmentModule.OrderSummary"/>
/// straight out of the (JSON) rendered payload and delivers it via the application logger, with no manual JSON
/// handling in the module's own code. Pair this with a JSON-producing render step (for example the built-in
/// <see cref="JsonRenderModule"/>, or directly after <see cref="SamplePluginTypedAugmentModule"/> with an
/// identity render) rather than <see cref="SamplePluginTypedRenderModule"/>, which renders to plain text.
/// </summary>
public sealed class SamplePluginTypedDeliverModule(ILogger<SamplePluginTypedDeliverModule> logger) : TypedDeliverModule<SamplePluginTypedAugmentModule.OrderSummary>
{
    private static readonly ModuleRecommendationProfile Recommendation = new(
        [ModuleDataFormat.Json],
        [ModuleProtocol.Plugin],
        [ModuleCapability.Logging, ModuleCapability.Delivery]);

    public override ModuleDescriptor Descriptor { get; } = new(
        "sample-plugin-typed-deliver",
        "Sample plugin typed deliver",
        ModuleKind.Deliver,
        "Demonstrates strongly-typed module authoring: deserializes an OrderSummary out of the rendered payload and logs it with no manual JSON deserialization in the module's own code.",
        recommendation: Recommendation);

    protected override Task DeliverAsync(
        FlowExecutionContext context,
        SamplePluginTypedAugmentModule.OrderSummary input,
        IntegrationPayload sourcePayload,
        ModuleStepDefinition step,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        logger.LogInformation(
            "Deliver module {ModuleId} dispatched order {OrderId} ({LineCount} lines, total {Total:C}) for flow {FlowId} execution {ExecutionId}",
            Descriptor.Id,
            input.OrderId,
            input.LineCount,
            input.Total,
            context.FlowId,
            context.ExecutionId);

        return Task.CompletedTask;
    }
}
