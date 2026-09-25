using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.RegularExpressions;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
var apiUrl = builder.Configuration["Quality:CommandCenter:ApiUrl"] ?? "http://127.0.0.1:5080";
if (!Uri.TryCreate(apiUrl, UriKind.Absolute, out var apiBase) || apiBase.Scheme is not ("http" or "https") ||
    apiBase.UserInfo.Length != 0 || apiBase.Query.Length != 0 || apiBase.Fragment.Length != 0)
    throw new ArgumentException("Quality__CommandCenter__ApiUrl must be an HTTP(S) origin");
var apiKey = builder.Configuration["Quality:CommandCenter:ApiKey"] ?? "";
builder.Services.AddHttpClient("quality", client =>
{
    client.BaseAddress = apiBase;
    client.Timeout = TimeSpan.FromSeconds(20);
    if (!string.IsNullOrEmpty(apiKey)) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
});
builder.Services.AddHttpClient("quality-artifacts", client =>
{
    client.BaseAddress = apiBase;
    client.Timeout = TimeSpan.FromMinutes(6);
    if (!string.IsNullOrEmpty(apiKey)) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
});
builder.Services.AddHttpClient("quality-execution", client =>
{
    client.BaseAddress = apiBase;
    client.Timeout = TimeSpan.FromMinutes(6);
    if (!string.IsNullOrEmpty(apiKey)) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
});

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions { OnPrepareResponse = context =>
    context.Context.Response.Headers.CacheControl = "no-cache" });

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "quality-command-center" }));
app.MapGet("/bff/status", async (IHttpClientFactory factory, CancellationToken ct) =>
    await ForwardAsync(factory, HttpMethod.Get, "/ready", null, ct));
app.MapGet("/bff/jobs", async (int? limit, string? cursor, IHttpClientFactory factory, CancellationToken ct) =>
{
    var size = limit ?? 20;
    if (size is < 1 or > 50) return Results.BadRequest(new { error = "limit_must_be_1_to_50" });
    var path = $"/jobs?limit={size}" + (string.IsNullOrEmpty(cursor) ? "" : $"&cursor={Uri.EscapeDataString(cursor)}");
    return await ForwardAsync(factory, HttpMethod.Get, path, null, ct);
});
app.MapGet("/bff/jobs/{id}", async (string id, IHttpClientFactory factory, CancellationToken ct) =>
{
    if (!Guid.TryParseExact(id, "N", out _)) return Results.BadRequest(new { error = "invalid_job_id" });
    return await ForwardAsync(factory, HttpMethod.Get, $"/jobs/{id}", null, ct);
});
app.MapGet("/bff/jobs/{id}/runs", async (string id, IHttpClientFactory factory, CancellationToken ct) =>
{
    if (!Guid.TryParseExact(id, "N", out _)) return Results.BadRequest(new { error = "invalid_job_id" });
    return await ForwardAsync(factory, HttpMethod.Get, $"/jobs/{id}/runs", null, ct);
});
app.MapGet("/bff/jobs/{id}/execution-requests", async (string id, IHttpClientFactory factory, CancellationToken ct) =>
{
    if (!Guid.TryParseExact(id, "N", out _)) return Results.BadRequest(new { error = "invalid_job_id" });
    return await ForwardAsync(factory, HttpMethod.Get, $"/jobs/{id}/execution-requests", null, ct);
});
app.MapGet("/bff/jobs/{id}/automation-workflows", async (string id, IHttpClientFactory factory, CancellationToken ct) =>
{
    if (!Guid.TryParseExact(id, "N", out _)) return Results.BadRequest(new { error = "invalid_job_id" });
    return await ForwardAsync(factory, HttpMethod.Get, $"/jobs/{id}/automation-workflows", null, ct);
});
app.MapGet("/bff/execution-policy", async (IHttpClientFactory factory, CancellationToken ct) =>
    await ForwardAsync(factory, HttpMethod.Get, "/execution-policy", null, ct));
app.MapGet("/bff/execution-policies", async (IHttpClientFactory factory, CancellationToken ct) =>
    await ForwardAsync(factory, HttpMethod.Get, "/execution-policies", null, ct));
