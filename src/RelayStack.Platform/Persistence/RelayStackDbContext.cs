using System.Data.Common;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using RelayStack.Platform.Tenancy;

namespace RelayStack.Platform.Persistence;

/// <summary>How a DbContext instance is allowed to see tenant data.</summary>
public enum DatabaseAccessMode
{
    /// <summary>API requests. Rows are limited to the authenticated organization by EF filters AND Postgres RLS.</summary>
    Tenant,

    /// <summary>
    /// Background worker (relaystack_worker role). Each context is scoped to one organization with
    /// <see cref="RelayStackDbContext.ScopeToOrganization"/>; EF filters AND Postgres RLS limit it to that organization.
    /// An unscoped worker context sees no tenant rows and can only call the SECURITY DEFINER claim functions (ADR-014).
    /// </summary>
    Worker,

    /// <summary>
    /// Operator tooling connected as the owner role (migrations, provisioning CLI, tests). Sees all tenants.
    /// Never used by the API or worker hosts.
    /// </summary>
    Operator,
}

public sealed class RelayStackDbContext : DbContext
{
    private readonly TenantContext _tenant;

    public RelayStackDbContext(DbContextOptions<RelayStackDbContext> options, TenantContext tenant, DatabaseAccessMode mode)
        : base(options)
    {
        _tenant = tenant;
        Mode = mode;
    }

    public DatabaseAccessMode Mode { get; }

    private Guid? _workerOrganizationId;

    /// <summary>Organization used for EF query filters and the Postgres RLS session setting. Null means "no rows".</summary>
    public Guid? CurrentOrganizationId => Mode == DatabaseAccessMode.Worker ? _workerOrganizationId : _tenant.OrganizationIdOrNull;

    /// <summary>Environment of the authenticated API key. Environment-scoped resources are filtered by it as well.</summary>
    public Guid? CurrentEnvironmentId => _tenant.IsSet ? _tenant.Scope.EnvironmentId : null;

    /// <summary>Operator contexts see every tenant. Worker and tenant contexts never do.</summary>
    private bool AllTenants => Mode == DatabaseAccessMode.Operator;

    /// <summary>The worker acts for an organization across its environments; API requests are bound to one environment.</summary>
    private bool AllEnvironments => Mode != DatabaseAccessMode.Tenant;

