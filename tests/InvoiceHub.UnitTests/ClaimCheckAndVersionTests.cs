using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using InvoiceHub.BuildingBlocks.Storage;
using InvoiceHub.Jobs;
using InvoiceHub.Webhooks.Core;
using InvoiceHub.Webhooks.Delivery;

namespace InvoiceHub.UnitTests;

/// <summary>M5's pure rules: the claim-check envelope (what a receiver may see of a stored file), payload version projections, and the streamed export file.</summary>
public class ClaimCheckAndVersionTests
{
    // ------------------------------------------------------------------ claim-check envelope

    private sealed class FakeStore : IClaimCheckStore
    {
        public List<BlobRef> Minted { get; } = [];

        public Task<BlobRef> PutAsync(string name, Stream content, string contentType, CancellationToken ct) => throw new NotSupportedException();

        public DownloadLink CreateDownloadLink(BlobRef blob, TimeSpan validFor, string? downloadName = null)
        {
            Minted.Add(blob);
            return new DownloadLink(new Uri($"http://storage.test/{blob.Container}/{blob.Name}?sig=ONE-TIME-{Minted.Count}"), new DateTimeOffset(2026, 11, 13, 10, 0, 0, TimeSpan.Zero).Add(validFor));
        }

        public Task<bool> ExistsAsync(BlobRef blob, CancellationToken ct) => Task.FromResult(true);
    }

    private const string Export = """{"export_id":"exp_1","rows":120000,"format":"csv","file":{"container":"claim-checks","name":"t/exports/1.csv","size":52428800,"sha256":"abc123","content_type":"text/csv"}}""";

    [Fact]
    public void A_receiver_gets_size_checksum_and_a_link_but_never_the_internal_file_reference()
    {
        var store = new FakeStore();
        using var doc = JsonDocument.Parse(ClaimCheckEnvelope.Apply(Export, store, TimeSpan.FromMinutes(15)));
        var data = doc.RootElement;

        Assert.False(data.TryGetProperty("file", out _)); // container and blob name stay inside
        Assert.Equal(52_428_800, data.GetProperty("size_bytes").GetInt64());
        Assert.Equal("abc123", data.GetProperty("sha256").GetString());
        Assert.Equal("text/csv", data.GetProperty("content_type").GetString());
        Assert.Equal("exp_1", data.GetProperty("export_id").GetString());
        Assert.Contains("sig=ONE-TIME-1", data.GetProperty("download").GetProperty("url").GetString(), StringComparison.Ordinal);
        Assert.Equal("2026-11-13T10:15:00Z", data.GetProperty("download").GetProperty("expires_at").GetString());
    }

    [Fact]
    public void Every_attempt_mints_its_own_link()
    {
        var store = new FakeStore();
        var first = ClaimCheckEnvelope.Apply(Export, store, TimeSpan.FromMinutes(15));
        var second = ClaimCheckEnvelope.Apply(Export, store, TimeSpan.FromMinutes(15));
        Assert.NotEqual(first, second);
        Assert.Equal(2, store.Minted.Count);
    }

    [Fact]
    public void The_envelope_is_tiny_next_to_the_file_it_stands_for()
    {
        var envelope = ClaimCheckEnvelope.Apply(Export, new FakeStore(), TimeSpan.FromMinutes(15));
        Assert.True(Encoding.UTF8.GetByteCount(envelope) < 700); // about 600 bytes for a 50 MB file
    }

