namespace Mulse.Modules;

/// <summary>
/// Protects (and later reverses protection of) the raw bytes written to the runtime state file. The default
/// implementation is a no-op passthrough, preserving the existing plaintext-JSON-on-disk behavior; opting into
/// encryption swaps in an AES-GCM implementation without changing anything else about how state is persisted.
/// </summary>
public interface IRuntimeStatePayloadProtector
{
    byte[] Protect(byte[] plaintext);

    byte[] Unprotect(byte[] protectedPayload);
}
