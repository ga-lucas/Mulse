using Mulse.Ui.Models;

namespace Mulse.Ui.Services;

public interface IMulseApiClient
{
    Task<IReadOnlyList<ModuleViewModel>> GetModulesAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<ModulePackageViewModel>> GetModulePackagesAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<FlowViewModel>> GetFlowsAsync(CancellationToken cancellationToken);

    Task<FlowDesignAnalysisViewModel> AnalyzeFlowDesignAsync(FlowDesignAnalysisRequestViewModel request, CancellationToken cancellationToken);

    Task<BizTalkSolutionAnalysisViewModel> AnalyzeBizTalkSolutionAsync(AnalyzeBizTalkSolutionRequestViewModel request, CancellationToken cancellationToken);

    Task<FlowViewModel> ImportBizTalkDraftFlowAsync(CreateImportedBizTalkFlowRequestViewModel request, CancellationToken cancellationToken);

    Task<FlowViewModel> CreateDesignedFlowAsync(CreateDesignedFlowRequestViewModel request, CancellationToken cancellationToken);

    Task<FlowRunViewModel> RunFlowAsync(string flowId, CancellationToken cancellationToken);

    Task<ModulePackageViewModel> ReloadModulePackageAsync(string packageId, CancellationToken cancellationToken);
}
