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

    private AnalyzeBizTalkSolutionRequestViewModel CurrentBizTalkAnalysisRequest() => new(
        _sourcePath, SplitPaths(_additionalBindingPaths), SplitPaths(_additionalSourcePaths), SplitPaths(_additionalRulePaths), _generateNativeBizTalkFlow,
        _generateNativeBizTalkFlow ? _nativePolicyVersions.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal) : null,
        _generateNativeBizTalkFlow && !string.IsNullOrWhiteSpace(_nativeHostConfigurationPath) ? _nativeHostConfigurationPath : null,
        _generateNativeBizTalkFlow ? SplitPaths(_nativeConfigurationHelperPaths) : null,
        _generateNativeBizTalkFlow ? _nativeMapSettings["casesJson"] : null,
        _generateNativeBizTalkFlow && _deriveNativeMapChoices && SplitPaths(_additionalRulePaths).Length > 0);

    private string? _bizTalkAnalysisFingerprint;
    private static string BizTalkFingerprint(AnalyzeBizTalkSolutionRequestViewModel request) => System.Text.Json.JsonSerializer.Serialize(request);
    private bool BizTalkAnalysisIsCurrent => !_isAnalyzingBizTalk && _bizTalkAnalysis is not null
        && _bizTalkAnalysisFingerprint == BizTalkFingerprint(CurrentBizTalkAnalysisRequest());

    private void RequireCurrentBizTalkAnalysis()
    {
        if (!BizTalkAnalysisIsCurrent) throw new InvalidOperationException("Import settings changed or analysis failed. Analyze the source again before loading or downloading a candidate.");
    }

    private void BizTalkMapChoicesChanged() { }

    // Copies the derived choices into the editable explicit choices; the analysis becomes stale until reanalyzed.
    private void UseDerivedMapChoicesAsExplicit()
    {
        if (_bizTalkAnalysis?.EffectiveMapChoicesJson is { } json) _nativeMapSettings["casesJson"] = json;
    }

    private async Task AnalyzeBizTalkAsync()
    {
        var request = CurrentBizTalkAnalysisRequest();
        _isAnalyzingBizTalk = true;
        _bizTalkAnalysis = null;
        _bizTalkAnalysisFingerprint = null;
        _statusMessage = null;
        _errorMessage = null;

        try
        {
            _bizTalkAnalysis = await ApiClient.AnalyzeBizTalkSolutionAsync(request, CancellationToken.None);
            _bizTalkAnalysisFingerprint = BizTalkFingerprint(request);
            _statusMessage = $"Loaded {_bizTalkAnalysis.BizTalkProjectCount} BizTalk project(s), {_bizTalkAnalysis.BindingFiles.Count} binding file(s), {_bizTalkAnalysis.RulePolicies.Count} rule policy version(s), and {_bizTalkAnalysis.FlowCandidates.Count} draft flow candidate(s).";
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
            RequireCurrentBizTalkAnalysis();
            var request = CreateBizTalkImportRequest(candidate);
            var file = await ApiClient.DownloadBizTalkScaffoldedModulesAsync(request, CancellationToken.None);
            await TriggerBrowserDownloadAsync(file);
        }
        catch (Exception exception)
        {
            _errorMessage = $"Unable to download scaffolded modules: {exception.Message}";
        }
    }

    private async Task DownloadBizTalkControlFlowAsync(BizTalkFlowCandidateViewModel candidate)
    {
        try
        {
            RequireCurrentBizTalkAnalysis();
            var request = CreateBizTalkImportRequest(candidate);
            await TriggerBrowserDownloadAsync(await ApiClient.DownloadBizTalkControlFlowAsync(request, CancellationToken.None));
        }
        catch (Exception exception)
        {
            _errorMessage = $"Unable to download ODX control-flow source model: {exception.Message}";
        }
    }

    private async Task DownloadBizTalkScaffoldedModuleAsync(BizTalkFlowCandidateViewModel candidate, BizTalkScaffoldedModuleViewModel module)
    {
        try
        {
            RequireCurrentBizTalkAnalysis();
            var request = CreateBizTalkImportRequest(candidate);
            var file = await ApiClient.DownloadBizTalkScaffoldedModuleAsync(module.ModuleId, request, CancellationToken.None);
            await TriggerBrowserDownloadAsync(file);
        }
        catch (Exception exception)
        {
            _errorMessage = $"Unable to download scaffolded module '{module.FileName}': {exception.Message}";
        }
    }

    private CreateImportedBizTalkFlowRequestViewModel CreateBizTalkImportRequest(BizTalkFlowCandidateViewModel candidate) => new(
        _sourcePath, candidate.SuggestedFlowId, SplitPaths(_additionalSourcePaths), SplitPaths(_additionalBindingPaths), SplitPaths(_additionalRulePaths), _generateNativeBizTalkFlow,
        _generateNativeBizTalkFlow ? new Dictionary<string, string>(_nativePolicyVersions, StringComparer.Ordinal) : null,
        _generateNativeBizTalkFlow && !string.IsNullOrWhiteSpace(_nativeHostConfigurationPath) ? _nativeHostConfigurationPath : null,
        _generateNativeBizTalkFlow ? SplitPaths(_nativeConfigurationHelperPaths) : null,
        _generateNativeBizTalkFlow ? _nativeMapSettings["casesJson"] : null,
        _generateNativeBizTalkFlow && _deriveNativeMapChoices && SplitPaths(_additionalRulePaths).Length > 0);

    private async Task SelectNativePolicyTarget(BizTalkRulePolicyViewModel policy)
    {
        _nativePolicyVersions[policy.Name] = $"{policy.MajorVersion}.{policy.MinorVersion}";
        await AnalyzeBizTalkAsync();
    }

    private async Task ClearNativePolicyTargets()
    {
        _nativePolicyVersions.Clear();
        await AnalyzeBizTalkAsync();
    }

    private async Task TriggerBrowserDownloadAsync(DownloadedFileViewModel file)
    {
        await using var module = await JS.InvokeAsync<IJSObjectReference>("import", "./js/file-download.js");
        await module.InvokeVoidAsync("downloadFile", file.FileName, file.ContentType, Convert.ToBase64String(file.Content));
    }

    private void ApplyBizTalkCandidate(BizTalkFlowCandidateViewModel candidate)
    {
        if (!BizTalkAnalysisIsCurrent)
        {
            _errorMessage = "Import settings changed or analysis failed. Analyze the source again before loading a candidate.";
            return;
        }
        var draft = candidate.DraftFlow;

        _nodes.Clear();
        _joinEditors.Clear();
        _selectedNodeId = null;

        if (!IsExistingFlow)
        {
            _flowId = string.IsNullOrWhiteSpace(draft.Id) ? candidate.SuggestedFlowId : draft.Id;
            _enabled = false;
        }

        _triggerMode = draft.Trigger.Mode;
        _intervalText = draft.Trigger.Interval?.ToString() ?? "00:05:00";
        _runOnStartup = draft.Trigger.RunOnStartup;

        foreach (var fetch in draft.Fetches)
        {
            CreateFetchNode(fetch.Id, fetch.Fetch.Module, fetch.Fetch.Settings, fetch.InputParseIds);
        }

        foreach (var parse in draft.Parses)
        {
            CreateParseNode(parse.Id, parse.Parse.Module, parse.Parse.Settings, parse.FetchNodeIds);
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
            var render = CreateNode(NodeStage.Render, route.Render.Module, route.Render.Settings, order: routeIndex, routeId: routeId);
            render.AtomicScope = NormalizeAtomicScope(route.AtomicScope);
            foreach (var action in route.Delivers ?? [])
            {
                var deliver = CreateNode(NodeStage.Deliver, action.Deliver.Module, action.Deliver.Settings, order: routeIndex, routeId: routeId);
                deliver.Compensation = action.Compensation is { } compensation
                    ? new FlowStepRequestViewModel(compensation.Module, new Dictionary<string, string>(compensation.Settings, StringComparer.OrdinalIgnoreCase))
                    : null;
            }

            routeIndex++;
        }

        _bizTalkRequirements = draft.ConfigurationRequirements;
        _bizTalkWarnings = draft.Warnings;
        _migration = draft.Migration;
        _seedSource = SeedSource.BizTalk;
        _showBizTalkPanel = false;
        AutoLayout();
        _statusMessage = $"Loaded BizTalk draft flow '{candidate.SuggestedFlowId}' onto the canvas with {ParseNodes.Count} source lane(s) ({FetchNodes.Count} fetch node(s)). Save it disabled; execution remains blocked until verified translation support exists.";
    }

    private void AddImportedRulePolicy(string policyJson)
    {
        CreateNode(NodeStage.Augment, "xml-rules-augment", new Dictionary<string, string> { ["policyJson"] = policyJson }, order: AugmentNodes.Count);
        AutoLayout();
        _statusMessage = "Added the explicitly selected native XML rule proposal. Configure its input source and position after constructing its fact document; this does not wire or approve the original CallRules orchestration.";
    }

    private void ClearBizTalk()
    {
        _sourcePath = string.Empty;
        _additionalSourcePaths = string.Empty;
        _additionalBindingPaths = string.Empty;
        _additionalRulePaths = string.Empty;
        _nativeHostConfigurationPath = string.Empty;
        _nativeConfigurationHelperPaths = string.Empty;
        _nativePolicyVersions.Clear();
        _nativeMapSettings["casesJson"] = "[]";
        _bizTalkAnalysis = null;
        _bizTalkAnalysisFingerprint = null;
    }
}
