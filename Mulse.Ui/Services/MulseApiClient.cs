using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Mulse.Ui.Models;

namespace Mulse.Ui.Services;

public sealed class MulseApiClient(HttpClient httpClient) : IMulseApiClient
{
    public async Task<IReadOnlyList<ModuleViewModel>> GetModulesAsync(CancellationToken cancellationToken)
    {
        return await httpClient.GetFromJsonAsync<ModuleViewModel[]>("/api/modules", cancellationToken).ConfigureAwait(false)
            ?? [];
    }

    public async Task<IReadOnlyList<ModulePackageViewModel>> GetModulePackagesAsync(CancellationToken cancellationToken)
    {
        return await httpClient.GetFromJsonAsync<ModulePackageViewModel[]>("/api/module-packages", cancellationToken).ConfigureAwait(false)
            ?? [];
    }

    public async Task<IReadOnlyList<FlowViewModel>> GetFlowsAsync(CancellationToken cancellationToken)
    {
        return await httpClient.GetFromJsonAsync<FlowViewModel[]>("/api/flows", cancellationToken).ConfigureAwait(false)
            ?? [];
    }

    public async Task<FlowDesignAnalysisViewModel> AnalyzeFlowDesignAsync(FlowDesignAnalysisRequestViewModel request, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync("/api/flow-designer/analyze", request, cancellationToken).ConfigureAwait(false);
        return await ReadRequiredAsync<FlowDesignAnalysisViewModel>(response, cancellationToken, "flow design analysis response").ConfigureAwait(false);
    }

    public async Task<BizTalkSolutionAnalysisViewModel> AnalyzeBizTalkSolutionAsync(AnalyzeBizTalkSolutionRequestViewModel request, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync("/api/biztalk-import/analyze", request, cancellationToken).ConfigureAwait(false);
        return await ReadRequiredAsync<BizTalkSolutionAnalysisViewModel>(response, cancellationToken, "BizTalk migration analysis response").ConfigureAwait(false);
    }

    public async Task<FlowViewModel> ImportBizTalkDraftFlowAsync(CreateImportedBizTalkFlowRequestViewModel request, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync("/api/biztalk-import/flows", request, cancellationToken).ConfigureAwait(false);
        return await ReadRequiredAsync<FlowViewModel>(response, cancellationToken, "imported BizTalk flow response").ConfigureAwait(false);
    }

    public async Task<FlowViewModel> CreateDesignedFlowAsync(CreateDesignedFlowRequestViewModel request, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync("/api/flow-designer/flows", request, cancellationToken).ConfigureAwait(false);
        return await ReadRequiredAsync<FlowViewModel>(response, cancellationToken, "created flow response").ConfigureAwait(false);
    }

    public async Task<FlowRunViewModel> RunFlowAsync(string flowId, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsync($"/api/flows/{Uri.EscapeDataString(flowId)}/run", content: null, cancellationToken).ConfigureAwait(false);
        return await ReadRequiredAsync<FlowRunViewModel>(response, cancellationToken, "flow run response").ConfigureAwait(false);
    }

    public async Task<ModulePackageViewModel> ReloadModulePackageAsync(string packageId, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsync($"/api/module-packages/{Uri.EscapeDataString(packageId)}/reload", content: null, cancellationToken).ConfigureAwait(false);
        return await ReadRequiredAsync<ModulePackageViewModel>(response, cancellationToken, "module package response").ConfigureAwait(false);
    }

    private static async Task<TModel> ReadRequiredAsync<TModel>(HttpResponseMessage response, CancellationToken cancellationToken, string responseName)
    {
        if (response.IsSuccessStatusCode)
        {
            return await response.Content.ReadFromJsonAsync<TModel>(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"The API returned an empty {responseName}.");
        }

        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(cancellationToken).ConfigureAwait(false);
        var detail = problem?.Detail ?? problem?.Title ?? $"The API returned status code {(int)response.StatusCode}.";
        throw new InvalidOperationException(detail);
    }
}
