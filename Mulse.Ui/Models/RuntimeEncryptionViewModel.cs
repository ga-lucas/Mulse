namespace Mulse.Ui.Models;

public sealed record RuntimeEncryptionViewModel(bool Enabled, bool KeyAvailable, string KeyEnvironmentVariable, string? KeyError);
