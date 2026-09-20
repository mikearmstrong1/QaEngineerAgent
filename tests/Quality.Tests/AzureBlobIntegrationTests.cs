using System.Text;
using Quality.Orchestrator;
using Xunit;

namespace Quality.Tests;

public sealed class AzureBlobFactAttribute : FactAttribute
{
    public AzureBlobFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("QUALITY_TEST_AZURE_CONNECTION_STRING")))
            Skip = "Set QUALITY_TEST_AZURE_CONNECTION_STRING to run Azure Blob integration checks";
    }
}

public sealed class AzureBlobIntegrationTests
{
    [AzureBlobFact]
    public async Task PrivateContainerUploadAndVerifiedDownload()
    {
        var options = new AzureBlobOptions(Environment.GetEnvironmentVariable("QUALITY_TEST_AZURE_CONNECTION_STRING"), null,
            "quality-test-" + Guid.NewGuid().ToString("N"));
        var client = AzureBlobArtifactStore.CreateClient(options);
        var store = new AzureBlobArtifactStore(client, options);
        try
        {
            await store.InitializeAsync(default);
            var properties = await client.GetPropertiesAsync();
            Assert.Null(properties.Value.PublicAccess);
            await using var input = new MemoryStream(Encoding.UTF8.GetBytes("azure artifact evidence"));
            var handle = await store.PutAsync("quality-system/runs/run/result.json", input, "application/json", default);
            Assert.Equal("AzureBlob", handle.Provider);
            Assert.Equal(options.Container, handle.Bucket);
            await using var output = await store.OpenReadAsync(handle.Key, default);
            Assert.Equal("azure artifact evidence", await new StreamReader(output).ReadToEndAsync());
        }
        finally { await client.DeleteIfExistsAsync(); }
    }
}
