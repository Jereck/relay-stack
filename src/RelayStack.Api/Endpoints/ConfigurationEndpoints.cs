using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RelayStack.Api.Auth;
using RelayStack.Platform;
using RelayStack.Platform.Persistence;
using RelayStack.Platform.Processing;
using RelayStack.Platform.Security;
using RelayStack.Platform.Tenancy;

namespace RelayStack.Api.Endpoints;

internal static class ConfigurationEndpoints
{
    public static void MapConfigurationEndpoints(this RouteGroupBuilder v1)
    {
        v1.MapGet("/project", GetProject).WithName("GetProject").WithSummary("Describe the organization, project and environment of the API key.");

        var destinations = v1.MapGroup("/destinations").WithTags("Destinations");
        destinations.MapPost("", CreateDestination).RequireAuthorization(ApiKeyDefaults.Policy(Scopes.DestinationsWrite))
            .WithName("CreateDestination").WithSummary("Create a webhook destination. The signing secret is returned only once.");
        destinations.MapGet("", ListDestinations).RequireAuthorization(ApiKeyDefaults.Policy(Scopes.DestinationsRead)).WithName("ListDestinations");
        destinations.MapGet("/{id:guid}", GetDestination).RequireAuthorization(ApiKeyDefaults.Policy(Scopes.DestinationsRead)).WithName("GetDestination");

        var inboxes = v1.MapGroup("/inboxes").WithTags("Inboxes");
        inboxes.MapPost("", CreateInbox).RequireAuthorization(ApiKeyDefaults.Policy(Scopes.InboxesWrite))
            .WithName("CreateInbox").WithSummary("Create an inbox with an explicit CSV validation schema.");
        inboxes.MapGet("", ListInboxes).RequireAuthorization(ApiKeyDefaults.Policy(Scopes.InboxesRead)).WithName("ListInboxes");
        inboxes.MapGet("/{id:guid}", GetInbox).RequireAuthorization(ApiKeyDefaults.Policy(Scopes.InboxesRead)).WithName("GetInbox");
    }

    private static async Task<IResult> GetProject(RelayStackDbContext db, TenantContext tenant, HttpContext http, CancellationToken ct)
    {
        var s = tenant.Scope;
        var org = await db.Organizations.SingleAsync(o => o.Id == s.OrganizationId, ct);
        var project = await db.Projects.SingleAsync(p => p.Id == s.ProjectId, ct);
        var scopes = http.User.FindAll(ApiKeyDefaults.ScopeClaim).Select(c => c.Value).ToList();
        return Results.Ok(new ProjectResponse(new IdName(org.Id, org.Name), new IdName(project.Id, project.Name),
            new EnvironmentInfo(s.EnvironmentId, s.EnvironmentKind), new ApiKeyInfo(s.ApiKeyId, scopes)));
    }

