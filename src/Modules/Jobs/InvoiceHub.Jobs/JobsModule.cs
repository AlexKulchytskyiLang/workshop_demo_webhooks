using System.Text;
using System.Text.Json;
using InvoiceHub.BuildingBlocks.Data;
using InvoiceHub.BuildingBlocks.Modules;
using InvoiceHub.BuildingBlocks.Storage;
using InvoiceHub.BuildingBlocks.Tenancy;
using InvoiceHub.Events.Contracts;
using InvoiceHub.Jobs.Contracts;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace InvoiceHub.Jobs;

internal sealed class JobsDbContext(DbContextOptions<JobsDbContext> options) : DbContext(options);

/// <summary>Bulk exports and vendor imports. The API side only records the request; the delivery host's runner does the work and announces the result with a claim-check event.</summary>
public sealed class JobsModule : IWorkerModule
{
    public const string ModuleName = "jobs";

    public string Name => ModuleName;

    public void ConfigureServices(IHostApplicationBuilder builder)
    {
        builder.Services.AddModuleDbContext<JobsDbContext>(builder.Configuration, builder.Environment.ApplicationName);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddScoped<IJobCommands, JobCommands>();
    }

    public void ConfigureWorkers(IHostApplicationBuilder builder) => builder.Services.AddHostedService<JobRunner>();

    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        // no HTTP surface: the simulator asks for jobs in process, the runner lives in the delivery host
    }
}

internal sealed class JobCommands(JobsDbContext db, ITenantContext tenant) : IJobCommands
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task<JobRef> RequestExportAsync(RequestExport c, CancellationToken ct = default)
    {
        if (c.SizeMegabytes is < 1 or > 200)
            throw new ArgumentOutOfRangeException(nameof(c), "An export is 1 to 200 MB in the demo.");

        return Enqueue("export", new { kind = c.Kind, sizeMegabytes = c.SizeMegabytes }, ct);
    }

    public Task<JobRef> RequestImportAsync(RequestImport c, CancellationToken ct = default)
    {
        if (c.Products is < 1 or > 100_000)
            throw new ArgumentOutOfRangeException(nameof(c), "An import has 1 to 100,000 products in the demo.");

        return Enqueue("import", new { vendorId = c.VendorId, source = c.Source, products = c.Products }, ct);
    }

    private async Task<JobRef> Enqueue(string kind, object parameters, CancellationToken ct)
    {
        var tenantId = tenant.TenantId ?? throw new InvalidOperationException("Jobs are asked for on behalf of a tenant: set the tenant context first.");
        var id = Guid.CreateVersion7();
        await db.ExecuteAsync("INSERT jobs.job (id, tenant_id, kind, status, params_json) VALUES (@id, @tenant, @kind, 'queued', @params)", ct,
                             ("@id", id), ("@tenant", tenantId), ("@kind", kind), ("@params", JsonSerializer.Serialize(parameters, Json)));
        return new JobRef(id, kind, "queued");
    }
}

