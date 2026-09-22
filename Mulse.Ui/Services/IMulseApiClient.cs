using Mulse.Ui.Models;

namespace Mulse.Ui.Services;

public interface IMulseApiClient
{
    Task<IReadOnlyList<ModuleViewModel>> GetModulesAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<ModulePackageViewModel>> GetModulePackagesAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<FlowViewModel>> GetFlowsAsync(CancellationToken cancellationToken);

    Task<FlowViewModel> GetFlowAsync(string flowId, CancellationToken cancellationToken);

    Task<FlowViewModel> CreateFlowAsync(CreateFlowRequestViewModel request, CancellationToken cancellationToken);

    Task<FlowViewModel> UpdateFlowAsync(UpdateFlowRequestViewModel request, CancellationToken cancellationToken);

    Task<FlowDesignAnalysisViewModel> AnalyzeFlowDesignAsync(FlowDesignAnalysisRequestViewModel request, CancellationToken cancellationToken);

    Task<BizTalkSolutionAnalysisViewModel> AnalyzeBizTalkSolutionAsync(AnalyzeBizTalkSolutionRequestViewModel request, CancellationToken cancellationToken);

    Task<FlowViewModel> ImportBizTalkDraftFlowAsync(CreateImportedBizTalkFlowRequestViewModel request, CancellationToken cancellationToken);

    Task<DownloadedFileViewModel> DownloadBizTalkScaffoldedModulesAsync(CreateImportedBizTalkFlowRequestViewModel request, CancellationToken cancellationToken);

    Task<DownloadedFileViewModel> DownloadBizTalkScaffoldedModuleAsync(string moduleId, CreateImportedBizTalkFlowRequestViewModel request, CancellationToken cancellationToken);

    Task<FlowViewModel> CreateDesignedFlowAsync(CreateDesignedFlowRequestViewModel request, CancellationToken cancellationToken);

    Task<FlowRunViewModel> RunFlowAsync(string flowId, CancellationToken cancellationToken);

    Task<FlowViewModel> SetFlowEnabledAsync(string flowId, bool enabled, CancellationToken cancellationToken);

    Task<IReadOnlyList<ConfigValueViewModel>> GetConfigValuesAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<ConfigReferenceUsageViewModel>> GetConfigValueUsagesAsync(CancellationToken cancellationToken);

    Task<ConfigValueViewModel> SetConfigValueAsync(string reference, string value, CancellationToken cancellationToken);

    Task DeleteConfigValueAsync(string reference, CancellationToken cancellationToken);

    Task<ModulePackageViewModel> ReloadModulePackageAsync(string packageId, CancellationToken cancellationToken);
}
