using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RelayStack.Platform.Deliveries;
using RelayStack.Platform.Metering;
using RelayStack.Platform.Persistence;
using RelayStack.Platform.Storage;

namespace RelayStack.Platform.Processing;

public static class InboxEventTypes
{
    public const string RecordsBatch = "inbox.file.records";
    public const string FileProcessed = "inbox.file.processed";
    public const string FileFailed = "inbox.file.failed";
}

/// <summary>A failure that retrying will not fix (bad file, schema mismatch). The file is marked failed immediately.</summary>
public sealed class PermanentProcessingException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// Processes one uploaded CSV file: stream → parse → validate → batch → events + deliveries.
/// Idempotent: batches are numbered deterministically, so a re-run after a crash produces the same dedupe
/// keys and already-written batches are skipped by the database.
/// </summary>
public sealed class FileProcessor(RelayStackDbContext db, IObjectStore store, PlatformOptions options, TimeProvider clock, ILogger<FileProcessor> logger)
{
    private static readonly ActivitySource Activity = new("RelayStack.Processing");

    public async Task ProcessAsync(Guid fileId, CancellationToken ct)
    {
        using var activity = Activity.StartActivity("process_file");
        activity?.SetTag("relaystack.file_id", fileId);
        var started = Stopwatch.StartNew();

        var file = await db.Files.SingleOrDefaultAsync(f => f.Id == fileId, ct)
            ?? throw new PermanentProcessingException("file_not_found", "File no longer exists.");
        if (file.Status is FileStatuses.Completed or FileStatuses.Failed or FileStatuses.Expired)
        {
            logger.LogInformation("File {FileId} already in terminal state {Status}; nothing to do", fileId, file.Status);
            return;
        }

        var inbox = await db.Inboxes.SingleAsync(i => i.Id == file.InboxId && i.OrganizationId == file.OrganizationId, ct);
        var env = await db.Environments.SingleAsync(e => e.Id == file.EnvironmentId && e.OrganizationId == file.OrganizationId, ct);
        DestinationTarget? destination = null;
        if (inbox.DestinationId is { } destId)
        {
            var d = await db.Destinations.SingleAsync(x => x.Id == destId && x.OrganizationId == file.OrganizationId, ct);
            destination = new DestinationTarget(d.Id, d.MaxAttempts);
        }

        var now = clock.GetUtcNow();
        file.Status = FileStatuses.Processing;
        file.ProcessingStartedAt ??= now;
        await db.SaveChangesAsync(ct);
        // A previous attempt may have stored partial validation errors; they are rebuilt from scratch.
        await db.ValidationErrors.Where(v => v.FileId == file.Id).ExecuteDeleteAsync(ct);

        var schema = ValidationSchema.FromJson(inbox.ValidationSchema);
        var draftBase = new EventDraft(file.OrganizationId, file.ProjectId, file.EnvironmentId, env.Kind, EventSourceKinds.InboxFile,
            file.Id, "", "", new JsonObject(), 0, file.ExpiresAt);

        long total = 0, valid = 0, invalid = 0;
        try
        {
            var key = TenantObjectKey.Parse(file.OrganizationId, file.StorageKey);
            await using var stream = await store.OpenReadAsync(key, ct);
            using var reader = new StreamReader(stream, new UTF8Encoding(false, throwOnInvalidBytes: true), detectEncodingFromByteOrderMarks: false, bufferSize: 64 * 1024);
            var csv = new CsvStreamReader(reader, CsvLimits.FromOptions(options.Processing));
            var validator = new RecordValidator(schema);

            var header = await csv.ReadAsync(ct) ?? throw new PermanentProcessingException("empty_file", "File contains no header row.");
            validator.BindHeader(header.Fields);

            var batch = new JsonArray();
            var batchBytes = 0;
            var batchIndex = 0;
            long firstRow = 0, lastRow = 0;
            var errors = new List<FieldError>();
            var pendingErrors = new List<ValidationErrorRecord>();
            var storedErrors = 0;

            async Task FlushAsync()
            {
                if (batch.Count == 0)
                {
                    return;
                }

                var data = new JsonObject
                {
                    ["inbox_id"] = inbox.Id.ToString(),
                    ["file_id"] = file.Id.ToString(),
                    ["file_name"] = file.OriginalName,
                    ["batch_index"] = batchIndex,
                    ["first_row"] = firstRow,
                    ["last_row"] = lastRow,
                    ["record_count"] = batch.Count,
                    ["records"] = batch,
                };
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                await EventWriter.AppendAsync(db, draftBase with
                {
                    DedupeKey = $"batch:{batchIndex}",
                    Type = InboxEventTypes.RecordsBatch,
                    Data = data,
                    RecordCount = batch.Count,
                }, destination, clock.GetUtcNow(), ct);
                if (pendingErrors.Count > 0)
                {
                    db.ValidationErrors.AddRange(pendingErrors);
                    await db.SaveChangesAsync(ct);
                    pendingErrors.Clear();
                }

                await tx.CommitAsync(ct);
                db.ChangeTracker.Clear();
                batch = new JsonArray();
                batchBytes = 0;
                batchIndex++;
            }

            while (await csv.ReadAsync(ct) is { } record)
            {
                total++;
                // Row numbers are 1-based data rows (the header is row 0) so they match what users see in a spreadsheet minus the header.
                var rowNumber = record.RecordNumber - 1;
                var obj = validator.Validate(record.Fields, errors);
                if (obj is null)
                {
                    invalid++;
                    foreach (var e in errors)
                    {
                        if (storedErrors >= options.Processing.MaxStoredValidationErrors)
                        {
                            break;
                        }

                        storedErrors++;
                        pendingErrors.Add(new ValidationErrorRecord
                        {
                            OrganizationId = file.OrganizationId,
                            FileId = file.Id,
                            RowNumber = rowNumber,
                            ColumnName = e.Column,
                            Code = e.Code,
                            Message = e.Message,
                        });
                    }

                    continue;
                }

                valid++;
                var recordBytes = Encoding.UTF8.GetByteCount(obj.ToJsonString()) + 1;
                // Envelope + batch metadata overhead is well under 2 KiB.
                if (batch.Count > 0 && (batch.Count >= options.Processing.BatchMaxRecords || batchBytes + recordBytes > options.Processing.MaxEventBodyBytes - 2048))
                {
                    await FlushAsync();
                }

                if (batch.Count == 0)
                {
                    firstRow = rowNumber;
                }

                batch.Add(obj);
                batchBytes += recordBytes;
                lastRow = rowNumber;
            }

            await FlushAsync();
            if (pendingErrors.Count > 0)
            {
                db.ValidationErrors.AddRange(pendingErrors);
                await db.SaveChangesAsync(ct);
            }

            await CompleteAsync(file, env.Kind, destination, FileStatuses.Completed, null, null, total, valid, invalid, batchIndex, started.Elapsed, ct);
            activity?.SetTag("relaystack.records", total);
            logger.LogInformation("Processed file {FileId}: {Total} rows, {Valid} valid, {Invalid} invalid, {Batches} batches in {Ms} ms",
                file.Id, total, valid, invalid, batchIndex, started.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ToPermanent(ex) is { } permanent)
        {
            db.ChangeTracker.Clear();
            await CompleteAsync(file, env.Kind, destination, FileStatuses.Failed, permanent.Code, permanent.Message, total, valid, invalid, 0, started.Elapsed, ct);
            logger.LogWarning("File {FileId} failed permanently: {Code}", file.Id, permanent.Code);
        }
    }

    private static PermanentProcessingException? ToPermanent(Exception ex) => ex switch
    {
        PermanentProcessingException p => p,
        CsvFormatException c => new PermanentProcessingException(c.Code, c.Message),
        SchemaMismatchException s => new PermanentProcessingException(s.Code, s.Message),
        DecoderFallbackException => new PermanentProcessingException("invalid_encoding", "File is not valid UTF-8."),
        FileNotFoundException or DirectoryNotFoundException => new PermanentProcessingException("object_missing", "Stored file content is missing."),
        _ => null,
    };

    private async Task CompleteAsync(InboxFile file, string envKind, DestinationTarget? destination, string status, string? errorCode, string? errorMessage,
        long total, long valid, long invalid, int batches, TimeSpan elapsed, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var data = new JsonObject
        {
            ["inbox_id"] = file.InboxId.ToString(),
            ["file_id"] = file.Id.ToString(),
            ["file_name"] = file.OriginalName,
            ["status"] = status,
            ["total_rows"] = total,
            ["valid_rows"] = valid,
            ["invalid_rows"] = invalid,
            ["batch_count"] = batches,
            ["error_code"] = errorCode,
            ["error_message"] = errorMessage,
        };
        await EventWriter.AppendAsync(db, new EventDraft(file.OrganizationId, file.ProjectId, file.EnvironmentId, envKind, EventSourceKinds.InboxFile,
            file.Id, "completed", status == FileStatuses.Completed ? InboxEventTypes.FileProcessed : InboxEventTypes.FileFailed, data, 0, file.ExpiresAt),
            destination, now, ct);

        await db.Files.Where(f => f.Id == file.Id && f.OrganizationId == file.OrganizationId).ExecuteUpdateAsync(s => s
            .SetProperty(f => f.Status, status)
            .SetProperty(f => f.TotalRows, total)
            .SetProperty(f => f.ValidRows, valid)
            .SetProperty(f => f.InvalidRows, invalid)
            .SetProperty(f => f.ErrorCode, errorCode)
            .SetProperty(f => f.ErrorMessage, errorMessage)
            .SetProperty(f => f.CompletedAt, now), ct);

        db.UsageRecords.Add(Usage.Record(file.OrganizationId, file.ProjectId, file.EnvironmentId, UsageMetrics.RecordsProcessed, total, file.Id, now));
        db.UsageRecords.Add(Usage.Record(file.OrganizationId, file.ProjectId, file.EnvironmentId, UsageMetrics.RecordsInvalid, invalid, file.Id, now));
        db.UsageRecords.Add(Usage.Record(file.OrganizationId, file.ProjectId, file.EnvironmentId, UsageMetrics.ProcessingMilliseconds, (long)elapsed.TotalMilliseconds, file.Id, now));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }
}
