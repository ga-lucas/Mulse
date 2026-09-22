using System.Security.Cryptography;

namespace Mulse.Modules.Security;

/// <summary>
/// Encrypts runtime state bytes at rest using AES-GCM. Each call to <see cref="Protect"/> generates a fresh
/// random 96-bit nonce; the on-disk layout is <c>[12-byte nonce][16-byte tag][ciphertext]</c>.
/// </summary>
public sealed class AesGcmRuntimeStatePayloadProtector : IRuntimeStatePayloadProtector
{
    private const int NonceSizeBytes = 12;
    private const int TagSizeBytes = 16;

    private readonly byte[] _key;

    public AesGcmRuntimeStatePayloadProtector(byte[] key)
    {
        if (key.Length is not (16 or 24 or 32))
        {
            throw new ArgumentException("AES-GCM key must be 128, 192, or 256 bits (16, 24, or 32 bytes).", nameof(key));
        }

        _key = key;
    }

    public byte[] Protect(byte[] plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSizeBytes);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSizeBytes];

        using var aesGcm = new AesGcm(_key, TagSizeBytes);
        aesGcm.Encrypt(nonce, plaintext, ciphertext, tag);

        var result = new byte[NonceSizeBytes + TagSizeBytes + ciphertext.Length];
        Buffer.BlockCopy(nonce, 0, result, 0, NonceSizeBytes);
        Buffer.BlockCopy(tag, 0, result, NonceSizeBytes, TagSizeBytes);
        Buffer.BlockCopy(ciphertext, 0, result, NonceSizeBytes + TagSizeBytes, ciphertext.Length);
        return result;
    }

    public byte[] Unprotect(byte[] protectedPayload)
    {
        if (protectedPayload.Length < NonceSizeBytes + TagSizeBytes)
        {
            throw new InvalidOperationException(
                "The runtime state file is too short to be a valid AES-GCM protected payload. It may be corrupt, or it may be an unencrypted file left over from before encryption was enabled.");
        }

        var nonce = protectedPayload[..NonceSizeBytes];
        var tag = protectedPayload[NonceSizeBytes..(NonceSizeBytes + TagSizeBytes)];
        var ciphertext = protectedPayload[(NonceSizeBytes + TagSizeBytes)..];
        var plaintext = new byte[ciphertext.Length];

        using var aesGcm = new AesGcm(_key, TagSizeBytes);
        aesGcm.Decrypt(nonce, ciphertext, tag, plaintext);
        return plaintext;
    }
}
