using Microsoft.AspNetCore.Components;
using Mulse.Ui.Models;

namespace Mulse.Ui.Components.Pages;

// Canvas graph editing: fetch/parse nodes (grouped into source lanes), augment/route nodes, auto-layout, and edge geometry.
public partial class Designer
{
    private List<FlowNode> AugmentNodes => _nodes.Where(static node => node.Stage == NodeStage.Augment).OrderBy(static node => node.Order).ToList();

    private List<RouteView> Routes => _nodes
        .Where(static node => node.Stage is NodeStage.Render or NodeStage.Deliver)
        .GroupBy(static node => node.RouteId, StringComparer.Ordinal)
        .Select(static group => new RouteView(
            group.Key,
            group.Min(static node => node.Order),
            group.FirstOrDefault(static node => node.Stage == NodeStage.Render),
            // OrderBy is stable, so deliver nodes sharing an Order keep their canvas creation order.
            group.Where(static node => node.Stage == NodeStage.Deliver).OrderBy(static node => node.Order).ToList()))
        .OrderBy(static route => route.Order)
        .ToList();

    private string SeedSourceLabel => _seedSource switch
    {
        SeedSource.Sample => "Sample data",
        SeedSource.BizTalk => "BizTalk import",
        SeedSource.ExistingFlow => "Existing flow",
        _ => "Manual"
    };

    private double CanvasX => _nodes.Count == 0 ? 0 : Math.Min(0, _nodes.Min(static node => node.X) - CanvasPadding - LanePaddingX);

    private double CanvasY => _nodes.Count == 0 ? 0 : Math.Min(0, _nodes.Min(static node => node.Y) - CanvasPadding - LaneHeaderHeight);

    private double CanvasWidth => Math.Max(1080, (_nodes.Count == 0 ? 0 : _nodes.Max(static node => node.X)) + NodeWidth + CanvasPadding + LanePaddingX - CanvasX);

    private double CanvasHeight => Math.Max(520, (_nodes.Count == 0 ? 0 : _nodes.Max(static node => node.Y)) + NodeHeight + CanvasPadding + LanePaddingX - CanvasY);

    private string? SourceGraphError => DescribeSourceGraphError();

    /// <summary>The toolbox's primary "Source" action: one fetch node plus one parse node that parses it, pre-linked.</summary>
    private void AddSourceLane()
    {
        var (fetch, parse) = CreateDefaultSourcePair();
        AutoLayout();
        _selectedNodeId = fetch.Id;
        _statusMessage = $"Added source '{parse.GraphId}' (fetch node '{fetch.GraphId}' parsed by parse node '{parse.GraphId}'). Pick their modules, and wire the fetch's inputs if it depends on another parse node's output.";
    }

    /// <summary>Same as <see cref="AddSourceLane"/>, but places the pair at an explicit drop position (from a toolbox drag) instead of running <see cref="AutoLayout"/>.</summary>
    private void AddSourceLaneAt(double x, double y)
    {
        var (fetch, parse) = CreateDefaultSourcePair();
        fetch.X = x;
        fetch.Y = y;
        parse.X = x + ColumnSpacing;
        parse.Y = y;

        _selectedNodeId = fetch.Id;
        _statusMessage = $"Added source '{parse.GraphId}' (fetch node '{fetch.GraphId}' parsed by parse node '{parse.GraphId}'). Pick their modules, and wire the fetch's inputs if it depends on another parse node's output.";
    }

    private (FlowNode Fetch, FlowNode Parse) CreateDefaultSourcePair()
    {
        var fetchModule = ModulesForStage(NodeStage.Fetch).FirstOrDefault();
        var parseModule = ModulesForStage(NodeStage.Parse).FirstOrDefault();

        var pair = CreateSourcePair(NextParseId(), fetchModule?.Id ?? string.Empty, null, parseModule?.Id ?? string.Empty, null);
        ApplyModuleDefaults(pair.Fetch, fetchModule);
        ApplyModuleDefaults(pair.Parse, parseModule);
        return pair;
    }

    /// <summary>
    /// Adds a standalone fetch node. When a parse node is currently selected, the new fetch is linked to it
    /// straight away (the "attach another same-format fetch to an existing parse node" workflow).
    /// </summary>
    private void AddFetchNode()
    {
        var targetParse = SelectedNode is { Stage: NodeStage.Parse } selectedParse ? selectedParse : null;
        var fetch = CreateStandaloneFetch(targetParse);
        AutoLayout();
        _selectedNodeId = fetch.Id;
        _statusMessage = DescribeStandaloneFetch(fetch, targetParse);
    }

    /// <summary>
    /// Toolbox-drop variant of <see cref="AddFetchNode"/>: dropping onto (or close to) an existing parse node's
    /// source group links the new fetch to that parse node; dropping elsewhere leaves it unlinked.
    /// </summary>
    private void AddFetchNodeAt(double x, double y)
    {
        var targetParse = FindParseGroupAt(x, y);
        var fetch = CreateStandaloneFetch(targetParse);
        fetch.X = x;
        fetch.Y = y;
        _selectedNodeId = fetch.Id;
        _statusMessage = DescribeStandaloneFetch(fetch, targetParse);
    }

