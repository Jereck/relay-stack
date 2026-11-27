namespace RelayStack.Platform;

/// <summary>All tunables in one place, bound from the "RelayStack" configuration section.</summary>
public sealed class PlatformOptions
{
    public const string SectionName = "RelayStack";

    public SecurityOptions Security { get; set; } = new();
    public StorageOptions Storage { get; set; } = new();
    public LimitOptions Limits { get; set; } = new();
    public ProcessingOptions Processing { get; set; } = new();
    public DeliveryOptions Delivery { get; set; } = new();
    public PollingOptions Polling { get; set; } = new();
}

public sealed class SecurityOptions
{
    /// <summary>Base64 server-side pepper mixed into API key hashes (HMAC-SHA256). At least 32 bytes.</summary>
    public string ApiKeyPepper { get; set; } = "";

    /// <summary>AES-256 keys by id, base64. Production: replace with KMS envelope encryption (docs/SECURITY_MODEL.md).</summary>
    public Dictionary<string, string> EncryptionKeys { get; set; } = new();

    public string CurrentEncryptionKeyId { get; set; } = "1";
}

public sealed class StorageOptions
{
    /// <summary>
    /// Absolute root directory of the local filesystem object store. The API and worker must point at the same
    /// directory (a shared volume in containers). When empty in Development, a shared temp directory is used.
    /// </summary>
    public string RootPath { get; set; } = "";
}

public sealed class LimitOptions
{
    /// <summary>Hard platform cap on a single upload, regardless of inbox configuration.</summary>
    public long MaxUploadBytes { get; set; } = 100L * 1024 * 1024;

    public long DefaultInboxMaxFileBytes { get; set; } = 25L * 1024 * 1024;
    public int DefaultRetentionDays { get; set; } = 7;
    public int MaxRetentionDays { get; set; } = 30;
    public int MaxJsonRequestBytes { get; set; } = 256 * 1024;
    public int ApiRequestsPerSecondPerKey { get; set; } = 20;
    public int ApiBurstPerKey { get; set; } = 100;
    public int UploadsPerMinutePerKey { get; set; } = 60;
}

public sealed class ProcessingOptions
{
    public int BatchMaxRecords { get; set; } = 100;
    public int MaxEventBodyBytes { get; set; } = 256 * 1024;
    public int MaxStoredValidationErrors { get; set; } = 1000;
    public int JobTimeoutSeconds { get; set; } = 300;
    public int JobLeaseSeconds { get; set; } = 600;
    public int JobMaxAttempts { get; set; } = 3;
    public int MaxConcurrentJobs { get; set; } = 2;
    public int MaxFieldChars { get; set; } = 16 * 1024;
    public int MaxFieldsPerRecord { get; set; } = 200;
    public int MaxRecordChars { get; set; } = 64 * 1024;
    public long MaxRecordsPerFile { get; set; } = 1_000_000;
}

public sealed class DeliveryOptions
{
    /// <summary>
    /// Exact "host:port" pairs that may resolve to private/loopback addresses. Honoured ONLY when the host
    /// environment is Development AND the destination belongs to a development environment. Startup fails if
    /// this is non-empty outside Development. Cloud metadata and link-local addresses are never allowed.
    /// </summary>
    public List<string> DevelopmentReceiverAllowlist { get; set; } = [];

    public double RetryBaseDelaySeconds { get; set; } = 5;
    public double RetryMaxDelaySeconds { get; set; } = 3600;
    public int DefaultMaxAttempts { get; set; } = 8;
    public int DefaultTimeoutSeconds { get; set; } = 10;
    public int MaxConcurrentPerOrganization { get; set; } = 4;
    public int ClaimBatchSize { get; set; } = 20;
    public int LeaseSeconds { get; set; } = 60;
    public int WorkerParallelism { get; set; } = 8;
    public int MaxResponseBytesRead { get; set; } = 4096;
}

public sealed class PollingOptions
{
    public int MaxResponseBytes { get; set; } = 5 * 1024 * 1024;
    public int MaxRecordsPerPoll { get; set; } = 10_000;
    public int RequestTimeoutSeconds { get; set; } = 15;
}
