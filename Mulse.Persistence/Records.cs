namespace Mulse.Persistence;

public sealed class WorkspaceRecord
{
    public int Id { get; set; } = 1;
    public Guid Version { get; set; }
    public bool Encrypted { get; set; }
    public string? KeyId { get; set; }
    public byte[] PluginDirectories { get; set; } = [];
    public string? ImportSource { get; set; }
}

public sealed class FlowRecord
{
    public string Key { get; set; } = "";
    public string OriginalId { get; set; } = "";
    public string? Name { get; set; }
    public string? Category { get; set; }
    public bool Enabled { get; set; }
    public Guid CurrentRevisionId { get; set; }
    public FlowRevisionRecord CurrentRevision { get; set; } = null!;
}

public sealed class FlowRevisionRecord
{
    public Guid Id { get; set; }
    public string FlowKey { get; set; } = "";
    public long Number { get; set; }
    public long CreatedUtcTicks { get; set; }
    public byte[] Definition { get; set; } = [];
}

public interface IPayloadRecord
{
    string Key { get; set; }
    byte[] Payload { get; set; }
}

public sealed class ConfigurationRecord : IPayloadRecord
{
    public string Key { get; set; } = "";
    public byte[] Payload { get; set; } = [];
}

public sealed class ModulePackageRecord : IPayloadRecord
{
    public string Key { get; set; } = "";
    public byte[] Payload { get; set; } = [];
}

public sealed class CheckpointRecord : IPayloadRecord
{
    public string Key { get; set; } = "";
    public string FlowKey { get; set; } = "";
    public byte[] Payload { get; set; } = [];
}

public sealed class RetryRecord : IPayloadRecord
{
    public string Key { get; set; } = "";
    public string FlowKey { get; set; } = "";
    public string ExecutionKey { get; set; } = "";
    public long NextAttemptUtcTicks { get; set; }
    public bool NotificationsExhausted { get; set; }
    public byte[] Payload { get; set; } = [];
}
