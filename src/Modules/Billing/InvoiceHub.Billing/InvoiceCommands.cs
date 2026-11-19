using System.Text.Json;
using InvoiceHub.Billing.Contracts;
using InvoiceHub.BuildingBlocks.Data;
using InvoiceHub.Events.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace InvoiceHub.Billing;

internal sealed class InvoiceEntity
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string Number { get; set; } = "";
    public string CustomerName { get; set; } = "";
    public string CustomerCountry { get; set; } = "";
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "";
    public string Status { get; set; } = "open";
    public long Sequence { get; set; }
    public DateTime IssuedAt { get; set; }
    public DateTime DueAt { get; set; }
    public DateTime? PaidAt { get; set; }
}

internal sealed class BillingDbContext(DbContextOptions<BillingDbContext> options) : DbContext(options)
{
    public DbSet<InvoiceEntity> Invoices => Set<InvoiceEntity>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema("billing");
        b.Entity<InvoiceEntity>(e =>
        {
            e.ToTable("invoice");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.Number).HasColumnName("number").HasMaxLength(30).IsUnicode(false);
            e.Property(x => x.CustomerName).HasColumnName("customer_name").HasMaxLength(120);
            e.Property(x => x.CustomerCountry).HasColumnName("customer_country").HasMaxLength(2).IsFixedLength().IsUnicode(false);
            e.Property(x => x.Amount).HasColumnName("amount").HasPrecision(18, 2);
            e.Property(x => x.Currency).HasColumnName("currency").HasMaxLength(3).IsFixedLength().IsUnicode(false);
            e.Property(x => x.Status).HasColumnName("status").HasMaxLength(12).IsUnicode(false);
            e.Property(x => x.Sequence).HasColumnName("sequence");
            e.Property(x => x.IssuedAt).HasColumnName("issued_at");
            e.Property(x => x.DueAt).HasColumnName("due_at");
            e.Property(x => x.PaidAt).HasColumnName("paid_at");
        });
    }
}

