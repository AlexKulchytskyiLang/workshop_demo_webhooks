namespace InvoiceHub.Billing.Contracts;

public sealed record GenerateInvoice(string CustomerName, string CustomerCountry, decimal Amount, string Currency, int DueInDays = 30);

public sealed record InvoiceRef(Guid InvoiceId, string Number, decimal Amount, string Currency, string Status, long Sequence);

/// <summary>Commands of the simulated invoicing domain. Each one changes state and publishes its event in one transaction.</summary>
public interface IInvoiceCommands
{
    /// <summary>Issues an invoice for the current tenant and publishes <c>invoice.generated</c>.</summary>
    Task<InvoiceRef> GenerateAsync(GenerateInvoice command, CancellationToken ct = default);

    /// <summary>Records a payment: publishes <c>payment.completed</c> and <c>invoice.paid</c>.</summary>
    Task<InvoiceRef> MarkPaidAsync(Guid invoiceId, CancellationToken ct = default);

    /// <summary>Marks an open invoice overdue: publishes <c>invoice.overdue</c>.</summary>
    Task<InvoiceRef> MarkOverdueAsync(Guid invoiceId, CancellationToken ct = default);

    /// <summary>Records a failed payment attempt: publishes <c>payment.failed</c> (the invoice stays as it was).</summary>
    Task<InvoiceRef> FailPaymentAsync(Guid invoiceId, string reason, CancellationToken ct = default);

    /// <summary>A subscription renews and is paid: publishes <c>subscription.renewed</c>.</summary>
    Task<SubscriptionRef> RenewSubscriptionAsync(RenewSubscription command, CancellationToken ct = default);
}

public sealed record RenewSubscription(string Plan, decimal Amount, string Currency);

public sealed record SubscriptionRef(string SubscriptionId, string Plan);

/// <summary>The invoice does not exist for this tenant, or the command does not fit its state.</summary>
public sealed class InvoiceStateException(string message) : Exception(message);
