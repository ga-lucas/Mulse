using System.Text.Json;
using Mulse.Ui.Models;

namespace Mulse.Ui.Components.Pages;

public partial class Designer
{
    private const string ParsedMapModuleId = "parsed-data-map-augment";
    private static bool IsParsedMappingNode(FlowNode node) => node.Stage == NodeStage.Augment && node.ModuleId == ParsedMapModuleId;
    private IReadOnlyList<ParsedMappingSourceViewModel> ParsedMappingSources => ParseNodes
        .Select(node => new ParsedMappingSourceViewModel(node.GraphId, node.ModuleId, node.Settings)).ToArray();

    private void AddParsedDataMapping() => AddParsedDataMappingAt(null, null);

    private void AddParsedDataMappingAt(double? x, double? y)
    {
        if (FindCatalogModule(ParsedMapModuleId) is null)
        {
            _errorMessage = "Refresh modules after updating the Service to add Map parsed data.";
            return;
        }
        var selectedSource = SelectedNode?.Stage == NodeStage.Parse ? SelectedNode : AugmentNodes.Count == 0 && ParseNodes.Count == 1 ? ParseNodes[0] : null;
        var node = CreateNode(NodeStage.Augment, ParsedMapModuleId,
            new Dictionary<string, string>
            {
                ["mappingJson"] = JsonSerializer.Serialize(new ParsedDataMapViewModel(), JsonOptions),
                ["inputFormat"] = "Auto", ["sourceId"] = selectedSource?.GraphId ?? ""
            }, order: AugmentNodes.Count == 0 ? 0 : AugmentNodes.Max(node => node.Order) + 1);
        if (x.HasValue && y.HasValue) { node.X = x.Value; node.Y = y.Value; }
        else AutoLayout();
        _selectedNodeId = node.Id;
        OpenMappingDesigner(node);
        _statusMessage = "Added Map parsed data. Define or discover fields, connect them, then choose a renderer and delivery route. Save the flow to persist the map.";
    }
}
