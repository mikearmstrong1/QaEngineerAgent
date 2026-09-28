using System.Net;
using System.Text.Json;
using Azure.Core;
using Quality.Domain;
using Quality.Orchestrator;
using Xunit;

namespace Quality.Tests;

public sealed class AzureOpenAiPlanningTests
{
    private static PlanningPrompt Prompt => new(AppContext.BaseDirectory);
    private static Requirement Requirement => new("jira:AUTH-1427", new("jira", "AUTH-1427"), "Sign in", "Account access", [], [],
        [new("AC-1", "Valid credentials grant access"), new("AC-2", "Invalid credentials are rejected")], [], "revision-1", false);
    private static string Output => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "planning-output.json"));
    private static string Envelope => JsonSerializer.Serialize(new
    {
        id = "resp_azure", model = "deployment-snapshot", status = "completed",
        output = new[] { new { type = "message", role = "assistant", content = new[] { new { type = "output_text", text = Output } } } }
    });
    private sealed class Handler(Func<int, HttpResponseMessage> response) : HttpMessageHandler
    {
        public int Calls;
        public HttpRequestMessage? Request;
        public string? Body;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            Body = await request.Content!.ReadAsStringAsync(ct);
            return response(++Calls);
        }
    }
    private sealed class Credential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext context, CancellationToken ct)
            => new("identity-token", DateTimeOffset.UtcNow.AddHours(1));
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext context, CancellationToken ct)
            => ValueTask.FromResult(GetToken(context, ct));
    }
    private sealed class BrokenCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext context, CancellationToken ct)
            => throw new Azure.Identity.CredentialUnavailableException("private credential details");
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext context, CancellationToken ct)
            => throw new Azure.Identity.CredentialUnavailableException("private credential details");
    }
    private static HttpResponseMessage Response(HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(status == HttpStatusCode.OK ? Envelope : "private provider details") };

    [Fact]
    public async Task ApiKeyModeUsesAzureEndpointDeploymentAndMetadata()
    {
        using var handler = new Handler(_ => Response());
        using var http = new HttpClient(handler);
        var plan = await new AzureOpenAiPlanningProvider(http,
            new("https://example.openai.azure.com/", "quality-deployment", ApiKey: "server-secret"), Prompt)
            .PlanAsync(Requirement, PlanningPrompt.Version, default);
        Assert.Equal("azure-openai", plan.Planning!.Provider);
        Assert.Equal("quality-deployment", plan.Planning.RequestedModel);
        Assert.Equal("deployment-snapshot", plan.Planning.Model);
        Assert.Contains("2025-04-01-preview", plan.Planning.ProviderVersion);
        Assert.Equal("https://example.openai.azure.com/openai/v1/responses?api-version=2025-04-01-preview", handler.Request!.RequestUri!.AbsoluteUri);
        Assert.Equal("server-secret", Assert.Single(handler.Request.Headers.GetValues("api-key")));
        Assert.Null(handler.Request.Headers.Authorization);
        using var payload = JsonDocument.Parse(handler.Body!);
        Assert.Equal("quality-deployment", payload.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public async Task CredentialModeUsesBearerTokenAndRetainsBoundedRetries()
    {
        using var handler = new Handler(call => Response(call == 1 ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK));
        using var http = new HttpClient(handler);
        var plan = await new AzureOpenAiPlanningProvider(http,
            new("https://example.openai.azure.com", "quality-deployment"), Prompt, new Credential())
            .PlanAsync(Requirement, PlanningPrompt.Version, default);
        Assert.Equal(2, handler.Calls);
        Assert.Equal(2, plan.Planning!.Attempts);
        Assert.Equal("Bearer", handler.Request!.Headers.Authorization!.Scheme);
        Assert.Equal("identity-token", handler.Request.Headers.Authorization.Parameter);
        Assert.False(handler.Request.Headers.Contains("api-key"));
    }

    [Theory]
    [InlineData("http://example.openai.azure.com", "deployment", "2025-04-01-preview")]
    [InlineData("https://user:secret@example.openai.azure.com", "deployment", "2025-04-01-preview")]
    [InlineData("https://example.openai.azure.com?secret=x", "deployment", "2025-04-01-preview")]
    [InlineData("https://example.openai.azure.com", "", "2025-04-01-preview")]
    [InlineData("https://example.openai.azure.com", "deployment", "latest")]
    public void InvalidConfigurationFailsClosed(string endpoint, string deployment, string version)
        => Assert.Throws<ArgumentException>(() => new AzureOpenAiPlanningOptions(endpoint, deployment, version).Validate());

    [Fact]
    public async Task ProviderFailuresAreSanitized()
    {
        using var handler = new Handler(_ => Response(HttpStatusCode.Unauthorized));
        using var http = new HttpClient(handler);
        var provider = new AzureOpenAiPlanningProvider(http,
            new("https://example.openai.azure.com", "quality-deployment", ApiKey: "server-secret"), Prompt);
        var error = await Assert.ThrowsAsync<PlanningException>(() => provider.PlanAsync(Requirement, PlanningPrompt.Version, default));
        Assert.Equal("planning_http_error", error.Code);
        Assert.DoesNotContain("secret", error.ToString());
        Assert.Equal("azure-openai", error.Metadata.Provider);
    }

    [Fact]
    public async Task CredentialFailuresAreSanitized()
    {
        using var handler = new Handler(_ => throw new InvalidOperationException("HTTP must not be called"));
        using var http = new HttpClient(handler);
        var provider = new AzureOpenAiPlanningProvider(http,
            new("https://example.openai.azure.com", "quality-deployment"), Prompt, new BrokenCredential());
        var error = await Assert.ThrowsAsync<PlanningException>(() => provider.PlanAsync(Requirement, PlanningPrompt.Version, default));
        Assert.Equal("planning_auth_error", error.Code);
        Assert.DoesNotContain("private", error.ToString());
        Assert.Equal(0, handler.Calls);
    }
}
