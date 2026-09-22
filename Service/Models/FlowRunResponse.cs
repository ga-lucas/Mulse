namespace Service.Models;

/// <summary>Represents the result of manually executing a configured flow.</summary>
public sealed record FlowRunResponse(
    string FlowId,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    int PayloadCount,
    IReadOnlyList<string> DeliverModules,
    int ResponsePayloadCount = 0,
    string Outcome = "Completed",
    DateTimeOffset? NextAttemptAt = null,
    IReadOnlyList<FlowRunResponsePayload>? ResponsePayloads = null);

/// <summary>
/// A single response payload captured from a request-response deliver module (see
/// <c>IRequestResponseDeliverModule</c>), surfaced to callers of the "run flow" API so a
/// synchronous caller can actually read the reply instead of only learning how many were captured.
/// </summary>
public sealed record FlowRunResponsePayload(
    string Name,
    string ContentType,
    string ContentBase64,
    IReadOnlyDictionary<string, string> Metadata);
