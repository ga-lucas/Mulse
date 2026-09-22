using Microsoft.JSInterop;
using Mulse.Ui.Models;

namespace Mulse.Ui.Components.Pages;

// "Import from BizTalk" side panel: analyze a BizTalk solution, apply a draft flow candidate onto
// the canvas (disabled until reviewed), and download the scaffolded module stubs it suggests.
public partial class Designer
{
    private void ToggleBizTalkPanel()
    {
        _showBizTalkPanel = !_showBizTalkPanel;
        if (_showBizTalkPanel)
        {
            _showSamplePanel = false;
        }
    }

    private async Task AnalyzeBizTalkAsync()
    {
        _isAnalyzingBizTalk = true;
        _statusMessage = null;
        _errorMessage = null;

        try
        {
            _bizTalkAnalysis = await ApiClient.AnalyzeBizTalkSolutionAsync(
                new AnalyzeBizTalkSolutionRequestViewModel(_sourcePath, SplitPaths(_additionalBindingPaths), SplitPaths(_additionalSourcePaths)),
                CancellationToken.None);
            _statusMessage = $"Loaded {_bizTalkAnalysis.BizTalkProjectCount} BizTalk project(s), {_bizTalkAnalysis.BindingFiles.Count} binding file(s), and {_bizTalkAnalysis.FlowCandidates.Count} draft flow candidate(s).";
        }
        catch (Exception exception)
        {
            _errorMessage = $"Unable to analyze BizTalk source: {exception.Message}";
        }
        finally
        {
            _isAnalyzingBizTalk = false;
        }
    }

    private async Task DownloadBizTalkScaffoldedModulesAsync(BizTalkFlowCandidateViewModel candidate)
    {
        try
        {
            var request = new CreateImportedBizTalkFlowRequestViewModel(_sourcePath, candidate.SuggestedFlowId, SplitPaths(_additionalSourcePaths));
            var file = await ApiClient.DownloadBizTalkScaffoldedModulesAsync(request, CancellationToken.None);
            await TriggerBrowserDownloadAsync(file);
        }
        catch (Exception exception)
        {
            _errorMessage = $"Unable to download scaffolded modules: {exception.Message}";
        }
    }

    private async Task DownloadBizTalkScaffoldedModuleAsync(BizTalkFlowCandidateViewModel candidate, BizTalkScaffoldedModuleViewModel module)
    {
        try
        {
            var request = new CreateImportedBizTalkFlowRequestViewModel(_sourcePath, candidate.SuggestedFlowId, SplitPaths(_additionalSourcePaths));
            var file = await ApiClient.DownloadBizTalkScaffoldedModuleAsync(module.ModuleId, request, CancellationToken.None);
            await TriggerBrowserDownloadAsync(file);
        }
        catch (Exception exception)
        {
            _errorMessage = $"Unable to download scaffolded module '{module.FileName}': {exception.Message}";
        }
    }

    private async Task TriggerBrowserDownloadAsync(DownloadedFileViewModel file)
    {
        await using var module = await JS.InvokeAsync<IJSObjectReference>("import", "./js/file-download.js");
        await module.InvokeVoidAsync("downloadFile", file.FileName, file.ContentType, Convert.ToBase64String(file.Content));
    }

    private void ApplyBizTalkCandidate(BizTalkFlowCandidateViewModel candidate)
    {
        var draft = candidate.DraftFlow;

        _nodes.Clear();
        _sources.Clear();
        _joinEditors.Clear();
        _mappingRows.Clear();
        _selectedNodeId = null;

        if (!IsExistingFlow)
        {
            _flowId = string.IsNullOrWhiteSpace(draft.Id) ? candidate.SuggestedFlowId : draft.Id;
            _enabled = false;
        }

        _triggerMode = draft.Trigger.Mode;
        _intervalText = draft.Trigger.Interval?.ToString() ?? "00:05:00";
        _runOnStartup = draft.Trigger.RunOnStartup;

        foreach (var source in draft.Sources)
        {
            CreateSourceLane(
                source.Id,
                source.Fetch.Module,
                source.Fetch.Settings,
                source.Parse.Module,
                source.Parse.Settings,
                source.InputSourceIds);
        }

        var order = 0;
        foreach (var augment in draft.Augments)
        {
            CreateNode(NodeStage.Augment, augment.Module, augment.Settings, order: order++);
        }

        var routeIndex = 0;
        foreach (var route in draft.Deliveries)
        {
            var routeId = Guid.NewGuid().ToString("n");
            CreateNode(NodeStage.Render, route.Render.Module, route.Render.Settings, order: routeIndex, routeId: routeId);
            CreateNode(NodeStage.Deliver, route.Deliver.Module, route.Deliver.Settings, order: routeIndex, routeId: routeId);
            routeIndex++;
        }

        _bizTalkRequirements = draft.ConfigurationRequirements;
        _bizTalkWarnings = draft.Warnings;
        _seedSource = SeedSource.BizTalk;
        _showBizTalkPanel = false;
        AutoLayout();
        _statusMessage = $"Loaded BizTalk draft flow '{candidate.SuggestedFlowId}' onto the canvas with {_sources.Count} source lane(s). It stays disabled until you review its settings and enable it.";
    }

    private void ClearBizTalk()
    {
        _sourcePath = string.Empty;
        _additionalSourcePaths = string.Empty;
        _additionalBindingPaths = string.Empty;
        _bizTalkAnalysis = null;
    }
}
