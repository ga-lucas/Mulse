namespace Mulse.Modules.Payloads;

public sealed record IntegrationBatch(IReadOnlyList<IntegrationPayload> Payloads)
{
    public static IntegrationBatch Empty { get; } = new([]);

    /// <summary>Optional opaque receipt returned by an IFetchOutcomeModule. The runtime captures it before parsing.</summary>
    public IReadOnlyDictionary<string, string>? SourceReceipt { get; init; }

    public int Count => Payloads.Count;
}
