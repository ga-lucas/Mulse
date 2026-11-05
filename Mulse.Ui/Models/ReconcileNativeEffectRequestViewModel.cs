namespace Mulse.Ui.Models;

public sealed record ReconcileNativeEffectRequestViewModel(string ExpectedEffectId, string Evidence, IReadOnlyList<NativeRecoveryPayloadViewModel>? Output = null);

public sealed record NativeRecoveryPayloadViewModel(string Name, string ContentType, string ContentBase64, IReadOnlyDictionary<string, string>? Metadata = null);