/// <summary>
/// The transactional outbox in action: the invoice row and its event(s) commit together or not at all.
/// The whole unit is wrapped in the EF execution strategy, so a deadlock or a transient network error re-runs it from the start.
/// </summary>
internal sealed class InvoiceCommands(BillingDbContext db, IEventPublisher events, TimeProvider clock) : IInvoiceCommands
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task<InvoiceRef> GenerateAsync(GenerateInvoice c, CancellationToken ct = default) => InTransactionAsync(async (tx, now) =>
    {
        var number = await NextNumberAsync(now, ct);
        var invoice = new InvoiceEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = Guid.Empty, // overwritten below from the session: RLS blocks any other tenant
            Number = number,
            CustomerName = c.CustomerName,
            CustomerCountry = c.CustomerCountry.ToUpperInvariant(),
            Amount = decimal.Round(c.Amount, 2),
            Currency = c.Currency.ToUpperInvariant(),
            Status = "open",
            Sequence = 1,
            IssuedAt = now.UtcDateTime,
            DueAt = now.UtcDateTime.AddDays(c.DueInDays),
        };
        invoice.TenantId = await CurrentTenantAsync(ct);
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync(ct);
        await events.PublishAsync(tx, new DomainEvent("invoice.generated", Aggregate(invoice), invoice.Sequence, InvoiceData(invoice), now), ct);
        return invoice;
    }, ct);

    public Task<InvoiceRef> MarkPaidAsync(Guid invoiceId, CancellationToken ct = default) => InTransactionAsync(async (tx, now) =>
    {
        var invoice = await Load(invoiceId, ct);
        if (invoice.Status is not ("open" or "overdue")) throw new InvoiceStateException($"Invoice {invoice.Number} is {invoice.Status} and cannot be paid.");
        invoice.Status = "paid";
        invoice.PaidAt = now.UtcDateTime;
        invoice.Sequence++;
        await db.SaveChangesAsync(ct);

        var paymentId = $"pay_{Guid.CreateVersion7():N}";
        var payment = JsonSerializer.Serialize(new { payment_id = paymentId, invoice_id = Aggregate(invoice), amount = invoice.Amount, currency = invoice.Currency, method = "card" }, Json);
        await events.PublishAsync(tx, new DomainEvent("payment.completed", paymentId, 1, payment, now), ct);
        await events.PublishAsync(tx, new DomainEvent("invoice.paid", Aggregate(invoice), invoice.Sequence, InvoiceData(invoice), now), ct);
        return invoice;
    }, ct);

    public Task<InvoiceRef> MarkOverdueAsync(Guid invoiceId, CancellationToken ct = default) => InTransactionAsync(async (tx, now) =>
    {
        var invoice = await Load(invoiceId, ct);
        if (invoice.Status != "open") throw new InvoiceStateException($"Invoice {invoice.Number} is {invoice.Status}; only open invoices become overdue.");
        invoice.Status = "overdue";
        invoice.Sequence++;
        await db.SaveChangesAsync(ct);
        await events.PublishAsync(tx, new DomainEvent("invoice.overdue", Aggregate(invoice), invoice.Sequence, InvoiceData(invoice), now), ct);
        return invoice;
    }, ct);

    public Task<InvoiceRef> FailPaymentAsync(Guid invoiceId, string reason, CancellationToken ct = default) => InTransactionAsync(async (tx, now) =>
    {
        var invoice = await Load(invoiceId, ct);
        var paymentId = $"pay_{Guid.CreateVersion7():N}";
        var data = JsonSerializer.Serialize(new { payment_id = paymentId, invoice_id = Aggregate(invoice), amount = invoice.Amount, currency = invoice.Currency, failure_reason = reason }, Json);
        await events.PublishAsync(tx, new DomainEvent("payment.failed", paymentId, 1, data, now), ct);
        return invoice;
    }, ct);

    public async Task<SubscriptionRef> RenewSubscriptionAsync(RenewSubscription c, CancellationToken ct = default)
    {
        var id = $"sub_{Guid.CreateVersion7():N}";
        var now = clock.GetUtcNow();
        var data = JsonSerializer.Serialize(new
        {
            subscription_id = id,
            plan = c.Plan,
            period_start = now.UtcDateTime.Date,
            period_end = now.UtcDateTime.Date.AddYears(1),
            amount = decimal.Round(c.Amount, 2),
            currency = c.Currency.ToUpperInvariant(),
        }, Json);
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using IDbContextTransaction tx = await db.Database.BeginTransactionAsync(ct);
            await events.PublishAsync(tx.GetDbTransaction(), new DomainEvent("subscription.renewed", id, 1, data, now), ct);
            await tx.CommitAsync(ct);
        });
        return new SubscriptionRef(id, c.Plan);
    }

    private async Task<InvoiceRef> InTransactionAsync(Func<System.Data.Common.DbTransaction, DateTimeOffset, Task<InvoiceEntity>> work, CancellationToken ct)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear(); // a retry starts from a clean slate
            await using IDbContextTransaction tx = await db.Database.BeginTransactionAsync(ct);
            var invoice = await work(tx.GetDbTransaction(), clock.GetUtcNow());
            await tx.CommitAsync(ct);
            return new InvoiceRef(invoice.Id, invoice.Number, invoice.Amount, invoice.Currency, invoice.Status, invoice.Sequence);
        });
    }

    private async Task<InvoiceEntity> Load(Guid id, CancellationToken ct) =>
        await db.Invoices.FirstOrDefaultAsync(i => i.Id == id, ct) ?? throw new InvoiceStateException("Invoice not found.");

    private async Task<string> NextNumberAsync(DateTimeOffset now, CancellationToken ct)
    {
        var next = await db.ScalarAsync<long>("SELECT NEXT VALUE FOR billing.invoice_number_seq", ct);
        return $"INV-{now.Year}-{next:D5}";
    }

    /// <summary>The tenant of this session, read back from SQL so the row can never carry a tenant the connection is not allowed to see.</summary>
    private async Task<Guid> CurrentTenantAsync(CancellationToken ct) =>
        Guid.Parse((await db.ScalarAsync<string>("SELECT CONVERT(varchar(36), CAST(SESSION_CONTEXT(N'tenant_id') AS uniqueidentifier))", ct))!);

    private static string Aggregate(InvoiceEntity i) => $"inv_{i.Id:N}";

    private static string InvoiceData(InvoiceEntity i) => JsonSerializer.Serialize(new
    {
        invoice_id = Aggregate(i),
        number = i.Number,
        amount = i.Amount,
        currency = i.Currency,
        status = i.Status,
        customer = new { name = i.CustomerName, country = i.CustomerCountry },
        issued_at = new DateTimeOffset(DateTime.SpecifyKind(i.IssuedAt, DateTimeKind.Utc)),
        due_at = new DateTimeOffset(DateTime.SpecifyKind(i.DueAt, DateTimeKind.Utc)),
        paid_at = i.PaidAt is null ? (DateTimeOffset?)null : new DateTimeOffset(DateTime.SpecifyKind(i.PaidAt.Value, DateTimeKind.Utc)),
    }, Json);
}
