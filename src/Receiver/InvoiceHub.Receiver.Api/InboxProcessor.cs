using System.Globalization;
using System.Text.Json;

namespace InvoiceHub.Receiver.Api;

/// <summary>
/// Works through the inbox after the HTTP answer has already gone out (receive fast, process later). Each event is applied by type; for invoices the
/// SEQUENCE GUARD makes reordering harmless: an event is applied only if it is newer than what we hold, so "paid" arriving before "generated" still ends
/// with the correct final state (ordering is not guaranteed by the platform, by design). A failing event is retried a few times, then parked as dead,
/// never allowed to block the ones behind it.
/// </summary>
internal sealed class InboxProcessor(ReceiverDb db, InboxSignal signal, FileTransfers files,
                                     KycPuller kyc, ILogger<InboxProcessor> logger) : BackgroundService
{
    private const int MaxAttempts = 5;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var handled = await ProcessBatchAsync(stoppingToken);
                if (handled == 0)
                    await signal.WaitAsync(TimeSpan.FromSeconds(1), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "Inbox processing failed; retrying shortly");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    internal async Task<int> ProcessBatchAsync(CancellationToken ct)
    {
        var rows = await db.QueryAsync("SELECT TOP (50) id, webhook_id, type, body, attempts FROM dbo.inbox_message WHERE status = 'received' ORDER BY id",
                            r => (Id: r.GetInt64(0), WebhookId: r.GetString(1), Type: r.GetString(2), Body: r.GetString(3), Attempts: r.GetInt32(4)), ct);

        foreach (var row in rows)
        {
            try
            {
                var outcome = await ApplyAsync(row.Type, row.Body, row.WebhookId, ct);
                var status = outcome is "unhandled_type" or "test_event" ? "ignored" : "processed";
                await db.ExecuteAsync("UPDATE dbo.inbox_message SET status = @status, outcome = @outcome, attempts = attempts + 1, processed_at = SYSUTCDATETIME(), error = NULL WHERE id = @id", ct,
                    ("@status", status), ("@outcome", outcome), ("@id", row.Id));
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                var dead = row.Attempts + 1 >= MaxAttempts;
                logger.LogWarning(e, "Processing {WebhookId} failed (attempt {Attempt}){Dead}", row.WebhookId, row.Attempts + 1, dead ? "; parked as dead" : "");
                await db.ExecuteAsync("UPDATE dbo.inbox_message SET attempts = attempts + 1, error = @error, status = CASE WHEN @dead = 1 THEN 'dead' ELSE status END WHERE id = @id", ct,
                    ("@error", e.Message.Length > 400 ? e.Message[..400] : e.Message), ("@dead", dead), ("@id", row.Id));
            }
        }
        return rows.Count;
    }

    private async Task<string> ApplyAsync(string type, string body, string webhookId, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (root.TryGetProperty("test", out var t) && t.ValueKind == JsonValueKind.True) return "test_event";
        var data = root.GetProperty("data");
        var sequence = root.TryGetProperty("sequence", out var s) && s.TryGetInt64(out var n) ? n : 0;

        switch (type)
        {
            case "invoice.generated":
            case "invoice.paid":
            case "invoice.overdue":
                return await ApplyInvoiceAsync(data, sequence, webhookId, ct);

            case "payment.completed":
            case "payment.failed":
                await db.ExecuteAsync("""
                    IF NOT EXISTS (SELECT 1 FROM dbo.payment_log WHERE payment_id = @id)
                      INSERT dbo.payment_log (payment_id, invoice_id, amount, currency, kind, reason)
                      VALUES (@id, @invoice, @amount, @currency, @kind, @reason)
                    """, ct,
                    ("@id", data.GetProperty("payment_id").GetString()), ("@invoice", Text(data, "invoice_id")), ("@amount", Money(data, "amount")),
                    ("@currency", MoneyText(data, "currency")), ("@kind", type == "payment.completed" ? "completed" : "failed"), ("@reason", Text(data, "failure_reason")));
                return "applied";

            case "export.completed":
            case "catalog.products.imported":
            case "import.completed":
            case "bulk_payment.batch.completed":
                return await files.FetchAsync(webhookId, type, data, ct); // a claim-check envelope: download, hash, compare

            case "kyc.completed":
                return await kyc.PullAsync(data, ct); // a thin event: ids only, the details are pulled with an API key

            case "subscription.renewed":
                await db.ExecuteAsync("""
                    IF NOT EXISTS (SELECT 1 FROM dbo.subscription_mirror WHERE subscription_id = @id)
                      INSERT dbo.subscription_mirror (subscription_id, plan_name, amount, currency) VALUES (@id, @plan, @amount, @currency)
                    """, ct, ("@id", data.GetProperty("subscription_id").GetString()), ("@plan", Text(data, "plan")), ("@amount", Number(data, "amount")), ("@currency", Text(data, "currency")));
                return "applied";

            default:
                return "unhandled_type";
        }
    }

    private async Task<string> ApplyInvoiceAsync(JsonElement data, long sequence, string webhookId, CancellationToken ct)
    {
        // every payload version this application may be pinned to: 2026-06-01 (customer_name), 2026-11-01 (customer.name), 2027-02-01 (customer.display_name, total.amount)
        var customer = data.TryGetProperty("customer", out var c) && c.ValueKind == JsonValueKind.Object ? Text(c, "name") ?? Text(c, "display_name") : Text(data, "customer_name");
        await using var connection = await db.OpenAsync(ct);
        await using var tx = (Microsoft.Data.SqlClient.SqlTransaction)await connection.BeginTransactionAsync(ct);
        await using var command = ReceiverDb.Command(connection, """
            DECLARE @current bigint = (SELECT sequence FROM dbo.invoice_mirror WITH (UPDLOCK, HOLDLOCK) WHERE invoice_id = @id);
            IF @current IS NULL
            BEGIN
              INSERT dbo.invoice_mirror (invoice_id, number, customer, amount, currency, status, sequence, last_webhook_id)
              VALUES (@id, @number, @customer, @amount, @currency, @status, @sequence, @webhook);
              SELECT 'applied';
            END
            ELSE IF @current < @sequence
            BEGIN
              UPDATE dbo.invoice_mirror
              SET number = @number, customer = @customer, amount = @amount, currency = @currency, status = @status, sequence = @sequence,
                  updated_at = SYSUTCDATETIME(), last_webhook_id = @webhook
              WHERE invoice_id = @id;
              SELECT 'applied';
            END
            ELSE SELECT 'stale_ignored';
            """,
            ("@id", data.GetProperty("invoice_id").GetString()), ("@number", Text(data, "number")), ("@customer", customer), ("@amount", Money(data, "amount")),
            ("@currency", MoneyText(data, "currency")), ("@status", Text(data, "status") ?? "open"), ("@sequence", sequence), ("@webhook", webhookId));
        command.Transaction = tx;
        var outcome = (string)(await command.ExecuteScalarAsync(ct))!;
        await tx.CommitAsync(ct);
        return outcome;
    }

    /// <summary>Money is <c>amount</c> and <c>currency</c> up to version 2026-11-01 and <c>total.amount</c> / <c>total.currency</c> from 2027-02-01: read whichever this payload has.</summary>
    private static decimal? Money(JsonElement e, string name) =>
        Number(e, name) ?? (e.TryGetProperty("total", out var t) && t.ValueKind == JsonValueKind.Object ? Number(t, name) : null);

    private static string? MoneyText(JsonElement e, string name) =>
        Text(e, name) ?? (e.TryGetProperty("total", out var t) && t.ValueKind == JsonValueKind.Object ? Text(t, name) : null);

    private static string? Text(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static decimal? Number(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : null;
}
