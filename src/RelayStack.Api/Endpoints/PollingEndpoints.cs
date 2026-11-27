using Microsoft.EntityFrameworkCore;
using RelayStack.Api.Auth;
using RelayStack.Platform.Persistence;
using RelayStack.Platform.Polling;
using RelayStack.Platform.Security;
using RelayStack.Platform.Tenancy;

namespace RelayStack.Api.Endpoints;

/// <summary>Phase 0 polling experiment (Part 9): register one synthetic REST source. Not a general polling product.</summary>
internal static class PollingEndpoints
{
    public static void MapPollingEndpoints(this RouteGroupBuilder v1)
    {
        var g = v1.MapGroup("/poll-sources").WithTags("Polling (experiment)");
        g.MapPost("", Create).RequireAuthorization(ApiKeyDefaults.Policy(Scopes.PollingWrite)).WithName("CreatePollSource")
            .WithSummary("Experimental: poll a JSON array endpoint on a schedule and emit events for new or changed records.");
        g.MapGet("", List).RequireAuthorization(ApiKeyDefaults.Policy(Scopes.PollingRead)).WithName("ListPollSources");
        g.MapGet("/{id:guid}", Get).RequireAuthorization(ApiKeyDefaults.Policy(Scopes.PollingRead)).WithName("GetPollSource");
    }

    private static async Task<IResult> Create(CreatePollSourceRequest req, RelayStackDbContext db, TenantContext tenant, OutboundPolicy policy,
        TimeProvider clock, HttpContext http, CancellationToken ct)
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

        if (string.IsNullOrWhiteSpace(req.RecordIdField) || req.RecordIdField.Length > 100)
        {
            errors.Add("record_id_field is required (max 100 characters).");
        }

        if (req.IntervalSeconds is null or < 10 or > 86400)
        {
            errors.Add("interval_seconds must be between 10 and 86400.");
        }

        if (req.RetentionDays is < 1 or > 30)
        {
            errors.Add("retention_days must be between 1 and 30.");
        }

        if (req.DestinationId is not { } destId || !await db.Destinations.AnyAsync(d => d.Id == destId, ct))
        {
            errors.Add("destination_id must refer to a destination in this environment.");
        }

        if (errors.Count > 0)
        {
            return ApiErrors.Validation([.. errors]);
        }

        var now = clock.GetUtcNow();
        var s = tenant.Scope;
        var source = new PollSource
        {
            Id = Guid.CreateVersion7(now),
            OrganizationId = s.OrganizationId,
            ProjectId = s.ProjectId,
            EnvironmentId = s.EnvironmentId,
            Name = req.Name!.Trim(),
            Url = req.Url!,
            RecordIdField = req.RecordIdField!,
            IntervalSeconds = req.IntervalSeconds!.Value,
            DestinationId = req.DestinationId!.Value,
            RetentionDays = req.RetentionDays ?? 7,
            NextPollAt = now,
            CreatedAt = now,
        };
        db.PollSources.Add(source);
        Audit.Write(db, tenant, http, "poll_source.created", "poll_source", source.Id.ToString(), new { url = Redaction.Url(source.Url) }, now);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/v1/poll-sources/{source.Id}", PollSourceResponse.From(source));
    }

    private static async Task<IResult> List(RelayStackDbContext db, CancellationToken ct)
    {
        var rows = await db.PollSources.AsNoTracking().OrderByDescending(p => p.Id).Take(100).ToListAsync(ct);
        return Results.Ok(new ListResponse<PollSourceResponse>(rows.Select(PollSourceResponse.From).ToList(), false, null));
    }

    private static async Task<IResult> Get(Guid id, RelayStackDbContext db, CancellationToken ct) =>
        await db.PollSources.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id, ct) is { } p
            ? Results.Ok(PollSourceResponse.From(p))
            : ApiErrors.NotFound("Poll source");
}