    private void AddParseNode()
    {
        var target = SelectedNode is { Stage: NodeStage.Fetch } selected ? selected : null;
        target ??= FetchNodes.Count(fetch => ParsesForFetch(fetch).Count == 0) == 1
            ? FetchNodes.Single(fetch => ParsesForFetch(fetch).Count == 0) : null;
        AddParseForFetch(target, null);
    }

    private void AddParseNodeAt(double x, double y)
    {
        var target = FetchNodes
            .Where(fetch => x >= fetch.X - LaneGap && x <= fetch.X + NodeWidth + LaneGap
                && y >= fetch.Y - LaneGap && y <= fetch.Y + NodeHeight + LaneGap)
            .OrderBy(fetch => Math.Abs(fetch.X - x) + Math.Abs(fetch.Y - y))
            .FirstOrDefault();
        AddParseForFetch(target, (x, y));
    }

    private void AddParseForFetch(FlowNode? fetch, (double X, double Y)? position = null)
    {
        var formats = fetch is null ? null : FindCatalogModule(fetch.ModuleId)?.SupportedFormats;
        var module = formats is { Count: 1 } && string.Equals(formats[0], "Json", StringComparison.OrdinalIgnoreCase)
            ? ModulesForStage(NodeStage.Parse).FirstOrDefault(candidate => candidate.Id == "json-parse") : null;
        var parse = CreateParseNode(NextParseId(), module?.Id ?? string.Empty, null,
            fetch is null ? [] : [fetch.GraphId]);
        ApplyModuleDefaults(parse, module);
        parse.X = position?.X ?? (fetch is null ? CanvasPadding + ColumnSpacing : fetch.X + ColumnSpacing);
        parse.Y = position?.Y ?? (fetch?.Y ?? (_nodes.Count == 1 ? CanvasPadding : _nodes.Where(node => node != parse).Max(node => node.Y) + RowSpacing));
        _selectedNodeId = parse.Id;
        _errorMessage = null;
        _statusMessage = fetch is null
            ? $"Added parser '{parse.GraphId}'. Choose its module and select the fetch outputs it parses in Properties."
            : $"Added parser '{parse.GraphId}' for fetch '{fetch.GraphId}', keeping the fetch's settings. Review the parser module and settings; reselect this source in lookup inputs and join settings if it replaces a deleted parser.";
    }

    private FlowNode CreateStandaloneFetch(FlowNode? targetParse)
    {
        var fetchModule = ModulesForStage(NodeStage.Fetch).FirstOrDefault();
        var baseId = targetParse is null ? "fetch" : $"{targetParse.GraphId}-fetch";
        var fetch = CreateFetchNode(NextFetchId(baseId), fetchModule?.Id ?? string.Empty, null, null);
        ApplyModuleDefaults(fetch, fetchModule);
        targetParse?.FetchNodeIds.Add(fetch.GraphId);
        return fetch;
    }

    private static string DescribeStandaloneFetch(FlowNode fetch, FlowNode? targetParse)
        => targetParse is null
            ? $"Added fetch node '{fetch.GraphId}'. Link it to a parse node (in its properties, or from the parse node's list of fetch nodes) before saving."
            : $"Added fetch node '{fetch.GraphId}', parsed by parse node '{targetParse.GraphId}' alongside its other fetch nodes.";

    /// <summary>The parse node whose source-group bounds contain the canvas point, if any (closest parse node wins when groups overlap).</summary>
    private FlowNode? FindParseGroupAt(double x, double y)
    {
        return ParseNodes
            .Select(parse => (Parse: parse, Bounds: LaneBounds(parse)))
            .Where(candidate => x >= candidate.Bounds.X - LaneGap
                && x <= candidate.Bounds.X + candidate.Bounds.Width + LaneGap
                && y >= candidate.Bounds.Y - LaneGap
                && y <= candidate.Bounds.Y + candidate.Bounds.Height + LaneGap)
            .OrderBy(candidate => Math.Abs(candidate.Parse.X - x) + Math.Abs(candidate.Parse.Y - y))
            .Select(static candidate => candidate.Parse)
            .FirstOrDefault();
    }

    /// <summary>
    /// Removes a fetch node and strips its id from every parse node's <see cref="FlowNode.FetchNodeIds"/>. A parse
    /// node left with no fetch nodes is kept (and flagged by validation) so its module settings aren't lost.
    /// </summary>
    private void RemoveFetchNode(FlowNode fetch)
    {
        _nodes.Remove(fetch);

        foreach (var parse in ParseNodes)
        {
            parse.FetchNodeIds.RemoveAll(fetchId => string.Equals(fetchId, fetch.GraphId, StringComparison.OrdinalIgnoreCase));
        }

        _selectedNodeId = null;
        AutoLayout();
        _statusMessage = $"Removed fetch node '{fetch.GraphId}'.";
    }