    [Fact]
    public void The_log_view_never_contains_a_working_link()
    {
        var redacted = ClaimCheckEnvelope.Redact(Export, TimeSpan.FromMinutes(15));
        Assert.DoesNotContain("http", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("claim-checks", redacted, StringComparison.Ordinal);
        Assert.Contains("minted for every attempt", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void An_event_without_a_file_passes_through_and_a_missing_store_only_omits_the_link()
    {
        const string test = """{"export_id":"exp_test","rows":10}""";
        Assert.Equal(test, ClaimCheckEnvelope.Apply(test, new FakeStore(), TimeSpan.FromMinutes(15)));
        using var noStore = JsonDocument.Parse(ClaimCheckEnvelope.Apply(Export, null, TimeSpan.FromMinutes(15)));
        Assert.False(noStore.RootElement.TryGetProperty("download", out _));
        Assert.False(noStore.RootElement.TryGetProperty("file", out _));
    }

    // ------------------------------------------------------------------ payload versions

    private const string Invoice = """{"invoice_id":"inv_1","number":"INV-1","amount":7250.00,"currency":"USD","status":"paid","customer":{"name":"Fabrikam Inc","country":"US"},"due_at":"2026-12-01T00:00:00Z","paid_at":"2026-11-13T09:00:00Z"}""";
    private const string Payment = """{"payment_id":"pay_1","invoice_id":"inv_1","amount":7250.00,"currency":"USD","method":"card"}""";

    private static JsonElement Project(string version, string type, string data) => JsonDocument.Parse(VersionProjection.Project(version, type, data)).RootElement.Clone();

    [Fact]
    public void The_current_version_is_the_canonical_event_unchanged()
    {
        Assert.Equal(Invoice, VersionProjection.Project(ApiVersions.Current, "invoice.paid", Invoice));
        Assert.Equal(Invoice, VersionProjection.Project("1999-01-01", "invoice.paid", Invoice)); // an unknown pin never rewrites anything
    }

    [Fact]
    public void The_older_version_flattens_the_customer_and_has_no_paid_at()
    {
        var older = Project(ApiVersions.Older, "invoice.paid", Invoice);
        Assert.False(older.TryGetProperty("customer", out _));
        Assert.False(older.TryGetProperty("paid_at", out _));
        Assert.Equal("Fabrikam Inc", older.GetProperty("customer_name").GetString());
        Assert.Equal("US", older.GetProperty("customer_country").GetString());
        Assert.Equal(7250.00m, older.GetProperty("amount").GetDecimal()); // everything else is as it was
    }

    [Fact]
    public void The_preview_groups_money_and_renames_the_customer_name_for_invoices_and_payments()
    {
        var invoice = Project(ApiVersions.Preview, "invoice.paid", Invoice);
        Assert.False(invoice.TryGetProperty("amount", out _));
        Assert.Equal(7250.00m, invoice.GetProperty("total").GetProperty("amount").GetDecimal());
        Assert.Equal("USD", invoice.GetProperty("total").GetProperty("currency").GetString());
        Assert.Equal("Fabrikam Inc", invoice.GetProperty("customer").GetProperty("display_name").GetString());
        Assert.False(invoice.GetProperty("customer").TryGetProperty("name", out _));

        var payment = Project(ApiVersions.Preview, "payment.completed", Payment);
        Assert.Equal("USD", payment.GetProperty("total").GetProperty("currency").GetString());
        Assert.Equal("card", payment.GetProperty("method").GetString());
    }

    [Fact]
    public void Other_events_are_not_touched_by_a_version()
    {
        const string renewal = """{"subscription_id":"sub_1","plan":"business","amount":2400,"currency":"USD"}""";
        Assert.Equal(renewal, VersionProjection.Project(ApiVersions.Preview, "subscription.renewed", renewal));
        Assert.Equal(renewal, VersionProjection.Project(ApiVersions.Older, "subscription.renewed", renewal));
    }

    [Fact]
    public void Exactly_one_version_is_current_and_each_other_one_says_what_changes()
    {
        Assert.Single(ApiVersions.All, v => v.Status == "current");
        Assert.All(ApiVersions.All.Where(v => v.Status != "current"), v => Assert.NotEmpty(v.Changes));
        Assert.NotNull(ApiVersions.Find(ApiVersions.Older)!.SunsetOn); // an older version announces its end
        Assert.Equal(["2026-06-01", "2026-11-01", "2027-02-01"], ApiVersions.Supported);
        Assert.False(ApiVersions.IsSupported("2030-01-01"));
    }

    // ------------------------------------------------------------------ the streamed export

    private static (byte[] Bytes, long Rows) Drain(long target)
    {
        using var stream = new GeneratedCsvStream(target);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return (buffer.ToArray(), stream.Rows);
    }

    [Fact]
    public void The_export_reaches_its_size_ends_on_a_row_boundary_and_counts_its_rows()
    {
        var (bytes, rows) = Drain(2 * 1024 * 1024);
        Assert.InRange(bytes.Length, 2 * 1024 * 1024, 2 * 1024 * 1024 + 200); // at most one row over
        Assert.Equal((byte)'\n', bytes[^1]);
        var text = Encoding.ASCII.GetString(bytes);
        Assert.StartsWith("invoice_number,customer,amount,currency,issued_at,status\n", text, StringComparison.Ordinal);
        Assert.Equal(rows, text.Count(c => c == '\n') - 1); // header line excluded
        Assert.Contains("INV-2026-00000001,", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_same_job_produces_the_same_file()
    {
        // a runner that is killed and replaced does the job again: same bytes, same checksum, so the blob is simply overwritten
        Assert.Equal(SHA256.HashData(Drain(300_000).Bytes), SHA256.HashData(Drain(300_000).Bytes));
        Assert.NotEqual(SHA256.HashData(Drain(300_000).Bytes), SHA256.HashData(Drain(400_000).Bytes));
    }

    [Fact]
    public void A_50_megabyte_export_never_has_to_exist_in_memory()
    {
        using var stream = new GeneratedCsvStream(50L * 1024 * 1024);
        var buffer = new byte[81_920];
        long total = 0;
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0) total += read; // pulled through a small buffer, like the upload does
        Assert.InRange(total, 50L * 1024 * 1024, 50L * 1024 * 1024 + 200);
        Assert.True(stream.Rows > 500_000);
    }
}