app.MapPut("/bff/execution-policies/{name}", async Task<IResult> (string name, HttpRequest request, ExecutionPolicyInput input, IHttpClientFactory factory, CancellationToken ct) =>
{
    if (request.Headers["X-Command-Center"] != "1") return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (!Regex.IsMatch(name, "^[a-z0-9][a-z0-9-]{0,99}$")) return Results.BadRequest(new { error = "invalid_policy_name" });
    if (!string.Equals(name, input.Name, StringComparison.Ordinal)) return Results.BadRequest(new { error = "policy_name_mismatch" });
    return await ForwardAsync(factory, HttpMethod.Put, $"/execution-policies/{name}", JsonContent.Create(input), ct);
});
app.MapPost("/bff/execution-policies/{name}/revisions/{version}/{operation}", async Task<IResult> (string name, string version,
    string operation, HttpRequest request, IHttpClientFactory factory, CancellationToken ct) =>
{
    if (request.Headers["X-Command-Center"] != "1") return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (!Regex.IsMatch(name, "^[a-z0-9][a-z0-9-]{0,99}$") || string.IsNullOrWhiteSpace(version) || version.Length > 100)
        return Results.BadRequest(new { error = "invalid_policy_revision" });
    if (operation is not ("activate" or "disable" or "retire")) return Results.NotFound();
    return await ForwardAsync(factory, HttpMethod.Post,
        $"/execution-policies/{Uri.EscapeDataString(name)}/revisions/{Uri.EscapeDataString(version)}/{operation}", null, ct);
});
app.MapPost("/bff/jobs/{id}/execution-requests", async Task<IResult> (string id, HttpRequest request, CreateExecution input, IHttpClientFactory factory, CancellationToken ct) =>
{
    if (request.Headers["X-Command-Center"] != "1") return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (!Guid.TryParseExact(id, "N", out _)) return Results.BadRequest(new { error = "invalid_job_id" });
    if (!Uri.TryCreate(input.Target, UriKind.Absolute, out var target) || target.Scheme is not ("http" or "https") || target.UserInfo.Length != 0)
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["target"] = ["Enter an absolute HTTP(S) target without credentials"] });
    return await ForwardAsync(factory, HttpMethod.Post, $"/jobs/{id}/execution-requests", JsonContent.Create(input), ct);
});
app.MapPost("/bff/jobs/{id}/autonomous-executions", async Task<IResult> (string id, HttpRequest request, CreateAutonomousExecution input, IHttpClientFactory factory, CancellationToken ct) =>
{
    if (request.Headers["X-Command-Center"] != "1") return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (!Guid.TryParseExact(id, "N", out _)) return Results.BadRequest(new { error = "invalid_job_id" });
    return await ForwardExecutionAsync(factory, HttpMethod.Post, $"/jobs/{id}/autonomous-executions", JsonContent.Create(input), ct);
});
app.MapPost("/bff/automation-workflows/{id}/review", async Task<IResult> (string id, HttpRequest request,
    ReviewAutomationWorkflow input, IHttpClientFactory factory, CancellationToken ct) =>
{
    if (request.Headers["X-Command-Center"] != "1") return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (!Guid.TryParseExact(id, "N", out _)) return Results.BadRequest(new { error = "invalid_automation_workflow_id" });
    return await ForwardAsync(factory, HttpMethod.Post, $"/automation-workflows/{id}/review", JsonContent.Create(input), ct);
});
app.MapPost("/bff/automation-workflows/{id}/cancel", async Task<IResult> (string id, HttpRequest request,
    IHttpClientFactory factory, CancellationToken ct) =>
{
    if (request.Headers["X-Command-Center"] != "1") return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (!Guid.TryParseExact(id, "N", out _)) return Results.BadRequest(new { error = "invalid_automation_workflow_id" });
    return await ForwardAsync(factory, HttpMethod.Post, $"/automation-workflows/{id}/cancel", null, ct);
});
app.MapPut("/bff/execution-requests/{id}/manifest", async Task<IResult> (string id, HttpRequest request, UpdateExecution input, IHttpClientFactory factory, CancellationToken ct) =>
{
    if (request.Headers["X-Command-Center"] != "1") return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (!Guid.TryParseExact(id, "N", out _)) return Results.BadRequest(new { error = "invalid_execution_request_id" });
    return await ForwardAsync(factory, HttpMethod.Put, $"/execution-requests/{id}/manifest", JsonContent.Create(input), ct);
});
app.MapPost("/bff/execution-requests/{id}/inspect", async Task<IResult> (string id, HttpRequest request, IHttpClientFactory factory, CancellationToken ct) =>
{
    if (request.Headers["X-Command-Center"] != "1") return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (!Guid.TryParseExact(id, "N", out _)) return Results.BadRequest(new { error = "invalid_execution_request_id" });
    return await ForwardExecutionAsync(factory, HttpMethod.Post, $"/execution-requests/{id}/inspect", null, ct);
});
app.MapPost("/bff/execution-requests/{id}/prepare-manifest", async Task<IResult> (string id, HttpRequest request, PrepareExecution input, IHttpClientFactory factory, CancellationToken ct) =>
{
    if (request.Headers["X-Command-Center"] != "1") return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (!Guid.TryParseExact(id, "N", out _)) return Results.BadRequest(new { error = "invalid_execution_request_id" });
    return await ForwardExecutionAsync(factory, HttpMethod.Post, $"/execution-requests/{id}/prepare-manifest", JsonContent.Create(input), ct);
});
app.MapPost("/bff/execution-requests/{id}/approve", async Task<IResult> (string id, HttpRequest request, ApproveExecution input, IHttpClientFactory factory, CancellationToken ct) =>
{
    if (request.Headers["X-Command-Center"] != "1") return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (!Guid.TryParseExact(id, "N", out _)) return Results.BadRequest(new { error = "invalid_execution_request_id" });
    if (!Regex.IsMatch(input.ReviewedManifestHash ?? "", "^[a-f0-9]{64}$") || string.IsNullOrWhiteSpace(input.Reviewer))
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["approval"] = ["Confirm the exact SHA-256 and provide a reviewer identity"] });
    return await ForwardAsync(factory, HttpMethod.Post, $"/execution-requests/{id}/approve", JsonContent.Create(input), ct);
});
app.MapPost("/bff/execution-requests/{id}/launch", async Task<IResult> (string id, HttpRequest request, LaunchExecution input, IHttpClientFactory factory, CancellationToken ct) =>
{
    if (request.Headers["X-Command-Center"] != "1") return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (!Guid.TryParseExact(id, "N", out _)) return Results.BadRequest(new { error = "invalid_execution_request_id" });
    return await ForwardExecutionAsync(factory, HttpMethod.Post, $"/execution-requests/{id}/launch", JsonContent.Create(input), ct);
});
app.MapGet("/bff/runs/{id}/artifacts", async (string id, string? key, bool? download, HttpContext context,
    IHttpClientFactory factory, CancellationToken ct) =>
{
    if (!Guid.TryParseExact(id, "N", out _))
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsJsonAsync(new { error = "invalid_run_id" }, ct);
        return;
    }
    if (string.IsNullOrWhiteSpace(key) || key.Length > 1000 || key.Any(char.IsControl))
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsJsonAsync(new { error = "invalid_artifact_key" }, ct);
        return;
    }
    await ForwardArtifactAsync(factory, context, id, key, download == true, ct);
});
app.MapPost("/bff/runs/{id}/classification", async Task<IResult> (string id, HttpRequest request, FailureReview input, IHttpClientFactory factory, CancellationToken ct) =>
{
    if (request.Headers["X-Command-Center"] != "1") return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (!Guid.TryParseExact(id, "N", out _)) return Results.BadRequest(new { error = "invalid_run_id" });
    if (input.Classification is not ("ApplicationFailure" or "TestFailure" or "InfrastructureFailure") ||
        string.IsNullOrWhiteSpace(input.Reason) || input.Reason.Length > 4000)
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["review"] = ["Choose a classification and enter a reason of 1-4000 characters"] });
    return await ForwardAsync(factory, HttpMethod.Post, $"/runs/{id}/classification",
        JsonContent.Create(new { input.Classification, Reason = input.Reason.Trim() }), ct);
});
app.MapPost("/bff/jobs/{jobId}/runs/{runId}/promotion", async Task<IResult> (string jobId, string runId, HttpRequest request, PromotionReview input, IHttpClientFactory factory, CancellationToken ct) =>
{
    if (request.Headers["X-Command-Center"] != "1") return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (!Guid.TryParseExact(jobId, "N", out _) || !Guid.TryParseExact(runId, "N", out _)) return Results.BadRequest(new { error = "invalid_job_or_run_id" });
    if (!Regex.IsMatch(input.ReviewedManifestHash ?? "", "^[a-f0-9]{64}$"))
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["promotion"] = ["Review the manifest SHA-256 before creating a patch"] });
    return await ForwardAsync(factory, HttpMethod.Post, $"/jobs/{jobId}/runs/{runId}/promotion", JsonContent.Create(input), ct);
});
app.MapGet("/bff/regression-proposals/{runId}", async (string runId, IHttpClientFactory factory, CancellationToken ct) =>
{
    if (!Guid.TryParseExact(runId, "N", out _)) return Results.BadRequest(new { error = "invalid_run_id" });
    return await ForwardAsync(factory, HttpMethod.Get, $"/regression-proposals/{runId}", null, ct);
});
app.MapPost("/bff/regression-proposals/{runId}/apply", async Task<IResult> (string runId, HttpRequest request, ApplyProposalReview input, IHttpClientFactory factory, CancellationToken ct) =>
{
    if (request.Headers["X-Command-Center"] != "1") return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (!Guid.TryParseExact(runId, "N", out _)) return Results.BadRequest(new { error = "invalid_run_id" });
    if (!Regex.IsMatch(input.ReviewedPatchSha256 ?? "", "^[a-f0-9]{64}$"))
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["proposal"] = ["Review the patch SHA-256 before applying it"] });
    return await ForwardAsync(factory, HttpMethod.Post, $"/regression-proposals/{runId}/apply", JsonContent.Create(input), ct);
});
app.MapPost("/bff/jobs", async Task<IResult> (HttpRequest request, SubmitRequest input, IHttpClientFactory factory, CancellationToken ct) =>
{
    if (request.Headers["X-Command-Center"] != "1") return Results.StatusCode(StatusCodes.Status403Forbidden);
    var key = input.JiraKey?.Trim().ToUpperInvariant() ?? "";
    if (!Regex.IsMatch(key, "^[A-Z][A-Z0-9_]*-[1-9][0-9]*$"))
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["jiraKey"] = ["Use a Jira key such as KAN-4"] });
    var payload = JsonContent.Create(new { reference = new { source = "jira", id = key } });
    return await ForwardAsync(factory, HttpMethod.Post, "/jobs", payload, ct);
});

