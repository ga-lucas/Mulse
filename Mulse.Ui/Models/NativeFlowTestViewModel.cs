namespace Mulse.Ui.Models;

public sealed record NativeFlowTestPayloadViewModel(string Name, string Content, string ContentType, Dictionary<string, string> Metadata);
public sealed record NativeFlowTestMockViewModel(string Kind, string Module, NativeFlowTestPayloadViewModel? Output);
public sealed record NativeFlowTestRequestViewModel(string FlowJson, NativeFlowTestPayloadViewModel Input, List<NativeFlowTestMockViewModel> Mocks, bool AdvanceWaits = false);
public sealed record NativeFlowTestCallViewModel(string Kind, string Module, List<NativeFlowTestPayloadViewModel> Input);
public sealed record NativeFlowTestResponseViewModel(string Status, string? SuspensionReason, List<NativeFlowTestPayloadViewModel> Output,
    List<NativeFlowTestPayloadViewModel> Replies, List<NativeFlowTestCallViewModel> Calls, int WaitResumes = 0);
