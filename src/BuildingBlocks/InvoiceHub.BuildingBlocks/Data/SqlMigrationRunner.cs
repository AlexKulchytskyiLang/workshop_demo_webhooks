using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace InvoiceHub.BuildingBlocks.Data;

/// <summary>
/// SQL-first, idempotent, journalled migrations: each module ships numbered <c>.sql</c> files as embedded resources
/// (<c>Migrations/001_name.sql</c>). The runner applies the missing ones in order, one transaction per script, and refuses
/// to run if an already-applied script was edited (hash mismatch). SQL-first keeps schemas, grants and RLS policies reviewable.
/// </summary>
public sealed partial class SqlMigrationRunner(string adminConnectionString, ILogger<SqlMigrationRunner> logger)
{
    public async Task ApplyAsync(string module, Assembly assembly, CancellationToken ct = default)
    {
        var scripts = assembly.GetManifestResourceNames()
            .Where(n => n.Contains(".Migrations.", StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        await using var connection = new SqlConnection(adminConnectionString);
        await connection.OpenAsync(ct);
        await using var lockHandle = await AcquireLockAsync(connection, ct);
        await EnsureJournalAsync(connection, ct);

        foreach (var resource in scripts)
        {
            var name = resource[(resource.LastIndexOf(".Migrations.", StringComparison.Ordinal) + ".Migrations.".Length)..];
            await using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var sql = await reader.ReadToEndAsync(ct);
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sql.ReplaceLineEndings("\n"))));

            var applied = await GetAppliedHashAsync(connection, module, name, ct);
            if (applied is not null)
            {
                if (!string.Equals(applied, hash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Migration {module}/{name} was already applied but its content changed. Add a new script instead of editing an applied one.");
                continue;
            }

            logger.LogInformation("Applying migration {Module}/{Script}", module, name);
            await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(ct);
            foreach (var batch in SplitBatches(sql))
            {
                await using var command = new SqlCommand(batch, connection, tx) { CommandTimeout = 120 };
                await command.ExecuteNonQueryAsync(ct);
            }
            await using (var journal = new SqlCommand(
                "INSERT dbo.schema_journal (module, script, hash, applied_at) VALUES (@m, @s, @h, SYSUTCDATETIME())", connection, tx))
            {
                journal.Parameters.AddWithValue("@m", module);
                journal.Parameters.AddWithValue("@s", name);
                journal.Parameters.AddWithValue("@h", hash);
                await journal.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
        }
    }

    private static IEnumerable<string> SplitBatches(string sql) =>
        GoSeparator().Split(sql).Select(b => b.Trim()).Where(b => b.Length > 0);

    [GeneratedRegex(@"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex GoSeparator();

    private static async Task EnsureJournalAsync(SqlConnection connection, CancellationToken ct)
    {
        await using var command = new SqlCommand(
            """
            IF OBJECT_ID(N'dbo.schema_journal') IS NULL
              CREATE TABLE dbo.schema_journal (
                module     nvarchar(80)  NOT NULL,
                script     nvarchar(200) NOT NULL,
                hash       char(64)      NOT NULL,
                applied_at datetime2     NOT NULL,
                CONSTRAINT pk_schema_journal PRIMARY KEY (module, script));
            """, connection);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<string?> GetAppliedHashAsync(SqlConnection connection, string module, string script, CancellationToken ct)
    {
        await using var command = new SqlCommand("SELECT hash FROM dbo.schema_journal WHERE module = @m AND script = @s", connection);
        command.Parameters.AddWithValue("@m", module);
        command.Parameters.AddWithValue("@s", script);
        return await command.ExecuteScalarAsync(ct) as string;
    }

    /// <summary>Only one migrator may run at a time (session-scoped application lock).</summary>
    private static async Task<IAsyncDisposable> AcquireLockAsync(SqlConnection connection, CancellationToken ct)
    {
        await using var command = new SqlCommand(
            "DECLARE @r int; EXEC @r = sp_getapplock @Resource = N'invoicehub-migrations', @LockMode = N'Exclusive', @LockOwner = N'Session', @LockTimeout = 60000; SELECT @r;",
            connection);
        var result = (int)(await command.ExecuteScalarAsync(ct) ?? -999);
        if (result < 0) throw new InvalidOperationException("Could not acquire the migration lock within 60 seconds.");
        return new LockHandle(connection);
    }

    private sealed class LockHandle(SqlConnection connection) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await using var command = new SqlCommand("EXEC sp_releaseapplock @Resource = N'invoicehub-migrations', @LockOwner = N'Session';", connection);
            await command.ExecuteNonQueryAsync();
        }
    }
}
