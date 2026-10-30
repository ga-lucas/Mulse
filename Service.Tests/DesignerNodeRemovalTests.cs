using System.Collections;
using System.Reflection;
using Mulse.Ui.Components.Pages;

namespace Service.Tests;

public sealed class DesignerNodeRemovalTests
{
    [Theory]
    [InlineData("Fetch")]
    [InlineData("Parse")]
    [InlineData("Augment")]
    [InlineData("Render")]
    [InlineData("Deliver")]
    public void CanvasRemoval_RemovesOnlyClickedNodeAndClearsItsSelection(string stage)
    {
        var designer = new Designer();
        var nodes = new[] { "Fetch", "Parse", "Augment", "Render", "Deliver" }
            .Select(kind => AddNode(designer, kind, kind.ToLowerInvariant())).ToArray();
        var removed = nodes.Single(node => Property<object>(node, "Stage").ToString() == stage);
        Field("_selectedNodeId").SetValue(designer, Property<string>(removed, "Id"));

        Invoke(designer, "RemoveCanvasNode", removed);

        Assert.Equal(4, Nodes(designer).Count);
        Assert.DoesNotContain(removed, Nodes(designer).Cast<object>());
        Assert.All(nodes.Where(node => !ReferenceEquals(node, removed)), node => Assert.Contains(node, Nodes(designer).Cast<object>()));
        Assert.Null(Field("_selectedNodeId").GetValue(designer));
        Assert.Contains("Removed", (string)Field("_statusMessage").GetValue(designer)!);
    }

    [Fact]
    public void RemovingSharedFetch_DetachesEveryParserButKeepsOtherFetches()
    {
        var designer = new Designer();
        var shared = AddNode(designer, "Fetch", "shared");
        var other = AddNode(designer, "Fetch", "other");
        var first = AddNode(designer, "Parse", "first");
        var second = AddNode(designer, "Parse", "second");
        Property<List<string>>(first, "FetchNodeIds").AddRange(["shared", "other"]);
        Property<List<string>>(second, "FetchNodeIds").Add("SHARED");

        Invoke(designer, "RemoveCanvasNode", shared);

        Assert.Equal(new[] { "other" }, Property<List<string>>(first, "FetchNodeIds"));
        Assert.Empty(Property<List<string>>(second, "FetchNodeIds"));
        Assert.Contains(other, Nodes(designer).Cast<object>());
        Assert.Contains(first, Nodes(designer).Cast<object>());
        Assert.Contains(second, Nodes(designer).Cast<object>());
    }

    [Fact]
    public void RemovingParse_KeepsItsFetchAndClearsDependentFetchAndJoinReferences()
    {
        var designer = new Designer();
        var fetch = AddNode(designer, "Fetch", "input");
        var parse = AddNode(designer, "Parse", "parsed");
        Property<List<string>>(parse, "FetchNodeIds").Add("input");
        var lookup = AddNode(designer, "Fetch", "lookup");
        Property<List<string>>(lookup, "InputParseIds").AddRange(["PARSED", "reference"]);
        var join = AddNode(designer, "Augment", "join");
        var settings = Property<Dictionary<string, string>>(join, "Settings");
        settings["leftSourceId"] = "parsed";
        settings["rightSourceId"] = "reference";

        Invoke(designer, "RemoveCanvasNode", parse);

        Assert.Contains(fetch, Nodes(designer).Cast<object>());
        Assert.Equal(new[] { "reference" }, Property<List<string>>(lookup, "InputParseIds"));
        Assert.Equal(string.Empty, settings["leftSourceId"]);
        Assert.Equal("reference", settings["rightSourceId"]);
    }

    [Fact]
    public void RemovingUnselectedNode_PreservesSelectionAndOtherDeliveryActions()
    {
        var designer = new Designer();
        var render = AddNode(designer, "Render", "render");
        var first = AddNode(designer, "Deliver", "first");
        var second = AddNode(designer, "Deliver", "second");
        Field("_selectedNodeId").SetValue(designer, Property<string>(render, "Id"));

        Invoke(designer, "RemoveCanvasNode", first);

        Assert.Equal(Property<string>(render, "Id"), Field("_selectedNodeId").GetValue(designer));
        Assert.Contains(render, Nodes(designer).Cast<object>());
        Assert.Contains(second, Nodes(designer).Cast<object>());
    }

