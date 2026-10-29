using Mulse.Ui.Models;

namespace Service.Tests;

public sealed class FlowListFilterTests
{
    private static FlowViewModel Flow(string id, string? category, bool enabled, string trigger, string module)
        => new(id, enabled, trigger, null, false, ["input"], ["parsed"], [module], ["json-parse"], [], ["json-render"], ["http-deliver"], [], [], [], [], Category: category);

    private static readonly FlowViewModel[] Flows =
    [
        Flow("Orders", "Sales", true, "Interval", "sql-fetch"),
        Flow("Invoices", "Finance", false, "OnDemand", "file-fetch"),
        Flow("Customers", null, true, "Push", "http-fetch")
    ];

    [Fact]
    public void Search_IsCaseInsensitiveAndMatchesAllTermsAcrossMetadata()
    {
        Assert.Equal("Orders", new FlowListFilter(Search: " SALES sql-FETCH input ").Apply(Flows, []).Single().Id);
        Assert.Equal("Invoices", new FlowListFilter(Search: "invoice finance").Apply(Flows, []).Single().Id);
        Assert.Empty(new FlowListFilter(Search: "orders finance").Apply(Flows, []));
        Assert.Equal(3, new FlowListFilter(Search: "  ").Apply(Flows, []).Count);
    }

    [Fact]
    public void Search_MatchesNameAndIdAndLegacyFlowsDisplayTheirId()
    {
        var named = Flows[0] with { Name = "Customer order import" };
        Assert.Equal("Orders", new FlowListFilter(Search: "customer import").Apply([named], []).Single().Id);
        Assert.Equal("Orders", new FlowListFilter(Search: "Orders").Apply([named], []).Single().Id);
        Assert.Equal("Customer order import", named.DisplayName);
        Assert.Equal("Orders", (named with { Name = null }).DisplayName);
        Assert.Equal("Orders", (named with { Name = " " }).DisplayName);
    }

    [Fact]
    public void CategoryStatusTriggerAndModuleFiltersCombine()
    {
        var filter = new FlowListFilter(Status: "enabled", Trigger: "interval", Module: "SQL-FETCH", Category: "sales");
        Assert.Equal("Orders", filter.Apply(Flows, []).Single().Id);
        Assert.Empty((filter with { Status = "disabled" }).Apply(Flows, []));
        Assert.Equal("Customers", new FlowListFilter(UncategorizedOnly: true).Apply(Flows, []).Single().Id);
        Assert.Equal(3, new FlowListFilter(Module: "http-deliver").Apply(Flows, []).Count);
    }

    [Fact]
    public void UnresolvedFilterUsesOnlyUnresolvedReferencesAndCanCombineWithSearch()
    {
        ConfigReferenceUsageViewModel[] usages =
        [
            new("path", "ORDERS", "fetch", false),
            new("url", "Customers", "deliver", true)
        ];
        Assert.Equal("Orders", new FlowListFilter(UnresolvedOnly: true).Apply(Flows, usages).Single().Id);
        Assert.Empty(new FlowListFilter(Search: "customers", UnresolvedOnly: true).Apply(Flows, usages));
    }

    [Fact]
    public void SortingIsStableRegardlessOfInputOrderAndDoesNotMutateSource()
    {
        Assert.Equal(["Customers", "Invoices", "Orders"], new FlowListFilter().Apply(Flows, []).Select(flow => flow.Id));
        Assert.Equal(["Orders", "Invoices", "Customers"], new FlowListFilter(Descending: true).Apply(Flows, []).Select(flow => flow.Id));
        Assert.Equal("Orders", Flows[0].Id);
    }
}
