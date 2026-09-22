using Service.Models;

namespace Service.BizTalkImport;

public sealed partial class BizTalkImportService
{
    internal sealed record AnalysisScope(
        string SourcePath,
        string SourceType,
        string DisplayName,
        string RootDirectory,
        IReadOnlyList<string> BizTalkProjects,
        IReadOnlyList<string> CustomProjects);

    /// <summary>
    /// Identifies one orchestration's slice of a multi-orchestration BizTalk project when that project is split
    /// into one draft flow candidate per orchestration.
    /// </summary>
    /// <param name="OrchestrationName">The orchestration file name (without extension) this candidate covers.</param>
    /// <param name="TotalOrchestrations">How many orchestrations the source project contains in total.</param>
    internal sealed record OrchestrationSplit(string OrchestrationName, int TotalOrchestrations);

    internal sealed record SolutionProjectSet(
        IReadOnlyList<string> BizTalkProjects,
        IReadOnlyList<string> CustomProjects);

    internal sealed record BizTalkProjectAnalysis(BizTalkProjectResponse Response, IReadOnlyList<string> References);

    internal sealed record BindingFileAnalysis(
        string Name,
        string FilePath,
        IReadOnlyList<ReceivePortAnalysis> ReceivePorts,
        IReadOnlyList<SendPortAnalysis> SendPorts);

    internal sealed record ReceivePortAnalysis(
        string Name,
        bool IsTwoWay,
        IReadOnlyList<ReceiveLocationAnalysis> ReceiveLocations);

    internal sealed record ReceiveLocationAnalysis(
        string Name,
        string TransportType,
        string Address,
        string ReceivePipeline,
        IReadOnlyDictionary<string, string> TransportProperties);

    internal sealed record SendPortAnalysis(
        string Name,
        string Description,
        string TransportType,
        string Address,
        string TransmitPipeline,
        string ReceivePipeline,
        string FilterExpression,
        bool IsTwoWay,
        IReadOnlyDictionary<string, string> TransportProperties,
        int RetryCount = 0,
        int RetryIntervalMinutes = 0);
}
