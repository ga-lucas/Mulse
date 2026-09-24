namespace Mulse.Modules.Pipeline;

/// <summary>
/// One parse node inside a flow's source graph. A parse node normalizes the raw output of one or more
/// <see cref="FetchNodeDefinition"/>s listed in <see cref="FetchNodeIds"/>: every listed fetch node's raw batch is
/// parsed independently with this node's single <see cref="Parse"/> module and settings, and the resulting parsed
/// batches are merged into this node's one output (every payload tagged with a <c>sourceId</c> metadata entry equal
/// to this node's <see cref="Id"/>). This lets several fetches that produce the same document format (for example
/// three file watchers) share one parse configuration.
/// </summary>
public sealed class ParseNodeDefinition
{
    /// <summary>
    /// Unique id of this parse node within the flow, e.g. "primary" or "sqlLookup". Must also be distinct from every
    /// <see cref="FetchNodeDefinition.Id"/> in the same flow. This is the id downstream consumers (fetch nodes'
    /// <see cref="FetchNodeDefinition.InputParseIds"/>, join augments' source ids) refer to.
    /// </summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>The parse step applied to the raw batch of every fetch node in <see cref="FetchNodeIds"/>.</summary>
    public ModuleStepDefinition Parse { get; init; } = new();

    /// <summary>
    /// Ids of the fetch nodes whose RAW batches this node parses. Must contain at least one id; each id must name a
    /// fetch node of the same flow.
    /// </summary>
    public List<string> FetchNodeIds { get; init; } = [];
}
