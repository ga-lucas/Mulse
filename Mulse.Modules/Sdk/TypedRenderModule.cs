using System.Text.Json;

namespace Mulse.Modules.Sdk;

/// <summary>
/// The final rendered form of one payload produced by a <see cref="TypedRenderModule{TIn}"/> - the wire-format
/// bytes and content type to hand to the delivery stage, plus optional overrides for the output payload's name
/// and metadata (defaults to <see cref="TypedRenderModule{TIn}.BuildPayloadName"/>/<see cref="TypedRenderModule{TIn}.BuildMetadata"/> when omitted).
/// </summary>
public sealed record RenderedDocument(
    BinaryData Content,
    string ContentType,
    string? Name = null,
    IReadOnlyDictionary<string, string>? Metadata = null);

/// <summary>
/// Optional base class for <see cref="IRenderModule"/> implementations that deserialize the strongly-typed
/// working document and produce a final wire-format document (XML, CSV, a custom text format, ...) instead of
/// hand-rolling JSON deserialization of the input.
/// </summary>
/// <remarks>
/// <para>
/// By the time a flow reaches its render stage, the working payload is the JSON document produced by the
/// augment chain (or by parse directly, if there are no augments) - so unlike <see cref="TypedParseModule{TOut}"/>,
/// this base class auto-deserializes each input payload as <typeparamref name="TIn"/> using the same
/// <see cref="TypedModulePayloadSerializer"/> convention as <see cref="TypedAugmentModule{TIn,TOut}"/>. Implement
/// <see cref="RenderAsync"/> against your own POCO/record type and return a <see cref="RenderedDocument"/> with
/// the final bytes and content type (XML, CSV, plain text, or any other format your delivery target expects).
/// Return <c>null</c> to drop a payload entirely (for example, to skip a document that doesn't need to be
/// delivered given its own field values).
/// </para>
/// <para>
/// See also <see cref="TypedFetchModule{TOut}"/>, <see cref="TypedParseModule{TOut}"/>,
/// <see cref="TypedAugmentModule{TIn,TOut}"/>, and <see cref="TypedDeliverModule{TIn}"/> for the other module
/// kinds in this SDK family.
/// </para>
/// </remarks>
/// <typeparam name="TIn">The strongly-typed shape this module expects each working payload to deserialize as.</typeparam>
public abstract class TypedRenderModule<TIn> : IRenderModule
{
    /// <summary>Serializer options used to deserialize each <typeparamref name="TIn"/> working document. Defaults to <see cref="JsonSerializerDefaults.Web"/> (camelCase).</summary>
    protected virtual JsonSerializerOptions SerializerOptions { get; } = new(JsonSerializerDefaults.Web);

    /// <inheritdoc />
    public abstract ModuleDescriptor Descriptor { get; }

    /// <summary>
    /// Renders one strongly-typed working document into its final wire format. Return <c>null</c> to drop the
    /// payload from the output batch entirely.
    /// </summary>
    /// <param name="context">The current flow execution context (flow id, execution id, orchestration state, etc.).</param>
    /// <param name="input">The deserialized working document for <paramref name="sourcePayload"/>.</param>
    /// <param name="sourcePayload">The original JSON working payload this input was deserialized from.</param>
    /// <param name="step">This step's module settings, exactly as for any other module.</param>
    /// <param name="cancellationToken">Cancellation token for the flow execution.</param>
    protected abstract Task<RenderedDocument?> RenderAsync(
        FlowExecutionContext context,
        TIn input,
        IntegrationPayload sourcePayload,
        ModuleStepDefinition step,
        CancellationToken cancellationToken);

    /// <summary>
    /// Builds the output payload's file name when a <see cref="RenderedDocument"/> doesn't specify one. Defaults
    /// to the source payload's name unchanged; override to change the extension for your rendered format.
    /// </summary>
    protected virtual string BuildPayloadName(IntegrationPayload sourcePayload, TIn input, RenderedDocument rendered)
        => sourcePayload.Name;

    /// <summary>
    /// Builds the metadata dictionary attached to the output payload when a <see cref="RenderedDocument"/>
    /// doesn't specify one. Defaults to copying the source payload's metadata, plus a <c>renderedBy</c> entry
    /// naming this module.
    /// </summary>
    protected virtual IReadOnlyDictionary<string, string> BuildMetadata(IntegrationPayload sourcePayload, TIn input, RenderedDocument rendered)
    {
        var metadata = new Dictionary<string, string>(sourcePayload.Metadata, StringComparer.OrdinalIgnoreCase)
        {
            ["renderedBy"] = Descriptor.Id
        };
        return metadata;
    }

    async Task<IntegrationBatch> IRenderModule.RenderAsync(
        FlowExecutionContext context,
        IntegrationBatch batch,
        ModuleStepDefinition step,
        CancellationToken cancellationToken)
    {
        var results = new List<IntegrationPayload>(batch.Payloads.Count);

        foreach (var payload in batch.Payloads)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var input = TypedModulePayloadSerializer.Deserialize<TIn>(payload, Descriptor.Id, SerializerOptions);
            var rendered = await RenderAsync(context, input, payload, step, cancellationToken).ConfigureAwait(false);
            if (rendered is null)
            {
                continue;
            }

            var name = rendered.Name ?? BuildPayloadName(payload, input, rendered);
            var metadata = rendered.Metadata ?? BuildMetadata(payload, input, rendered);
            results.Add(new IntegrationPayload(name, rendered.Content, rendered.ContentType, metadata));
        }

        return new IntegrationBatch(results);
    }
}