app.MapFallbackToFile("index.html");
await app.RunAsync();

static async Task<IResult> ForwardAsync(IHttpClientFactory factory, HttpMethod method, string path, HttpContent? content, CancellationToken ct)
{
    try
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        using var response = await factory.CreateClient("quality").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        var body = await response.Content.ReadAsByteArrayAsync(ct);
        var contentType = response.Content.Headers.ContentType?.ToString() ?? "application/json";
        return Results.Content(Encoding.UTF8.GetString(body), contentType, statusCode: (int)response.StatusCode);
    }
    catch (HttpRequestException) { return Results.Json(new { error = "quality_api_unavailable" }, statusCode: 503); }
    catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return Results.Json(new { error = "quality_api_timeout" }, statusCode: 504); }
}

static async Task<IResult> ForwardExecutionAsync(IHttpClientFactory factory, HttpMethod method, string path, HttpContent? content, CancellationToken ct)
{
    try
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        using var response = await factory.CreateClient("quality-execution").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        var body = await response.Content.ReadAsByteArrayAsync(ct);
        return Results.Content(Encoding.UTF8.GetString(body), response.Content.Headers.ContentType?.ToString() ?? "application/json", statusCode: (int)response.StatusCode);
    }
    catch (HttpRequestException) { return Results.Json(new { error = "quality_api_unavailable" }, statusCode: 503); }
    catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return Results.Json(new { error = "execution_timeout" }, statusCode: 504); }
}

