using Microsoft.AspNetCore.Components;
using Mulse.Ui.Models;

namespace Mulse.Ui.Components.Pages;

// Canvas graph editing: source lanes, augment/route nodes, auto-layout, and edge geometry.
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
            group.FirstOrDefault(static node => node.Stage == NodeStage.Deliver)))
        .OrderBy(static route => route.Order)
        .ToList();

    private string SeedSourceLabel => _seedSource switch
    {
        SeedSource.Sample => "Sample data",
        SeedSource.BizTalk => "BizTalk import",
        SeedSource.ExistingFlow => "Existing flow",
        _ => "Manual"
    };

    private double CanvasWidth => Math.Max(1080, (_nodes.Count == 0 ? 0 : _nodes.Max(static node => node.X)) + NodeWidth + CanvasPadding + LanePaddingX);

    private double CanvasHeight => Math.Max(520, (_nodes.Count == 0 ? 0 : _nodes.Max(static node => node.Y)) + NodeHeight + CanvasPadding + LanePaddingX);

    private string? SourceGraphError => DescribeSourceGraphError();

    private void AddSourceLane()
    {
        var fetchModule = ModulesForStage(NodeStage.Fetch).FirstOrDefault();
        var parseModule = ModulesForStage(NodeStage.Parse).FirstOrDefault();

        var lane = CreateSourceLane(NextSourceId(), fetchModule?.Id ?? string.Empty, null, parseModule?.Id ?? string.Empty, null, null);
        ApplyModuleDefaults(lane.Fetch, fetchModule);
        ApplyModuleDefaults(lane.Parse, parseModule);

        AutoLayout();
        _selectedNodeId = lane.Fetch.Id;
        _statusMessage = $"Added source '{lane.Id}'. Pick its fetch and parse modules, and wire its inputs if it depends on another source.";
    }

    private void RemoveSourceLane(SourceLane lane)
    {
        _nodes.Remove(lane.Fetch);
        _nodes.Remove(lane.Parse);
        _sources.Remove(lane);

        foreach (var other in _sources)
        {
            other.InputSourceIds.RemoveAll(inputSourceId => string.Equals(inputSourceId, lane.Id, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var augment in AugmentNodes)
        {
            foreach (var key in new[] { LeftSourceKey, RightSourceKey })
            {
                if (augment.Settings.TryGetValue(key, out var value) && string.Equals(value, lane.Id, StringComparison.OrdinalIgnoreCase))
                {
                    augment.Settings[key] = string.Empty;
                }
            }
        }

        _selectedNodeId = null;
        AutoLayout();
        _statusMessage = $"Removed source '{lane.Id}'.";
    }

    private void RenameSource(SourceLane lane, string? value)
    {
        var newId = (value ?? string.Empty).Trim();
        if (string.Equals(newId, lane.Id, StringComparison.Ordinal))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(newId))
        {
            _errorMessage = "A source id cannot be empty.";
            return;
        }

        if (_sources.Any(other => !ReferenceEquals(other, lane) && string.Equals(other.Id, newId, StringComparison.OrdinalIgnoreCase)))
        {
            _errorMessage = $"Source id '{newId}' is already used by another source.";
            return;
        }

        var previousId = lane.Id;
        lane.Id = newId;
        lane.Fetch.SourceId = newId;
        lane.Parse.SourceId = newId;

        foreach (var other in _sources)
        {
            for (var index = 0; index < other.InputSourceIds.Count; index++)
            {
                if (string.Equals(other.InputSourceIds[index], previousId, StringComparison.OrdinalIgnoreCase))
                {
                    other.InputSourceIds[index] = newId;
                }
            }
        }

        foreach (var augment in AugmentNodes)
        {
            foreach (var key in new[] { LeftSourceKey, RightSourceKey })
            {
                if (augment.Settings.TryGetValue(key, out var current) && string.Equals(current, previousId, StringComparison.OrdinalIgnoreCase))
                {
                    augment.Settings[key] = newId;
                }
            }
        }

        _errorMessage = null;
        _statusMessage = $"Renamed source '{previousId}' to '{newId}'.";
    }

    private void ToggleSourceDependency(SourceLane lane, string inputSourceId, bool selected)
    {
        if (selected)
        {
            if (!lane.InputSourceIds.Contains(inputSourceId, StringComparer.OrdinalIgnoreCase))
            {
                lane.InputSourceIds.Add(inputSourceId);
            }
        }
        else
        {
            lane.InputSourceIds.RemoveAll(existing => string.Equals(existing, inputSourceId, StringComparison.OrdinalIgnoreCase));
        }
    }

    private string? DescribeSourceGraphError()
    {
        if (_sources.Any(static source => string.IsNullOrWhiteSpace(source.Id)))
        {
            return "Every source needs a non-empty id.";
        }

        var duplicate = _sources
            .GroupBy(static source => source.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicate is not null)
        {
            return $"Source id '{duplicate.Key}' is used more than once. Source ids must be unique within a flow.";
        }

        foreach (var source in _sources)
        {
            foreach (var inputSourceId in source.InputSourceIds)
            {
                if (string.Equals(inputSourceId, source.Id, StringComparison.OrdinalIgnoreCase))
                {
                    return $"Source '{source.Id}' cannot depend on itself.";
                }

                if (FindSource(inputSourceId) is null)
                {
                    return $"Source '{source.Id}' depends on unknown source '{inputSourceId}'.";
                }
            }
        }

        var cycle = FindSourceCycle();
        return cycle is null
            ? null
            : $"The source dependency graph has a cycle: {string.Join(" -> ", cycle)}. Remove one of those 'depends on' links before saving.";
    }

    private List<string>? FindSourceCycle()
    {
        var states = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var stack = new List<string>();

        List<string>? Visit(string sourceId)
        {
            if (states.TryGetValue(sourceId, out var state))
            {
                if (state == 1)
                {
                    return null;
                }

                var startIndex = stack.FindIndex(entry => string.Equals(entry, sourceId, StringComparison.OrdinalIgnoreCase));
                var cycle = stack.Skip(startIndex < 0 ? 0 : startIndex).ToList();
                cycle.Add(sourceId);
                return cycle;
            }

            states[sourceId] = 0;
            stack.Add(sourceId);

            if (FindSource(sourceId) is { } lane)
            {
                foreach (var inputSourceId in lane.InputSourceIds)
                {
                    var cycle = Visit(inputSourceId);
                    if (cycle is not null)
                    {
                        return cycle;
                    }
                }
            }

            states[sourceId] = 1;
            stack.RemoveAt(stack.Count - 1);
            return null;
        }

        foreach (var source in _sources)
        {
            var cycle = Visit(source.Id);
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

        if (_sources.Count >= 2)
        {
            node.Settings[LeftSourceKey] = _sources[0].Id;
            node.Settings[RightSourceKey] = _sources[1].Id;
        }

        AutoLayout();
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

    private void AutoLayout()
    {
        var laneStride = NodeHeight + LaneHeaderHeight + LaneGap;
        var laneTop = CanvasPadding + LaneHeaderHeight;
        var laneIndex = 0;

        foreach (var source in _sources)
        {
            var y = laneTop + (laneIndex * laneStride);
            source.Fetch.X = CanvasPadding + LanePaddingX;
            source.Fetch.Y = y;
            source.Parse.X = source.Fetch.X + ColumnSpacing;
            source.Parse.Y = y;
            laneIndex++;
        }

        var downstreamX = CanvasPadding + LanePaddingX + ColumnSpacing + NodeWidth + LanePaddingX + LaneGap;
        var downstreamY = _sources.Count == 0
            ? CanvasPadding
            : laneTop + ((_sources.Count - 1) * laneStride / 2);

        var column = 0;
        foreach (var augment in AugmentNodes)
        {
            augment.X = downstreamX + (column * ColumnSpacing);
            augment.Y = downstreamY;
            column++;
        }

        var routeIndex = 0;
        foreach (var route in Routes)
        {
            if (route.Render is { } render)
            {
                render.X = downstreamX + (column * ColumnSpacing);
                render.Y = downstreamY + (routeIndex * RowSpacing);
            }

            if (route.Deliver is { } deliver)
            {
                deliver.X = downstreamX + ((column + 1) * ColumnSpacing);
                deliver.Y = downstreamY + (routeIndex * RowSpacing);
            }

            routeIndex++;
        }
    }

    private (double X, double Y, double Width, double Height) LaneBounds(SourceLane source)
    {
        var minX = Math.Max(0, Math.Min(source.Fetch.X, source.Parse.X) - LanePaddingX);
        var minY = Math.Max(0, Math.Min(source.Fetch.Y, source.Parse.Y) - LaneHeaderHeight);
        var maxX = Math.Max(source.Fetch.X, source.Parse.X) + NodeWidth + LanePaddingX;
        var maxY = Math.Max(source.Fetch.Y, source.Parse.Y) + NodeHeight + LanePaddingX;
        return (minX, minY, maxX - minX, maxY - minY);
    }

    private static string LaneLabel(SourceLane source)
    {
        var label = $"Source: {source.Id}";
        return source.InputSourceIds.Count == 0
            ? label
            : $"{label} (input from {string.Join(", ", source.InputSourceIds)})";
    }

    private List<FlowEdge> BuildEdges()
    {
        var edges = new List<FlowEdge>();

        foreach (var source in _sources)
        {
            edges.Add(new FlowEdge(source.Fetch, source.Parse, IsDependency: false));

            foreach (var inputSourceId in source.InputSourceIds)
            {
                var upstream = FindSource(inputSourceId);
                if (upstream is not null && !ReferenceEquals(upstream, source))
                {
                    edges.Add(new FlowEdge(upstream.Parse, source.Fetch, IsDependency: true));
                }
            }
        }

        var heads = _sources.Select(static source => source.Parse).ToList();
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

            if (route.Deliver is { } deliver)
            {
                edges.Add(new FlowEdge(render, deliver, IsDependency: false));
            }
        }

        return edges;
    }

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