    /// <summary>
    /// Removes a parse node and strips its id from every fetch node's <see cref="FlowNode.InputParseIds"/> and from
    /// join augment source pickers. With <paramref name="includeExclusiveFetches"/> false, the fetch nodes it used to
    /// parse are deliberately left on the canvas, unlinked, rather than cascade-deleted: a fetch can be shared by
    /// several parse nodes, and its (often connection-heavy) settings are usually worth re-linking to a replacement
    /// parse node. Validation flags orphaned fetch nodes until they're linked again or removed. The "remove with
    /// fetches" action cascades only to fetch nodes that no other parse node still parses.
    /// </summary>
    private void RemoveParseNode(FlowNode parse, bool includeExclusiveFetches)
    {
        var exclusiveFetches = includeExclusiveFetches
            ? FetchesForParse(parse).Where(fetch => ParsesForFetch(fetch).All(other => ReferenceEquals(other, parse))).ToList()
            : [];

        _nodes.Remove(parse);

        foreach (var fetch in FetchNodes)
        {
            fetch.InputParseIds.RemoveAll(parseId => string.Equals(parseId, parse.GraphId, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var augment in AugmentNodes)
        {
            foreach (var key in new[] { LeftSourceKey, RightSourceKey })
            {
                if (augment.Settings.TryGetValue(key, out var value) && string.Equals(value, parse.GraphId, StringComparison.OrdinalIgnoreCase))
                {
                    augment.Settings[key] = string.Empty;
                }
            }
        }

        foreach (var fetch in exclusiveFetches)
        {
            _nodes.Remove(fetch);
        }

        _selectedNodeId = null;
        AutoLayout();
        _statusMessage = exclusiveFetches.Count == 0
            ? $"Removed parse node '{parse.GraphId}'."
            : $"Removed parse node '{parse.GraphId}' and its fetch node(s) {string.Join(", ", exclusiveFetches.Select(static fetch => $"'{fetch.GraphId}'"))}.";
    }

    /// <summary>
    /// Renames a fetch or parse node. Renaming a fetch rewrites every parse node's <see cref="FlowNode.FetchNodeIds"/>;
    /// renaming a parse rewrites every fetch node's <see cref="FlowNode.InputParseIds"/> and join augment source settings.
    /// </summary>
    private void RenameGraphNode(FlowNode node, string? value)
    {
        var newId = (value ?? string.Empty).Trim();
        if (string.Equals(newId, node.GraphId, StringComparison.Ordinal))
        {
            return;
        }

        var kind = node.Stage == NodeStage.Fetch ? "fetch" : "parse";
        if (string.IsNullOrWhiteSpace(newId))
        {
            _errorMessage = $"A {kind} node id cannot be empty.";
            return;
        }

        if (IsGraphIdTaken(newId, except: node))
        {
            _errorMessage = $"Id '{newId}' is already used by another fetch or parse node. Fetch and parse node ids must be unique within a flow.";
            return;
        }

        var previousId = node.GraphId;
        node.GraphId = newId;

        if (node.Stage == NodeStage.Fetch)
        {
            foreach (var parse in ParseNodes)
            {
                ReplaceId(parse.FetchNodeIds, previousId, newId);
            }
        }
        else
        {
            foreach (var fetch in FetchNodes)
            {
                ReplaceId(fetch.InputParseIds, previousId, newId);
            }

            foreach (var augment in AugmentNodes)
            {
                var sourceKeys = IsParsedMappingNode(augment) ? new[] { "sourceId" } : new[] { LeftSourceKey, RightSourceKey };
                foreach (var key in sourceKeys)
                {
                    if (augment.Settings.TryGetValue(key, out var current) && string.Equals(current, previousId, StringComparison.OrdinalIgnoreCase))
                    {
                        augment.Settings[key] = newId;
                    }
                }
            }
        }

        _errorMessage = null;
        _statusMessage = $"Renamed {kind} node '{previousId}' to '{newId}'.";
    }

    private static void ReplaceId(List<string> ids, string previousId, string newId)
    {
        for (var index = 0; index < ids.Count; index++)
        {
            if (string.Equals(ids[index], previousId, StringComparison.OrdinalIgnoreCase))
            {
                ids[index] = newId;
            }
        }
    }

    /// <summary>Links/unlinks a fetch node as one of the raw inputs <paramref name="parse"/> parses.</summary>
    private static void ToggleParseFetchLink(FlowNode parse, string fetchId, bool selected)
        => ToggleId(parse.FetchNodeIds, fetchId, selected);

    /// <summary>Adds/removes a parse node whose finished output is merged into <paramref name="fetch"/>'s input.</summary>
    private static void ToggleFetchInputParse(FlowNode fetch, string parseId, bool selected)
        => ToggleId(fetch.InputParseIds, parseId, selected);

    private static void ToggleId(List<string> ids, string id, bool selected)
    {
        if (selected)
        {
            if (!ids.Contains(id, StringComparer.OrdinalIgnoreCase))
            {
                ids.Add(id);
            }
        }
        else
        {
            ids.RemoveAll(existing => string.Equals(existing, id, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// Client-side mirror of the backend's source-graph validation (FlowRuntime.OrderSourceGraph): unique ids across
    /// fetch and parse nodes, no dangling references, every parse node parses at least one fetch node, every fetch
    /// node is parsed by at least one parse node, and no cycle through fetch inputs and parse links.
    /// </summary>
    private string? DescribeSourceGraphError()
    {
        var fetchNodes = FetchNodes;
        var parseNodes = ParseNodes;

        if (fetchNodes.Any(static node => string.IsNullOrWhiteSpace(node.GraphId)))
        {
            return "Every fetch node needs a non-empty id.";
        }

        if (parseNodes.Any(static node => string.IsNullOrWhiteSpace(node.GraphId)))
        {
            return "Every parse node needs a non-empty id.";
        }

        var duplicateFetch = fetchNodes.GroupBy(static node => node.GraphId, StringComparer.OrdinalIgnoreCase).FirstOrDefault(static group => group.Count() > 1);
        if (duplicateFetch is not null)
        {
            return $"Fetch node id '{duplicateFetch.Key}' is used more than once. Fetch node ids must be unique within a flow.";
        }

        var duplicateParse = parseNodes.GroupBy(static node => node.GraphId, StringComparer.OrdinalIgnoreCase).FirstOrDefault(static group => group.Count() > 1);
        if (duplicateParse is not null)
        {
            return $"Parse node id '{duplicateParse.Key}' is used more than once. Parse node ids must be unique within a flow.";
        }

        var shared = fetchNodes.FirstOrDefault(fetch => FindParse(fetch.GraphId) is not null);
        if (shared is not null)
        {
            return $"Id '{shared.GraphId}' is used by both a fetch node and a parse node. Fetch and parse node ids must be unique within a flow.";
        }

        foreach (var parse in parseNodes)
        {
            if (parse.FetchNodeIds.Count == 0)
            {
                return $"Parse node '{parse.GraphId}' does not parse any fetch node. Link at least one fetch node to it.";
            }

            if (parse.FetchNodeIds.FirstOrDefault(fetchId => FindFetch(fetchId) is null) is { } unknownFetchId)
            {
                return $"Parse node '{parse.GraphId}' references unknown fetch node '{unknownFetchId}'.";
            }
        }

        foreach (var fetch in fetchNodes)
        {
            if (fetch.InputParseIds.FirstOrDefault(parseId => FindParse(parseId) is null) is { } unknownParseId)
            {
                return $"Fetch node '{fetch.GraphId}' takes input from unknown parse node '{unknownParseId}'.";
            }

            if (ParsesForFetch(fetch).Count == 0)
            {
                return $"Fetch node '{fetch.GraphId}' is not parsed by any parse node. Link it to a parse node, or remove it.";
            }
        }

        var cycle = FindSourceGraphCycle();
        return cycle is null
            ? null
            : $"The source graph has a cycle: {string.Join(" -> ", cycle)}. Remove one of those input links before saving.";
    }

    /// <summary>
    /// Depth-first search over the combined fetch/parse graph, where a fetch node depends on the parse nodes in its
    /// <see cref="FlowNode.InputParseIds"/> and a parse node depends on the fetch nodes in its <see cref="FlowNode.FetchNodeIds"/>.
    /// Returns the ids along the first cycle found, or null when the graph is acyclic.
    /// </summary>
    private List<string>? FindSourceGraphCycle()
    {
        var states = new Dictionary<FlowNode, int>();
        var stack = new List<FlowNode>();

        IEnumerable<FlowNode> Dependencies(FlowNode node)
            => node.Stage == NodeStage.Fetch
                ? node.InputParseIds.Select(FindParse).OfType<FlowNode>()
                : node.FetchNodeIds.Select(FindFetch).OfType<FlowNode>();

        List<string>? Visit(FlowNode node)
        {
            if (states.TryGetValue(node, out var state))
            {
                if (state == 1)
                {
                    return null;
                }

                var startIndex = stack.IndexOf(node);
                var cycle = stack.Skip(startIndex < 0 ? 0 : startIndex).Select(static entry => entry.GraphId).ToList();
                cycle.Add(node.GraphId);
                return cycle;
            }

            states[node] = 0;
            stack.Add(node);

            foreach (var dependency in Dependencies(node))
            {
                var cycle = Visit(dependency);
                if (cycle is not null)
                {
                    return cycle;
                }
            }

            states[node] = 1;
            stack.RemoveAt(stack.Count - 1);
            return null;
        }

        foreach (var node in _nodes.Where(static node => node.Stage is NodeStage.Fetch or NodeStage.Parse))
        {
            var cycle = Visit(node);
            if (cycle is not null)
            {
                return cycle;
            }
        }

        return null;
    }

    private void AddAugmentNode()
    {
        var augments = AugmentNodes;
        var order = augments.Count == 0 ? 0 : augments.Max(static node => node.Order) + 1;
        AddStageNode(NodeStage.Augment, order);
    }

    private void AddJoinAugmentNode()
    {
        var module = FindCatalogModule(JoinModuleId);
        if (module is null)
        {
            _errorMessage = $"The '{JoinModuleId}' module is not available in the module catalog.";
            return;
        }

        var augments = AugmentNodes;
        var order = augments.Count == 0 ? 0 : augments.Max(static node => node.Order) + 1;
        var node = CreateNode(NodeStage.Augment, module.Id, order: order);
        ApplyModuleDefaults(node, module);

        var parseNodes = ParseNodes;
        if (parseNodes.Count >= 2)
        {
            node.Settings[LeftSourceKey] = parseNodes[0].GraphId;
            node.Settings[RightSourceKey] = parseNodes[1].GraphId;
        }

        AutoLayout();
        _selectedNodeId = node.Id;
        _statusMessage = "Added a multi-source join and map augment. Configure its join keys and field mappings in the node details panel.";
    }

    /// <summary>Toolbox-drop variant of <see cref="AddAugmentNode"/> that places the node at an explicit position instead of auto-laying-out the whole canvas.</summary>
    private void AddAugmentNodeAt(double x, double y)
    {
        var augments = AugmentNodes;
        var order = augments.Count == 0 ? 0 : augments.Max(static node => node.Order) + 1;
        var module = ModulesForStage(NodeStage.Augment).FirstOrDefault();
        var node = CreateNode(NodeStage.Augment, module?.Id ?? string.Empty, order: order);
        ApplyModuleDefaults(node, module);
        node.X = x;
        node.Y = y;
        _selectedNodeId = node.Id;
    }

    /// <summary>Toolbox-drop variant of <see cref="AddJoinAugmentNode"/> that places the node at an explicit position instead of auto-laying-out the whole canvas.</summary>
    private void AddJoinAugmentNodeAt(double x, double y)
    {
        var module = FindCatalogModule(JoinModuleId);
        if (module is null)
        {
            _errorMessage = $"The '{JoinModuleId}' module is not available in the module catalog.";
            return;
        }

        var augments = AugmentNodes;
        var order = augments.Count == 0 ? 0 : augments.Max(static node => node.Order) + 1;
        var node = CreateNode(NodeStage.Augment, module.Id, order: order);
        ApplyModuleDefaults(node, module);

        var parseNodes = ParseNodes;
        if (parseNodes.Count >= 2)
        {
            node.Settings[LeftSourceKey] = parseNodes[0].GraphId;
            node.Settings[RightSourceKey] = parseNodes[1].GraphId;
        }

        node.X = x;
        node.Y = y;
        _selectedNodeId = node.Id;
        _statusMessage = "Added a multi-source join and map augment. Configure its join keys and field mappings in the node details panel.";
    }

    private void AddStageNode(NodeStage stage, int order = 0)
    {
        var module = ModulesForStage(stage).FirstOrDefault();
        var node = CreateNode(stage, module?.Id ?? string.Empty, order: order);
        ApplyModuleDefaults(node, module);
        AutoLayout();
        _selectedNodeId = node.Id;
    }

    private void AddRenderNode() => AddRenderNodeCore(null);

    private void AddRenderNodeAt(double x, double y) => AddRenderNodeCore((x, y));

    private void AddRenderNodeCore((double X, double Y)? position)
    {
        var target = position is { } drop ? FindRouteAt(drop.X, drop.Y) : null;
        target ??= SelectedNode is { Stage: NodeStage.Render or NodeStage.Deliver } selected ? FindRoute(selected.RouteId) : null;
        target ??= Routes.Count(route => route.Render is null) == 1 ? Routes.Single(route => route.Render is null) : null;
        if (target?.Render is not null)
        {
            _errorMessage = "This route already has a render. Select it to change its module, or deselect the route and add a separate render.";
            return;
        }

        AddRenderToRoute(target?.RouteId, position);
    }

    private void AddRenderToRoute(string? routeId, (double X, double Y)? position = null)
    {
        var route = routeId is null ? null : FindRoute(routeId);
        if (routeId is not null && route is null)
        {
            _errorMessage = "The delivery route no longer exists. Select a current route and try again.";
            return;
        }
        if (route?.Render is not null)
        {
            _errorMessage = "This route already has a render. Select it to configure it.";
            return;
        }

        var module = ModulesForStage(NodeStage.Render).FirstOrDefault();
        var render = CreateNode(NodeStage.Render, module?.Id ?? string.Empty,
            order: route?.Order ?? Routes.Count, routeId: routeId ?? Guid.NewGuid().ToString("n"));
        ApplyModuleDefaults(render, module);
        if (position is { } drop)
        {
            render.X = drop.X;
            render.Y = drop.Y;
        }
        else if (route is not null)
        {
            render.X = route.Delivers.Min(node => node.X) - ColumnSpacing;
            render.Y = route.Delivers.Average(node => node.Y);
        }
        else
        {
            AutoLayout();
        }

        _selectedNodeId = render.Id;
        _errorMessage = null;
        _statusMessage = route is null
            ? "Added a render in a new route. Add a deliver node before saving."
            : "Restored this route's render. Existing deliver nodes and their settings were kept.";
    }

    private void AddDeliveryRoute()
    {
        var routeId = Guid.NewGuid().ToString("n");
        var order = Routes.Count;
        var renderModule = ModulesForStage(NodeStage.Render).FirstOrDefault();
        var deliverModule = ModulesForStage(NodeStage.Deliver).FirstOrDefault();

        var render = CreateNode(NodeStage.Render, renderModule?.Id ?? string.Empty, order: order, routeId: routeId);
        var deliver = CreateNode(NodeStage.Deliver, deliverModule?.Id ?? string.Empty, order: order, routeId: routeId);
        ApplyModuleDefaults(render, renderModule);
        ApplyModuleDefaults(deliver, deliverModule);

        AutoLayout();
        _selectedNodeId = render.Id;
    }

    /// <summary>Toolbox-drop variant of <see cref="AddDeliveryRoute"/> that places the render/deliver pair at an explicit position instead of auto-laying-out the whole canvas.</summary>
    private void AddDeliveryRouteAt(double x, double y)
    {
        var routeId = Guid.NewGuid().ToString("n");
        var order = Routes.Count;
        var renderModule = ModulesForStage(NodeStage.Render).FirstOrDefault();
        var deliverModule = ModulesForStage(NodeStage.Deliver).FirstOrDefault();

        var render = CreateNode(NodeStage.Render, renderModule?.Id ?? string.Empty, order: order, routeId: routeId);
        var deliver = CreateNode(NodeStage.Deliver, deliverModule?.Id ?? string.Empty, order: order, routeId: routeId);
        ApplyModuleDefaults(render, renderModule);
        ApplyModuleDefaults(deliver, deliverModule);

        render.X = x;
        render.Y = y;
        deliver.X = x + ColumnSpacing;
        deliver.Y = y;

        _selectedNodeId = render.Id;
    }

    /// <summary>
    /// The toolbox's "Extra deliver" click action: adds one more deliver node to the delivery route of the currently
    /// selected render/deliver node (or to the only route, when there is just one), then re-lays out the canvas.
    /// </summary>
    private void AddDeliverNode()
    {
        var routes = Routes;
        var targetRoute = SelectedNode is { Stage: NodeStage.Render or NodeStage.Deliver } selected
            ? routes.FirstOrDefault(route => string.Equals(route.RouteId, selected.RouteId, StringComparison.Ordinal))
            : routes.Count == 1 ? routes[0] : null;

        if (targetRoute is null)
        {
            _errorMessage = routes.Count == 0
                ? "Add a delivery route first; an extra deliver node joins an existing route's render step."
                : "Select one of a delivery route's render or deliver nodes (or drop the Extra deliver item onto a route) to add a deliver node to it.";
            return;
        }

        AddDeliverToRoute(targetRoute.RouteId);
    }

    /// <summary>
    /// Toolbox-drop variant of <see cref="AddDeliverNode"/>: dropping onto (or close to) an existing delivery route
    /// attaches the new deliver node to that route at the drop position; dropping elsewhere falls back to the
    /// selected node's route, if any.
    /// </summary>
    private void AddDeliverNodeAt(double x, double y)
    {
        var targetRoute = FindRouteAt(x, y)
            ?? (SelectedNode is { Stage: NodeStage.Render or NodeStage.Deliver } selected ? FindRoute(selected.RouteId) : null);

        if (targetRoute is null)
        {
            _errorMessage = Routes.Count == 0
                ? "Add a delivery route first; an extra deliver node joins an existing route's render step."
                : "Drop the Extra deliver item onto an existing delivery route (its render or deliver nodes) to add a deliver node to it.";
            return;
        }

        AddDeliverToRoute(targetRoute.RouteId, (x, y));
    }

    /// <summary>
    /// Adds one more deliver node to an existing delivery route: it shares the route's <see cref="FlowNode.RouteId"/>
    /// and <see cref="FlowNode.Order"/>, so it receives the same rendered output as the route's other deliver nodes
    /// and runs independently of them. Placed at <paramref name="position"/> when given (toolbox drop), otherwise the
    /// canvas is re-laid out so the new node stacks below the route's existing deliver nodes.
    /// </summary>
    private FlowNode? AddDeliverToRoute(string routeId, (double X, double Y)? position = null)
    {
        var route = FindRoute(routeId);
        if (route is null)
        {
            return null;
        }

        var deliverModule = ModulesForStage(NodeStage.Deliver).FirstOrDefault();
        var deliver = CreateNode(NodeStage.Deliver, deliverModule?.Id ?? string.Empty, order: route.Order, routeId: routeId);
        ApplyModuleDefaults(deliver, deliverModule);

        if (position is { } drop)
        {
            deliver.X = drop.X;
            deliver.Y = drop.Y;
        }
        else
        {
            AutoLayout();
        }

        _selectedNodeId = deliver.Id;
        _errorMessage = null;
        _statusMessage = $"Added deliver node {route.Delivers.Count + 1} to delivery route {RouteNumber(deliver)}. It receives the same rendered output as the route's other deliver nodes and runs independently of them.";
        return deliver;
    }

    /// <summary>The delivery route whose render/deliver bounds contain the canvas point, if any (closest render node wins when routes overlap).</summary>
    private RouteView? FindRouteAt(double x, double y)
    {
        return Routes
            .Select(route => (Route: route, Members: RouteMembers(route)))
            .Where(static candidate => candidate.Members.Count > 0)
            .Select(candidate => (candidate.Route, Anchor: candidate.Members[0], Bounds: NodeBounds(candidate.Members)))
            .Where(candidate => x >= candidate.Bounds.X - LaneGap
                && x <= candidate.Bounds.X + candidate.Bounds.Width + LaneGap
                && y >= candidate.Bounds.Y - LaneGap
                && y <= candidate.Bounds.Y + candidate.Bounds.Height + LaneGap)
            .OrderBy(candidate => Math.Abs(candidate.Anchor.X - x) + Math.Abs(candidate.Anchor.Y - y))
            .Select(static candidate => candidate.Route)
            .FirstOrDefault();
    }

    private static List<FlowNode> RouteMembers(RouteView route)
    {
        var members = new List<FlowNode>(route.Delivers.Count + 1);
        if (route.Render is { } render)
        {
            members.Add(render);
        }

        members.AddRange(route.Delivers);
        return members;
    }

    private static (double X, double Y, double Width, double Height) NodeBounds(IReadOnlyList<FlowNode> nodes)
    {
        var minX = nodes.Min(static node => node.X);
        var minY = nodes.Min(static node => node.Y);
        var maxX = nodes.Max(static node => node.X) + NodeWidth;
        var maxY = nodes.Max(static node => node.Y) + NodeHeight;
        return (minX, minY, maxX - minX, maxY - minY);
    }

    private void RemoveCanvasNode(FlowNode node)
    {
        if (node.Stage == NodeStage.Fetch)
        {
            RemoveFetchNode(node);
        }
        else if (node.Stage == NodeStage.Parse)
        {
            RemoveParseNode(node, includeExclusiveFetches: false);
        }
        else
        {
            var heading = NodeHeading(node);
            RemoveNode(node);
            _statusMessage = $"Removed {heading}. Connected nodes were kept; review the flow before saving.";
        }
    }

    private void RemoveNode(FlowNode node)
    {
        _nodes.Remove(node);
        _joinEditors.Remove(node.Id);
        if (string.Equals(_selectedNodeId, node.Id, StringComparison.Ordinal))
        {
            _selectedNodeId = null;
        }
    }

    private void RemoveRoute(string routeId)
    {
        foreach (var node in _nodes.Where(candidate => string.Equals(candidate.RouteId, routeId, StringComparison.Ordinal)).ToArray())
        {
            _joinEditors.Remove(node.Id);
        }

        _nodes.RemoveAll(node => string.Equals(node.RouteId, routeId, StringComparison.Ordinal));
        _selectedNodeId = null;
    }

    /// <summary>Removes one deliver node from a route that still has at least one other deliver node (use <see cref="RemoveRoute"/> otherwise).</summary>
    private void RemoveDeliverNode(FlowNode deliver)
    {
        if (FindRoute(deliver.RouteId) is not { Delivers.Count: > 1 })
        {
            return;
        }

        var heading = NodeHeading(deliver);
        RemoveNode(deliver);
        AutoLayout();
        _statusMessage = $"Removed {heading}.";
    }

    private void MoveAugment(FlowNode node, int delta)
    {
        var ordered = AugmentNodes;
        var index = ordered.IndexOf(node);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= ordered.Count)
        {
            return;
        }

        (ordered[index].Order, ordered[target].Order) = (ordered[target].Order, ordered[index].Order);
        AutoLayout();
    }

    private void SelectNode(FlowNode node)
    {
        _selectedNodeId = node.Id;
        _newSettingKey = string.Empty;
    }

    private void HandleNodeModuleChanged(FlowNode node, ChangeEventArgs args)
    {
        node.ModuleId = args.Value?.ToString() ?? string.Empty;
        _joinEditors.Remove(node.Id);
        ApplyModuleDefaults(node, FindCatalogModule(node.ModuleId));
    }

    private static void ApplyModuleDefaults(FlowNode node, ModuleViewModel? module)
    {
        node.Settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (module is null)
        {
            return;
        }

        foreach (var setting in module.Settings)
        {
            node.Settings[setting.Key] = setting.DefaultValue ?? string.Empty;
        }
    }

    private void AddCustomSetting(FlowNode node)
    {
        if (string.IsNullOrWhiteSpace(_newSettingKey))
        {
            return;
        }

        node.Settings.TryAdd(_newSettingKey.Trim(), string.Empty);
        _newSettingKey = string.Empty;
    }

    /// <summary>
    /// Lays out each parse node's source group as a lane: its linked fetch nodes stacked vertically in the left
    /// column, with the parse node centred beside them. A fetch shared by several parse nodes sits in the first
    /// group that lists it; fetch nodes no parse node parses yet get their own rows below the groups. Augments and
    /// delivery routes follow to the right, vertically centred on the source groups.
    /// </summary>
    private void AutoLayout()
    {
        var laneStride = NodeHeight + LaneHeaderHeight + LaneGap;
        var fetchStride = NodeHeight + LanePaddingX;
        var laneTop = CanvasPadding + LaneHeaderHeight;
        var fetchX = CanvasPadding + LanePaddingX;
        var cursorY = laneTop;
        double? lastRowY = null;
        var placed = new HashSet<FlowNode>();

        foreach (var parse in ParseNodes)
        {
            var fetches = FetchesForParse(parse).Where(placed.Add).ToList();
            var rows = Math.Max(1, fetches.Count);

            for (var index = 0; index < fetches.Count; index++)
            {
                fetches[index].X = fetchX;
                fetches[index].Y = cursorY + (index * fetchStride);
            }

            parse.X = fetchX + ColumnSpacing;
            parse.Y = cursorY + ((rows - 1) * fetchStride / 2);
            lastRowY = cursorY + ((rows - 1) * fetchStride);
            cursorY += ((rows - 1) * fetchStride) + laneStride;
        }

        foreach (var orphan in FetchNodes.Where(placed.Add))
        {
            orphan.X = fetchX;
            orphan.Y = cursorY;
            lastRowY = cursorY;
            cursorY += laneStride;
        }

        var downstreamX = CanvasPadding + LanePaddingX + ColumnSpacing + NodeWidth + LanePaddingX + LaneGap;
        var downstreamY = lastRowY is { } bottom
            ? laneTop + ((bottom - laneTop) / 2)
            : CanvasPadding;

        var column = 0;
        foreach (var augment in AugmentNodes)
        {
            augment.X = downstreamX + (column * ColumnSpacing);
            augment.Y = downstreamY;
            column++;
        }

        // Each route renders once and fans out to its deliver nodes, stacked in the next column with the render node
        // centred beside them (mirroring how a parse node sits beside its stacked fetch nodes).
        var deliverStride = NodeHeight + LanePaddingX;
        var routeY = downstreamY;
        foreach (var route in Routes)
        {
            var rows = Math.Max(1, route.Delivers.Count);
            if (route.Render is { } render)
            {
                render.X = downstreamX + (column * ColumnSpacing);
                render.Y = routeY + ((rows - 1) * deliverStride / 2);
            }

            for (var index = 0; index < route.Delivers.Count; index++)
            {
                route.Delivers[index].X = downstreamX + ((column + 1) * ColumnSpacing);
                route.Delivers[index].Y = routeY + (index * deliverStride);
            }

            routeY += ((rows - 1) * deliverStride) + RowSpacing;
        }
    }

    private List<FlowNode> LaneMembers(FlowNode parse)
    {
        var members = FetchesForParse(parse);
        members.Add(parse);
        return members;
    }

    /// <summary>Bounding box of one source lane: a parse node plus every fetch node it currently parses.</summary>
    private (double X, double Y, double Width, double Height) LaneBounds(FlowNode parse)
    {
        var members = LaneMembers(parse);
        var minX = members.Min(static node => node.X) - LanePaddingX;
        var minY = members.Min(static node => node.Y) - LaneHeaderHeight;
        var maxX = members.Max(static node => node.X) + NodeWidth + LanePaddingX;
        var maxY = members.Max(static node => node.Y) + NodeHeight + LanePaddingX;
        return (minX, minY, maxX - minX, maxY - minY);
    }

    private string LaneLabel(FlowNode parse)
    {
        var fetches = FetchesForParse(parse);
        var label = $"Source: {parse.GraphId}";
        if (fetches.Count > 1)
        {
            label += $" (combines {fetches.Count} fetches)";
        }

        var inputParseIds = fetches
            .SelectMany(static fetch => fetch.InputParseIds)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return inputParseIds.Length == 0
            ? label
            : $"{label} (input from {string.Join(", ", inputParseIds)})";
    }

    private List<FlowEdge> BuildEdges()
    {
        var edges = new List<FlowEdge>();
        var parseNodes = ParseNodes;

        foreach (var parse in parseNodes)
        {
            foreach (var fetch in FetchesForParse(parse))
            {
                edges.Add(new FlowEdge(fetch, parse, IsDependency: false));
            }
        }

        foreach (var fetch in FetchNodes)
        {
            foreach (var upstream in fetch.InputParseIds.Select(FindParse).OfType<FlowNode>().Distinct())
            {
                edges.Add(new FlowEdge(upstream, fetch, IsDependency: true));
            }
        }

        var heads = parseNodes;
        FlowNode? tail = null;

        foreach (var augment in AugmentNodes)
        {
            if (tail is null)
            {
                foreach (var head in heads)
                {
                    edges.Add(new FlowEdge(head, augment, IsDependency: false));
                }
            }
            else
            {
                edges.Add(new FlowEdge(tail, augment, IsDependency: false));
            }

            tail = augment;
        }

        foreach (var route in Routes)
        {
            if (route.Render is not { } render)
            {
                continue;
            }

            if (tail is null)
            {
                foreach (var head in heads)
                {
                    edges.Add(new FlowEdge(head, render, IsDependency: false));
                }
            }
            else
            {
                edges.Add(new FlowEdge(tail, render, IsDependency: false));
            }

            foreach (var deliver in route.Delivers)
            {
                edges.Add(new FlowEdge(render, deliver, IsDependency: false));
            }
        }

        return edges;
    }

    private string IncomingBatchLabel(FlowNode node, int count)
        => IsJoinNode(node) ? "JOIN BY KEY" : $"BATCH x{count}";

    /// <summary>Counts inbound edges per node id (both regular flow edges and dependency edges).</summary>
    private Dictionary<string, int> IncomingEdgeCounts()
        => BuildEdges()
            .GroupBy(edge => edge.To.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

    /// <summary>Counts outbound edges per node id, used to flag split/fan-out points in the canvas UI.</summary>
    private Dictionary<string, int> OutgoingEdgeCounts()
        => BuildEdges()
            .GroupBy(edge => edge.From.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

    private static string EdgePath(FlowEdge edge)
        => edge.IsDependency ? DependencyEdgePath(edge.From, edge.To) : FlowEdgePath(edge.From, edge.To);

    private static string FlowEdgePath(FlowNode from, FlowNode to)
    {
        var x1 = from.X + NodeWidth;
        var y1 = from.Y + (NodeHeight / 2);
        var x2 = to.X;
        var y2 = to.Y + (NodeHeight / 2);
        var control = (x1 + x2) / 2;
        return $"M {F(x1)} {F(y1)} C {F(control)} {F(y1)}, {F(control)} {F(y2)}, {F(x2)} {F(y2)}";
    }

    private static string DependencyEdgePath(FlowNode from, FlowNode to)
    {
        var x1 = from.X + (NodeWidth / 2);
        var y1 = from.Y + NodeHeight;
        var x2 = to.X + (NodeWidth / 2);
        var y2 = to.Y;
        var control = (y1 + y2) / 2;
        return $"M {F(x1)} {F(y1)} C {F(x1)} {F(control)}, {F(x2)} {F(control)}, {F(x2)} {F(y2)}";
    }
}
