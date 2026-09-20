using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace Quality.Orchestrator;

public sealed record AzureBlobOptions(string? ConnectionString, string? ServiceUri, string Container,
    long MaxArtifactBytes = 134217728, int TimeoutSeconds = 60)
{
    public void Validate()
    {
        var hasConnectionString = !string.IsNullOrWhiteSpace(ConnectionString);
        var hasServiceUri = !string.IsNullOrWhiteSpace(ServiceUri);
        if (hasConnectionString == hasServiceUri)
            throw new ArgumentException("Configure exactly one Azure connection string or service URI");
        if (hasServiceUri && (!Uri.TryCreate(ServiceUri, UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || uri.AbsolutePath != "/" || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0))
            throw new ArgumentException("Azure service URI must be an HTTPS storage-account origin");
        if (!Regex.IsMatch(Container, "^(?!.*--)[a-z0-9](?:[a-z0-9-]{1,61}[a-z0-9])$"))
            throw new ArgumentException("Azure artifact container must be a valid 3-63 character lowercase container name");
        if (MaxArtifactBytes is < 1 or > 1073741824 || TimeoutSeconds is < 1 or > 300)
            throw new ArgumentException("Azure artifact limits are outside supported bounds");
    }
}

public sealed class AzureBlobArtifactStore(BlobContainerClient container, AzureBlobOptions options) : IRemoteArtifactStore
{
    public const string Prefix = "quality-system/";
    public string Provider => "AzureBlob";
    public ArtifactStoreInfo Info => new(Provider, options.Container, Prefix, null, false);

    public static BlobContainerClient CreateClient(AzureBlobOptions options)
    {
        options.Validate();
        var service = !string.IsNullOrWhiteSpace(options.ConnectionString)
            ? new BlobServiceClient(options.ConnectionString)
            : new BlobServiceClient(new Uri(options.ServiceUri!), new DefaultAzureCredential());
        return service.GetBlobContainerClient(options.Container);
    }

    public async Task InitializeAsync(CancellationToken ct)
    {
        using var timeout = Deadline(ct);
        await container.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: timeout.Token);
    }

    public async Task<ArtifactHandle> PutAsync(string key, Stream content, string contentType, CancellationToken ct)
    {
        ValidateKey(key);
        if (!MediaTypeHeaderValue.TryParse(contentType, out _)) throw new ArgumentException("Invalid artifact content type");
        using var timeout = Deadline(ct);
        await using var snapshot = TemporaryFile();
        var checksum = await CopyAndHashAsync(content, snapshot, timeout.Token);
        snapshot.Position = 0;
        var blob = container.GetBlobClient(key);
        await blob.UploadAsync(snapshot, new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = contentType },
            Metadata = new Dictionary<string, string> { ["sha256"] = checksum }
        }, timeout.Token);
        return new(key, contentType, snapshot.Length, checksum, options.Container, Provider);
    }

    public async Task<Stream> OpenReadAsync(string key, CancellationToken ct)
    {
        ValidateKey(key);
        using var timeout = Deadline(ct);
        var response = await container.GetBlobClient(key).DownloadStreamingAsync(cancellationToken: timeout.Token);
        await using var remote = response.Value.Content;
        var file = TemporaryFile();
        try
        {
            var actual = await CopyAndHashAsync(remote, file, timeout.Token);
            if (!response.Value.Details.Metadata.TryGetValue("sha256", out var expected)
                || !string.Equals(actual, expected, StringComparison.Ordinal))
                throw new InvalidDataException("Artifact checksum mismatch");
            file.Position = 0;
            return file;
        }
        catch
        {
            await file.DisposeAsync();
            throw;
        }
    }

    private async Task<string> CopyAndHashAsync(Stream input, Stream output, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long size = 0;
        int count;
        while ((count = await input.ReadAsync(buffer, ct)) > 0)
        {
            size += count;
            if (size > options.MaxArtifactBytes) throw new InvalidDataException("Artifact exceeds configured size limit");
            hash.AppendData(buffer, 0, count);
            await output.WriteAsync(buffer.AsMemory(0, count), ct);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private CancellationTokenSource Deadline(CancellationToken ct)
    {
        options.Validate();
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
        return timeout;
    }

    private static FileStream TemporaryFile() => new(Path.Combine(Path.GetTempPath(), "quality-azure-artifact-" + Guid.NewGuid().ToString("N")),
        FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.DeleteOnClose);

    public static void ValidateKey(string key)
    {
        if (!key.StartsWith(Prefix, StringComparison.Ordinal) || key.Length > 1000 || key.Any(char.IsControl)
            || key.Contains('\\') || key.Split('/').Any(segment => segment is "" or "." or ".."))
            throw new ArgumentException("Invalid artifact object key");
    }
}
