using Npgsql;

namespace RelayStack.Platform.Persistence;

/// <summary>
/// Creates worker DbContexts (ADR-014). Work is found with <see cref="ForClaims"/>, whose context sees no tenant
/// rows and is only used to call the SECURITY DEFINER claim functions. Each claimed item is then handled with
/// <see cref="For"/>, which binds the context, and therefore Postgres RLS, to the item's organization.
/// </summary>
public sealed class WorkerDbFactory(NpgsqlDataSource dataSource)
{
    /// <summary>A context for calling claim functions. Ordinary queries through it return no rows.</summary>
    public RelayStackDbContext ForClaims() => ServiceCollectionExtensions.CreateContext(dataSource, DatabaseAccessMode.Worker);

    /// <summary>A context limited to one organization's rows.</summary>
    public RelayStackDbContext For(Guid organizationId)
    {
        var db = ServiceCollectionExtensions.CreateContext(dataSource, DatabaseAccessMode.Worker);
        db.ScopeToOrganization(organizationId);
        return db;
    }
}
