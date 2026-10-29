using Microsoft.AspNetCore.Components.Forms;
using Mulse.Ui.Models;

namespace Mulse.Ui.Components.Pages;

// Sample files help infer a source lane's data shape; they are never the runtime source.
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

    private string? _sampleBinary;

    private static async Task<FlowDesignAnalysisRequestViewModel> ReadSampleFileAsync(IBrowserFile file)
    {
        var excel = Path.GetExtension(file.Name).ToLowerInvariant() is ".xls" or ".xlsx";
        await using var stream = file.OpenReadStream(maxAllowedSize: excel ? 10 * 1024 * 1024 : 1_000_000);
        if (excel)
        {
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer);
            return new(file.Name, file.ContentType, string.Empty, Convert.ToBase64String(buffer.ToArray()));
        }
        using var reader = new StreamReader(stream, new System.Text.UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
        return new(file.Name, file.ContentType, await reader.ReadToEndAsync());
    }

    private async Task HandleFileSelectedAsync(InputFileChangeEventArgs args)
    {
        _errorMessage = _statusMessage = null;
        _analysis = null;
        try
        {
            var request = await ReadSampleFileAsync(args.File);
            _fileName = request.FileName;
            _contentType = request.ContentType;
            _sampleText = request.TextContent;
            _sampleBinary = request.BinaryContent;
            await AnalyzeSampleAsync();
        }
        catch (Exception exception)
        {
            _errorMessage = $"Unable to read sample file: {exception.Message}";
        }
    }

    private async Task HandleCanvasFilesAsync(InputFileChangeEventArgs args)
    {
        if (_isAnalyzingSample) return;
        _isAnalyzingSample = true;
        _errorMessage = _statusMessage = null;
        try
        {
            var analyses = new List<FlowDesignAnalysisViewModel>();
            foreach (var file in args.GetMultipleFiles(10))
            {
                var request = await ReadSampleFileAsync(file);
                analyses.Add(await ApiClient.AnalyzeFlowDesignAsync(request, CancellationToken.None));
            }
            foreach (var analysis in analyses) AddFileSource(analysis);
            _statusMessage = $"Added {analyses.Count} sample-shaped source lane(s) without replacing existing nodes. Samples were analyzed only, not saved on the server. Choose each lane's real fetch module, review parser settings and delivery routes before saving. "
                + (analyses.Any(analysis => analysis.DetectedFormat == "Excel")
                    ? "For Excel, choose sheetName or 1-based sheetNumber and review firstRowIsHeader. Load Mulse.Excel if unavailable. " : string.Empty)
                + "Existing routes may receive the new source's data; review their source selection.";
        }
        catch (Exception exception)
        {
            _errorMessage = $"Unable to create draft from files: {exception.Message}";
        }
        finally { _isAnalyzingSample = false; }
    }

    [Microsoft.JSInterop.JSInvokable]
    public Task OnFileDropError(string message)
    {
        _errorMessage = message;
        StateHasChanged();
        return Task.CompletedTask;
    }

    private void AddFileSource(FlowDesignAnalysisViewModel analysis)
    {
        var empty = _nodes.Count == 0;
        var y = empty ? CanvasPadding : _nodes.Max(node => node.Y) + RowSpacing;
        var parseId = analysis.DetectedFormat == "Excel" ? "excel-parse"
            : analysis.ParseModules.FirstOrDefault(module => module.SupportedFormats.Contains(analysis.DetectedFormat, StringComparer.OrdinalIgnoreCase)
                && module.Capabilities.Contains("Parsing", StringComparer.OrdinalIgnoreCase))?.Id ?? string.Empty;
        var (fetch, parse) = CreateSourcePair(NextParseId(), string.Empty, null, parseId, null);
        fetch.Name = "Choose the real data source";
        ApplyModuleDefaults(parse, FindCatalogModule(parseId));
        if (analysis.DetectedFormat == "Excel")
        {
            parse.Settings["sheetNumber"] = "1";
            parse.Settings["firstRowIsHeader"] = "true";
        }
        if (FindCatalogModule(parseId) is null)
            _errorMessage = analysis.DetectedFormat == "Excel"
                ? "The Excel source needs excel-parse. Load Mulse.Excel in Module Packages in another tab, then choose Refresh modules here before saving."
                : $"No loaded parser supports {analysis.DetectedFormat}. Select or load a compatible parser before saving.";
        fetch.X = CanvasPadding;
        fetch.Y = y;
        parse.X = CanvasPadding + ColumnSpacing;
        parse.Y = y;
        if (Routes.Count == 0)
        {
            SeedRoute("json-render", "logging-deliver", 0);
            var route = Routes.Single();
            route.Render!.X = CanvasPadding + 2 * ColumnSpacing;
            route.Render.Y = y;
            route.Delivers[0].X = CanvasPadding + 3 * ColumnSpacing;
            route.Delivers[0].Y = y;
        }
        if (empty && !IsExistingFlow)
        {
            _flowId = CreateDefaultFlowId(analysis.DetectedFormat);
            _enabled = false;
            _triggerMode = "OnDemand";
        }
        _seedSource = SeedSource.Sample;
        _selectedNodeId = fetch.Id;
        if (empty) FitView();
    }

    private async Task AnalyzeSampleAsync()
    {
        _isAnalyzingSample = true;
        _statusMessage = null;
        _errorMessage = null;

        try
        {
            _analysis = await ApiClient.AnalyzeFlowDesignAsync(
                new FlowDesignAnalysisRequestViewModel(_fileName, _contentType, _sampleText, string.IsNullOrWhiteSpace(_sampleText) ? _sampleBinary : null),
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

        var analysis = _analysis;
        AddFileSource(analysis);
        _showSamplePanel = false;
        _statusMessage = $"Added a {analysis.DetectedFormat} sample-shaped source lane. Configure its real data source before saving; the sample itself was not added to the flow.";
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
        _sampleBinary = null;
        _fileName = null;
        _contentType = null;
        _analysis = null;
    }
}
