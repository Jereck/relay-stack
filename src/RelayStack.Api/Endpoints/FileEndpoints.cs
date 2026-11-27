using System.Security.Cryptography;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using RelayStack.Api.Auth;
using RelayStack.Platform;
using RelayStack.Platform.Jobs;
using RelayStack.Platform.Metering;
using RelayStack.Platform.Persistence;
using RelayStack.Platform.Security;
using RelayStack.Platform.Storage;
using RelayStack.Platform.Tenancy;

namespace RelayStack.Api.Endpoints;

internal static class FileEndpoints
{
    public const string UploadRateLimitPolicy = "uploads";

    public static void MapFileEndpoints(this RouteGroupBuilder v1)
    {
        v1.MapPost("/inboxes/{inboxId:guid}/files", Upload)
            .RequireAuthorization(ApiKeyDefaults.Policy(Scopes.FilesWrite))
            .RequireRateLimiting(UploadRateLimitPolicy)
            .WithTags("Files").WithName("UploadFile")
            .WithSummary("Upload a CSV file as the raw request body (Content-Type: text/csv). Supports Idempotency-Key.")
            .Accepts<Stream>("text/csv");
        v1.MapGet("/inboxes/{inboxId:guid}/files", ListFiles).RequireAuthorization(ApiKeyDefaults.Policy(Scopes.FilesRead)).WithTags("Files").WithName("ListFiles");
        v1.MapGet("/files/{id:guid}", GetFile).RequireAuthorization(ApiKeyDefaults.Policy(Scopes.FilesRead)).WithTags("Files").WithName("GetFile")
            .WithSummary("Processing status and row counts for an uploaded file.");
        v1.MapGet("/files/{id:guid}/validation-errors", GetValidationErrors).RequireAuthorization(ApiKeyDefaults.Policy(Scopes.FilesRead))
            .WithTags("Files").WithName("ListValidationErrors");
    }

    private static async Task<IResult> Upload(Guid inboxId, HttpContext http, [FromQuery] string? filename, [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        RelayStackDbContext db, IObjectStore store, TenantContext tenant, PlatformOptions options, TimeProvider clock, ILogger<Program> logger, CancellationToken ct)
    {
        var inbox = await db.Inboxes.AsNoTracking().SingleOrDefaultAsync(i => i.Id == inboxId, ct);
        if (inbox is null)
        {
            return ApiErrors.NotFound("Inbox");
        }

        var contentType = http.Request.ContentType ?? "";
        if (!contentType.StartsWith("text/csv", StringComparison.OrdinalIgnoreCase))
        {
            return ApiErrors.Problem(415, "unsupported_media_type", "Upload the file as the raw request body with Content-Type: text/csv.");
        }

        if (idempotencyKey is { Length: 0 or > 255 })
        {
            return ApiErrors.Validation("Idempotency-Key must be 1-255 characters.");
        }

        var limit = Math.Min(inbox.MaxFileBytes, options.Limits.MaxUploadBytes);
        if (http.Request.ContentLength is { } declared && declared > limit)
        {
            return ApiErrors.Problem(413, "file_too_large", $"File exceeds this inbox's limit of {limit} bytes.");
        }

        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } sizeFeature)
        {
            sizeFeature.MaxRequestBodySize = limit + 1;
        }

        var name = SanitizeFileName(filename);
        var s = tenant.Scope;

        if (idempotencyKey is not null && await db.Files.AsNoTracking().SingleOrDefaultAsync(f => f.InboxId == inboxId && f.IdempotencyKey == idempotencyKey, ct) is { } existing)
        {
            return await ReplayIdempotentAsync(http.Request.Body, existing, limit, ct);
        }

        var now = clock.GetUtcNow();
        var fileId = Guid.CreateVersion7(now);
        var key = TenantObjectKey.ForUpload(s.OrganizationId, s.ProjectId, inbox.Id, fileId);
        StoredObject stored;
        try
        {
            stored = await store.PutAsync(key, http.Request.Body, limit, ct);
        }
        catch (ObjectTooLargeException)
        {
            return ApiErrors.Problem(413, "file_too_large", $"File exceeds this inbox's limit of {limit} bytes.");
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return ApiErrors.Problem(413, "file_too_large", $"File exceeds this inbox's limit of {limit} bytes.");
        }

        if (stored.SizeBytes == 0)
        {
            await store.DeleteAsync(key, CancellationToken.None);
            return ApiErrors.Validation("The uploaded file is empty.");
        }

        var file = new InboxFile
        {
            Id = fileId,
            OrganizationId = s.OrganizationId,
            ProjectId = s.ProjectId,
            EnvironmentId = s.EnvironmentId,
            InboxId = inbox.Id,
            OriginalName = name,
            ContentType = "text/csv",
            SizeBytes = stored.SizeBytes,
            Sha256 = stored.Sha256,
            StorageKey = key.Value,
            Status = FileStatuses.Received,
            IdempotencyKey = idempotencyKey,
            ReceivedAt = now,
            ExpiresAt = now.AddDays(inbox.RetentionDays),
        };

