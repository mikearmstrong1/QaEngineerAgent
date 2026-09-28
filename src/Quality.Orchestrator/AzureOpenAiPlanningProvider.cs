using System.Text.RegularExpressions;
using Azure;
using Azure.Core;
using Azure.Identity;

namespace Quality.Orchestrator;

public sealed record AzureOpenAiPlanningOptions(
    string Endpoint,
    string Deployment,
    string ApiVersion = "2025-04-01-preview",
    string? ApiKey = null,
    int MaxAttempts = 3,
    int AttemptTimeoutSeconds = 20,
    int TotalTimeoutSeconds = 75,
    int MaxOutputTokens = 8192)
{
    public void Validate()
    {
        if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(endpoint.UserInfo)
            || endpoint.AbsolutePath.Trim('/') != ""
            || !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment))
            throw new ArgumentException("Azure OpenAI endpoint must be an HTTPS origin without credentials, query, or fragment");
        if (string.IsNullOrWhiteSpace(Deployment) || Deployment.Length > 200 || Deployment.Any(char.IsControl)
            || !Regex.IsMatch(ApiVersion, "^[0-9]{4}-[0-9]{2}-[0-9]{2}(?:-preview)?$", RegexOptions.CultureInvariant))
            throw new ArgumentException("Azure OpenAI deployment and API version are required");
        new PlanningOptions("configured-server-side", Deployment, MaxAttempts, AttemptTimeoutSeconds,
            TotalTimeoutSeconds, MaxOutputTokens).Validate();
    }
}

/// <summary>Azure OpenAI Responses API adapter. Authentication remains server-side.</summary>
public sealed class AzureOpenAiPlanningProvider : OpenAiPlanningProvider
{
    private static readonly TokenRequestContext TokenContext = new(["https://cognitiveservices.azure.com/.default"]);
    private readonly AzureOpenAiPlanningOptions azure;
    private readonly TokenCredential? credential;
    private readonly Uri requestUri;

    public override string Name => "azure-openai";
    protected override string ProviderRevision => $"azure-responses/{azure.ApiVersion};quality-planner/v1";
    protected override Uri RequestUri => requestUri;

    public AzureOpenAiPlanningProvider(HttpClient http, AzureOpenAiPlanningOptions options, PlanningPrompt prompt,
        TokenCredential? credential = null)
        : base(http, ToPlanningOptions(options), prompt)
    {
        options.Validate();
        azure = options;
        this.credential = string.IsNullOrWhiteSpace(options.ApiKey)
            ? credential ?? new DefaultAzureCredential()
            : credential;
        var endpoint = options.Endpoint.TrimEnd('/');
        requestUri = new Uri($"{endpoint}/openai/v1/responses?api-version={Uri.EscapeDataString(options.ApiVersion)}");
    }

    protected override async ValueTask AuthorizeAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(azure.ApiKey))
        {
            request.Headers.TryAddWithoutValidation("api-key", azure.ApiKey);
            return;
        }
        try
        {
            var token = await credential!.GetTokenAsync(TokenContext, ct);
            request.Headers.Authorization = new("Bearer", token.Token);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is AuthenticationFailedException or CredentialUnavailableException or RequestFailedException)
        {
            throw new AzurePlanningAuthenticationException();
        }
    }

    private static PlanningOptions ToPlanningOptions(AzureOpenAiPlanningOptions options)
    {
        options.Validate();
        return new("configured-server-side", options.Deployment, options.MaxAttempts,
            options.AttemptTimeoutSeconds, options.TotalTimeoutSeconds, options.MaxOutputTokens);
    }
}

internal sealed class AzurePlanningAuthenticationException : Exception { }