/// <summary>
/// Picks queued jobs (several replicas compete: <c>UPDLOCK, READPAST</c>), does the work under the job's tenant, and commits the result and the announcing event together.
/// A runner that dies mid-job leaves a lease that runs out; another one takes the job over. The blob is named after the job, so doing it again overwrites, never duplicates.
/// </summary>
internal sealed class JobRunner(IServiceScopeFactory scopes, TimeProvider clock, ILogger<JobRunner> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string Me = $"{Environment.MachineName}:{Environment.ProcessId}";

    private sealed record Claimed(Guid Id, Guid TenantId, string Kind, string ParamsJson);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var job = await ClaimAsync(stoppingToken);
                if (job is null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                    continue;
                }
                await RunAsync(job, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "The job runner hit a problem; trying again shortly");
                try { await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken); } catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task<Claimed?> ClaimAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantScope>().SetSystem(); // cross-tenant queue: only the worker SQL login may do this
        var db = scope.ServiceProvider.GetRequiredService<JobsDbContext>();
        var rows = await db.QueryAsync("""
            ;WITH next AS (
              SELECT TOP (1) * FROM jobs.job WITH (UPDLOCK, READPAST, ROWLOCK)
              WHERE status = 'queued' OR (status = 'running' AND claimed_until < SYSUTCDATETIME())
              ORDER BY created_at)
            UPDATE next SET status = 'running', claimed_by = @me, claimed_until = DATEADD(MINUTE, 10, SYSUTCDATETIME()), started_at = COALESCE(started_at, SYSUTCDATETIME())
            OUTPUT inserted.id, inserted.tenant_id, inserted.kind, inserted.params_json
            """, r => new Claimed(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetString(3)), ct, ("@me", Me));
        return rows.Count == 0 ? null : rows[0];
    }

    private async Task RunAsync(Claimed job, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantScope>().SetTenant(job.TenantId); // from here on everything sees this tenant only

        var db = scope.ServiceProvider.GetRequiredService<JobsDbContext>();
        var store = scope.ServiceProvider.GetRequiredService<IClaimCheckStore>();
        var publisher = scope.ServiceProvider.GetRequiredService<IEventPublisher>();
        var started = clock.GetUtcNow();

        try
        {
            var (eventType, aggregate, data, result) = job.Kind == "export" ? await ExportAsync(job, store, ct) : await ImportAsync(job, store, ct);
            await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                await using IDbContextTransaction tx = await db.Database.BeginTransactionAsync(ct);
                await db.ExecuteAsync("UPDATE jobs.job SET status = 'completed', result_json = @result, completed_at = SYSUTCDATETIME(), claimed_until = NULL WHERE id = @id", ct, ("@id", job.Id), ("@result", result));
                await publisher.PublishAsync(tx.GetDbTransaction(), new DomainEvent(eventType, aggregate, 1, data, clock.GetUtcNow()), ct); // the announcement commits with the result
                await tx.CommitAsync(ct);
            });
            logger.LogInformation("Job {JobId} ({Kind}) finished in {Seconds:0.0} s", job.Id, job.Kind, (clock.GetUtcNow() - started).TotalSeconds);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogError(e, "Job {JobId} ({Kind}) failed", job.Id, job.Kind);
            await db.ExecuteAsync("UPDATE jobs.job SET status = 'failed', error = @error, completed_at = SYSUTCDATETIME(), claimed_until = NULL WHERE id = @id", CancellationToken.None,
                ("@id", job.Id), ("@error", e.Message.Length > 400 ? e.Message[..400] : e.Message));
            if (job.Kind == "export") await PublishFailureAsync(db, publisher, job, e.Message);
        }
    }

    private async Task<(string Type, string Aggregate, string Data, string Result)> ExportAsync(Claimed job, IClaimCheckStore store, CancellationToken ct)
    {
        using var p = JsonDocument.Parse(job.ParamsJson);
        var megabytes = p.RootElement.GetProperty("sizeMegabytes").GetInt32();
        var kind = p.RootElement.TryGetProperty("kind", out var k) ? k.GetString() ?? "invoices" : "invoices";

        await using var csv = new GeneratedCsvStream((long)megabytes * 1024 * 1024);
        var blob = await store.PutAsync($"{job.TenantId:N}/exports/{job.Id:N}.csv", csv, "text/csv", ct);
        var exportId = $"exp_{job.Id:N}";
        var data = JsonSerializer.Serialize(new { export_id = exportId, kind, rows = csv.Rows, format = "csv", file = FileRef(blob) }, Json);
        return ("export.completed", exportId, data, JsonSerializer.Serialize(new { rows = csv.Rows, size = blob.Size, sha256 = blob.Sha256 }, Json));
    }

    private async Task<(string Type, string Aggregate, string Data, string Result)> ImportAsync(Claimed job, IClaimCheckStore store, CancellationToken ct)
    {
        using var p = JsonDocument.Parse(job.ParamsJson);
        var vendor = p.RootElement.GetProperty("vendorId").GetString() ?? "ven_000";
        var source = p.RootElement.GetProperty("source").GetString() ?? "edi:832";
        var products = p.RootElement.GetProperty("products").GetInt32();

        // a believable result: most lines are new, some update a product we know, a few are rejected with a reason
        var rejected = Math.Max(1, products * 3 / 100);
        var updated = products * 12 / 100;
        var added = products - rejected - updated;
        var reasons = new[] { "missing_unit_price", "unknown_unit_of_measure", "duplicate_sku_in_file", "gtin_check_digit_invalid" };
        var report = JsonSerializer.Serialize(new
        {
            import_id = $"imp_{job.Id:N}", vendor_id = vendor, source, received_at = clock.GetUtcNow(), products_added = added, products_updated = updated,
            rejected = Enumerable.Range(1, Math.Min(rejected, 2000)).Select(i => new { line = i * 31, sku = $"SKU-{i * 31:D6}", reason = reasons[i % reasons.Length] }),
        }, Json);

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(report));
        var blob = await store.PutAsync($"{job.TenantId:N}/imports/{job.Id:N}.json", stream, "application/json", ct);
        var importId = $"imp_{job.Id:N}";
        var data = JsonSerializer.Serialize(new { import_id = importId, vendor_id = vendor, source, products_added = added, products_updated = updated, rejected_count = rejected, file = FileRef(blob) }, Json);
        return ("catalog.products.imported", importId, data, JsonSerializer.Serialize(new { added, updated, rejected, size = blob.Size, sha256 = blob.Sha256 }, Json));
    }

    private async Task PublishFailureAsync(JobsDbContext db, IEventPublisher publisher, Claimed job, string reason)
    {
        try
        {
            await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                await using IDbContextTransaction tx = await db.Database.BeginTransactionAsync(CancellationToken.None);
                var data = JsonSerializer.Serialize(new { export_id = $"exp_{job.Id:N}", reason }, Json);
                await publisher.PublishAsync(tx.GetDbTransaction(), new DomainEvent("export.failed", $"exp_{job.Id:N}", 2, data, clock.GetUtcNow()), CancellationToken.None);
                await tx.CommitAsync(CancellationToken.None);
            });
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Could not announce the failure of job {JobId}", job.Id);
        }
    }

    /// <summary>The internal reference: container and blob name stay inside the platform (delivery turns this into a size, a checksum and an expiring link).</summary>
    private static object FileRef(BlobRef b) => new { container = b.Container, name = b.Name, size = b.Size, sha256 = b.Sha256, content_type = b.ContentType };
}

