using System.Text.Json;

namespace Mulse.Modules.Sdk;

/// <summary>
/// Optional base class for <see cref="IParseModule"/> implementations that interpret each raw payload as a
/// strongly-typed CLR document instead of hand-rolling text/byte parsing and JSON re-serialization.
/// </summary>
/// <remarks>
/// <para>
/// A parse module's whole job is to interpret an arbitrary raw format (XML, CSV, HL7, a custom delimited or
/// binary layout, ...) that only that module understands, so - unlike <see cref="TypedAugmentModule{TIn,TOut}"/>
/// and the other typed bases below - this base class does not attempt to auto-deserialize the input. Implement
/// <see cref="ParseAsync"/> to read <paramref name="sourcePayload"/>'s raw text/bytes yourself (via
/// <see cref="IntegrationPayloadTextExtensions.GetText(IntegrationPayload)"/> or direct byte access) and return
/// a <typeparamref name="TOut"/>; this base class then JSON-serializes it into the normalized working payload,
/// the same convention every built-in parse module (<see cref="JsonParseModule"/> and friends) already follows.
/// Return <c>default</c>/<c>null</c> to drop a payload entirely (for example, to skip an empty or header-only file).
/// </para>
/// <para>
/// See also <see cref="TypedFetchModule{TOut}"/>, <see cref="TypedAugmentModule{TIn,TOut}"/>,
/// <see cref="TypedRenderModule{TIn}"/>, and <see cref="TypedDeliverModule{TIn}"/> for the other module kinds in
/// this SDK family, all built on the shared <see cref="TypedModulePayloadSerializer"/> helpers.
/// </para>
/// </remarks>
/// <typeparam name="TOut">The strongly-typed shape this module parses each raw payload into.</typeparam>
public abstract class TypedParseModule<TOut> : IParseModule
{
    /// <summary>Serializer options used to serialize each parsed <typeparamref name="TOut"/> document. Defaults to <see cref="JsonSerializerDefaults.Web"/> (camelCase).</summary>
    protected virtual JsonSerializerOptions SerializerOptions { get; } = new(JsonSerializerDefaults.Web);

    /// <inheritdoc />
    public abstract ModuleDescriptor Descriptor { get; }

    /// <summary>
    /// Parses one raw payload into a strongly-typed document. Return <c>default</c>/<c>null</c> to drop the
    /// payload from the output batch entirely.
    /// </summary>
    /// <param name="context">The current flow execution context (flow id, execution id, orchestration state, etc.).</param>
    /// <param name="sourcePayload">The raw payload to parse.</param>
    /// <param name="step">This step's module settings, exactly as for any other module.</param>
    /// <param name="cancellationToken">Cancellation token for the flow execution.</param>
    protected abstract Task<TOut?> ParseAsync(
        FlowExecutionContext context,
        IntegrationPayload sourcePayload,
        ModuleStepDefinition step,
        CancellationToken cancellationToken);

    /// <summary>
    /// Builds the metadata dictionary attached to the parsed output payload. Defaults to copying the source
    /// payload's metadata, plus a <c>parsedBy</c> entry naming this module.
    /// </summary>
    protected virtual IReadOnlyDictionary<string, string> BuildMetadata(IntegrationPayload sourcePayload, TOut output)
    {
        var metadata = new Dictionary<string, string>(sourcePayload.Metadata, StringComparer.OrdinalIgnoreCase)
        {
            ["parsedBy"] = Descriptor.Id
        };
        return metadata;
    }

    /// <summary>
    /// Builds the output payload's file name. Defaults to the source payload's name with a <c>.json</c>
    /// extension, matching the convention used throughout this codebase for JSON-producing modules.
    /// </summary>
    protected virtual string BuildPayloadName(IntegrationPayload sourcePayload, TOut output)
        => Path.ChangeExtension(sourcePayload.Name, ".json");

    async Task<IntegrationBatch> IParseModule.ParseAsync(
        FlowExecutionContext context,
        IntegrationBatch batch,
        ModuleStepDefinition step,
        CancellationToken cancellationToken)
    {
        var results = new List<IntegrationPayload>(batch.Payloads.Count);

        foreach (var payload in batch.Payloads)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var output = await ParseAsync(context, payload, step, cancellationToken).ConfigureAwait(false);
            if (output is null)
            {
                continue;
            }

            var metadata = BuildMetadata(payload, output);
            var name = BuildPayloadName(payload, output);
            results.Add(TypedModulePayloadSerializer.ToJsonPayload(output, name, SerializerOptions, metadata));
        }

        return new IntegrationBatch(results);
    }
}
