using System.Security.Cryptography;
using System.Text;

namespace Mulse.Persistence;

public sealed class DatabasePayloadCodec(string keyId, string keyEnvironmentVariable)
{
    public string KeyId { get; } = !string.IsNullOrWhiteSpace(keyId) && keyId.Length <= 128
        ? keyId : throw new ArgumentException("A database encryption key ID of 1-128 characters is required.", nameof(keyId));

    private byte[] ReadKey(string? requestedKeyId)
    {
        if (requestedKeyId != KeyId)
            throw new InvalidOperationException($"Database encryption key '{requestedKeyId}' is not configured. Restore the original key configuration; the database was not changed.");
        var encoded = Environment.GetEnvironmentVariable(keyEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(encoded))
            throw new InvalidOperationException($"Set '{keyEnvironmentVariable}' to the base64-encoded database AES key before opening encrypted state.");
        byte[] key;
        try { key = Convert.FromBase64String(encoded); }
        catch (FormatException) { throw new InvalidOperationException($"'{keyEnvironmentVariable}' must contain a base64-encoded AES key."); }
        if (key.Length is 16 or 24 or 32) return key;
        CryptographicOperations.ZeroMemory(key);
        throw new InvalidOperationException($"'{keyEnvironmentVariable}' must contain a 128, 192 or 256-bit AES key.");
    }

    public void ValidateKey()
    {
        var key = ReadKey(KeyId);
        CryptographicOperations.ZeroMemory(key);
    }

    public byte[] Protect(string plaintext, string identity, bool encrypted)
    {
        var bytes = Encoding.UTF8.GetBytes(plaintext);
        if (!encrypted) return bytes;
        var key = ReadKey(KeyId);
        try
        {
            var result = new byte[1 + 12 + 16 + bytes.Length];
            result[0] = 1;
            RandomNumberGenerator.Fill(result.AsSpan(1, 12));
            using var aes = new AesGcm(key, 16);
            aes.Encrypt(result.AsSpan(1, 12), bytes, result.AsSpan(29), result.AsSpan(13, 16), Encoding.UTF8.GetBytes(identity));
            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public string Unprotect(byte[] payload, string identity, bool encrypted, string? storedKeyId)
    {
        if (!encrypted) return Encoding.UTF8.GetString(payload);
        if (payload.Length < 29 || payload[0] != 1)
            throw new InvalidOperationException("Unsupported or damaged encrypted database payload. State was not reseeded.");
        var key = ReadKey(storedKeyId);
        var plaintext = new byte[payload.Length - 29];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(payload.AsSpan(1, 12), payload.AsSpan(29), payload.AsSpan(13, 16), plaintext, Encoding.UTF8.GetBytes(identity));
            return Encoding.UTF8.GetString(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }
}
