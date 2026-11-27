using RelayStack.Platform;
using RelayStack.Platform.Persistence;
using RelayStack.Platform.Tenancy;

namespace RelayStack.Api;

internal static class ApiErrors
{
    public static IResult Problem(int status, string code, string detail, IDictionary<string, object?>? extra = null)
    {
        var ext = new Dictionary<string, object?> { ["code"] = code };
        if (extra is not null)
        {
            foreach (var (k, v) in extra)
            {
                ext[k] = v;
            }
        }

        return Results.Problem(statusCode: status, title: code.Replace('_', ' '), detail: detail, extensions: ext);
    }

    public static IResult NotFound(string resource) => Problem(404, "not_found", $"{resource} was not found.");

    public static IResult Validation(params string[] errors) =>
        Problem(400, "validation_failed", string.Join(" ", errors), new Dictionary<string, object?> { ["errors"] = errors });
}

internal static class Paging
{
    public const int DefaultLimit = 25;
    public const int MaxLimit = 100;

    public static int Clamp(int? limit) => Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);

    /// <summary>Lists are newest first. Ids are UUIDv7 so ordering by id is ordering by creation time.</summary>
    public static ListResponse<TOut> Page<TIn, TOut>(List<TIn> rowsPlusOne, int limit, Func<TIn, Guid> id, Func<TIn, TOut> map)
    {
        var hasMore = rowsPlusOne.Count > limit;
        var page = rowsPlusOne.Take(limit).ToList();
        return new ListResponse<TOut>(page.Select(map).ToList(), hasMore, hasMore ? id(page[^1]).ToString() : null);
    }
}

internal static class Audit
{
    public static void Write(RelayStackDbContext db, TenantContext tenant, HttpContext http, string action, string targetType, string targetId,
        object? details, DateTimeOffset now)
    {
        db.AuditLog.Add(new AuditEntry
        {
            OrganizationId = tenant.Scope.OrganizationId,
            ActorType = "api_key",
            ActorId = tenant.Scope.ApiKeyId.ToString(),
            Action = action,
            TargetType = targetType,
            TargetId = targetId,
            RemoteIp = http.Connection.RemoteIpAddress?.ToString(),
            Details = details is null ? null : System.Text.Json.JsonSerializer.Serialize(details),
            OccurredAt = now,
        });
    }
}
