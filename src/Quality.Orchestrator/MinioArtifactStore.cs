using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace Quality.Orchestrator;

public sealed record MinioOptions(string Endpoint, string AccessKey, string SecretKey, string Bucket,
    int RetentionDays = 30, long MaxArtifactBytes = 134217728, int TimeoutSeconds = 60)
{
    public void Validate()
    {
        if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
            || uri.AbsolutePath != "/" || uri.UserInfo != "" || uri.Query != "" || uri.Fragment != "")
            throw new ArgumentException("MinIO endpoint must be an HTTP(S) origin");
        if (string.IsNullOrWhiteSpace(AccessKey) || string.IsNullOrWhiteSpace(SecretKey)
            || !Regex.IsMatch(Bucket, "^[a-z0-9][a-z0-9-]{1,61}[a-z0-9]$"))
            throw new ArgumentException("MinIO credentials and a valid bucket name are required");
        if (RetentionDays is < 1 or > 3650 || MaxArtifactBytes is < 1 or > 1073741824 || TimeoutSeconds is < 1 or > 300)
            throw new ArgumentException("MinIO limits are outside supported bounds");
    }
}

public sealed class MinioArtifactStore(IAmazonS3 client, MinioOptions options) : IRemoteArtifactStore
{
    public const string Prefix = "quality-system/";
    public string Provider => "MinIO";
    public ArtifactStoreInfo Info => new(Provider, options.Bucket, Prefix, options.RetentionDays, true);
    public static AmazonS3Client CreateClient(MinioOptions options)
    {
        options.Validate();
        return new AmazonS3Client(new BasicAWSCredentials(options.AccessKey, options.SecretKey), new AmazonS3Config
        {
            ServiceURL = options.Endpoint, ForcePathStyle = true, AuthenticationRegion = "us-east-1",
            MaxErrorRetry = 2, Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds),
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED
        });
    }

    // Explicit administrative operation; ordinary uploads never replace bucket lifecycle settings.
    public async Task InitializeAsync(CancellationToken ct)
    {
        using var timeout = Deadline(ct);
        try { await client.GetBucketLocationAsync(new GetBucketLocationRequest { BucketName = options.Bucket }, timeout.Token); }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound || ex.ErrorCode == "NoSuchBucket")
        {
            try { await client.PutBucketAsync(new PutBucketRequest { BucketName = options.Bucket }, timeout.Token); }
            catch (AmazonS3Exception race) when (race.ErrorCode == "BucketAlreadyOwnedByYou") { }
        }
        var versioning = await client.GetBucketVersioningAsync(new GetBucketVersioningRequest { BucketName = options.Bucket }, timeout.Token);
        if (versioning.VersioningConfig?.Status == VersionStatus.Enabled || versioning.VersioningConfig?.Status == VersionStatus.Suspended)
            throw new InvalidOperationException("Artifact retention requires a dedicated unversioned bucket");
        LifecycleConfiguration lifecycle;
        try { lifecycle = (await client.GetLifecycleConfigurationAsync(new GetLifecycleConfigurationRequest { BucketName = options.Bucket }, timeout.Token)).Configuration ?? new(); }
        catch (AmazonS3Exception ex) when (ex.ErrorCode == "NoSuchLifecycleConfiguration") { lifecycle = new(); }
        var rules = lifecycle.Rules ?? [];
        var owned = rules.SingleOrDefault(rule => rule.Id == "quality-system-retention");
        if (owned is not null && (owned.Filter?.LifecycleFilterPredicate is not LifecyclePrefixPredicate predicate || predicate.Prefix != Prefix))
            throw new InvalidOperationException("Artifact retention rule ID is already used by another prefix");
        rules.RemoveAll(rule => rule.Id == "quality-system-retention");
        rules.Add(new LifecycleRule
        {
            Id = "quality-system-retention", Status = LifecycleRuleStatus.Enabled,
            Filter = new LifecycleFilter { LifecycleFilterPredicate = new LifecyclePrefixPredicate { Prefix = Prefix } },
            Expiration = new LifecycleRuleExpiration { Days = options.RetentionDays }
        });
        lifecycle.Rules = rules;
        await client.PutLifecycleConfigurationAsync(new PutLifecycleConfigurationRequest { BucketName = options.Bucket, Configuration = lifecycle }, timeout.Token);
    }

    public async Task<ArtifactHandle> PutAsync(string key, Stream content, string contentType, CancellationToken ct)
    {
        ValidateKey(key);
        if (!MediaTypeHeaderValue.TryParse(contentType, out _)) throw new ArgumentException("Invalid artifact content type");
        using var timeout = Deadline(ct);
        await using var snapshot = TemporaryFile();
        var checksum = await CopyAndHashAsync(content, snapshot, timeout.Token);
        snapshot.Position = 0;
        var request = new PutObjectRequest
        {
            BucketName = options.Bucket, Key = key, InputStream = snapshot, ContentType = contentType,
            AutoCloseStream = false, UseChunkEncoding = false, ChecksumSHA256 = Convert.ToBase64String(Convert.FromHexString(checksum))
        };
        request.Metadata["sha256"] = checksum;
        await client.PutObjectAsync(request, timeout.Token);
        return new(key, contentType, snapshot.Length, checksum, options.Bucket, Provider);
    }

    public async Task<Stream> OpenReadAsync(string key, CancellationToken ct)
    {
        ValidateKey(key);
        using var timeout = Deadline(ct);
        using var response = await client.GetObjectAsync(new GetObjectRequest { BucketName = options.Bucket, Key = key }, timeout.Token);
        var file = TemporaryFile();
        try
        {
            var actual = await CopyAndHashAsync(response.ResponseStream, file, timeout.Token);
            var expected = response.Metadata["x-amz-meta-sha256"];
            if (!string.Equals(actual, expected, StringComparison.Ordinal)) throw new InvalidDataException("Artifact checksum mismatch");
            file.Position = 0;
            return file; // DeleteOnClose: caller owns the verified stream.
        }
        catch { await file.DisposeAsync(); throw; }
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
    private static FileStream TemporaryFile() => new(Path.Combine(Path.GetTempPath(), "quality-artifact-" + Guid.NewGuid().ToString("N")),
        FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
    public static void ValidateKey(string key)
    {
        if (!key.StartsWith(Prefix, StringComparison.Ordinal) || key.Length > 1000 || key.Any(char.IsControl)
            || key.Contains('\\') || key.Split('/').Any(segment => segment is "" or "." or ".."))
            throw new ArgumentException("Invalid artifact object key");
    }
}
