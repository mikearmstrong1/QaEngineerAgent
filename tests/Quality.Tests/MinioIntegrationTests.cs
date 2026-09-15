using System.Text.Json;
using System.Diagnostics;
using Amazon.S3.Model;
using Quality.Domain;
using Quality.Orchestrator;
using Quality.Persistence;
using Xunit;
namespace Quality.Tests;

public sealed class MinioFactAttribute : FactAttribute
{
    public MinioFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("QUALITY_TEST_MINIO_ENDPOINT")))
            Skip = "Set QUALITY_TEST_MINIO_ENDPOINT, QUALITY_TEST_MINIO_ACCESS_KEY and QUALITY_TEST_MINIO_SECRET_KEY";
    }
}
public sealed class MinioIntegrationTests
{
    [MinioFact]
    public async Task RealBucketRetentionUploadDownloadAndRunAssociation()
    {
        var options = new MinioOptions(Environment.GetEnvironmentVariable("QUALITY_TEST_MINIO_ENDPOINT")!,
            Environment.GetEnvironmentVariable("QUALITY_TEST_MINIO_ACCESS_KEY")!, Environment.GetEnvironmentVariable("QUALITY_TEST_MINIO_SECRET_KEY")!,
            "quality-test-" + Guid.NewGuid().ToString("N"));
        using var client = MinioArtifactStore.CreateClient(options);
        var store = new MinioArtifactStore(client, options);
        var root = Path.Combine(Path.GetTempPath(), options.Bucket);
        try
        {
            await CliAsync(options, root, "init-artifacts");
            await store.InitializeAsync(default);
            var lifecycle = await client.GetLifecycleConfigurationAsync(new GetLifecycleConfigurationRequest { BucketName = options.Bucket });
            Assert.Equal(30, Assert.Single(lifecycle.Configuration.Rules).Expiration.Days);
            var runs = new FileTestRunStore(root);
            var id = Guid.NewGuid().ToString("N");
            await runs.SaveAsync(new(id, "plan", "Failed", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
                [id + "/report.json", id + "/screen.png", id + "/trace.zip", id + "/process.log"], "fixture"), default);
            foreach (var name in new[] { "report.json", "screen.png", "trace.zip", "process.log" })
                await File.WriteAllTextAsync(Path.Combine(runs.DirectoryFor(id), name), "fixture evidence for " + name);
            var publisher = new RunArtifactPublisher(runs, store);
            var cliResult = await CliAsync(options, root, "publish-artifacts", "--run", id);
            var run = JsonSerializer.Deserialize<TestRun>(cliResult, ContractJson.Options)!;
            Assert.Equal("Uploaded", run.ArtifactUploadStatus);
            Assert.Equal(4, run.StoredArtifacts!.Length);
            foreach (var artifact in run.StoredArtifacts)
            {
                await using var stream = await store.OpenReadAsync(artifact.ObjectKey, default);
                Assert.Equal("fixture evidence for " + Path.GetFileName(artifact.LocalKey), await new StreamReader(stream).ReadToEndAsync());
                var metadata = await client.GetObjectMetadataAsync(options.Bucket, artifact.ObjectKey);
                Assert.Equal(artifact.ContentType, metadata.Headers.ContentType);
                Assert.Equal(artifact.Sha256, metadata.Metadata["x-amz-meta-sha256"]);
            }
            var again = await publisher.PublishAsync(id, default);
            Assert.Equal(run.StoredArtifacts.Select(a => a.ObjectKey), again.StoredArtifacts!.Select(a => a.ObjectKey));
            var analysis = await publisher.AssociateFailureAsync(new(Guid.NewGuid().ToString("N"), id, "Unclassified", "Fixture review", [id + "/trace.zip"], 0.5, true), default);
            Assert.Equal(run.StoredArtifacts.Single(a => a.LocalKey.EndsWith("trace.zip")).ObjectKey, Assert.Single(analysis.EvidenceArtifactKeys));
            Assert.Equal("Uploaded", (await new FileTestRunStore(root).GetAsync(id, default))!.ArtifactUploadStatus);
        }
        finally
        {
            try
            {
                var objects = await client.ListObjectsV2Async(new() { BucketName = options.Bucket });
                foreach (var item in objects.S3Objects ?? []) await client.DeleteObjectAsync(options.Bucket, item.Key);
                await client.DeleteBucketAsync(options.Bucket);
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }
    }
    private static async Task<string> CliAsync(MinioOptions options, string runs, params string[] arguments)
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(Path.Combine(root, "src/Quality.Api/bin/Release/net10.0/Quality.Api.dll"));
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["Quality__Store"] = "File";
        start.Environment["Quality__DataDirectory"] = Path.Combine(runs, "jobs");
        start.Environment["Quality__Planning__Mode"] = "Stub";
        start.Environment["Quality__Requirements__Mode"] = "Stub";
        start.Environment["Quality__Execution__RunDirectory"] = runs;
        start.Environment["Quality__Artifacts__Mode"] = "MinIO";
        start.Environment["Quality__Artifacts__Endpoint"] = options.Endpoint;
        start.Environment["Quality__Artifacts__AccessKey"] = options.AccessKey;
        start.Environment["Quality__Artifacts__SecretKey"] = options.SecretKey;
        start.Environment["Quality__Artifacts__Bucket"] = options.Bucket;
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { if (!process.HasExited) process.Kill(true); throw; }
        await stderr;
        Assert.Equal(0, process.ExitCode);
        return await stdout;
    }

}
