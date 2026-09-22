namespace Mulse.Modules.Payloads;

public sealed record IntegrationBatch(IReadOnlyList<IntegrationPayload> Payloads)
{
    public static IntegrationBatch Empty { get; } = new([]);

    public int Count => Payloads.Count;
}
