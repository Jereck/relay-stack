using Microsoft.EntityFrameworkCore;
using RelayStack.Platform.Persistence;
using RelayStack.Platform.Security;

namespace RelayStack.Platform.Tenancy;

public sealed record ProvisionedTenant(Guid OrganizationId, Guid ProjectId, Guid DevelopmentEnvironmentId, Guid ProductionEnvironmentId);

public sealed record CreatedApiKey(Guid Id, string Key, string[] Scopes, string EnvironmentKind);

/// <summary>
/// Operator-only provisioning used by the bootstrap CLI and tests. Runs with the schema-owner connection
/// because there is no self-service sign-up in Phase 0 (ADR-009).
/// </summary>
public static class Provisioning
{
    public static async Task<ProvisionedTenant> CreateOrganizationAsync(RelayStackDbContext db, string organizationName, string projectName,
        DateTimeOffset now, CancellationToken ct)
    {
        var org = new Organization { Id = Guid.CreateVersion7(now), Name = organizationName, CreatedAt = now };
        var project = new Project { Id = Guid.CreateVersion7(now), OrganizationId = org.Id, Name = projectName, CreatedAt = now };
        var dev = new ProjectEnvironment { Id = Guid.CreateVersion7(now), OrganizationId = org.Id, ProjectId = project.Id, Kind = EnvironmentKinds.Development, CreatedAt = now };
        var prod = new ProjectEnvironment { Id = Guid.CreateVersion7(now), OrganizationId = org.Id, ProjectId = project.Id, Kind = EnvironmentKinds.Production, CreatedAt = now };
        // No navigation properties are mapped, so EF cannot order inserts by foreign key: save parent rows first.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.Organizations.Add(org);
        await db.SaveChangesAsync(ct);
        db.Projects.Add(project);
        await db.SaveChangesAsync(ct);
        db.Environments.AddRange(dev, prod);
        db.AuditLog.Add(new AuditEntry
        {
            OrganizationId = org.Id, ActorType = "operator", ActorId = "bootstrap", Action = "organization.created",
            TargetType = "organization", TargetId = org.Id.ToString(), OccurredAt = now,
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new ProvisionedTenant(org.Id, project.Id, dev.Id, prod.Id);
    }

    public static async Task<CreatedApiKey> CreateApiKeyAsync(RelayStackDbContext db, ApiKeyHasher hasher, Guid organizationId, Guid projectId,
        string environmentKind, string name, IReadOnlyCollection<string> scopes, DateTimeOffset now, CancellationToken ct)
    {
        var invalid = scopes.Where(s => !Scopes.IsValid(s)).ToList();
        if (invalid.Count > 0 || scopes.Count == 0)
        {
            throw new ArgumentException($"Invalid scopes: {string.Join(", ", invalid)}. Allowed: {string.Join(", ", Scopes.All)}.");
        }

        var env = await db.Environments.SingleAsync(e => e.OrganizationId == organizationId && e.ProjectId == projectId && e.Kind == environmentKind, ct);
        var generated = hasher.Generate(environmentKind);
        var key = new ApiKey
        {
            Id = Guid.CreateVersion7(now),
            OrganizationId = organizationId,
            ProjectId = projectId,
            EnvironmentId = env.Id,
            Name = name,
            Prefix = generated.Prefix,
            SecretHash = generated.SecretHash,
            Scopes = scopes.Distinct().Order().ToArray(),
            CreatedAt = now,
        };
        db.ApiKeys.Add(key);
        db.AuditLog.Add(new AuditEntry
        {
            OrganizationId = organizationId, ActorType = "operator", ActorId = "bootstrap", Action = "api_key.created",
            TargetType = "api_key", TargetId = key.Id.ToString(), OccurredAt = now,
            Details = System.Text.Json.JsonSerializer.Serialize(new { name, scopes = key.Scopes, environment = environmentKind, prefix = key.Prefix }),
        });
        await db.SaveChangesAsync(ct);
        return new CreatedApiKey(key.Id, generated.FullKey, key.Scopes, environmentKind);
    }
}
