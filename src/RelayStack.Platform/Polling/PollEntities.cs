namespace RelayStack.Platform.Polling;

public sealed class PollSource
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid EnvironmentId { get; set; }
    public required string Name { get; set; }
    public required string Url { get; set; }
    public required string RecordIdField { get; set; }
    public int IntervalSeconds { get; set; }
    public Guid DestinationId { get; set; }
    public int RetentionDays { get; set; }
    public bool Enabled { get; set; } = true;
    public DateTimeOffset NextPollAt { get; set; }
    public DateTimeOffset? LastPolledAt { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class PollObservedRecord
{
    public Guid OrganizationId { get; set; }
    public Guid SourceId { get; set; }
    public required string RecordKey { get; set; }
    public required byte[] ContentHash { get; set; }
    public DateTimeOffset FirstSeenAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public DateTimeOffset LastChangedAt { get; set; }
}
