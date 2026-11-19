using System.Data;
using System.Data.Common;
using Microsoft.Data.SqlClient;

namespace InvoiceHub.Receiver.Api;

/// <summary>The receiver's own database (a tenant's ERP database in the story). Schema is created at startup; plain ADO.NET keeps it independent of the platform.</summary>
internal sealed class ReceiverDb(IConfiguration configuration, ILogger<ReceiverDb> logger)
{
    public const string ConnectionName = "receiverdb";

    private string ConnectionString => configuration.GetConnectionString(ConnectionName)
        ?? throw new InvalidOperationException($"Connection string '{ConnectionName}' is missing. Start the receiver through the AppHost.");

    public async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(ct);
        return connection;
    }

    public async Task EnsureSchemaAsync(CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var connection = await OpenAsync(ct);
                await using var command = new SqlCommand(Schema, connection);
                await command.ExecuteNonQueryAsync(ct);
                logger.LogInformation("Receiver database is ready");
                return;
            }
            catch (SqlException e) when (attempt < 90)
            {
                logger.LogInformation("Receiver database not ready yet (attempt {Attempt}/90): {Reason}", attempt, e.Message.ReplaceLineEndings(" "));
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
        }
    }

    public async Task<List<T>> QueryAsync<T>(string sql, Func<DbDataReader, T> map, CancellationToken ct, params (string, object?)[] parameters)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = Command(connection, sql, parameters);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<T>();
        while (await reader.ReadAsync(ct)) rows.Add(map(reader));
        return rows;
    }

    public async Task<int> ExecuteAsync(string sql, CancellationToken ct, params (string, object?)[] parameters)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = Command(connection, sql, parameters);
        return await command.ExecuteNonQueryAsync(ct);
    }

    public static SqlCommand Command(SqlConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = new SqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            var p = command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            if (value is DateTime) p.SqlDbType = SqlDbType.DateTime2;
        }
        return command;
    }

    public static bool IsDuplicateKey(SqlException e) => e.Number is 2601 or 2627;

    private const string Schema = """
        IF OBJECT_ID(N'dbo.inbox_message') IS NULL
        CREATE TABLE dbo.inbox_message (
          id               bigint IDENTITY(1, 1) NOT NULL CONSTRAINT pk_inbox PRIMARY KEY,
          webhook_id       varchar(100)   NOT NULL CONSTRAINT uq_inbox_webhook UNIQUE,   -- the idempotency key: one row per event, however often it is sent
          type             varchar(80)    NOT NULL,
          aggregate_id     varchar(64)    NULL,
          sequence         bigint         NULL,
          api_version      varchar(10)    NULL,
          is_test          bit            NOT NULL DEFAULT 0,
          received_at      datetime2      NOT NULL DEFAULT SYSUTCDATETIME(),
          body             nvarchar(max)  NOT NULL,
          status           varchar(12)    NOT NULL DEFAULT 'received',                    -- received | processed | ignored | dead
          outcome          varchar(40)    NULL,                                           -- applied | stale_ignored | unhandled_type | ...
          attempts         int            NOT NULL DEFAULT 0,
          processed_at     datetime2      NULL,
          error            nvarchar(400)  NULL,
          duplicate_count  int            NOT NULL DEFAULT 0,
          last_duplicate_at datetime2     NULL);

        IF COL_LENGTH(N'dbo.inbox_message', N'headers') IS NULL ALTER TABLE dbo.inbox_message ADD headers nvarchar(max) NULL;
        IF COL_LENGTH(N'dbo.inbox_message', N'trace_parent') IS NULL ALTER TABLE dbo.inbox_message ADD trace_parent varchar(70) NULL;
        IF COL_LENGTH(N'dbo.inbox_message', N'source') IS NULL ALTER TABLE dbo.inbox_message ADD source varchar(8) NOT NULL CONSTRAINT df_inbox_source DEFAULT 'push';   -- push | pull (recovered by reconciliation)

        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix_inbox_status' AND object_id = OBJECT_ID(N'dbo.inbox_message'))
          CREATE INDEX ix_inbox_status ON dbo.inbox_message (status, id);

        IF OBJECT_ID(N'dbo.request_log') IS NULL
        CREATE TABLE dbo.request_log (
          id          bigint IDENTITY(1, 1) NOT NULL CONSTRAINT pk_request_log PRIMARY KEY,
          at          datetime2      NOT NULL DEFAULT SYSUTCDATETIME(),
          webhook_id  varchar(100)   NULL,
          verdict     varchar(30)    NOT NULL,           -- accepted | duplicate | rejected_signature | rejected_timestamp | rejected_malformed | chaos_*
          http_status int            NOT NULL,
          detail      nvarchar(300)  NULL,
          duration_ms int            NOT NULL DEFAULT 0);

        IF OBJECT_ID(N'dbo.invoice_mirror') IS NULL
        CREATE TABLE dbo.invoice_mirror (
          invoice_id       varchar(64)    NOT NULL CONSTRAINT pk_invoice_mirror PRIMARY KEY,
          number           varchar(30)    NULL,
          customer         nvarchar(120)  NULL,
          amount           decimal(18, 2) NULL,
          currency         char(3)        NULL,
          status           varchar(12)    NOT NULL,
          sequence         bigint         NOT NULL,      -- the sequence guard: an event older than this is ignored
          updated_at       datetime2      NOT NULL DEFAULT SYSUTCDATETIME(),
          last_webhook_id  varchar(100)   NULL);

        IF OBJECT_ID(N'dbo.trust_config') IS NULL
        CREATE TABLE dbo.trust_config (
          id    int           NOT NULL CONSTRAINT pk_trust_config PRIMARY KEY,
          json  nvarchar(max) NOT NULL);

        IF OBJECT_ID(N'dbo.payment_log') IS NULL
        CREATE TABLE dbo.payment_log (
          payment_id   varchar(64)    NOT NULL CONSTRAINT pk_payment_log PRIMARY KEY,
          invoice_id   varchar(64)    NULL,
          amount       decimal(18, 2) NULL,
          currency     char(3)        NULL,
          kind         varchar(12)    NOT NULL,          -- completed | failed
          reason       nvarchar(120)  NULL,
          received_at  datetime2      NOT NULL DEFAULT SYSUTCDATETIME());

        IF OBJECT_ID(N'dbo.file_transfer') IS NULL
        CREATE TABLE dbo.file_transfer (
          id               bigint IDENTITY(1, 1) NOT NULL CONSTRAINT pk_file_transfer PRIMARY KEY,
          webhook_id       varchar(100)   NOT NULL,
          type             varchar(80)    NOT NULL,
          url_host         varchar(120)   NOT NULL,        -- where the file came from; the link itself (a credential) is never stored
          expected_sha256  varchar(64)    NULL,
          actual_sha256    varchar(64)    NULL,
          expected_size    bigint         NULL,
          actual_size      bigint         NULL,
          verdict          varchar(14)    NOT NULL,        -- verified | mismatch | link_expired
          error            nvarchar(200)  NULL,
          duration_ms      int            NOT NULL DEFAULT 0,
          fetched_at       datetime2      NOT NULL DEFAULT SYSUTCDATETIME());

        IF OBJECT_ID(N'dbo.kyc_result') IS NULL
        CREATE TABLE dbo.kyc_result (
          verification_id  varchar(64)    NOT NULL CONSTRAINT pk_kyc_result PRIMARY KEY,
          decision         varchar(10)    NOT NULL,
          risk_score       int            NOT NULL,
          subject_name     nvarchar(120)  NULL,            -- personal data, pulled with an API key: it lives in the tenant's own system, never in a webhook
          document_number  nvarchar(30)   NULL,
          country          char(2)        NULL,
          fetched_at       datetime2      NOT NULL DEFAULT SYSUTCDATETIME());

        IF OBJECT_ID(N'dbo.subscription_mirror') IS NULL
        CREATE TABLE dbo.subscription_mirror (
          subscription_id  varchar(64)    NOT NULL CONSTRAINT pk_subscription_mirror PRIMARY KEY,
          plan_name        varchar(40)    NULL,
          amount           decimal(18, 2) NULL,
          currency         char(3)        NULL,
          renewed_at       datetime2      NOT NULL DEFAULT SYSUTCDATETIME());
        """;
}
