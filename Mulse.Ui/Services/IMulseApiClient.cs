using Mulse.Ui.Models;

namespace Mulse.Ui.Services;

public interface IMulseApiClient
{
    Task<IReadOnlyList<ModuleViewModel>> GetModulesAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<ModulePackageViewModel>> GetModulePackagesAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<FlowViewModel>> GetFlowsAsync(CancellationToken cancellationToken);

    Task<FlowViewModel> GetFlowAsync(string flowId, CancellationToken cancellationToken);

    Task<IReadOnlyList<FlowCheckpointViewModel>> GetFlowCheckpointsAsync(string flowId, CancellationToken cancellationToken);

    Task<IReadOnlyList<HistoryExecutionViewModel>> GetHistoryExecutionsAsync(string? flowId, CancellationToken cancellationToken)
        => throw new NotSupportedException("This API client does not support execution history.");

    Task<HistoryExecutionDetailViewModel> GetHistoryExecutionAsync(string executionId, CancellationToken cancellationToken)
        => throw new NotSupportedException("This API client does not support execution history.");

    Task<IReadOnlyList<HistoryMessageViewModel>> GetHistoryMessagesAsync(CancellationToken cancellationToken)
        => throw new NotSupportedException("This API client does not support inbox history.");

    Task<HistoryMessagePayloadViewModel> GetHistoryMessagePayloadAsync(Guid messageId, CancellationToken cancellationToken)
        => throw new NotSupportedException("This API client does not support message payload inspection.");

    Task ConfirmHistoryDeliveryAsync(string executionId, Guid deliveryId, string confirmation, string evidence, CancellationToken cancellationToken)
        => throw new NotSupportedException("This API client does not support delivery confirmation.");

    Task CloseHistoryExecutionAsync(string executionId, string confirmation, string evidence, CancellationToken cancellationToken)
        => throw new NotSupportedException("This API client does not support held execution closure.");

    Task<FlowViewModel> CreateFlowAsync(CreateFlowRequestViewModel request, CancellationToken cancellationToken);

    Task<FlowViewModel> UpdateFlowAsync(UpdateFlowRequestViewModel request, CancellationToken cancellationToken);

    Task<FlowDesignAnalysisViewModel> AnalyzeFlowDesignAsync(FlowDesignAnalysisRequestViewModel request, CancellationToken cancellationToken);

    Task<ParsedDataInspectionViewModel> InspectParsedDataAsync(ParsedDataSampleRequestViewModel request, CancellationToken cancellationToken);

    Task<ParsedDataPreviewViewModel> PreviewParsedDataMappingAsync(ParsedDataPreviewRequestViewModel request, CancellationToken cancellationToken);

    Task<XmlMappingPreviewViewModel> PreviewXmlMappingAsync(XmlMappingPreviewRequestViewModel request, CancellationToken cancellationToken);

    Task<NativeFlowTestResponseViewModel> TestNativeFlowAsync(NativeFlowTestRequestViewModel request, CancellationToken cancellationToken)
        => throw new NotSupportedException("This API client does not support isolated native testing.");

    Task<XmlSchemaTreeViewModel> AnalyzeXmlMappingSchemasAsync(XmlSchemaAnalysisRequestViewModel request, CancellationToken cancellationToken);

    Task<NativeMapConversionViewModel> ImportNativeXmlMappingAsync(XmlMappingImportRequestViewModel request, CancellationToken cancellationToken);

    Task<BizTalkSolutionAnalysisViewModel> AnalyzeBizTalkSolutionAsync(AnalyzeBizTalkSolutionRequestViewModel request, CancellationToken cancellationToken);

    Task<XmlRulePreviewResultViewModel> PreviewXmlRulesAsync(XmlRulePreviewRequestViewModel request, CancellationToken cancellationToken);

    Task<FlowViewModel> ImportBizTalkDraftFlowAsync(CreateImportedBizTalkFlowRequestViewModel request, CancellationToken cancellationToken);

    Task<DownloadedFileViewModel> DownloadBizTalkScaffoldedModulesAsync(CreateImportedBizTalkFlowRequestViewModel request, CancellationToken cancellationToken);

    Task<DownloadedFileViewModel> DownloadBizTalkControlFlowAsync(CreateImportedBizTalkFlowRequestViewModel request, CancellationToken cancellationToken);

    Task<DownloadedFileViewModel> DownloadBizTalkScaffoldedModuleAsync(string moduleId, CreateImportedBizTalkFlowRequestViewModel request, CancellationToken cancellationToken);

    Task<FlowViewModel> CreateDesignedFlowAsync(CreateDesignedFlowRequestViewModel request, CancellationToken cancellationToken);

    Task<FlowRunViewModel> RunFlowAsync(string flowId, CancellationToken cancellationToken);

    Task<FlowRunViewModel> ReleaseCollectedBatchAsync(string flowId, string instanceId, int expectedPayloadCount, CancellationToken cancellationToken);

    Task<FlowRunViewModel> ReconcileNativeEffectAsync(string flowId, string instanceId, ReconcileNativeEffectRequestViewModel request, CancellationToken cancellationToken)
        => throw new NotSupportedException("This API client does not support native effect reconciliation.");

    Task<FlowViewModel> SetFlowEnabledAsync(string flowId, bool enabled, CancellationToken cancellationToken);

    Task<FlowViewModel> DuplicateFlowAsync(string flowId, string newFlowId, CancellationToken cancellationToken);

    Task<FlowViewModel> SetFlowCategoryAsync(string flowId, string? category, CancellationToken cancellationToken);

    Task DeleteFlowAsync(string flowId, CancellationToken cancellationToken);

    Task<IReadOnlyList<ConfigValueViewModel>> GetConfigValuesAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> GetConfigReferencesAsync(CancellationToken cancellationToken);

    Task<RuntimeEncryptionViewModel> GetEncryptionAsync(CancellationToken cancellationToken);

    Task<RuntimeEncryptionViewModel> SetEncryptionAsync(bool enabled, CancellationToken cancellationToken);

    Task<RuntimeStorageViewModel> GetStorageAsync(CancellationToken cancellationToken)
        => throw new NotSupportedException("This API client does not support storage diagnostics.");

    Task<StorageMigrationStatusViewModel> GetStorageMigrationAsync(CancellationToken cancellationToken)
        => throw new NotSupportedException("This API client does not support storage migration.");

    Task<StorageMigrationReportViewModel> RunStorageMigrationAsync(StorageMigrationRequestViewModel request, bool execute, CancellationToken cancellationToken)
        => throw new NotSupportedException("This API client does not support storage migration.");

    Task<ProcessingStatusViewModel> GetProcessingAsync(CancellationToken cancellationToken)
        => throw new NotSupportedException("This API client does not support processing controls.");

    Task<ProcessingBacklogViewModel> GetProcessingBacklogAsync(CancellationToken cancellationToken)
        => throw new NotSupportedException("This API client does not support processing backlog diagnostics.");

    Task<ProcessingStatusViewModel> SetMaintenanceAsync(bool requested, CancellationToken cancellationToken)
        => throw new NotSupportedException("This API client does not support processing controls.");

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
