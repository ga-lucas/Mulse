namespace Mulse.Ui.Models;

public sealed record BizTalkBindingFileViewModel(
    string Name,
    string FilePath,
    int ReceivePortCount,
    int SendPortCount,
    IReadOnlyList<BizTalkReceivePortViewModel> ReceivePorts,
    IReadOnlyList<BizTalkSendPortViewModel> SendPorts);

public sealed record BizTalkReceivePortViewModel(
    string Name,
    IReadOnlyList<BizTalkReceiveLocationViewModel> ReceiveLocations);

public sealed record BizTalkReceiveLocationViewModel(
    string Name,
    string TransportType,
    string Address,
    string ReceivePipeline,
    string SuggestedFetchModule,
    IReadOnlyDictionary<string, string> DraftSettings);

public sealed record BizTalkSendPortViewModel(
    string Name,
    string Description,
    string TransportType,
    string Address,
    string TransmitPipeline,
    string ReceivePipeline,
    string FilterExpression,
    string SuggestedRenderModule,
    string SuggestedDeliverModule,
    IReadOnlyDictionary<string, string> DraftRenderSettings,
    IReadOnlyDictionary<string, string> DraftDeliverSettings);
