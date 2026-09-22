using Microsoft.AspNetCore.Components.Forms;
using Mulse.Ui.Models;

namespace Mulse.Ui.Components.Pages;

// "Seed from sample data" side panel: upload a sample payload, analyze it, and populate the canvas.
public partial class Designer
{
    private void ToggleSamplePanel()
    {
        _showSamplePanel = !_showSamplePanel;
        if (_showSamplePanel)
        {
            _showBizTalkPanel = false;
        }
    }

    private async Task HandleFileSelectedAsync(InputFileChangeEventArgs args)
    {
        _errorMessage = null;
        _statusMessage = null;

        var file = args.File;
        if (file is null)
        {
            return;
        }

        _fileName = file.Name;
        _contentType = file.ContentType;

        await using var stream = file.OpenReadStream(maxAllowedSize: 1_000_000);
        using var reader = new StreamReader(stream);
        _sampleText = await reader.ReadToEndAsync();
        await AnalyzeSampleAsync();
    }

    private async Task AnalyzeSampleAsync()
    {
        _isAnalyzingSample = true;
        _statusMessage = null;
        _errorMessage = null;

        try
        {
            _analysis = await ApiClient.AnalyzeFlowDesignAsync(
                new FlowDesignAnalysisRequestViewModel(_fileName, _contentType, _sampleText),
                CancellationToken.None);
            _statusMessage = $"Analyzed {_analysis.Fields.Count} field(s) from {_analysis.DetectedFormat} sample data.";
        }
        catch (Exception exception)
        {
            _errorMessage = $"Unable to analyze sample data: {exception.Message}";
        }
        finally
        {
            _isAnalyzingSample = false;
        }
    }

    private void ApplySampleAnalysis()
    {
        if (_analysis is null)
        {
            return;
        }

        _nodes.Clear();
        _sources.Clear();
        _joinEditors.Clear();
        _mappingRows.Clear();
        _bizTalkRequirements = [];
        _bizTalkWarnings = [];
        _selectedNodeId = null;

        var fetchModuleId = ChooseModule(_analysis.FetchModules, requiredProtocol: "Sftp");
        var parseModuleId = ChooseModule(_analysis.ParseModules, requiredCapability: "Parsing", requiredFormat: _analysis.DetectedFormat);
        var lane = CreateSourceLane("primary", fetchModuleId ?? string.Empty, null, parseModuleId ?? string.Empty, null, null);
        ApplyModuleDefaults(lane.Fetch, FindCatalogModule(fetchModuleId));
        ApplyModuleDefaults(lane.Parse, FindCatalogModule(parseModuleId));

        var mappingAugment = _analysis.OrchestrationAugmentModules
            .FirstOrDefault(static module => string.Equals(module.Id, JoinModuleId, StringComparison.OrdinalIgnoreCase))?.Id;
        if (mappingAugment is not null)
        {
            var joinNode = CreateNode(NodeStage.Augment, mappingAugment);
            ApplyModuleDefaults(joinNode, FindCatalogModule(mappingAugment));
            joinNode.Settings[LeftSourceKey] = lane.Id;
        }

        var jsonRender = ChooseModule(_analysis.RenderModules, requiredCapability: "Serialization", requiredFormat: "Json");
        var httpDeliver = ChooseModule(_analysis.DeliverModules, requiredCapability: "Delivery", requiredProtocol: "Http");
        SeedRoute(jsonRender, httpDeliver, 0);

        var xmlRender = ChooseModule(_analysis.RenderModules, jsonRender, requiredCapability: "Serialization", requiredFormat: "Xml");
        var fileDeliver = ChooseModule(_analysis.DeliverModules, httpDeliver, requiredCapability: "Delivery", requiredProtocol: "FileSystem");
        SeedRoute(xmlRender, fileDeliver, 1);

        foreach (var field in _analysis.Fields)
        {
            _mappingRows.Add(new FieldMappingRow
            {
                SourceKind = "Left",
                SourcePath = field.SourcePath,
                TargetField = field.SuggestedTargetField,
                Condition = "Always",
                LiteralValue = string.Empty
            });
        }

        if (!IsExistingFlow)
        {
            _flowId = CreateDefaultFlowId(_fileName, _analysis.DetectedFormat);
            _triggerMode = "Interval";
        }

        _seedSource = SeedSource.Sample;
        _showSamplePanel = false;
        AutoLayout();
        _statusMessage = $"Populated the canvas with {_nodes.Count} node(s) from the analyzed {_analysis.DetectedFormat} sample.";
    }

    private void SeedRoute(string? renderModuleId, string? deliverModuleId, int order)
    {
        if (string.IsNullOrWhiteSpace(renderModuleId) || string.IsNullOrWhiteSpace(deliverModuleId))
        {
            return;
        }

        var routeId = Guid.NewGuid().ToString("n");
        var render = CreateNode(NodeStage.Render, renderModuleId, order: order, routeId: routeId);
        var deliver = CreateNode(NodeStage.Deliver, deliverModuleId, order: order, routeId: routeId);
        ApplyModuleDefaults(render, FindCatalogModule(renderModuleId));
        ApplyModuleDefaults(deliver, FindCatalogModule(deliverModuleId));
    }

    private void ClearSample()
    {
        _sampleText = string.Empty;
        _fileName = null;
        _contentType = null;
        _analysis = null;
    }
}
