using Mulse.Ui.Models;

namespace Mulse.Ui.Components.Pages;

public partial class Designer
{
    private const string NativeAugmentModuleId = "native-control-flow-augment";
    private bool _showAugmentDesigner;
    private bool _augmentDesignerOpened;
    private string? _selectedAugmentNodeId;
    private Microsoft.AspNetCore.Components.ElementReference _augmentTab;
    private List<FlowNode> NativeAugmentNodes => AugmentNodes.Where(IsNativeAugment).ToList();
    private string? ActiveNativeAugmentId => NativeAugmentNodes.FirstOrDefault(node => node.Id == _selectedAugmentNodeId)?.Id ?? NativeAugmentNodes.FirstOrDefault()?.Id;
    private static bool IsNativeAugment(FlowNode node) => node.Stage == NodeStage.Augment && node.ModuleId.Equals(NativeAugmentModuleId, StringComparison.OrdinalIgnoreCase);

    private void SetAugmentWorkspace()
    {
        _showMappingDesigner = false;
        _showAugmentDesigner = true;
        _augmentDesignerOpened = true;
    }

    private void OpenAugmentDesigner(FlowNode node)
    {
        _selectedAugmentNodeId = node.Id;
        SelectNode(node);
        SetAugmentWorkspace();
    }

    private void AddNativeAugment() => AddNativeAugmentCore(null);
    private void AddNativeAugmentAt(double x, double y) => AddNativeAugmentCore((x, y));
    private void AddNativeAugmentCore((double X, double Y)? position)
    {
        var module = FindCatalogModule(NativeAugmentModuleId);
        if (module is null)
        {
            _errorMessage = "Load the native control-flow module before adding a control-flow augment.";
            return;
        }
        var node = CreateNode(NodeStage.Augment, module.Id, order: AugmentNodes.Count == 0 ? 0 : AugmentNodes.Max(step => step.Order) + 1);
        ApplyModuleDefaults(node, module);
        node.Settings.TryAdd("flowJson", "{\"version\":1,\"nodes\":[]}");
        if (position is { } drop) { node.X = drop.X; node.Y = drop.Y; }
        else AutoLayout();
        OpenAugmentDesigner(node);
        _errorMessage = null;
        _statusMessage = "Added a control-flow augment. Add and select its steps in the visual workspace, then Save flow to persist changes.";
    }
}