    /// <summary>
    /// Binds a worker context to the organization that owns the item being processed. Must be called before the
    /// context first opens a connection, and cannot be changed afterwards.
    /// </summary>
    public void ScopeToOrganization(Guid organizationId)
    {
        if (Mode != DatabaseAccessMode.Worker)
        {
            throw new InvalidOperationException("Only worker contexts are scoped explicitly; API contexts take the authenticated tenant.");
        }

        if (_workerOrganizationId is { } existing)
        {
            if (existing != organizationId)
            {
                throw new InvalidOperationException("A worker context cannot be re-scoped to a different organization.");
            }

            return;
        }

        if (Database.GetDbConnection().State != System.Data.ConnectionState.Closed)
        {
            throw new InvalidOperationException("Scope the worker context before it opens a connection.");
        }

        _workerOrganizationId = organizationId;
    }

    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<ProjectEnvironment> Environments => Set<ProjectEnvironment>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<WebhookDestination> Destinations => Set<WebhookDestination>();
    public DbSet<Inbox> Inboxes => Set<Inbox>();
    public DbSet<InboxFile> Files => Set<InboxFile>();
    public DbSet<ValidationErrorRecord> ValidationErrors => Set<ValidationErrorRecord>();
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<EventRecord> Events => Set<EventRecord>();
    public DbSet<Delivery> Deliveries => Set<Delivery>();
    public DbSet<DeliveryAttempt> DeliveryAttempts => Set<DeliveryAttempt>();
    public DbSet<UsageRecord> UsageRecords => Set<UsageRecord>();
    public DbSet<AuditEntry> AuditLog => Set<AuditEntry>();
    public DbSet<Polling.PollSource> PollSources => Set<Polling.PollSource>();
    public DbSet<Polling.PollObservedRecord> PollObservedRecords => Set<Polling.PollObservedRecord>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Organization>().ToTable("organizations").HasQueryFilter(e => AllTenants || e.Id == CurrentOrganizationId);
        b.Entity<Project>().ToTable("projects").HasQueryFilter(e => AllTenants || e.OrganizationId == CurrentOrganizationId);
        b.Entity<ProjectEnvironment>().ToTable("environments").HasQueryFilter(e => AllTenants || e.OrganizationId == CurrentOrganizationId);
        b.Entity<ApiKey>().ToTable("api_keys").HasQueryFilter(e => AllTenants || e.OrganizationId == CurrentOrganizationId);
        b.Entity<WebhookDestination>().ToTable("webhook_destinations").HasQueryFilter(e => AllTenants || (e.OrganizationId == CurrentOrganizationId && (AllEnvironments || e.EnvironmentId == CurrentEnvironmentId)));
        b.Entity<Inbox>(e =>
        {
            e.ToTable("inboxes").HasQueryFilter(x => AllTenants || (x.OrganizationId == CurrentOrganizationId && (AllEnvironments || x.EnvironmentId == CurrentEnvironmentId)));
            e.Property(x => x.ValidationSchema).HasColumnType("jsonb");
        });
        b.Entity<InboxFile>().ToTable("files").HasQueryFilter(e => AllTenants || (e.OrganizationId == CurrentOrganizationId && (AllEnvironments || e.EnvironmentId == CurrentEnvironmentId)));
        b.Entity<ValidationErrorRecord>(e =>
        {
            e.ToTable("validation_errors").HasQueryFilter(x => AllTenants || x.OrganizationId == CurrentOrganizationId);
            e.Property(x => x.Id).UseIdentityAlwaysColumn();
        });
        b.Entity<Job>().ToTable("jobs").HasQueryFilter(e => AllTenants || e.OrganizationId == CurrentOrganizationId);
        b.Entity<EventRecord>().ToTable("events").HasQueryFilter(e => AllTenants || (e.OrganizationId == CurrentOrganizationId && (AllEnvironments || e.EnvironmentId == CurrentEnvironmentId)));
        b.Entity<Delivery>().ToTable("deliveries").HasQueryFilter(e => AllTenants || e.OrganizationId == CurrentOrganizationId);
        b.Entity<DeliveryAttempt>().ToTable("delivery_attempts").HasQueryFilter(e => AllTenants || e.OrganizationId == CurrentOrganizationId);
        b.Entity<UsageRecord>(e =>
        {
            e.ToTable("usage_records").HasQueryFilter(x => AllTenants || (x.OrganizationId == CurrentOrganizationId && (AllEnvironments || x.EnvironmentId == CurrentEnvironmentId)));
            e.Property(x => x.Id).UseIdentityAlwaysColumn();
        });
        b.Entity<AuditEntry>(e =>
        {
            e.ToTable("audit_log").HasQueryFilter(x => AllTenants || x.OrganizationId == CurrentOrganizationId);
            e.Property(x => x.Id).UseIdentityAlwaysColumn();
            e.Property(x => x.Details).HasColumnType("jsonb");
        });
        b.Entity<Polling.PollSource>().ToTable("poll_sources").HasQueryFilter(e => AllTenants || (e.OrganizationId == CurrentOrganizationId && (AllEnvironments || e.EnvironmentId == CurrentEnvironmentId)));
        b.Entity<Polling.PollObservedRecord>(e =>
        {
            e.ToTable("poll_observed_records").HasQueryFilter(x => AllTenants || x.OrganizationId == CurrentOrganizationId);
            e.HasKey(x => new { x.SourceId, x.RecordKey });
        });

        // All tables use snake_case columns; map explicitly instead of adding a naming-convention package.
        foreach (var entity in b.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
            {
                property.SetColumnName(ToSnakeCase(property.Name));
            }
        }
    }

    internal static string ToSnakeCase(string name)
    {
        var sb = new StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                if (i > 0 && (char.IsLower(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1]) && char.IsUpper(name[i - 1]))))
                {
                    sb.Append('_');
                }

                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }
}

/// <summary>
/// Sets the Postgres session variable used by row-level security every time EF opens a connection, for API
/// (tenant) and worker contexts. The value is always written (empty when no organization is known, which matches
/// no rows) so a pooled connection can never carry a previous unit of work's tenant. Npgsql also resets session
/// state when connections return to the pool. Operator contexts connect as the table owner and are not subject to RLS.
/// </summary>
public sealed class TenantConnectionInterceptor : DbConnectionInterceptor
{
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        if (eventData.Context is RelayStackDbContext { Mode: DatabaseAccessMode.Tenant or DatabaseAccessMode.Worker } ctx)
        {
            using var cmd = CreateCommand(connection, ctx.CurrentOrganizationId);
            cmd.ExecuteNonQuery();
        }
    }

    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is RelayStackDbContext { Mode: DatabaseAccessMode.Tenant or DatabaseAccessMode.Worker } ctx)
        {
            await using var cmd = CreateCommand(connection, ctx.CurrentOrganizationId);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static DbCommand CreateCommand(DbConnection connection, Guid? organizationId)
    {
        var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT set_config('relaystack.organization_id', @org, false)";
        var p = cmd.CreateParameter();
        p.ParameterName = "org";
        p.Value = organizationId?.ToString() ?? string.Empty;
        cmd.Parameters.Add(p);
        return cmd;
    }
}
