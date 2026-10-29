namespace Mulse.Ui.Models;

public sealed record FlowListFilter(
    string Search = "",
    string Status = "",
    string Trigger = "",
    string Module = "",
    string? Category = null,
    bool UncategorizedOnly = false,
    bool UnresolvedOnly = false,
    bool Descending = false)
{
    public IReadOnlyList<FlowViewModel> Apply(IEnumerable<FlowViewModel> flows, IEnumerable<ConfigReferenceUsageViewModel> usages)
    {
        var unresolved = usages.Where(usage => !usage.IsResolved).Select(usage => usage.FlowId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var terms = Search.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var filtered = flows.Where(flow =>
            (Status.Length == 0 || flow.Enabled == (Status == "enabled"))
            && (Trigger.Length == 0 || string.Equals(flow.TriggerMode, Trigger, StringComparison.OrdinalIgnoreCase))
            && (Module.Length == 0 || Modules(flow).Contains(Module, StringComparer.OrdinalIgnoreCase))
            && (Category is null || string.Equals(flow.Category, Category, StringComparison.OrdinalIgnoreCase))
            && (!UncategorizedOnly || string.IsNullOrWhiteSpace(flow.Category))
            && (!UnresolvedOnly || unresolved.Contains(flow.Id))
            && terms.All(term => SearchValues(flow).Any(value => value.Contains(term, StringComparison.OrdinalIgnoreCase))));
        return Descending
            ? filtered.OrderByDescending(flow => flow.Id, StringComparer.OrdinalIgnoreCase).ToArray()
            : filtered.OrderBy(flow => flow.Id, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static IEnumerable<string> Modules(FlowViewModel flow)
        => flow.FetchModules.Concat(flow.ParseModules).Concat(flow.AugmentModules).Concat(flow.RenderModules).Concat(flow.DeliverModules);

    private static IEnumerable<string> SearchValues(FlowViewModel flow)
        => new[] { flow.Id, flow.Name ?? "", flow.Category ?? "", flow.TriggerMode }
            .Concat(Modules(flow)).Concat(flow.FetchIds).Concat(flow.ParseIds);
}