    private static async Task<IResult> CreateDestination(CreateDestinationRequest req, RelayStackDbContext db, TenantContext tenant, OutboundPolicy policy,
        SecretProtector secrets, PlatformOptions options, TimeProvider clock, HttpContext http, CancellationToken ct)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(req.Name) || req.Name.Length > 200)
        {
            errors.Add("name is required (max 200 characters).");
        }

        if (policy.ValidateDestinationUrl(req.Url, tenant.Scope.EnvironmentKind == EnvironmentKinds.Development) is { } urlError)
        {
            errors.Add(urlError);
        }

        if (req.TimeoutSeconds is < 1 or > 30)
        {
            errors.Add("timeout_seconds must be between 1 and 30.");
        }

        if (req.MaxAttempts is < 1 or > 20)
        {
            errors.Add("max_attempts must be between 1 and 20.");
        }

        if (errors.Count > 0)
        {
            return ApiErrors.Validation([.. errors]);
        }

        var now = clock.GetUtcNow();
        var s = tenant.Scope;
        var secret = WebhookSigner.GenerateSecret();
        var destination = new WebhookDestination
        {
            Id = Guid.CreateVersion7(now),
            OrganizationId = s.OrganizationId,
            ProjectId = s.ProjectId,
            EnvironmentId = s.EnvironmentId,
            Name = req.Name!.Trim(),
            Url = req.Url!,
            SigningSecretCiphertext = [],
            TimeoutSeconds = req.TimeoutSeconds ?? options.Delivery.DefaultTimeoutSeconds,
            MaxAttempts = req.MaxAttempts ?? options.Delivery.DefaultMaxAttempts,
            CreatedAt = now,
            UpdatedAt = now,
        };
        destination.SigningSecretCiphertext = secrets.Protect(secret, SecretPurposes.WebhookSigningSecret, s.OrganizationId);
        db.Destinations.Add(destination);
        Audit.Write(db, tenant, http, "destination.created", "destination", destination.Id.ToString(), new { url = Redaction.Url(destination.Url) }, now);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/v1/destinations/{destination.Id}", DestinationResponse.From(destination, secret));
    }

    private static async Task<IResult> ListDestinations(RelayStackDbContext db, int? limit, [FromQuery(Name = "starting_after")] Guid? startingAfter, CancellationToken ct)
    {
        var n = Paging.Clamp(limit);
        var q = db.Destinations.AsNoTracking();
        if (startingAfter is { } after)
        {
            q = q.Where(d => d.Id.CompareTo(after) < 0);
        }

        var rows = await q.OrderByDescending(d => d.Id).Take(n + 1).ToListAsync(ct);
        return Results.Ok(Paging.Page(rows, n, d => d.Id, d => DestinationResponse.From(d)));
    }

    private static async Task<IResult> GetDestination(Guid id, RelayStackDbContext db, CancellationToken ct) =>
        await db.Destinations.AsNoTracking().SingleOrDefaultAsync(d => d.Id == id, ct) is { } d
            ? Results.Ok(DestinationResponse.From(d))
            : ApiErrors.NotFound("Destination");

    private static async Task<IResult> CreateInbox(CreateInboxRequest req, RelayStackDbContext db, TenantContext tenant, PlatformOptions options,
        TimeProvider clock, HttpContext http, CancellationToken ct)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(req.Name) || req.Name.Length > 200)
        {
            errors.Add("name is required (max 200 characters).");
        }

        if (req.Schema is null)
        {
            errors.Add("schema is required.");
        }
        else
        {
            errors.AddRange(req.Schema.Validate());
        }

        var maxBytes = req.MaxFileBytes ?? options.Limits.DefaultInboxMaxFileBytes;
        if (maxBytes < 1 || maxBytes > options.Limits.MaxUploadBytes)
        {
            errors.Add($"max_file_bytes must be between 1 and {options.Limits.MaxUploadBytes}.");
        }

        var retention = req.RetentionDays ?? options.Limits.DefaultRetentionDays;
        if (retention < 1 || retention > options.Limits.MaxRetentionDays)
        {
            errors.Add($"retention_days must be between 1 and {options.Limits.MaxRetentionDays}.");
        }

        // The destination must exist in this environment. RLS, the EF filter and a composite FK all enforce this.
        if (req.DestinationId is { } destId && !await db.Destinations.AnyAsync(d => d.Id == destId, ct))
        {
            errors.Add("destination_id does not refer to a destination in this environment.");
        }

        if (errors.Count > 0)
        {
            return ApiErrors.Validation([.. errors]);
        }

        var now = clock.GetUtcNow();
        var s = tenant.Scope;
        var inbox = new Inbox
        {
            Id = Guid.CreateVersion7(now),
            OrganizationId = s.OrganizationId,
            ProjectId = s.ProjectId,
            EnvironmentId = s.EnvironmentId,
            Name = req.Name!.Trim(),
            DestinationId = req.DestinationId,
            ValidationSchema = req.Schema!.ToJson(),
            MaxFileBytes = maxBytes,
            RetentionDays = retention,
            CreatedAt = now,
        };
        db.Inboxes.Add(inbox);
        Audit.Write(db, tenant, http, "inbox.created", "inbox", inbox.Id.ToString(), new { inbox.Name, columns = req.Schema.Columns.Count }, now);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/v1/inboxes/{inbox.Id}", InboxResponse.From(inbox));
    }

    private static async Task<IResult> ListInboxes(RelayStackDbContext db, int? limit, [FromQuery(Name = "starting_after")] Guid? startingAfter, CancellationToken ct)
    {
        var n = Paging.Clamp(limit);
        var q = db.Inboxes.AsNoTracking();
        if (startingAfter is { } after)
        {
            q = q.Where(i => i.Id.CompareTo(after) < 0);
        }

        var rows = await q.OrderByDescending(i => i.Id).Take(n + 1).ToListAsync(ct);
        return Results.Ok(Paging.Page(rows, n, i => i.Id, InboxResponse.From));
    }

    private static async Task<IResult> GetInbox(Guid id, RelayStackDbContext db, CancellationToken ct) =>
        await db.Inboxes.AsNoTracking().SingleOrDefaultAsync(i => i.Id == id, ct) is { } i
            ? Results.Ok(InboxResponse.From(i))
            : ApiErrors.NotFound("Inbox");
}
