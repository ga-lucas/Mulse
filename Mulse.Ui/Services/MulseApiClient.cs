using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Mulse.Ui.Models;

namespace Mulse.Ui.Services;

public sealed class MulseApiClient(HttpClient httpClient) : IMulseApiClient
{
    public Task<IReadOnlyList<ModuleViewModel>> GetModulesAsync(CancellationToken cancellationToken)
        => GetListAsync<ModuleViewModel>("/api/modules", cancellationToken);

    public Task<IReadOnlyList<ModulePackageViewModel>> GetModulePackagesAsync(CancellationToken cancellationToken)
        => GetListAsync<ModulePackageViewModel>("/api/module-packages", cancellationToken);

    public Task<IReadOnlyList<FlowViewModel>> GetFlowsAsync(CancellationToken cancellationToken)
        => GetListAsync<FlowViewModel>("/api/flows", cancellationToken);

    public async Task<FlowViewModel> GetFlowAsync(string flowId, CancellationToken cancellationToken)
    {
        return await httpClient.GetFromJsonAsync<FlowViewModel>($"/api/flows/{Uri.EscapeDataString(flowId)}", cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The API returned an empty flow response.");
    }

    public async Task<FlowViewModel> CreateFlowAsync(CreateFlowRequestViewModel request, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync("/api/flows", request, cancellationToken).ConfigureAwait(false);
        return await ReadRequiredAsync<FlowViewModel>(response, cancellationToken, "created flow response").ConfigureAwait(false);
    }

    public async Task<FlowViewModel> UpdateFlowAsync(UpdateFlowRequestViewModel request, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PutAsJsonAsync($"/api/flows/{Uri.EscapeDataString(request.Id)}", request, cancellationToken).ConfigureAwait(false);
        return await ReadRequiredAsync<FlowViewModel>(response, cancellationToken, "updated flow response").ConfigureAwait(false);
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

    public async Task<DownloadedFileViewModel> DownloadBizTalkScaffoldedModulesAsync(CreateImportedBizTalkFlowRequestViewModel request, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync("/api/biztalk-import/scaffolded-modules/download", request, cancellationToken).ConfigureAwait(false);
        return await ReadDownloadedFileAsync(response, cancellationToken, "scaffolded modules zip").ConfigureAwait(false);
    }

    public async Task<DownloadedFileViewModel> DownloadBizTalkScaffoldedModuleAsync(string moduleId, CreateImportedBizTalkFlowRequestViewModel request, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync($"/api/biztalk-import/scaffolded-modules/{Uri.EscapeDataString(moduleId)}/download", request, cancellationToken).ConfigureAwait(false);
        return await ReadDownloadedFileAsync(response, cancellationToken, "scaffolded module file").ConfigureAwait(false);
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

    public async Task<FlowViewModel> SetFlowEnabledAsync(string flowId, bool enabled, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PatchAsJsonAsync($"/api/flows/{Uri.EscapeDataString(flowId)}/enabled", new SetFlowEnabledRequestViewModel(enabled), cancellationToken).ConfigureAwait(false);
        return await ReadRequiredAsync<FlowViewModel>(response, cancellationToken, "updated flow response").ConfigureAwait(false);
    }

    public async Task<FlowViewModel> DuplicateFlowAsync(string flowId, string newFlowId, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync($"/api/flows/{Uri.EscapeDataString(flowId)}/duplicate", new { Id = newFlowId }, cancellationToken).ConfigureAwait(false);
        return await ReadRequiredAsync<FlowViewModel>(response, cancellationToken, "duplicated flow response").ConfigureAwait(false);
    }

    public async Task<FlowViewModel> SetFlowCategoryAsync(string flowId, string? category, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PatchAsJsonAsync($"/api/flows/{Uri.EscapeDataString(flowId)}/category", new { Category = category }, cancellationToken).ConfigureAwait(false);
        return await ReadRequiredAsync<FlowViewModel>(response, cancellationToken, "updated flow response").ConfigureAwait(false);
    }

    public async Task DeleteFlowAsync(string flowId, CancellationToken cancellationToken)
    {
        using var response = await httpClient.DeleteAsync($"/api/flows/{Uri.EscapeDataString(flowId)}", cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException(problem?.Detail ?? problem?.Title ?? $"The API returned status code {(int)response.StatusCode}.");
        }
    }

    public Task<IReadOnlyList<ConfigValueViewModel>> GetConfigValuesAsync(CancellationToken cancellationToken)
        => GetListAsync<ConfigValueViewModel>("/api/config-values", cancellationToken);

    public Task<IReadOnlyList<string>> GetConfigReferencesAsync(CancellationToken cancellationToken)
        => GetListAsync<string>("/api/config-values/references", cancellationToken);

    public async Task<RuntimeEncryptionViewModel> GetEncryptionAsync(CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync("/api/workspace/encryption", cancellationToken).ConfigureAwait(false);
        return await ReadRequiredAsync<RuntimeEncryptionViewModel>(response, cancellationToken, "encryption status").ConfigureAwait(false);
    }

    public async Task<RuntimeEncryptionViewModel> SetEncryptionAsync(bool enabled, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PutAsJsonAsync("/api/workspace/encryption", new { Enabled = enabled }, cancellationToken).ConfigureAwait(false);
        return await ReadRequiredAsync<RuntimeEncryptionViewModel>(response, cancellationToken, "encryption status").ConfigureAwait(false);
    }

    public Task<IReadOnlyList<ConfigReferenceUsageViewModel>> GetConfigValueUsagesAsync(CancellationToken cancellationToken)
        => GetListAsync<ConfigReferenceUsageViewModel>("/api/config-values/usages", cancellationToken);

    public async Task<ConfigValueViewModel> SetConfigValueAsync(string reference, string value, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PutAsJsonAsync($"/api/config-values/{Uri.EscapeDataString(reference)}", new SetConfigValueRequestViewModel(value), cancellationToken).ConfigureAwait(false);
        return await ReadRequiredAsync<ConfigValueViewModel>(response, cancellationToken, "config value response").ConfigureAwait(false);
    }

    public async Task DeleteConfigValueAsync(string reference, CancellationToken cancellationToken)
    {
        using var response = await httpClient.DeleteAsync($"/api/config-values/{Uri.EscapeDataString(reference)}", cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException(problem?.Detail ?? problem?.Title ?? $"The API returned status code {(int)response.StatusCode}.");
        }
    }

    public async Task<ModulePackageViewModel> ReloadModulePackageAsync(string packageId, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsync($"/api/module-packages/{Uri.EscapeDataString(packageId)}/reload", content: null, cancellationToken).ConfigureAwait(false);
        return await ReadRequiredAsync<ModulePackageViewModel>(response, cancellationToken, "module package response").ConfigureAwait(false);
    }

    public async Task<ModulePackageViewModel> SetModulePackageEnabledAsync(string packageId, bool enabled, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PatchAsJsonAsync($"/api/module-packages/{Uri.EscapeDataString(packageId)}/enabled", new { Enabled = enabled, Confirmation = enabled ? null : "UNLOAD PACKAGE" }, cancellationToken).ConfigureAwait(false);
        return await ReadRequiredAsync<ModulePackageViewModel>(response, cancellationToken, "module package response").ConfigureAwait(false);
    }

    public async Task<ModulePackageViewModel> RegisterModulePackageAsync(string id, string assemblyPath, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync("/api/module-packages", new { Id = id, AssemblyPath = assemblyPath, Enabled = true }, cancellationToken).ConfigureAwait(false);
        return await ReadRequiredAsync<ModulePackageViewModel>(response, cancellationToken, "module package response").ConfigureAwait(false);
    }

    public async Task<DownloadedFileViewModel> BackupWorkspaceAsync(string password, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync("/api/workspace/backup", new { Password = password }, cancellationToken).ConfigureAwait(false);
        return await ReadDownloadedFileAsync(response, cancellationToken, "workspace backup").ConfigureAwait(false);
    }

    public async Task<WorkspaceRestorePreviewViewModel> PreviewWorkspaceRestoreAsync(RestoreWorkspaceRequestViewModel request, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync("/api/workspace/restore/preview", request, cancellationToken).ConfigureAwait(false);
        return await ReadRequiredAsync<WorkspaceRestorePreviewViewModel>(response, cancellationToken, "restore preview").ConfigureAwait(false);
    }

    public async Task<WorkspaceRestorePreviewViewModel> RestoreWorkspaceAsync(RestoreWorkspaceRequestViewModel request, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync("/api/workspace/restore", request, cancellationToken).ConfigureAwait(false);
        return await ReadRequiredAsync<WorkspaceRestorePreviewViewModel>(response, cancellationToken, "restore result").ConfigureAwait(false);
    }

    public async Task<int> DeleteAllFlowsAsync(string confirmation, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync("/api/workspace/flows/delete-all", new { Confirmation = confirmation }, cancellationToken).ConfigureAwait(false);
        var result = await ReadRequiredAsync<DeletedFlowsResult>(response, cancellationToken, "deleted flow count").ConfigureAwait(false);
        return result.DeletedCount;
    }

    private sealed record DeletedFlowsResult(int DeletedCount);

    private async Task<IReadOnlyList<TModel>> GetListAsync<TModel>(string url, CancellationToken cancellationToken)
    {
        return await httpClient.GetFromJsonAsync<TModel[]>(url, cancellationToken).ConfigureAwait(false)
            ?? [];
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

    private static async Task<DownloadedFileViewModel> ReadDownloadedFileAsync(HttpResponseMessage response, CancellationToken cancellationToken, string responseName)
    {
        if (!response.IsSuccessStatusCode)
        {
            // The download endpoints return a plain-text 404 body (rather than JSON) when no scaffolded module
            // matches, so fall back to reading it as text instead of attempting JSON deserialization.
            var errorText = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(errorText)
                ? $"The API returned status code {(int)response.StatusCode} while downloading the {responseName}."
                : errorText);
        }

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        var fileName = response.Content.Headers.ContentDisposition?.FileNameStar
            ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"')
            ?? $"{responseName.Replace(' ', '-')}.dat";
        var contentType = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
        return new DownloadedFileViewModel(fileName, contentType, bytes);
    }
}
