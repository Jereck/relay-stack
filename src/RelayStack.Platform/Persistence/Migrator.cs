using System.Reflection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace RelayStack.Platform.Persistence;

/// <summary>
/// Applies the embedded, hand-written SQL migrations in order (see ADR-006). Each migration runs in its own
/// transaction under an advisory lock so concurrent starts cannot apply the same migration twice.
/// Must run with the schema-owner connection, never the API or worker role.
/// </summary>
public static class Migrator
{
    private const long AdvisoryLockKey = 0x52656C6179537461; // "RelaySta"

    public static IReadOnlyList<(string Version, string Sql)> LoadMigrations()
    {
        var asm = typeof(Migrator).Assembly;
        const string prefix = "RelayStack.Migrations.";
        return asm.GetManifestResourceNames()
            .Where(n => n.StartsWith(prefix, StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .Select(n =>
            {
                using var stream = asm.GetManifestResourceStream(n)!;
                using var reader = new StreamReader(stream);
                return (n[prefix.Length..^4], reader.ReadToEnd());
            })
            .ToList();
    }

    public static async Task<int> MigrateAsync(string ownerConnectionString, ILogger? logger = null, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(ownerConnectionString);
        await conn.OpenAsync(ct);

        await using (var lockCmd = new NpgsqlCommand("SELECT pg_advisory_lock(@k)", conn))
        {
            lockCmd.Parameters.AddWithValue("k", AdvisoryLockKey);
            await lockCmd.ExecuteNonQueryAsync(ct);
        }

        try
        {
            await using (var create = new NpgsqlCommand(
                "CREATE TABLE IF NOT EXISTS schema_migrations (version text PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now())", conn))
            {
                await create.ExecuteNonQueryAsync(ct);
            }

            var applied = new HashSet<string>(StringComparer.Ordinal);
            await using (var read = new NpgsqlCommand("SELECT version FROM schema_migrations", conn))
            await using (var r = await read.ExecuteReaderAsync(ct))
            {
                while (await r.ReadAsync(ct))
                {
                    applied.Add(r.GetString(0));
                }
            }

            var count = 0;
            foreach (var (version, sql) in LoadMigrations())
            {
                if (applied.Contains(version))
                {
                    continue;
                }

                await using var tx = await conn.BeginTransactionAsync(ct);
                await using (var cmd = new NpgsqlCommand(sql, conn, tx))
                {
                    await cmd.ExecuteNonQueryAsync(ct);
                }

                await using (var record = new NpgsqlCommand("INSERT INTO schema_migrations (version) VALUES (@v)", conn, tx))
                {
                    record.Parameters.AddWithValue("v", version);
                    await record.ExecuteNonQueryAsync(ct);
                }

                await tx.CommitAsync(ct);
                logger?.LogInformation("Applied migration {Version}", version);
                count++;
            }

            return count;
        }
        finally
        {
            await using var unlock = new NpgsqlCommand("SELECT pg_advisory_unlock(@k)", conn);
            unlock.Parameters.AddWithValue("k", AdvisoryLockKey);
            await unlock.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }
}