    [Theory]
    [InlineData("Render", "json-render", "BATCH x2")]
    [InlineData("Parse", "json-parse", "BATCH x2")]
    [InlineData("Augment", "decision-augment", "BATCH x2")]
    [InlineData("Augment", "multi-source-join-map-augment", "JOIN BY KEY")]
    [InlineData("Render", "multi-source-join-map-augment", "BATCH x2")]
    public void IncomingBadge_OnlyLabelsActualJoinAugmentsAsJoins(string stage, string module, string expected)
    {
        var designer = new Designer();
        var node = AddNode(designer, stage, "node");
        node.GetType().GetProperty("ModuleId")!.SetValue(node, module);
        Assert.Equal(expected, Invoke(designer, "IncomingBatchLabel", node, 2));
    }

    [Fact]
    public void AddRender_RestoresMissingRenderWithoutReplacingDeliverNodesOrSettings()
    {
        var designer = new Designer();
        var first = AddNode(designer, "Deliver", "first");
        var second = AddNode(designer, "Deliver", "second");
        Property<Dictionary<string, string>>(first, "Settings")["path"] = "C:\\exports";
        Invoke(designer, "AddRenderNode");
        var render = Nodes(designer).Cast<object>().Single(node => Property<object>(node, "Stage").ToString() == "Render");
        Assert.Equal("route", Property<string>(render, "RouteId"));
        Assert.Contains(first, Nodes(designer).Cast<object>());
        Assert.Contains(second, Nodes(designer).Cast<object>());
        Assert.Equal("C:\\exports", Property<Dictionary<string, string>>(first, "Settings")["path"]);
        Invoke(designer, "AddRenderNode");
        Assert.Equal(3, Nodes(designer).Count);
        Assert.Contains("already has a render", (string)Field("_errorMessage").GetValue(designer)!);
    }

    [Fact]
    public void AddRender_CreatesStandaloneRoute_AndExtraDeliverCanAttach()
    {
        var designer = new Designer();
        Invoke(designer, "AddRenderNodeAt", 410d, 280d);
        var render = Assert.Single(Nodes(designer).Cast<object>());
        Assert.Equal("Render", Property<object>(render, "Stage").ToString());
        Assert.Equal(410d, Property<double>(render, "X"));
        Assert.Equal(280d, Property<double>(render, "Y"));
        Invoke(designer, "AddDeliverNode");
        var deliver = Nodes(designer).Cast<object>().Single(node => Property<object>(node, "Stage").ToString() == "Deliver");
        Assert.Equal(Property<string>(render, "RouteId"), Property<string>(deliver, "RouteId"));
    }

