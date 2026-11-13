namespace Mulse.Persistence;

public sealed class ExecutionRecord
{
    public string Key { get; set; } = "";
    public string ExecutionId { get; set; } = "";
    public string FlowId { get; set; } = "";
    public string FlowKey { get; set; } = "";
    public string State { get; set; } = "Running";
    public long StartedUtcTicks { get; set; }
    public long UpdatedUtcTicks { get; set; }
    public byte[] Definition { get; set; } = [];
}

public sealed class ExecutionEventRecord
{
    public Guid Id { get; set; }
    public string ExecutionKey { get; set; } = "";
    public string Stage { get; set; } = "";
    public string State { get; set; } = "";
    public long CreatedUtcTicks { get; set; }
    public byte[] Detail { get; set; } = [];
}

public sealed class MessageRecord
{
    public Guid Id { get; set; }
    public string ChannelKey { get; set; } = "";
    public string Channel { get; set; } = "";
    public string? FlowId { get; set; }
    public string? ExecutionKey { get; set; }
    public string? ExecutionId { get; set; }
    public string State { get; set; } = "Pending";
    public bool RetainForConfirmation { get; set; }
    public long CreatedUtcTicks { get; set; }
    public long UpdatedUtcTicks { get; set; }
    public byte[] Payload { get; set; } = [];
}

public sealed class DeliveryRecord
{
    public string Key { get; set; } = "";
    public Guid Id { get; set; }
    public string ExecutionKey { get; set; } = "";
    public string Stage { get; set; } = "";
    public string State { get; set; } = "Prepared";
    public long UpdatedUtcTicks { get; set; }
    public byte[] Payload { get; set; } = [];
}
