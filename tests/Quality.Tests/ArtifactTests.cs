using System.Net;
using System.Security.Cryptography;
using System.Text;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Quality.Domain;
using Quality.Orchestrator;
using Quality.Persistence;
using Xunit;
namespace Quality.Tests;

public sealed class ArtifactTests
{
    private static MinioOptions Options => new("http://localhost:9000", "fixture", "fixture-secret", "test-artifacts");
    private sealed class Client : AmazonS3Client
    {
        public Client() : base(new AnonymousAWSCredentials(), new AmazonS3Config { ServiceURL = "http://localhost:9000", ForcePathStyle = true }) { }
        public bool Exists;
        public bool Corrupt;
        public bool Versioned;
        public LifecycleConfiguration Lifecycle = new() { Rules = [] };
        public readonly Dictionary<string, (byte[] Bytes, string ContentType, string Hash)> Objects = [];
        public override Task<GetBucketLocationResponse> GetBucketLocationAsync(GetBucketLocationRequest request, CancellationToken ct = default)
            => Exists ? Task.FromResult(new GetBucketLocationResponse()) : throw new AmazonS3Exception("missing") { StatusCode = HttpStatusCode.NotFound };
        public override Task<PutBucketResponse> PutBucketAsync(PutBucketRequest request, CancellationToken ct = default)
        { Exists = true; return Task.FromResult(new PutBucketResponse()); }
        public override Task<GetBucketVersioningResponse> GetBucketVersioningAsync(GetBucketVersioningRequest request, CancellationToken ct = default)
            => Task.FromResult(new GetBucketVersioningResponse { VersioningConfig = new S3BucketVersioningConfig { Status = Versioned ? VersionStatus.Enabled : VersionStatus.Off } });
        public override Task<GetLifecycleConfigurationResponse> GetLifecycleConfigurationAsync(GetLifecycleConfigurationRequest request, CancellationToken ct = default)
            => Task.FromResult(new GetLifecycleConfigurationResponse { Configuration = Lifecycle });
        public override Task<PutLifecycleConfigurationResponse> PutLifecycleConfigurationAsync(PutLifecycleConfigurationRequest request, CancellationToken ct = default)
        { Lifecycle = request.Configuration; return Task.FromResult(new PutLifecycleConfigurationResponse()); }
        public override async Task<PutObjectResponse> PutObjectAsync(PutObjectRequest request, CancellationToken ct = default)
        {
            using var bytes = new MemoryStream();
            await request.InputStream.CopyToAsync(bytes, ct);
            var hash = Convert.ToHexString(SHA256.HashData(bytes.ToArray())).ToLowerInvariant();
            Assert.Equal(Convert.ToBase64String(Convert.FromHexString(hash)), request.ChecksumSHA256);
            Objects[request.Key] = (bytes.ToArray(), request.ContentType, hash);
            return new();
        }
        public override Task<GetObjectResponse> GetObjectAsync(GetObjectRequest request, CancellationToken ct = default)
        {
            var value = Objects[request.Key];
            var response = new GetObjectResponse { ResponseStream = new MemoryStream(value.Bytes), ContentLength = value.Bytes.Length };
            response.Metadata["x-amz-meta-sha256"] = Corrupt ? new string('0', 64) : value.Hash;
            return Task.FromResult(response);
        }
    }
    [Fact]
    public async Task UploadPreservesChecksumTypeLengthAndVerifiedRead()
    {
        using var client = new Client();
        var store = new MinioArtifactStore(client, Options);
        using var input = new MemoryStream(Encoding.UTF8.GetBytes("trace evidence"));
        var handle = await store.PutAsync("quality-system/runs/run/trace.zip", input, "application/zip", default);
        Assert.Equal(14, handle.Length);
        Assert.Equal("application/zip", handle.ContentType);
        Assert.Equal("test-artifacts", handle.Bucket);
        await using var result = await store.OpenReadAsync(handle.Key, default);
        Assert.Equal("trace evidence", await new StreamReader(result).ReadToEndAsync());
        client.Corrupt = true;
        await Assert.ThrowsAsync<InvalidDataException>(() => store.OpenReadAsync(handle.Key, default));
    }
    [Fact]
    public async Task InitializationPreservesOtherRulesAndScopesRetention()
    {
        using var client = new Client();
        client.Lifecycle.Rules.Add(new LifecycleRule { Id = "unrelated" });
        var store = new MinioArtifactStore(client, Options);
        await store.InitializeAsync(default);
        await store.InitializeAsync(default);
        Assert.True(client.Exists);
        Assert.Equal(2, client.Lifecycle.Rules.Count);
        var owned = Assert.Single(client.Lifecycle.Rules, rule => rule.Id == "quality-system-retention");
        Assert.Equal(30, owned.Expiration.Days);
        Assert.Equal(MinioArtifactStore.Prefix, ((LifecyclePrefixPredicate)owned.Filter.LifecycleFilterPredicate).Prefix);
        client.Versioned = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.InitializeAsync(default));
    }
    [Theory]
    [InlineData("other/run")]
    [InlineData("quality-system/../private")]
    [InlineData("quality-system/a\\b")]
    public async Task InvalidKeysNeverUpload(string key)
    {
        using var client = new Client();
        await Assert.ThrowsAsync<ArgumentException>(() => new MinioArtifactStore(client, Options).PutAsync(key, Stream.Null, "text/plain", default));
        Assert.Empty(client.Objects);
    }
    [Fact]
    public async Task LimitsAndCancellationFailBeforeUpload()
    {
        using var client = new Client();
        var store = new MinioArtifactStore(client, Options with { MaxArtifactBytes = 2 });
        await Assert.ThrowsAsync<InvalidDataException>(() => store.PutAsync("quality-system/large", new MemoryStream([1, 2, 3]), "image/png", default));
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.PutAsync("quality-system/cancel", new MemoryStream([1]), "text/plain", cancel.Token));
        Assert.Empty(client.Objects);
    }

    private sealed class FlakyStore : IArtifactStore
    {
        public int Calls;
        public bool Fail = true;
        public async Task<ArtifactHandle> PutAsync(string key, Stream content, string type, CancellationToken ct)
        {
            if (++Calls == 2 && Fail) throw new Exception("private provider secret");
            var size = content.Length;
            return new(key, type, size, Convert.ToHexString(await SHA256.HashDataAsync(content, ct)).ToLowerInvariant(), "test-bucket");
        }
        public Task<Stream> OpenReadAsync(string key, CancellationToken ct) => throw new NotSupportedException();
    }
    [Fact]
    public async Task PartialFailureIsResumableAndNeverChangesExecutionOutcome()
    {
        var root = Path.Combine(Path.GetTempPath(), "quality-artifact-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var runs = new FileTestRunStore(root);
            var id = Guid.NewGuid().ToString("N");
            var run = new TestRun(id, "plan", "Failed", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, [id + "/report.json", id + "/process.log"], "test");
            await runs.SaveAsync(run, default);
            await File.WriteAllTextAsync(Path.Combine(runs.DirectoryFor(id), "report.json"), "{}");
            await File.WriteAllTextAsync(Path.Combine(runs.DirectoryFor(id), "process.log"), "failure");
            var store = new FlakyStore();
            var publisher = new RunArtifactPublisher(runs, store);
            var partial = await publisher.PublishAsync(id, default);
            Assert.Equal("Failed", partial.Status);
            Assert.Equal("Failed", partial.ArtifactUploadStatus);
            Assert.Single(partial.StoredArtifacts!);
            store.Fail = false;
            var complete = await publisher.PublishAsync(id, default);
            Assert.Equal("Failed", complete.Status);
            Assert.Equal("Uploaded", complete.ArtifactUploadStatus);
            Assert.Equal(2, complete.StoredArtifacts!.Length);
            var analysis = new FailureAnalysis(Guid.NewGuid().ToString("N"), id, "Unclassified", "Review required", [id + "/process.log"], 0.5, true);
            var linked = await publisher.AssociateFailureAsync(analysis, default);
            Assert.StartsWith("quality-system/runs/", Assert.Single(linked.EvidenceArtifactKeys));
            Assert.Single(linked.EvidenceArtifacts!);
            Assert.True(File.Exists(Path.Combine(runs.DirectoryFor(id), "analyses", analysis.Id + ".json")));
            await Assert.ThrowsAsync<ArgumentException>(() => publisher.AssociateFailureAsync(analysis with { Id = Guid.NewGuid().ToString("N"), EvidenceArtifactKeys = ["another-run/file"] }, default));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Fact]
    public async Task LinkedFilesAreNotUploaded()
    {
        var root = Path.Combine(Path.GetTempPath(), "quality-artifact-link-" + Guid.NewGuid().ToString("N"));
        try
        {
            var runs = new FileTestRunStore(root);
            var id = Guid.NewGuid().ToString("N");
            await runs.SaveAsync(new(id, "plan", "Passed", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, [id + "/link"], "test"), default);
            File.CreateSymbolicLink(Path.Combine(runs.DirectoryFor(id), "link"), Path.Combine(root, "outside"));
            var store = new FlakyStore();
            var run = await new RunArtifactPublisher(runs, store).PublishAsync(id, default);
            Assert.Equal("Failed", run.ArtifactUploadStatus);
            Assert.Equal("Passed", run.Status);
            Assert.Equal(0, store.Calls);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
