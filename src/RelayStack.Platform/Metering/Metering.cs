namespace RelayStack.Platform.Metering;

/// <summary>
/// Usage metrics recorded from day one. Each maps to a real cost driver (docs/INFRASTRUCTURE_COSTS.md),
/// so future quotas and prices can be derived from measured consumption rather than guesses.
/// </summary>
public static class UsageMetrics
{
    public const string FilesUploaded = "files_uploaded";
    public const string BytesIngested = "bytes_ingested";
    public const string StorageBytesWritten = "storage_bytes_written";
    /// <summary>Bytes × retention days committed at upload: the storage-time the platform has promised to keep.</summary>
    public const string StorageByteDaysCommitted = "storage_byte_days_committed";
    public const string RecordsProcessed = "records_processed";
    public const string RecordsInvalid = "records_invalid";
    public const string ProcessingMilliseconds = "processing_ms";
    public const string EventsCreated = "events_created";
    public const string EventBytesCreated = "event_bytes_created";
    public const string DeliveryAttempts = "delivery_attempts";
    /// <summary>Request bytes sent on every attempt, including failures: this is what retries cost.</summary>
    public const string DeliveryBytesSent = "delivery_bytes_sent";
    /// <summary>Request bytes of successful attempts only.</summary>
    public const string DeliveryBytesDelivered = "delivery_bytes_delivered";
    public const string PollRequests = "poll_requests";
    public const string PollBytesFetched = "poll_bytes_fetched";

    public static readonly IReadOnlyList<string> All =
    [
        FilesUploaded, BytesIngested, StorageBytesWritten, StorageByteDaysCommitted, RecordsProcessed, RecordsInvalid,
        ProcessingMilliseconds, EventsCreated, EventBytesCreated, DeliveryAttempts, DeliveryBytesSent, DeliveryBytesDelivered,
        PollRequests, PollBytesFetched,
    ];
}

public static class Usage
{
    public static UsageRecord Record(Guid organizationId, Guid projectId, Guid environmentId, string metric, long quantity, Guid? subjectId, DateTimeOffset at) =>
        new()
        {
            OrganizationId = organizationId,
            ProjectId = projectId,
            EnvironmentId = environmentId,
            Metric = metric,
            Quantity = quantity,
            SubjectId = subjectId,
            OccurredAt = at,
        };
}
