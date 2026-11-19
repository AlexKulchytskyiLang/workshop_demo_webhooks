using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace InvoiceHub.BuildingBlocks.Data;

/// <summary>
/// Runs a hand-written statement on the DbContext's own connection. Used where EF's raw-SQL composition gets in the way:
/// <c>UPDATE ... OUTPUT</c> claims, <c>NEXT VALUE FOR</c>, lock hints. It still goes through the tenant session interceptor (the connection is opened
/// by the context), joins the context's current transaction, and is retried on transient errors when no transaction is open.
/// </summary>
public static class RawSql
{
    public static Task<List<T>> QueryAsync<T>(this DbContext db, string sql, Func<DbDataReader, T> map, CancellationToken ct, params (string Name, object? Value)[] parameters) =>
        RunAsync(db, async command =>
        {
            command.CommandText = sql;
            Bind(command, parameters);
            var rows = new List<T>();
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) rows.Add(map(reader));
            return rows;
        }, ct);

    public static async Task<T?> ScalarAsync<T>(this DbContext db, string sql, CancellationToken ct, params (string Name, object? Value)[] parameters)
    {
        var value = await RunAsync(db, async command =>
        {
            command.CommandText = sql;
            Bind(command, parameters);
            return await command.ExecuteScalarAsync(ct);
        }, ct);
        return value is null or DBNull ? default : (T)Convert.ChangeType(value, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Executes a statement and returns the number of rows it affected.</summary>
    public static Task<int> ExecuteAsync(this DbContext db, string sql, CancellationToken ct, params (string Name, object? Value)[] parameters) =>
        RunAsync(db, async command =>
        {
            command.CommandText = sql;
            Bind(command, parameters);
            return await command.ExecuteNonQueryAsync(ct);
        }, ct);

    private static async Task<TResult> RunAsync<TResult>(DbContext db, Func<DbCommand, Task<TResult>> body, CancellationToken ct)
    {
        async Task<TResult> Once()
        {
            await db.Database.OpenConnectionAsync(ct); // first open runs the tenant interceptor; reference-counted by EF
            try
            {
                await using var command = db.Database.GetDbConnection().CreateCommand();
                command.CommandTimeout = 30;
                command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
                return await body(command);
            }
            finally
            {
                await db.Database.CloseConnectionAsync();
            }
        }

        // inside a transaction the caller's execution-strategy scope owns retries (nesting them is an error)
        return db.Database.CurrentTransaction is not null
            ? await Once()
            : await db.Database.CreateExecutionStrategy().ExecuteAsync(Once);
    }

    private static void Bind(DbCommand command, (string Name, object? Value)[] parameters)
    {
        foreach (var (name, value) in parameters)
        {
            var p = command.CreateParameter();
            p.ParameterName = name;
            p.Value = value ?? DBNull.Value;
            if (value is DateTime) p.DbType = DbType.DateTime2; // the default would be datetime (3 ms resolution, narrower range)
            command.Parameters.Add(p);
        }
    }
}
