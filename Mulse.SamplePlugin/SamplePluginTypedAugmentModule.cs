using Mulse.Modules;

namespace Mulse.SamplePlugin;

/// <summary>
/// Demonstrates <see cref="TypedAugmentModule{TIn,TOut}"/>: a hot-loadable plugin module that works entirely in
/// terms of its own strongly-typed records instead of raw JSON payload bytes. The flow that references this
/// module by id ("sample-plugin-typed-augment") and the settings that configure it are still assembled at
/// runtime like any other module - only this module's own transform logic is compile-time checked.
/// </summary>
public sealed class SamplePluginTypedAugmentModule(TimeProvider timeProvider)
    : TypedAugmentModule<SamplePluginTypedAugmentModule.OrderDocument, SamplePluginTypedAugmentModule.OrderSummary>
{
    private static readonly ModuleRecommendationProfile Recommendation = new(
        [ModuleDataFormat.Json],
        [ModuleProtocol.Plugin],
        [ModuleCapability.Transformation]);

    public override ModuleDescriptor Descriptor { get; } = new(
        "sample-plugin-typed-augment",
        "Sample plugin typed augment",
        ModuleKind.OrchestrationAugment,
        "Demonstrates strongly-typed module authoring: deserializes each payload as OrderDocument and produces an OrderSummary, with no manual JSON handling in the module's own code.",
        recommendation: Recommendation);

    protected override Task<OrderSummary?> TransformAsync(
        FlowExecutionContext context,
        OrderDocument input,
        IntegrationPayload sourcePayload,
        ModuleStepDefinition step,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var summary = new OrderSummary(
            input.OrderId,
            input.Lines.Count,
            input.Lines.Sum(line => line.Quantity * line.UnitPrice),
            timeProvider.GetUtcNow());

        return Task.FromResult<OrderSummary?>(summary);
    }

    /// <summary>The strongly-typed shape this module expects each input payload to already be.</summary>
    public sealed record OrderDocument(string OrderId, IReadOnlyList<OrderLine> Lines);

    public sealed record OrderLine(string Sku, int Quantity, decimal UnitPrice);

    /// <summary>The strongly-typed shape this module produces for each output payload.</summary>
    public sealed record OrderSummary(string OrderId, int LineCount, decimal Total, DateTimeOffset SummarizedAtUtc);
}
