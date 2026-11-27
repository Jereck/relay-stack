namespace RelayStack.Platform;

// Persistence entities. Kept as plain classes; behaviour lives in the module services that use them.

public sealed class ApiKey
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid EnvironmentId { get; set; }
    public required string Name { get; set; }
    public required string Prefix { get; set; }
    public required byte[] SecretHash { get; set; }
    public required string[] Scopes { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

public sealed class WebhookDestination
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid EnvironmentId { get; set; }
    public required string Name { get; set; }
    public required string Url { get; set; }
    public required byte[] SigningSecretCiphertext { get; set; }
    public bool Enabled { get; set; } = true;
    public int TimeoutSeconds { get; set; }
    public int MaxAttempts { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class Inbox
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid EnvironmentId { get; set; }
    public required string Name { get; set; }
    public Guid? DestinationId { get; set; }
    /// <summary>Serialized <see cref="Processing.ValidationSchema"/>.</summary>
    public required string ValidationSchema { get; set; }
    public long MaxFileBytes { get; set; }
    public int RetentionDays { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public static class FileStatuses
{
    public const string Received = "received";
    public const string Processing = "processing";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Expired = "expired";
}

public sealed class InboxFile
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid EnvironmentId { get; set; }
    public Guid InboxId { get; set; }
    public required string OriginalName { get; set; }
    public required string ContentType { get; set; }
    public long SizeBytes { get; set; }
    public required byte[] Sha256 { get; set; }
    public required string StorageKey { get; set; }
    public required string Status { get; set; }
    public string? IdempotencyKey { get; set; }
    public long? TotalRows { get; set; }
    public long? ValidRows { get; set; }
    public long? InvalidRows { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public DateTimeOffset? ProcessingStartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

public sealed class ValidationErrorRecord
{
    public long Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid FileId { get; set; }
    public long RowNumber { get; set; }
    public string? ColumnName { get; set; }
    public required string Code { get; set; }
    public required string Message { get; set; }
}

public static class JobKinds
{
    public const string ProcessFile = "process_file";
    public const string PollSource = "poll_source";
}

public static class JobStatuses
{
    public const string Pending = "pending";
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
}

public sealed class Job
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public required string Kind { get; set; }
    public Guid SubjectId { get; set; }
    public required string Status { get; set; }
    public int Attempts { get; set; }
    public int MaxAttempts { get; set; }
    public DateTimeOffset RunAfter { get; set; }
    public string? LockedBy { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

public static class EventSourceKinds
{
    public const string InboxFile = "inbox_file";
    public const string PollSource = "poll_source";
}

public sealed class EventRecord
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid EnvironmentId { get; set; }
    public required string SourceKind { get; set; }
    public Guid SourceId { get; set; }
    public required string DedupeKey { get; set; }
    public required string Type { get; set; }
    public string? Body { get; set; }
    public int BodyBytes { get; set; }
    public int RecordCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? PurgedAt { get; set; }
}

public static class DeliveryStatuses
{
    public const string Pending = "pending";
    public const string InFlight = "in_flight";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
}

public sealed class Delivery
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid EventId { get; set; }
    public Guid DestinationId { get; set; }
    public required string Status { get; set; }
    public int AttemptCount { get; set; }
    public int MaxAttempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public string? LockedBy { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
    public DateTimeOffset? LastAttemptAt { get; set; }
    public int? LastStatusCode { get; set; }
    public string? LastError { get; set; }
    public Guid? ReplayOf { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

public static class AttemptOutcomes
{
    public const string Succeeded = "succeeded";
    public const string RetryableFailure = "retryable_failure";
    public const string PermanentFailure = "permanent_failure";
}

public sealed class DeliveryAttempt
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid DeliveryId { get; set; }
    public int AttemptNumber { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public int DurationMs { get; set; }
    public required string Outcome { get; set; }
    public int? StatusCode { get; set; }
    public string? Error { get; set; }
    public int RequestBytes { get; set; }
    public int ResponseBytes { get; set; }
}

public sealed class UsageRecord
{
    public long Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid EnvironmentId { get; set; }
    public required string Metric { get; set; }
    public long Quantity { get; set; }
    public Guid? SubjectId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}

public sealed class AuditEntry
{
    public long Id { get; set; }
    public Guid OrganizationId { get; set; }
    public required string ActorType { get; set; }
    public required string ActorId { get; set; }
    public required string Action { get; set; }
    public required string TargetType { get; set; }
    public required string TargetId { get; set; }
    public string? RemoteIp { get; set; }
    public string? Details { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}
