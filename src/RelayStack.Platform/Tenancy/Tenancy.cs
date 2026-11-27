namespace RelayStack.Platform.Tenancy;

public sealed class Organization
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class Project
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public required string Name { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public static class EnvironmentKinds
{
    public const string Development = "development";
    public const string Production = "production";

    public static bool IsValid(string kind) => kind is Development or Production;
}

/// <summary>A development or production environment inside a project. Every resource is environment-scoped.</summary>
public sealed class ProjectEnvironment
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid ProjectId { get; set; }
    public required string Kind { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// The authenticated tenant for the current request. Populated once by API key authentication and never
/// changed afterwards. Everything tenant-owned is read and written through this scope.
/// </summary>
public sealed class TenantContext
{
    private TenantScope? _scope;

    public bool IsSet => _scope is not null;

    public TenantScope Scope => _scope ?? throw new InvalidOperationException("Tenant context has not been established.");

    public Guid? OrganizationIdOrNull => _scope?.OrganizationId;

    public void Set(TenantScope scope)
    {
        if (_scope is not null && _scope != scope)
        {
            throw new InvalidOperationException("Tenant context cannot be changed once established.");
        }

        _scope = scope;
    }
}

public sealed record TenantScope(Guid OrganizationId, Guid ProjectId, Guid EnvironmentId, string EnvironmentKind, Guid ApiKeyId);