static async Task ForwardArtifactAsync(IHttpClientFactory factory, HttpContext context, string runId, string key, bool download, CancellationToken ct)
{
    try
    {
        var path = $"/runs/{runId}/artifacts?key={Uri.EscapeDataString(key)}&download={download.ToString().ToLowerInvariant()}";
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        using var upstream = await factory.CreateClient("quality-artifacts").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        context.Response.StatusCode = (int)upstream.StatusCode;
        context.Response.Headers.CacheControl = "private,no-store";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.Headers.ContentSecurityPolicy = "sandbox; default-src 'none'";
        context.Response.ContentType = upstream.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
        if (upstream.Content.Headers.ContentLength is { } length) context.Response.ContentLength = length;
        if (upstream.Content.Headers.ContentDisposition is { } disposition)
            context.Response.Headers.ContentDisposition = disposition.ToString();
        if (upstream.Headers.TryGetValues("X-Artifact-Content-Type", out var originalTypes))
            context.Response.Headers["X-Artifact-Content-Type"] = originalTypes.Single();
        await using var stream = await upstream.Content.ReadAsStreamAsync(ct);
        await stream.CopyToAsync(context.Response.Body, ct);
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
    catch (Exception) when (!context.Response.HasStarted)
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await context.Response.WriteAsJsonAsync(new { error = "artifact_api_unavailable" }, ct);
    }
}

sealed record SubmitRequest(string? JiraKey);
sealed record FailureReview(string? Classification, string? Reason);
sealed record PromotionReview(string? ReviewedManifestHash);
sealed record ApplyProposalReview(string? ReviewedPatchSha256);
sealed record CreateExecution(string? Target);
sealed record CreateAutonomousExecution(string? Target, string? PolicyName, string? IdempotencyKey = null);
sealed record ReviewAutomationWorkflow(long Revision, bool Approve, string? Reviewer);
sealed record ExecutionPolicyInput(string? Name, string? Version, string[]? AllowedOrigins, string[]? AllowedActions,
    int MaxTimeoutSeconds = 60, bool NonProduction = false, bool AutoApprove = false, bool AutoLaunch = false,
    int CanaryMaxAutoLaunches = 0, string? Environment = "default", int MaxConcurrentAutoLaunches = 1,
    int AutoLaunchWindowSeconds = 3600, int MaxAutoLaunchesPerWindow = 1);
sealed record UpdateExecution(long Revision, System.Text.Json.JsonElement Manifest);
sealed record PrepareExecution(long Revision);
sealed record ApproveExecution(long Revision, string? ReviewedManifestHash, string? Reviewer);
sealed record LaunchExecution(long Revision);

public partial class Program { }
