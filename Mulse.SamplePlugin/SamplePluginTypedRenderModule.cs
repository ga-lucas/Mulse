using Mulse.Modules;

namespace Mulse.SamplePlugin;

/// <summary>
/// Demonstrates <see cref="TypedRenderModule{TIn}"/>: renders a <see cref="SamplePluginTypedAugmentModule.OrderSummary"/>
/// (as produced by <see cref="SamplePluginTypedAugmentModule"/>) into a plain-text receipt, showing a coherent
/// typed pipeline of augment -&gt; render -&gt; deliver (see also <see cref="SamplePluginTypedDeliverModule"/>).
/// </summary>
public sealed class SamplePluginTypedRenderModule : TypedRenderModule<SamplePluginTypedAugmentModule.OrderSummary>
{
    private static readonly ModuleRecommendationProfile Recommendation = new(
        [ModuleDataFormat.Text],
        [ModuleProtocol.Plugin],
        [ModuleCapability.Transformation]);

    public override ModuleDescriptor Descriptor { get; } = new(
        "sample-plugin-typed-render",
        "Sample plugin typed render",
        ModuleKind.Render,
        "Demonstrates strongly-typed module authoring: renders an OrderSummary into a plain-text receipt with no manual JSON deserialization in the module's own code.",
        recommendation: Recommendation);

    protected override Task<RenderedDocument?> RenderAsync(
        FlowExecutionContext context,
        SamplePluginTypedAugmentModule.OrderSummary input,
        IntegrationPayload sourcePayload,
        ModuleStepDefinition step,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var receipt =
            $"""
             Order: {input.OrderId}
             Lines: {input.LineCount}
             Total: {input.Total:C}
             Summarized: {input.SummarizedAtUtc:O}
             """;

        var document = new RenderedDocument(
            BinaryData.FromString(receipt),
            "text/plain",
            Name: $"{input.OrderId}-receipt.txt");

        return Task.FromResult<RenderedDocument?>(document);
    }
}
