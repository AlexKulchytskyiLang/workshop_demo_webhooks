namespace InvoiceHub.BuildingBlocks.Storage;

/// <summary>Where a large file lives. This reference stays inside the platform: receivers only ever see an expiring download link.</summary>
public sealed record BlobRef(string Container, string Name, long Size, string Sha256, string ContentType);

public sealed record DownloadLink(Uri Url, DateTimeOffset ExpiresAt);

/// <summary>
/// The claim-check port (ASR-18): big results never travel inside a webhook or a broker message. They are written to blob storage once, the event carries a small
/// envelope with a size, a checksum and a LINK, and the link is minted again for every delivery attempt so it never outlives its purpose.
/// The Azure Blob adapter implements it; unit tests use a fake.
/// </summary>
public interface IClaimCheckStore
{
    /// <summary>Streams the content into storage (nothing is held in memory) and returns the reference with the size and the SHA-256 computed on the way.</summary>
    Task<BlobRef> PutAsync(string name, Stream content, string contentType, CancellationToken ct);

    /// <summary>A read-only link that stops working after <paramref name="validFor"/>. Cheap: no call to the storage service is made.</summary>
    DownloadLink CreateDownloadLink(BlobRef blob, TimeSpan validFor, string? downloadName = null);

    Task<bool> ExistsAsync(BlobRef blob, CancellationToken ct);
}
