using System.Text.Json;

namespace Mulse.Modules.Sdk;

/// <summary>
/// Optional base class for <see cref="IOrchestrationAugmentModule"/> implementations that want to work with a
/// strongly-typed CLR model instead of raw <see cref="IntegrationPayload"/> bytes.
/// </summary>
/// <remarks>
/// <para>
/// A flow's module graph is assembled entirely at runtime - modules are referenced by string id, wired together
/// by configuration, and exchange opaque <see cref="IntegrationPayload"/> content (bytes + content type +
/// string metadata) because the orchestrator itself has no idea what shape any given flow's data is. That part
/// necessarily stays weakly typed: two arbitrary modules can be connected by an operator who picks them from a
/// catalog, and the runtime can't know in advance whether they agree on a schema.
/// </para>
/// <para>
/// But nothing about that requires an individual module's own implementation to be weakly typed. A module
/// author writing ordinary, compiled C# (see <c>Mulse.SamplePlugin</c> for the plugin-hosting mechanism) already
/// knows - at compile time - exactly what shape of document they expect to receive and produce. This base class
/// removes the deserialize/reserialize boilerplate so that knowledge shows up as an ordinary generic type
/// parameter (<typeparamref name="TIn"/> / <typeparamref name="TOut"/>) instead of hand-rolled
/// <see cref="JsonSerializer"/> calls scattered through <see cref="AugmentAsync"/>: implement
/// <see cref="TransformAsync"/> against your own POCO/record types and get full IntelliSense, compiler
/// checking, and refactoring safety for your module's own logic, while the flow graph around it remains exactly
/// as runtime-composable as any other module.
/// </para>
/// <para>
/// If a payload doesn't deserialize as <typeparamref name="TIn"/> (the operator wired this module to a source
/// that doesn't actually produce the shape this module expects), that is a genuine configuration error and is
/// surfaced as an <see cref="InvalidOperationException"/> with the payload name and module id, following the
/// same "fail loud, name the payload and module" convention used by <see cref="JsonPayloadNavigator"/> and
/// <see cref="ModuleSettingReader"/>.
/// </para>
/// <para>
/// This base class is a convenience, not a requirement - modules with unusual payload shapes (non-JSON, one
/// input mapping to many outputs, streaming, etc.) can still implement <see cref="IOrchestrationAugmentModule"/>
/// directly, exactly as every built-in module in this project does today.
/// </para>
/// <para>
/// This is one of five "Typed*Module" SDK base classes covering every module kind - see also
/// <see cref="TypedFetchModule{TOut}"/>, <see cref="TypedParseModule{TOut}"/>,
/// <see cref="TypedRenderModule{TIn}"/>, and <see cref="TypedDeliverModule{TIn}"/> - all built on the shared
/// <see cref="TypedModulePayloadSerializer"/> helpers so every kind fails the same way on a shape mismatch.
/// </para>
/// </remarks>
/// <typeparam name="TIn">The strongly-typed shape this module expects each input payload to deserialize as.</typeparam>
/// <typeparam name="TOut">The strongly-typed shape this module produces for each output payload.</typeparam>
public abstract class TypedAugmentModule<TIn, TOut> : IOrchestrationAugmentModule
{
    /// <summary>
    /// Serializer options used for both deserializing <typeparamref name="TIn"/> and serializing
    /// <typeparamref name="TOut"/>. Override to customize naming policy, converters, etc. Defaults to
    /// <see cref="JsonSerializerDefaults.Web"/> (camelCase), matching every other JSON-producing module in
    /// this codebase.
    /// </summary>
    protected virtual JsonSerializerOptions SerializerOptions { get; } = new(JsonSerializerDefaults.Web);

    /// <inheritdoc />
    public abstract ModuleDescriptor Descriptor { get; }

    /// <summary>
    /// Transforms one strongly-typed input document into a strongly-typed output document. Return
    /// <c>default</c>/<c>null</c> to drop the payload from the output batch entirely (e.g. to implement
    /// filtering).
    /// </summary>
    /// <param name="context">The current flow execution context (flow id, execution id, orchestration state, etc.).</param>
    /// <param name="input">The deserialized input document for <paramref name="sourcePayload"/>.</param>
    /// <param name="sourcePayload">
    /// The original payload this input was deserialized from, in case the module needs its name, content type,
    /// or metadata (for example, to propagate or extend metadata on the output payload).
    /// </param>
    /// <param name="step">This step's module settings, exactly as for any other module.</param>
    /// <param name="cancellationToken">Cancellation token for the flow execution.</param>
    protected abstract Task<TOut?> TransformAsync(
        FlowExecutionContext context,
        TIn input,
        IntegrationPayload sourcePayload,
        ModuleStepDefinition step,
        CancellationToken cancellationToken);

    /// <summary>
    /// Builds the metadata dictionary attached to each output payload. Defaults to copying the source payload's
    /// metadata unchanged; override to add or replace entries (most built-in modules stamp a
    /// <c>transformedBy</c>/<c>transformedAtUtc</c> pair - do the same here if that's useful for your module).
    /// </summary>
    protected virtual IReadOnlyDictionary<string, string> BuildMetadata(IntegrationPayload sourcePayload, TIn input, TOut output)
        => sourcePayload.Metadata;

    /// <summary>
    /// Builds the output payload's file name. Defaults to the source payload's name with a <c>.json</c>
    /// extension, matching the convention used throughout this codebase for JSON-producing modules.
    /// </summary>
    protected virtual string BuildPayloadName(IntegrationPayload sourcePayload, TIn input, TOut output)
        => Path.ChangeExtension(sourcePayload.Name, ".json");

    async Task<IntegrationBatch> IOrchestrationAugmentModule.AugmentAsync(
        FlowExecutionContext context,
        IntegrationBatch batch,
        ModuleStepDefinition step,
        CancellationToken cancellationToken)
    {
        var results = new List<IntegrationPayload>(batch.Payloads.Count);

        foreach (var payload in batch.Payloads)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var input = Deserialize(payload);
            var output = await TransformAsync(context, input, payload, step, cancellationToken).ConfigureAwait(false);
            if (output is null)
            {
                continue;
            }

            var metadata = BuildMetadata(payload, input, output);
            var name = BuildPayloadName(payload, input, output);

            results.Add(TypedModulePayloadSerializer.ToJsonPayload(output, name, SerializerOptions, metadata));
        }

        return new IntegrationBatch(results);
    }

    private TIn Deserialize(IntegrationPayload payload)
        => TypedModulePayloadSerializer.Deserialize<TIn>(payload, Descriptor.Id, SerializerOptions);
}
