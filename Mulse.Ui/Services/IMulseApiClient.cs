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

    Task<ParsedDataInspectionViewModel> InspectParsedDataAsync(ParsedDataSampleRequestViewModel request, CancellationToken cancellationToken);

    Task<ParsedDataPreviewViewModel> PreviewParsedDataMappingAsync(ParsedDataPreviewRequestViewModel request, CancellationToken cancellationToken);

    Task<XmlMappingPreviewViewModel> PreviewXmlMappingAsync(XmlMappingPreviewRequestViewModel request, CancellationToken cancellationToken);

    Task<XmlSchemaTreeViewModel> AnalyzeXmlMappingSchemasAsync(XmlSchemaAnalysisRequestViewModel request, CancellationToken cancellationToken);

    Task<NativeMapConversionViewModel> ImportNativeXmlMappingAsync(XmlMappingImportRequestViewModel request, CancellationToken cancellationToken);

    Task<BizTalkSolutionAnalysisViewModel> AnalyzeBizTalkSolutionAsync(AnalyzeBizTalkSolutionRequestViewModel request, CancellationToken cancellationToken);

    Task<FlowViewModel> ImportBizTalkDraftFlowAsync(CreateImportedBizTalkFlowRequestViewModel request, CancellationToken cancellationToken);

    Task<DownloadedFileViewModel> DownloadBizTalkScaffoldedModulesAsync(CreateImportedBizTalkFlowRequestViewModel request, CancellationToken cancellationToken);

    Task<DownloadedFileViewModel> DownloadBizTalkScaffoldedModuleAsync(string moduleId, CreateImportedBizTalkFlowRequestViewModel request, CancellationToken cancellationToken);

    Task<FlowViewModel> CreateDesignedFlowAsync(CreateDesignedFlowRequestViewModel request, CancellationToken cancellationToken);

    Task<FlowRunViewModel> RunFlowAsync(string flowId, CancellationToken cancellationToken);

    Task<FlowViewModel> SetFlowEnabledAsync(string flowId, bool enabled, CancellationToken cancellationToken);

    Task<FlowViewModel> DuplicateFlowAsync(string flowId, string newFlowId, CancellationToken cancellationToken);

    Task<FlowViewModel> SetFlowCategoryAsync(string flowId, string? category, CancellationToken cancellationToken);

    Task DeleteFlowAsync(string flowId, CancellationToken cancellationToken);

    Task<IReadOnlyList<ConfigValueViewModel>> GetConfigValuesAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> GetConfigReferencesAsync(CancellationToken cancellationToken);

    Task<RuntimeEncryptionViewModel> GetEncryptionAsync(CancellationToken cancellationToken);

    Task<RuntimeEncryptionViewModel> SetEncryptionAsync(bool enabled, CancellationToken cancellationToken);

    Task<IReadOnlyList<ConfigReferenceUsageViewModel>> GetConfigValueUsagesAsync(CancellationToken cancellationToken);

    Task<ConfigValueViewModel> SetConfigValueAsync(string reference, string value, CancellationToken cancellationToken);

    Task DeleteConfigValueAsync(string reference, CancellationToken cancellationToken);

    Task<ModulePackageViewModel> ReloadModulePackageAsync(string packageId, CancellationToken cancellationToken);

    Task<ModulePackageViewModel> SetModulePackageEnabledAsync(string packageId, bool enabled, CancellationToken cancellationToken);

    Task<ModulePackageViewModel> RegisterModulePackageAsync(string id, string assemblyPath, CancellationToken cancellationToken);

    Task<DownloadedFileViewModel> BackupWorkspaceAsync(string password, CancellationToken cancellationToken);

    Task<WorkspaceRestorePreviewViewModel> PreviewWorkspaceRestoreAsync(RestoreWorkspaceRequestViewModel request, CancellationToken cancellationToken);

    Task<WorkspaceRestorePreviewViewModel> RestoreWorkspaceAsync(RestoreWorkspaceRequestViewModel request, CancellationToken cancellationToken);

    Task<int> DeleteAllFlowsAsync(string confirmation, CancellationToken cancellationToken);
}
