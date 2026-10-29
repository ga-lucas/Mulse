using System.Security.Cryptography;
using Mulse.Modules.Security;

namespace Service.Execution;

public sealed record RuntimeEncryptionStatus(bool Enabled, bool KeyAvailable, string KeyEnvironmentVariable, string? KeyError);

internal static class RuntimeEncryptionKey
{
    public static IRuntimeStatePayloadProtector FromConfiguration(RuntimeStateEncryptionOptions options)
    {
        if (!options.Enabled) return NoOpRuntimeStatePayloadProtector.Instance;
        if (string.IsNullOrWhiteSpace(options.KeyBase64)) return FromEnvironment(options.KeyEnvironmentVariable);
        try { return new AesGcmRuntimeStatePayloadProtector(Convert.FromBase64String(options.KeyBase64)); }
        catch (FormatException) { throw new InvalidOperationException("The configured runtime state encryption key is not valid base64."); }
    }

    public static AesGcmRuntimeStatePayloadProtector FromEnvironment(string variable)
    {
        var encoded = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(encoded))
            throw new InvalidOperationException($"Set the '{variable}' environment variable on the Service to a base64-encoded AES key, then restart the Service.");

        byte[] key;
        try { key = Convert.FromBase64String(encoded); }
        catch (FormatException) { throw new InvalidOperationException($"The '{variable}' environment variable must contain a base64-encoded AES key."); }
        if (key.Length is not (16 or 24 or 32))
        {
            CryptographicOperations.ZeroMemory(key);
            throw new InvalidOperationException($"The '{variable}' environment variable must contain a 128, 192 or 256-bit AES key.");
        }
        return new AesGcmRuntimeStatePayloadProtector(key);
    }
}
