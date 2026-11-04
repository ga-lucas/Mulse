namespace Service.Models;

public sealed record ReleaseCollectedBatchRequest(string ConfirmInstanceId, int ExpectedPayloadCount);
