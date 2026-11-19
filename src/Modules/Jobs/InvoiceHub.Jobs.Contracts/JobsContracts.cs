namespace InvoiceHub.Jobs.Contracts;

/// <summary>A bulk export of the tenant's invoices as a CSV file of about <paramref name="SizeMegabytes"/> MB (the demo's stand-in for "export everything").</summary>
public sealed record RequestExport(string Kind, int SizeMegabytes);

/// <summary>A vendor product file (an EDI 832 style price/sales catalogue) to import. The demo generates the result report.</summary>
public sealed record RequestImport(string VendorId, string Source, int Products);

public sealed record JobRef(Guid JobId, string Kind, string Status);

/// <summary>
/// Long-running work is asked for here and done elsewhere (the delivery host's job runner). The result is a big file, so it is stored once and announced by a
/// claim-check event: <c>export.completed</c> or <c>catalog.products.imported</c>.
/// </summary>
public interface IJobCommands
{
    Task<JobRef> RequestExportAsync(RequestExport command, CancellationToken ct = default);

    Task<JobRef> RequestImportAsync(RequestImport command, CancellationToken ct = default);
}
