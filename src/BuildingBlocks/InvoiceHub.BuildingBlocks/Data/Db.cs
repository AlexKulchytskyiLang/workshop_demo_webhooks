using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using InvoiceHub.BuildingBlocks.Tenancy;

namespace InvoiceHub.BuildingBlocks.Data;

/// <summary>Which SQL login a host uses. The API can never act across tenants; workers can, explicitly (Spike 0: RLS also filters sa).</summary>
public enum DbRole
{
    /// <summary>Migrator only: the container admin login, used to create schemas, logins and policies.</summary>
    Admin,
    /// <summary><c>api_user</c>: tenant context only.</summary>
    Api,
    /// <summary><c>worker_user</c>: tenant context or explicit system mode.</summary>
    Worker,
}

public sealed record DbRoleSelection(DbRole Role);

public static class Db
{
    public const string ConnectionName = "invoicehub";

    public static string ConnectionString(IConfiguration configuration, DbRole role, string applicationName)
    {
        var baseConnection = configuration.GetConnectionString(ConnectionName)
            ?? throw new InvalidOperationException($"Connection string '{ConnectionName}' is missing. Start the app through the AppHost.");
        var builder = new SqlConnectionStringBuilder(baseConnection)
        {
            ApplicationName = applicationName,
            ConnectTimeout = 15,
            MaxPoolSize = 100,
        };
        if (role != DbRole.Admin)
        {
            var user = configuration[$"Db:{role}:User"];
            var password = configuration[$"Db:{role}:Password"];
            if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(password))
                throw new InvalidOperationException($"Db:{role}:User and Db:{role}:Password are required for the {role} role.");
            builder.UserID = user;
            builder.Password = password;
        }
        return builder.ConnectionString;
    }

    public static IServiceCollection AddDbRole(this IServiceCollection services, DbRole role)
    {
        services.AddSingleton(new DbRoleSelection(role));
        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
        services.AddScoped<ITenantScope>(sp => sp.GetRequiredService<TenantContext>());
        services.AddScoped<TenantSessionInterceptor>();
        return services;
    }

    /// <summary>Registers a module DbContext with retry on transient SQL errors and the tenant session interceptor.</summary>
    public static IServiceCollection AddModuleDbContext<TContext>(this IServiceCollection services, IConfiguration configuration, string applicationName)
        where TContext : DbContext
        => services.AddDbContext<TContext>((sp, options) =>
        {
            var role = sp.GetRequiredService<DbRoleSelection>().Role;
            options.UseSqlServer(
                ConnectionString(configuration, role, applicationName),
                sql => sql.EnableRetryOnFailure(maxRetryCount: 5, maxRetryDelay: TimeSpan.FromSeconds(10), errorNumbersToAdd: null)
                          .CommandTimeout(30));
            options.AddInterceptors(sp.GetRequiredService<TenantSessionInterceptor>());
        });
}

/// <summary>
/// Copies the tenant context into <c>SESSION_CONTEXT</c> every time a connection is opened for a DbContext.
/// Spike 0 proved the pooled connection is reset between uses, so nothing leaks across requests.
/// </summary>
public sealed class TenantSessionInterceptor(ITenantContext tenant) : DbConnectionInterceptor
{
    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        await using var command = Build(connection);
        if (command is not null) await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var command = Build(connection);
        command?.ExecuteNonQuery();
    }

    private DbCommand? Build(DbConnection connection)
    {
        if (tenant.TenantId is null && !tenant.IsSystem) return null;
        var command = connection.CreateCommand();
        command.CommandText = tenant.IsSystem
            ? "EXEC sp_set_session_context @key = N'system', @value = 1, @read_only = 0;"
            : "EXEC sp_set_session_context @key = N'tenant_id', @value = @tenant, @read_only = 0;";
        if (!tenant.IsSystem)
        {
            var p = command.CreateParameter();
            p.ParameterName = "@tenant";
            p.Value = tenant.TenantId!.Value;
            command.Parameters.Add(p);
        }
        return command;
    }
}
