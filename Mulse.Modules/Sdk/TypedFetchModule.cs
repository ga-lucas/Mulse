using System.Text.Json;

namespace Mulse.Modules.Sdk;

/// <summary>
/// Optional base class for <see cref="IFetchModule"/> implementations that acquire a set of already-structured
/// documents (from an API, database, in-memory catalog, etc.) rather than opaque external bytes, and want to
/// hand them off as a strongly-typed CLR collection instead of hand-building <see cref="IntegrationPayload"/>s.
/// </summary>
/// <remarks>
/// <para>
/// Most built-in fetch modules (local file system, SFTP, HTTP inbound) acquire genuinely opaque bytes whose
/// shape isn't known until a parse module interprets them - those modules correctly implement
/// <see cref="IFetchModule"/> directly. This base class is for the other common case: a fetch module that talks
/// to a source which already returns structured data (a REST API's JSON response, a database query's rows, an
/// in-memory or generated catalog) where the module author knows the exact CLR shape of each item at compile
/// time. Implement <see cref="FetchAsync"/> to return a list of <typeparamref name="TOut"/> and this base class
/// handles JSON-serializing each item into its own output payload, the same convention every built-in fetch
/// module already follows for structured sources.
/// </para>
/// <para>
/// <paramref name="input"/> in <see cref="FetchAsync"/> is still the raw, weakly-typed merged batch from this
/// source's <see cref="SourceDefinition.InputSourceIds"/> (as for any <see cref="IFetchModule"/>) - a typed
/// fetch module that consumes another source's output should inspect it with <see cref="JsonPayloadNavigator"/>
/// or deserialize individual payloads itself, since the upstream source's shape isn't known to this base class.
/// </para>
/// <para>
/// See also <see cref="TypedParseModule{TOut}"/>, <see cref="TypedAugmentModule{TIn,TOut}"/>,
/// <see cref="TypedRenderModule{TIn}"/>, and <see cref="TypedDeliverModule{TIn}"/> for the other module kinds in
/// this SDK family, all built on the shared <see cref="TypedModulePayloadSerializer"/> helpers.
/// </para>
/// </remarks>
/// <typeparam name="TOut">The strongly-typed shape of each document this module fetches.</typeparam>
public abstract class TypedFetchModule<TOut> : IFetchModule
{
    /// <summary>Serializer options used to serialize each <typeparamref name="TOut"/> item. Defaults to <see cref="JsonSerializerDefaults.Web"/> (camelCase).</summary>
    protected virtual JsonSerializerOptions SerializerOptions { get; } = new(JsonSerializerDefaults.Web);

    /// <inheritdoc />
    public abstract ModuleDescriptor Descriptor { get; }

    /// <summary>
    /// Acquires this source's documents as a strongly-typed collection. Return an empty list to fetch nothing
    /// this run (for example, an API call that returned no new records).
    /// </summary>
    /// <param name="context">The current flow execution context (flow id, execution id, orchestration state, etc.).</param>
    /// <param name="input">The merged, weakly-typed parsed batch from this source's input sources, or <see cref="IntegrationBatch.Empty"/> for a root source.</param>
    /// <param name="step">This step's module settings, exactly as for any other module.</param>
    /// <param name="cancellationToken">Cancellation token for the flow execution.</param>
    protected abstract Task<IReadOnlyList<TOut>> FetchAsync(
        FlowExecutionContext context,
        IntegrationBatch input,
        ModuleStepDefinition step,
        CancellationToken cancellationToken);

    /// <summary>
    /// Builds the output payload's file name for the item at <paramref name="index"/>. Defaults to
    /// <c>{module-id}-{index}.json</c>; override for a more meaningful name (e.g. keyed by the item's id).
    /// </summary>
    protected virtual string BuildPayloadName(TOut item, int index)
        => $"{Descriptor.Id}-{index}.json";

    /// <summary>Builds the metadata dictionary attached to the output payload for <paramref name="item"/>. Defaults to empty.</summary>
    protected virtual IReadOnlyDictionary<string, string> BuildMetadata(TOut item, int index)
        => new Dictionary<string, string>();

    async Task<IntegrationBatch> IFetchModule.FetchAsync(
        FlowExecutionContext context,
        IntegrationBatch input,
        ModuleStepDefinition step,
        CancellationToken cancellationToken)
    {
        var items = await FetchAsync(context, input, step, cancellationToken).ConfigureAwait(false);
        var results = new List<IntegrationPayload>(items.Count);

        for (var index = 0; index < items.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var item = items[index];
            var name = BuildPayloadName(item, index);
            var metadata = BuildMetadata(item, index);
            results.Add(TypedModulePayloadSerializer.ToJsonPayload(item, name, SerializerOptions, metadata));
        }

        return new IntegrationBatch(results);
    }
}
