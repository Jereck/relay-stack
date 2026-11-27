using System.Text.Json;
using RelayStack.Platform;
using RelayStack.Platform.Polling;
using RelayStack.Platform.Processing;

namespace RelayStack.Api;

// Public API contracts. Serialized as snake_case JSON (see ApiApp). Breaking changes require a new /v{n} prefix.

public sealed record ListResponse<T>(IReadOnlyList<T> Data, bool HasMore, string? NextCursor);

public sealed record IdName(Guid Id, string Name);

public sealed record EnvironmentInfo(Guid Id, string Kind);

public sealed record ApiKeyInfo(Guid Id, IReadOnlyList<string> Scopes);

public sealed record ProjectResponse(IdName Organization, IdName Project, EnvironmentInfo Environment, ApiKeyInfo ApiKey);

public sealed record CreateDestinationRequest(string? Name, string? Url, int? TimeoutSeconds, int? MaxAttempts);

public sealed record DestinationResponse(Guid Id, string Name, string Url, bool Enabled, int TimeoutSeconds, int MaxAttempts,
    DateTimeOffset CreatedAt, string? SigningSecret)
{
    public static DestinationResponse From(WebhookDestination d, string? secret = null) =>
        new(d.Id, d.Name, d.Url, d.Enabled, d.TimeoutSeconds, d.MaxAttempts, d.CreatedAt, secret);
}

public sealed record CreateInboxRequest(string? Name, Guid? DestinationId, ValidationSchema? Schema, long? MaxFileBytes, int? RetentionDays);

public sealed record InboxResponse(Guid Id, string Name, Guid? DestinationId, ValidationSchema Schema, long MaxFileBytes, int RetentionDays,
    DateTimeOffset CreatedAt, string UploadPath)
{
    public static InboxResponse From(Inbox i) =>
        new(i.Id, i.Name, i.DestinationId, ValidationSchema.FromJson(i.ValidationSchema), i.MaxFileBytes, i.RetentionDays, i.CreatedAt,
            $"/v1/inboxes/{i.Id}/files");
}

public sealed record FileResponse(Guid Id, Guid InboxId, string OriginalName, string ContentType, long SizeBytes, string Sha256, string Status,
    long? TotalRows, long? ValidRows, long? InvalidRows, string? ErrorCode, string? ErrorMessage, DateTimeOffset ReceivedAt,
    DateTimeOffset? ProcessingStartedAt, DateTimeOffset? CompletedAt, DateTimeOffset ExpiresAt)
{
    public static FileResponse From(InboxFile f) =>
        new(f.Id, f.InboxId, f.OriginalName, f.ContentType, f.SizeBytes, Convert.ToHexStringLower(f.Sha256), f.Status, f.TotalRows, f.ValidRows,
            f.InvalidRows, f.ErrorCode, f.ErrorMessage, f.ReceivedAt, f.ProcessingStartedAt, f.CompletedAt, f.ExpiresAt);
}

public sealed record ValidationErrorResponse(long RowNumber, string? Column, string Code, string Message);

public sealed record EventResponse(Guid Id, string Type, string SourceKind, Guid SourceId, int RecordCount, int BodyBytes,
    DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, bool Purged, JsonElement? Payload)
{
    public static EventResponse From(EventRecord e, bool includePayload) =>
        new(e.Id, e.Type, e.SourceKind, e.SourceId, e.RecordCount, e.BodyBytes, e.CreatedAt, e.ExpiresAt, e.Body is null,
            includePayload && e.Body is not null ? JsonDocument.Parse(e.Body).RootElement.Clone() : null);
}

public sealed record DeliveryResponse(Guid Id, Guid EventId, Guid DestinationId, string Status, int AttemptCount, int MaxAttempts,
    DateTimeOffset NextAttemptAt, DateTimeOffset? LastAttemptAt, int? LastStatusCode, string? LastError, Guid? ReplayOf,
    DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt)
{
    public static DeliveryResponse From(Delivery d) =>
        new(d.Id, d.EventId, d.DestinationId, d.Status, d.AttemptCount, d.MaxAttempts, d.NextAttemptAt, d.LastAttemptAt, d.LastStatusCode,
            d.LastError, d.ReplayOf, d.CreatedAt, d.CompletedAt);
}

public sealed record AttemptResponse(int AttemptNumber, DateTimeOffset StartedAt, int DurationMs, string Outcome, int? StatusCode, string? Error,
    int RequestBytes, int ResponseBytes)
{
    public static AttemptResponse From(DeliveryAttempt a) =>
        new(a.AttemptNumber, a.StartedAt, a.DurationMs, a.Outcome, a.StatusCode, a.Error, a.RequestBytes, a.ResponseBytes);
}

public sealed record UsageResponse(DateTimeOffset From, DateTimeOffset To, IReadOnlyDictionary<string, long> Totals, UsageRatios Ratios);

/// <summary>Derived ratios that expose data amplification (docs/INFRASTRUCTURE_COSTS.md).</summary>
public sealed record UsageRatios(double? DeliveryBytesSentPerByteIngested, double? DeliveryAttemptsPerEvent, double? EventBytesPerByteIngested,
    double? RetryOverheadBytesRatio);

public sealed record CreatePollSourceRequest(string? Name, string? Url, string? RecordIdField, int? IntervalSeconds, Guid? DestinationId, int? RetentionDays);

public sealed record PollSourceResponse(Guid Id, string Name, string Url, string RecordIdField, int IntervalSeconds, Guid DestinationId, bool Enabled,
    DateTimeOffset NextPollAt, DateTimeOffset? LastPolledAt, string? LastError, DateTimeOffset CreatedAt)
{
    public static PollSourceResponse From(PollSource s) =>
        new(s.Id, s.Name, s.Url, s.RecordIdField, s.IntervalSeconds, s.DestinationId, s.Enabled, s.NextPollAt, s.LastPolledAt, s.LastError, s.CreatedAt);
}
