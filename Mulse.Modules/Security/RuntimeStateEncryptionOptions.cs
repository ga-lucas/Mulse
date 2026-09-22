namespace Mulse.Modules.Security;

/// <summary>Configures optional at-rest encryption for the persisted runtime state file. Disabled by default.</summary>
public sealed class RuntimeStateEncryptionOptions
{
    /// <summary>When <c>true</c>, the runtime state file is encrypted with AES-GCM using a key resolved from
    /// <see cref="KeyBase64"/> or, failing that, the <see cref="KeyEnvironmentVariable"/> environment variable.</summary>
    public bool Enabled { get; init; }

    /// <summary>A base64-encoded 128/192/256-bit AES key. Takes precedence over <see cref="KeyEnvironmentVariable"/> if set.</summary>
    public string? KeyBase64 { get; init; }

    /// <summary>The environment variable to read the base64-encoded AES key from when <see cref="KeyBase64"/> is not set.</summary>
    public string KeyEnvironmentVariable { get; init; } = "MULSE_STATE_ENCRYPTION_KEY";
}
