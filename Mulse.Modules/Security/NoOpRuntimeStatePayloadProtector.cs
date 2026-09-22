namespace Mulse.Modules.Security;

/// <summary>The default protector: passes bytes through unchanged. Used whenever state encryption is disabled.</summary>
public sealed class NoOpRuntimeStatePayloadProtector : IRuntimeStatePayloadProtector
{
    public static readonly NoOpRuntimeStatePayloadProtector Instance = new();

    public byte[] Protect(byte[] plaintext) => plaintext;

    public byte[] Unprotect(byte[] protectedPayload) => protectedPayload;
}
