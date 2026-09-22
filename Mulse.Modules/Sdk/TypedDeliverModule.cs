using System.Text.Json;

namespace Mulse.Modules.Sdk;

/// <summary>
/// Optional base class for <see cref="IDeliverModule"/> implementations that deserialize the rendered document
/// as a strongly-typed CLR model before delivering it, instead of hand-rolling JSON deserialization.
/// </summary>
/// <remarks>
/// <para>
/// This base class is only appropriate for delivery routes whose render stage produces JSON (for example, an
/// identity/JSON render step feeding a deliver module that publishes typed messages to a queue, or one that
/// inspects a few fields to make a routing/priority decision before shipping the payload). Delivery routes that
/// render to XML, CSV, or another non-JSON wire format should implement <see cref="IDeliverModule"/> directly,
/// exactly as every built-in deliver module (<see cref="HttpDeliverModule"/>, <see cref="FileSystemDeliverModule"/>)
/// already does - those modules ship whatever bytes render produced, unmodified, regardless of format.
/// </para>
/// <para>
/// See also <see cref="TypedFetchModule{TOut}"/>, <see cref="TypedParseModule{TOut}"/>,
/// <see cref="TypedAugmentModule{TIn,TOut}"/>, and <see cref="TypedRenderModule{TIn}"/> for the other module
/// kinds in this SDK family.
/// </para>
/// </remarks>
/// <typeparam name="TIn">The strongly-typed shape this module expects each delivered payload to deserialize as.</typeparam>
public abstract class TypedDeliverModule<TIn> : IDeliverModule
{
    /// <summary>Serializer options used to deserialize each <typeparamref name="TIn"/> document. Defaults to <see cref="JsonSerializerDefaults.Web"/> (camelCase).</summary>
    protected virtual JsonSerializerOptions SerializerOptions { get; } = new(JsonSerializerDefaults.Web);

    /// <inheritdoc />
    public abstract ModuleDescriptor Descriptor { get; }

    /// <summary>Delivers one strongly-typed document.</summary>
    /// <param name="context">The current flow execution context (flow id, execution id, orchestration state, etc.).</param>
    /// <param name="input">The deserialized document for <paramref name="sourcePayload"/>.</param>
    /// <param name="sourcePayload">The original rendered payload this input was deserialized from.</param>
    /// <param name="step">This step's module settings, exactly as for any other module.</param>
    /// <param name="cancellationToken">Cancellation token for the flow execution.</param>
    protected abstract Task DeliverAsync(
        FlowExecutionContext context,
        TIn input,
        IntegrationPayload sourcePayload,
        ModuleStepDefinition step,
        CancellationToken cancellationToken);

    async Task IDeliverModule.DeliverAsync(
        FlowExecutionContext context,
        IntegrationBatch batch,
        ModuleStepDefinition step,
        CancellationToken cancellationToken)
    {
        foreach (var payload in batch.Payloads)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var input = TypedModulePayloadSerializer.Deserialize<TIn>(payload, Descriptor.Id, SerializerOptions);
            await DeliverAsync(context, input, payload, step, cancellationToken).ConfigureAwait(false);
        }
    }
}