/// <summary>
/// A forward-only stream that GENERATES an invoice export as it is read, so a 50 MB file never exists in memory: the storage upload pulls it through in chunks.
/// Deterministic, so a job that is run twice produces the same bytes (and the same checksum).
/// </summary>
internal sealed class GeneratedCsvStream(long targetBytes) : Stream
{
    private static readonly byte[] Header = Encoding.ASCII.GetBytes("invoice_number,customer,amount,currency,issued_at,status\n");
    private byte[] _pending = Header;
    private int _pendingPosition;
    private long _produced = Header.Length;
    private long _row;

    public long Rows => _row;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var written = 0;
        while (written < buffer.Length)
        {
            if (_pendingPosition >= _pending.Length)
            {
                if (_produced >= targetBytes) break; // enough rows: the file ends on a row boundary
                _pending = NextRow();
                _pendingPosition = 0;
                _produced += _pending.Length;
            }
            var take = Math.Min(buffer.Length - written, _pending.Length - _pendingPosition);
            _pending.AsSpan(_pendingPosition, take).CopyTo(buffer[written..]);
            _pendingPosition += take;
            written += take;
        }
        return written;
    }

    private byte[] NextRow()
    {
        var n = _row++;
        var cents = (n * 7919 + 13) % 5_000_00;
        var status = n % 9 == 0 ? "overdue" : n % 3 == 0 ? "open" : "paid";
        var line = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"INV-2026-{n + 1:D8},Customer {n % 997:D3},{cents / 100}.{cents % 100:D2},USD,2026-{(n % 12) + 1:D2}-{(n % 28) + 1:D2},{status}\n");
        return Encoding.ASCII.GetBytes(line);
    }
}
