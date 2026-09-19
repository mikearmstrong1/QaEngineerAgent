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

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions { OnPrepareResponse = context =>
    context.Context.Response.Headers.CacheControl = context.File.Name == "index.html" ? "no-store" : "public,max-age=3600" });

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

sealed record SubmitRequest(string? JiraKey);

public partial class Program { }
