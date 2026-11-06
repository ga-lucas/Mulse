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

    internal sealed record BizTalkProjectAnalysis(BizTalkProjectResponse Response, IReadOnlyList<ProjectReference> References)
    {
        public IReadOnlyList<string> DependencyWarnings { get; init; } = [];
        public IReadOnlyList<BizTalkMapAnalysisResponse> ReferencedMapAnalyses { get; init; } = [];
    }

    internal sealed record BindingFileAnalysis(
        string Name,
        string FilePath,
        IReadOnlyList<ReceivePortAnalysis> ReceivePorts,
        IReadOnlyList<SendPortAnalysis> SendPorts)
    {
        public IReadOnlyList<BindingServiceAnalysis> Services { get; init; } = [];
    }

    internal sealed record BindingServiceAnalysis(string AssemblyName, string ServiceName,
        IReadOnlyList<string> SendPortNames, IReadOnlyList<string> ReceivePortNames)
    {
        public IReadOnlyList<BindingLogicalPortAnalysis> LogicalPorts { get; init; } = [];
    }

    internal sealed record BindingLogicalPortAnalysis(string Name, IReadOnlyList<string> SendPortNames, IReadOnlyList<string> ReceivePortNames);

    internal sealed record ReceivePortAnalysis(
        string Name,
        bool IsTwoWay,
        IReadOnlyList<ReceiveLocationAnalysis> ReceiveLocations);

    internal sealed record ReceiveLocationAnalysis(
        string Name,
        string TransportType,
        string Address,
        string ReceivePipeline,
        IReadOnlyDictionary<string, string> TransportProperties)
    {
        public ReceiveLocationOperationalSettings Operational { get; init; } = new();
    }

    /// <summary>BizTalk receive-location behavior that has no single Mulse equivalent and must be reviewed or configured.</summary>
    internal sealed record ReceiveLocationOperationalSettings
    {
        public bool Enabled { get; init; } = true;
        public bool ServiceWindowEnabled { get; init; }
        public string ServiceWindowFrom { get; init; } = string.Empty;
        public string ServiceWindowTo { get; init; } = string.Empty;
        public string ScheduleRecurrenceType { get; init; } = string.Empty;
        public bool StartDateEnabled { get; init; }
        public bool EndDateEnabled { get; init; }
        public bool HasPipelineConfiguration { get; init; }
    }

    /// <summary>BizTalk send-port behavior that has no single Mulse equivalent and must be reviewed or configured.</summary>
    internal sealed record SendPortOperationalSettings
    {
        public bool OrderedDelivery { get; init; }
        public bool StopSendingOnFailure { get; init; }
        public bool RouteFailedMessage { get; init; }
        public int Priority { get; init; } = 5;
        public int Tracking { get; init; }
        public bool ServiceWindowEnabled { get; init; }
        public string ServiceWindowFrom { get; init; } = string.Empty;
        public string ServiceWindowTo { get; init; } = string.Empty;
        public string DeliveryNotification { get; init; } = string.Empty;
        public string SecondaryTransportType { get; init; } = string.Empty;
        public string SecondaryAddress { get; init; } = string.Empty;
        public bool HasSendPipelineConfiguration { get; init; }
        public bool HasReceivePipelineConfiguration { get; init; }
        public bool HasInboundTransforms { get; init; }
        public bool HasOutboundTransforms { get; init; }
        public IReadOnlyList<string> PipelineProperties { get; init; } = [];
        public IReadOnlyList<PipelineComponentConfig> PipelineComponents { get; init; } = [];
    }

    /// <summary>A component instance from a binding's SendPipelineData, with non-secret property values.</summary>
    internal sealed record PipelineComponentConfig(string ClassName, IReadOnlyDictionary<string, string> Properties);

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
        int RetryIntervalMinutes = 0)
    {
        public SendPortOperationalSettings Operational { get; init; } = new();
        /// <summary>True when the binding declares IsStatic="false"; the transport is then chosen by the orchestration at run time.</summary>
        public bool IsDynamic { get; init; }
        /// <summary>Transport type names the binding allows for a dynamic send port (DynamicSendHanders).</summary>
        public IReadOnlyList<string> DynamicTransportTypes { get; init; } = [];
    }
}
