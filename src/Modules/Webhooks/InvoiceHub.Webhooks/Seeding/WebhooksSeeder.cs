using System.Text;
using System.Text.Json;
using InvoiceHub.BuildingBlocks.Security;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace InvoiceHub.Webhooks.Seeding;

public sealed record SeedEndpoint(
    Guid Id, Guid TenantId, string Name, string Url, string? Description, string PayloadStyle, string RetryPreset, IReadOnlyList<string> Events, string Secret);

public sealed record WebhookSeedData(IReadOnlyList<SeedEndpoint> Endpoints)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static WebhookSeedData Load()
    {
        using var stream = typeof(WebhookSeedData).Assembly.GetManifestResourceStream("InvoiceHub.Webhooks.Seed.webhook-seed.json")
            ?? throw new InvalidOperationException("webhook-seed.json is not embedded.");
        return JsonSerializer.Deserialize<WebhookSeedData>(stream, Json)!;
    }
}

/// <summary>
/// Gives the demo tenants an endpoint that points at the local receiver, with a known signing secret (the receiver is configured with the same one from the
/// same file). Idempotent. Runs with the admin login, so it sets the tenant context itself (row-level security filters even the admin).
/// </summary>
public static class WebhooksSeeder
{
    /// <param name="receiverBaseUrl">Where the demo receiver really listens (the seed file says <c>{receiver}/hooks/...</c>), so no port is hard-coded.</param>
    public static async Task ApplyAsync(string adminConnectionString, ISecretProtector protector, string receiverBaseUrl, IReadOnlyDictionary<string, string> otherReceivers, ILogger logger, CancellationToken ct = default)
    {
        var seed = WebhookSeedData.Load();
        await using var connection = new SqlConnection(adminConnectionString);
        await connection.OpenAsync(ct);

        foreach (var e in seed.Endpoints)
        {
            await using var command = new SqlCommand(
                """
                EXEC sp_set_session_context @key = N'tenant_id', @value = @tenant, @read_only = 0;
                IF NOT EXISTS (SELECT 1 FROM webhooks.endpoint WHERE id = @id)
                BEGIN
                  INSERT webhooks.endpoint (id, tenant_id, name, url, description, status, payload_style, retry_preset, verified_at, created_by)
                  VALUES (@id, @tenant, @name, @url, @description, 'active', @style, @retry, SYSUTCDATETIME(), 'seed');
                  INSERT webhooks.endpoint_secret (id, tenant_id, endpoint_id, secret_enc, key_id) VALUES (@secretId, @tenant, @id, @secret, 'k_seed0001');
                  DECLARE @n int = 1;
                  WHILE @n <= 10 BEGIN INSERT webhooks.endpoint_slot (endpoint_id, slot_no, tenant_id) VALUES (@id, @n, @tenant); SET @n += 1; END;
                END
                """, connection);
            command.Parameters.AddWithValue("@tenant", e.TenantId);
            command.Parameters.AddWithValue("@id", e.Id);
            command.Parameters.AddWithValue("@name", e.Name);
            var url = e.Url.Replace("{receiver}", receiverBaseUrl.TrimEnd('/'), StringComparison.Ordinal);
            foreach (var (key, address) in otherReceivers) url = url.Replace($"{{receiver:{key}}}", address.TrimEnd('/'), StringComparison.Ordinal); // e.g. the second receiver
            command.Parameters.AddWithValue("@url", url);
            command.Parameters.AddWithValue("@description", (object?)e.Description ?? DBNull.Value);
            command.Parameters.AddWithValue("@style", e.PayloadStyle);
            command.Parameters.AddWithValue("@retry", e.RetryPreset);
            command.Parameters.AddWithValue("@secretId", Guid.CreateVersion7());
            command.Parameters.Add("@secret", System.Data.SqlDbType.VarBinary, 256).Value = protector.Protect(Encoding.UTF8.GetBytes(e.Secret), $"endpoint:{e.Id:N}");
            await command.ExecuteNonQueryAsync(ct);

            foreach (var type in e.Events)
            {
                await using var sub = new SqlCommand(
                    """
                    EXEC sp_set_session_context @key = N'tenant_id', @value = @tenant, @read_only = 0;
                    IF NOT EXISTS (SELECT 1 FROM webhooks.subscription WHERE endpoint_id = @id AND event_type = @type)
                      INSERT webhooks.subscription (tenant_id, endpoint_id, event_type) VALUES (@tenant, @id, @type);
                    """, connection);
                sub.Parameters.AddWithValue("@tenant", e.TenantId);
                sub.Parameters.AddWithValue("@id", e.Id);
                sub.Parameters.AddWithValue("@type", type);
                await sub.ExecuteNonQueryAsync(ct);
            }
        }

        logger.LogInformation("Seeded {Count} demo webhook endpoints", seed.Endpoints.Count);
    }
}