    [Fact]
    public void RemovingJoin_KeepsRenderDeliverAndSources()
    {
        var designer = new Designer();
        var fetch = AddNode(designer, "Fetch", "fetch");
        var parse = AddNode(designer, "Parse", "parse");
        var join = AddNode(designer, "Augment", "join");
        join.GetType().GetProperty("ModuleId")!.SetValue(join, "multi-source-join-map-augment");
        var render = AddNode(designer, "Render", "render");
        var deliver = AddNode(designer, "Deliver", "deliver");
        Assert.StartsWith("Join sources", (string)Invoke(designer, "NodeHeading", join)!);
        Invoke(designer, "SelectNode", join);
        Assert.Equal(Property<string>(join, "Id"), Field("_selectedNodeId").GetValue(designer));
        Invoke(designer, "RemoveCanvasNode", join);
        Assert.All(new[] { fetch, parse, render, deliver }, node => Assert.Contains(node, Nodes(designer).Cast<object>()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FitView_UsesExactlyEightyPercentScale(bool populated)
    {
        var designer = new Designer();
        if (populated) AddNode(designer, "Render", "render");
        Invoke(designer, "FitView");
        var width = (double)typeof(Designer).GetProperty("CanvasWidth", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(designer)!;
        var height = (double)typeof(Designer).GetProperty("CanvasHeight", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(designer)!;
        Assert.Equal(width / 0.8, Field("_viewW").GetValue(designer));
        Assert.Equal(height / 0.8, Field("_viewH").GetValue(designer));
        Assert.Equal("80%", typeof(Designer).GetProperty("ZoomPercentLabel", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(designer));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AddParser_RepairsSelectedOrSoleUnparsedFetchWithoutLosingItsSettings(bool selected)
    {
        var designer = new Designer();
        var fetch = AddNode(designer, "Fetch", "existing-input");
        fetch.GetType().GetProperty("ModuleId")!.SetValue(fetch, "sql-server-lookup-fetch");
        Property<Dictionary<string, string>>(fetch, "Settings")["connectionString"] = "{{secret:sql}}";
        fetch.GetType().GetProperty("X")!.SetValue(fetch, 80d);
        fetch.GetType().GetProperty("Y")!.SetValue(fetch, 210d);
        Field("_modules").SetValue(designer, new Mulse.Ui.Models.ModuleViewModel[]
        {
            new("sql-server-lookup-fetch", "SQL lookup", "Fetch", "JSON rows", [], ["Json"], [], []),
            new("json-parse", "JSON parse", "Parse", "Validates JSON", [], ["Json"], [], [])
        });
        if (selected) Invoke(designer, "SelectNode", fetch);

        Invoke(designer, "AddParseNode");

        Assert.Equal(2, Nodes(designer).Count);
        var parse = Nodes(designer).Cast<object>().Single(node => Property<object>(node, "Stage").ToString() == "Parse");
        Assert.Equal("json-parse", Property<string>(parse, "ModuleId"));
        Assert.Equal(new[] { "existing-input" }, Property<List<string>>(parse, "FetchNodeIds"));
        Assert.Equal("{{secret:sql}}", Property<Dictionary<string, string>>(fetch, "Settings")["connectionString"]);
        Assert.Equal(80d, Property<double>(fetch, "X"));
        Assert.Equal(210d, Property<double>(fetch, "Y"));
        Assert.Contains("join settings", (string)Field("_statusMessage").GetValue(designer)!);
    }

    [Fact]
    public void AddParser_DoesNotGuessAmongMultipleOrphans_AndIdsRemainUnique()
    {
        var designer = new Designer();
        AddNode(designer, "Fetch", "primary");
        AddNode(designer, "Fetch", "other");
        Invoke(designer, "AddParseNode");
        var parse = Nodes(designer).Cast<object>().Single(node => Property<object>(node, "Stage").ToString() == "Parse");
        Assert.Empty(Property<List<string>>(parse, "FetchNodeIds"));
        Assert.Empty(Property<string>(parse, "ModuleId"));
        Assert.NotEqual("primary", Property<string>(parse, "GraphId"));
        Assert.Contains("select the fetch outputs", (string)Field("_statusMessage").GetValue(designer)!);
    }

    [Fact]
    public void ParserDrop_LinksOnlyNearbyFetch_AndPreservesOtherParsers()
    {
        var designer = new Designer();
        var fetch = AddNode(designer, "Fetch", "input");
        fetch.GetType().GetProperty("X")!.SetValue(fetch, 100d);
        fetch.GetType().GetProperty("Y")!.SetValue(fetch, 100d);
        var existing = AddNode(designer, "Parse", "existing");
        Property<List<string>>(existing, "FetchNodeIds").Add("input");
        Invoke(designer, "AddParseNodeAt", 150d, 120d);
        var added = Nodes(designer).Cast<object>().Single(node => !ReferenceEquals(node, existing) && Property<object>(node, "Stage").ToString() == "Parse");
        Assert.Equal(new[] { "input" }, Property<List<string>>(added, "FetchNodeIds"));
        Assert.Equal(150d, Property<double>(added, "X"));
        Assert.Equal(new[] { "input" }, Property<List<string>>(existing, "FetchNodeIds"));
        Invoke(designer, "AddParseNodeAt", 1500d, 900d);
        var standalone = Nodes(designer).Cast<object>().Last();
        Assert.Empty(Property<List<string>>(standalone, "FetchNodeIds"));
    }

    [Fact]
    public void TriggerGuidance_UsesTruePushCapabilityAndFirstFetchOrder_NotPollingLabels()
    {
        var designer = new Designer();
        var sftp = AddNode(designer, "Fetch", "remote");
        sftp.GetType().GetProperty("ModuleId")!.SetValue(sftp, "sftp-fetch");
        Field("_modules").SetValue(designer, new Mulse.Ui.Models.ModuleViewModel[]
        {
            new("sftp-fetch", "SFTP", "Fetch", "Scans once", [], [], [], ["Polling"]),
            new("custom-push", "Custom push", "Fetch", "Events", [], [], [], [], SupportsPushTrigger: true)
        });
        Field("_triggerMode").SetValue(designer, "Push");
        string Guidance() => (string)typeof(Designer).GetProperty("TriggerGuidance", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(designer)!;
        Assert.Contains("No currently loaded fetch", Guidance());
        Assert.Contains("no internal timer", (string)Invoke(designer, "FetchTriggerGuidance", sftp)!);
        var first = AddNode(designer, "Fetch", "event-1");
        var second = AddNode(designer, "Fetch", "event-2");
        foreach (var node in new[] { first, second }) node.GetType().GetProperty("ModuleId")!.SetValue(node, "custom-push");
        Assert.Contains("'event-1'", Guidance());
        Assert.Contains("no manual bootstrap", (string)Invoke(designer, "FetchTriggerGuidance", first)!);
        Assert.Contains("will not independently start", (string)Invoke(designer, "FetchTriggerGuidance", second)!);
        Field("_triggerMode").SetValue(designer, "OnDemand");
        Assert.Contains("will not automatically start", (string)Invoke(designer, "FetchTriggerGuidance", first)!);
    }

    [Theory]
    [InlineData(-4000d, -3000d, -2500d, -1800d)]
    [InlineData(-4000d, -3000d, 6000d, 5000d)]
    [InlineData(0d, 0d, 100d, 100d)]
    public void FitView_IncludesWholeGroupsInAnyQuadrant(double fetchX, double fetchY, double parseX, double parseY)
    {
        var designer = new Designer();
        var fetch = AddNode(designer, "Fetch", "fetch");
        var parse = AddNode(designer, "Parse", "parse");
        Property<List<string>>(parse, "FetchNodeIds").Add("fetch");
        foreach (var (node, x, y) in new[] { (fetch, fetchX, fetchY), (parse, parseX, parseY) })
        {
            node.GetType().GetProperty("X")!.SetValue(node, x);
            node.GetType().GetProperty("Y")!.SetValue(node, y);
        }
        var bounds = ((double X, double Y, double Width, double Height))Invoke(designer, "LaneBounds", parse)!;
        Assert.Equal(Math.Min(fetchX, parseX) - 18, bounds.X);
        Assert.Equal(Math.Min(fetchY, parseY) - 38, bounds.Y);
        Invoke(designer, "FitView");
        var viewX = (double)Field("_viewX").GetValue(designer)!;
        var viewY = (double)Field("_viewY").GetValue(designer)!;
        var viewW = (double)Field("_viewW").GetValue(designer)!;
        var viewH = (double)Field("_viewH").GetValue(designer)!;
        Assert.True(viewX < bounds.X);
        Assert.True(viewY < bounds.Y);
        Assert.True(viewX + viewW > bounds.X + bounds.Width);
        Assert.True(viewY + viewH > bounds.Y + bounds.Height);
    }

    [Theory]
    [InlineData("source", 2)]
    [InlineData("fetch", 1)]
    [InlineData("parse", 1)]
    [InlineData("augment", 1)]
    [InlineData("render", 1)]
    [InlineData("route", 2)]
    public void ToolboxCreation_PreservesNegativeDropCoordinates(string type, int expectedCount)
    {
        var designer = new Designer();
        var method = type switch
        {
            "source" => "AddSourceLaneAt",
            "fetch" => "AddFetchNodeAt",
            "parse" => "AddParseNodeAt",
            "augment" => "AddAugmentNodeAt",
            "render" => "AddRenderNodeAt",
            _ => "AddDeliveryRouteAt"
        };
        Invoke(designer, method, -1200d, -800d);
        Assert.Equal(expectedCount, Nodes(designer).Count);
        var first = Nodes(designer)[0]!;
        Assert.Equal(-1200d, Property<double>(first, "X"));
        Assert.Equal(-800d, Property<double>(first, "Y"));
    }

    [Fact]
    public void RestoringRender_PlacesItBesideNegativeCoordinateDelivery()
    {
        var designer = new Designer();
        var deliver = AddNode(designer, "Deliver", "deliver");
        deliver.GetType().GetProperty("X")!.SetValue(deliver, -500d);
        deliver.GetType().GetProperty("Y")!.SetValue(deliver, -300d);
        Invoke(designer, "AddRenderNode");
        var render = Nodes(designer).Cast<object>().Single(node => Property<object>(node, "Stage").ToString() == "Render");
        Assert.Equal(-770d, Property<double>(render, "X"));
        Assert.Equal(-300d, Property<double>(render, "Y"));
    }

    private static object AddNode(Designer designer, string stage, string graphId)
    {
        var stageType = typeof(Designer).GetNestedType("NodeStage", BindingFlags.NonPublic)!;
        return Invoke(designer, "CreateNode", Enum.Parse(stageType, stage), "test-module", null, 0, "route", graphId, null)!;
    }

    private static FieldInfo Field(string name) => typeof(Designer).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static IList Nodes(Designer designer) => (IList)Field("_nodes").GetValue(designer)!;
    private static T Property<T>(object instance, string name) => (T)instance.GetType().GetProperty(name)!.GetValue(instance)!;
    private static object? Invoke(Designer designer, string name, params object?[] arguments)
        => typeof(Designer).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(designer, arguments);
}
