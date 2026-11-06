namespace Service.Models;

public sealed record NativeFlowTestPayload(string Name, string Content, string ContentType, Dictionary<string, string> Metadata);
public sealed record NativeFlowTestMock(string Kind, string Module, NativeFlowTestPayload? Output);
public sealed record NativeFlowTestRequest(string FlowJson, NativeFlowTestPayload Input, List<NativeFlowTestMock> Mocks, bool AdvanceWaits = false);
public sealed record NativeFlowTestCall(string Kind, string Module, List<NativeFlowTestPayload> Input, Dictionary<string, string>? Settings = null);
public sealed record NativeFlowTestResponse(string Status, string? SuspensionReason, List<NativeFlowTestPayload> Output,
    List<NativeFlowTestPayload> Replies, List<NativeFlowTestCall> Calls, int WaitResumes = 0);
