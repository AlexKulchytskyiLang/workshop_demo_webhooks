using System.Security.Cryptography;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Azure.Storage.Sas;
using InvoiceHub.BuildingBlocks.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace InvoiceHub.Storage.AzureBlob;

public static class AzureBlobRegistration
{
    /// <summary>Name of the connection string the AppHost provides (the Azurite blob endpoint).</summary>
    public const string ConnectionName = "blobs";

    /// <summary>One private container for everything that is claim-checked. Nothing in it is public: access is only by a link minted per attempt.</summary>
    public const string Container = "claim-checks";

    public static IServiceCollection AddAzureBlobClaimCheck(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(_ => new BlobServiceClient(configuration.GetConnectionString(ConnectionName)
            ?? throw new InvalidOperationException($"Connection string '{ConnectionName}' is missing. Start the app through the AppHost.")));
        services.AddSingleton<IClaimCheckStore, AzureBlobClaimCheckStore>();
        return services;
    }
}

internal sealed class AzureBlobClaimCheckStore(BlobServiceClient service, TimeProvider clock) : IClaimCheckStore
{
    private const int BufferSize = 81_920;

    public async Task<BlobRef> PutAsync(string name, Stream content, string contentType, CancellationToken ct)
    {
        var container = service.GetBlobContainerClient(AzureBlobRegistration.Container);
        await container.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: ct);
        var blob = container.GetBlobClient(name);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long size = 0;
        var options = new BlobOpenWriteOptions { HttpHeaders = new BlobHttpHeaders { ContentType = contentType } };
        await using (var target = await blob.OpenWriteAsync(overwrite: true, options, ct))
        {
            var buffer = new byte[BufferSize];
            int read;
            while ((read = await content.ReadAsync(buffer, ct)) > 0)
            {
                hash.AppendData(buffer, 0, read); // checksum and size are computed while streaming: the file is never held in memory
                size += read;
                await target.WriteAsync(buffer.AsMemory(0, read), ct);
            }
        }
        return new BlobRef(AzureBlobRegistration.Container, name, size, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(), contentType);
    }

    public DownloadLink CreateDownloadLink(BlobRef blob, TimeSpan validFor, string? downloadName = null)
    {
        var client = service.GetBlobContainerClient(blob.Container).GetBlobClient(blob.Name);
        if (!client.CanGenerateSasUri) throw new InvalidOperationException("The storage client has no account key, so it cannot mint download links.");

        var now = clock.GetUtcNow();
        var expires = now.Add(validFor);
        var sas = new BlobSasBuilder(BlobSasPermissions.Read, expires)
        {
            BlobContainerName = blob.Container,
            BlobName = blob.Name,
            Resource = "b",
            StartsOn = now.AddMinutes(-2), // tolerate a little clock difference between us and the storage service
            Protocol = SasProtocol.HttpsAndHttp, // the local emulator is plain HTTP; a real account answers HTTPS only
            ContentDisposition = $"attachment; filename=\"{downloadName ?? Path.GetFileName(blob.Name)}\"",
            ContentType = blob.ContentType,
        };
        return new DownloadLink(client.GenerateSasUri(sas), expires);
    }

    public async Task<bool> ExistsAsync(BlobRef blob, CancellationToken ct) =>
        (await service.GetBlobContainerClient(blob.Container).GetBlobClient(blob.Name).ExistsAsync(ct)).Value;
}
