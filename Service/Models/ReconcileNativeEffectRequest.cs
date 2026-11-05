namespace Service.Models;

public sealed record ReconcileNativeEffectRequest(string ExpectedEffectId, string Evidence, IReadOnlyList<NativeRecoveryPayload>? Output = null);

public sealed record NativeRecoveryPayload(string Name, string ContentType, string ContentBase64, IReadOnlyDictionary<string, string>? Metadata = null);