        // The file row and its processing job commit together: an accepted upload always has durable work queued.
        db.Files.Add(file);
        db.Jobs.Add(JobQueue.NewProcessFileJob(s.OrganizationId, fileId, options.Processing.JobMaxAttempts, now));
        db.UsageRecords.AddRange(
            Usage.Record(s.OrganizationId, s.ProjectId, s.EnvironmentId, UsageMetrics.FilesUploaded, 1, fileId, now),
            Usage.Record(s.OrganizationId, s.ProjectId, s.EnvironmentId, UsageMetrics.BytesIngested, stored.SizeBytes, fileId, now),
            Usage.Record(s.OrganizationId, s.ProjectId, s.EnvironmentId, UsageMetrics.StorageBytesWritten, stored.SizeBytes, fileId, now),
            Usage.Record(s.OrganizationId, s.ProjectId, s.EnvironmentId, UsageMetrics.StorageByteDaysCommitted, stored.SizeBytes * inbox.RetentionDays, fileId, now));
        Audit.Write(db, tenant, http, "file.uploaded", "file", fileId.ToString(), new { inbox_id = inbox.Id, size = stored.SizeBytes }, now);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } && idempotencyKey is not null)
        {
            // A concurrent request with the same Idempotency-Key won the race.
            await store.DeleteAsync(key, CancellationToken.None);
            db.ChangeTracker.Clear();
            var winner = await db.Files.AsNoTracking().SingleAsync(f => f.InboxId == inboxId && f.IdempotencyKey == idempotencyKey, ct);
            return winner.Sha256.AsSpan().SequenceEqual(stored.Sha256)
                ? Results.Ok(FileResponse.From(winner))
                : IdempotencyConflict();
        }
        catch
        {
            await store.DeleteAsync(key, CancellationToken.None);
            throw;
        }

        logger.LogInformation("Accepted file {FileId} ({Bytes} bytes) for inbox {InboxId}", fileId, stored.SizeBytes, inbox.Id);
        return Results.Accepted($"/v1/files/{fileId}", FileResponse.From(file));
    }

    /// <summary>Same Idempotency-Key: same content returns the original file (200); different content is a conflict (409).</summary>
    private static async Task<IResult> ReplayIdempotentAsync(Stream body, InboxFile existing, long limit, CancellationToken ct)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await body.ReadAsync(buffer, ct)) > 0)
        {
            total += read;
            if (total > limit)
            {
                return IdempotencyConflict();
            }

            sha.AppendData(buffer, 0, read);
        }

        return sha.GetHashAndReset().AsSpan().SequenceEqual(existing.Sha256) ? Results.Ok(FileResponse.From(existing)) : IdempotencyConflict();
    }

    private static IResult IdempotencyConflict() =>
        ApiErrors.Problem(409, "idempotency_key_reused", "This Idempotency-Key was already used for a different file.");

    internal static string SanitizeFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "upload.csv";
        }

        var baseName = name.Replace('\\', '/').Split('/').Last();
        var cleaned = new string(baseName.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (cleaned.Length == 0 || cleaned is "." or "..")
        {
            return "upload.csv";
        }

        return cleaned.Length <= 255 ? cleaned : cleaned[..255];
    }

    private static async Task<IResult> ListFiles(Guid inboxId, RelayStackDbContext db, int? limit, [FromQuery(Name = "starting_after")] Guid? startingAfter, CancellationToken ct)
    {
        if (!await db.Inboxes.AnyAsync(i => i.Id == inboxId, ct))
        {
            return ApiErrors.NotFound("Inbox");
        }

        var n = Paging.Clamp(limit);
        var q = db.Files.AsNoTracking().Where(f => f.InboxId == inboxId);
        if (startingAfter is { } after)
        {
            q = q.Where(f => f.Id.CompareTo(after) < 0);
        }

        var rows = await q.OrderByDescending(f => f.Id).Take(n + 1).ToListAsync(ct);
        return Results.Ok(Paging.Page(rows, n, f => f.Id, FileResponse.From));
    }

    private static async Task<IResult> GetFile(Guid id, RelayStackDbContext db, CancellationToken ct) =>
        await db.Files.AsNoTracking().SingleOrDefaultAsync(f => f.Id == id, ct) is { } f ? Results.Ok(FileResponse.From(f)) : ApiErrors.NotFound("File");

    private static async Task<IResult> GetValidationErrors(Guid id, RelayStackDbContext db, int? limit, [FromQuery(Name = "after_row")] long? afterRow, CancellationToken ct)
    {
        if (!await db.Files.AnyAsync(f => f.Id == id, ct))
        {
            return ApiErrors.NotFound("File");
        }

        var n = Math.Clamp(limit ?? 100, 1, 1000);
        var q = db.ValidationErrors.AsNoTracking().Where(v => v.FileId == id);
        if (afterRow is { } row)
        {
            q = q.Where(v => v.RowNumber > row);
        }

        var rows = await q.OrderBy(v => v.RowNumber).ThenBy(v => v.Id).Take(n + 1).ToListAsync(ct);
        var hasMore = rows.Count > n;
        var page = rows.Take(n).ToList();
        return Results.Ok(new ListResponse<ValidationErrorResponse>(
            page.Select(v => new ValidationErrorResponse(v.RowNumber, v.ColumnName, v.Code, v.Message)).ToList(),
            hasMore, hasMore ? page[^1].RowNumber.ToString(System.Globalization.CultureInfo.InvariantCulture) : null));
    }
}
