namespace Service.Models;

/// <summary>Represents one BizTalk binding file discovered during migration analysis.</summary>
public sealed record BizTalkBindingFileResponse(
    string Name,
    string FilePath,
    int ReceivePortCount,
    int SendPortCount,
    IReadOnlyList<BizTalkReceivePortResponse> ReceivePorts,
    IReadOnlyList<BizTalkSendPortResponse> SendPorts);

/// <summary>Represents one BizTalk receive port and its receive locations.</summary>
public sealed record BizTalkReceivePortResponse(
    string Name,
    bool IsTwoWay,
    IReadOnlyList<BizTalkReceiveLocationResponse> ReceiveLocations);

/// <summary>Represents one BizTalk receive location and the draft Mulse fetch settings derived from it.</summary>
public sealed record BizTalkReceiveLocationResponse(
    string Name,
    string TransportType,
    string Address,
    string ReceivePipeline,
    string SuggestedFetchModule,
    IReadOnlyDictionary<string, string> DraftSettings);

/// <summary>Represents one BizTalk send port and the draft render and deliver settings derived from it.</summary>
public sealed record BizTalkSendPortResponse(
    string Name,
    string Description,
    string TransportType,
    string Address,
    string TransmitPipeline,
    string ReceivePipeline,
    string FilterExpression,
    bool IsTwoWay,
    string SuggestedRenderModule,
    string SuggestedDeliverModule,
    IReadOnlyDictionary<string, string> DraftRenderSettings,
    IReadOnlyDictionary<string, string> DraftDeliverSettings);
