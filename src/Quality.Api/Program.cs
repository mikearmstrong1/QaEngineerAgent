using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text;
using Npgsql;
using Quality.Api;
using Quality.Domain;
using Quality.Orchestrator;
using Quality.Persistence;

var mode = args.FirstOrDefault() ?? "api";
if (mode is "help" or "--help")
{
    Console.WriteLine("Quality.Api api | worker | run --source jira --reference AUTH-1427 [--idempotency-key <key>] | get --id <job-id> | cancel --id <job-id> | execution-create --job <job-id> --target <url> | execution-list --job <job-id> | execution-update --id <request-id> --manifest <path> --revision <n> | execution-approve --id <request-id> --revision <n> --sha256 <hash> --reviewer <identity> | execution-launch --id <request-id> --revision <n> | prepare-execution --job <job-id> --target <url> | execute --job <job-id> --manifest <path> --sha256 <reviewed-hash> | get-run --id <run-id> | init-artifacts | publish-artifacts --run <run-id> | associate-failure --file <analysis.json> | promote-regression --job <job-id> --run <run-id> --sha256 <reviewed-manifest-hash> | classify-failure --run <run-id> --classification <category> --reason <review-reason>");
    return 0;
}
if (mode is not ("api" or "worker" or "run" or "get" or "cancel" or "execution-create" or "execution-list" or "execution-update" or "execution-approve" or "execution-launch" or "prepare-execution" or "execute" or "get-run" or "init-artifacts" or "publish-artifacts" or "associate-failure" or "promote-regression" or "classify-failure"))
{
    Console.Error.WriteLine("Unknown mode; use --help");
    return 2;
}
try
{
    if (mode == "worker")
    {
        if (args.Length > 1) throw new ArgumentException("worker takes no arguments");
        var workerBuilder = Host.CreateApplicationBuilder();
        var metricsUrl = workerBuilder.Configuration["Quality:Metrics:WorkerUrl"];
        if (!string.IsNullOrEmpty(metricsUrl))
        {
            if (!Uri.TryCreate(metricsUrl, UriKind.Absolute, out var uri) || uri.Scheme != "http" ||
                uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.UserInfo.Length != 0)
                throw new ArgumentException("Quality__Metrics__WorkerUrl must be an HTTP origin, such as http://127.0.0.1:5081");
            var metricsBuilder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
            metricsBuilder.WebHost.UseUrls(metricsUrl);
            var metricsAccess = new ApiAccess(metricsBuilder.Configuration);
            ConfigureServices(metricsBuilder.Services, metricsBuilder.Configuration, true);
            await using var metricsHost = metricsBuilder.Build();
            await metricsHost.Services.GetRequiredService<IJobStore>().InitializeAsync(CancellationToken.None);
            metricsHost.UseRouting();
            metricsHost.Use((context, next) => metricsAccess.InvokeAsync(context, next));
            MapMetrics(metricsHost);
            await metricsHost.RunAsync();
            return 0;
        }
        ConfigureServices(workerBuilder.Services, workerBuilder.Configuration, true);
        using var host = workerBuilder.Build();
        await host.Services.GetRequiredService<IJobStore>().InitializeAsync(CancellationToken.None);
        await host.RunAsync();
        return 0;
    }
    if (mode == "api" && args.Length > 1) throw new ArgumentException("api takes no arguments; use environment configuration");
    var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
    builder.Logging.ClearProviders();
    builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
    builder.Services.ConfigureHttpJsonOptions(o => {
        o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
        o.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
    });
    var access = mode == "api" ? new ApiAccess(builder.Configuration) : null;
    ConfigureServices(builder.Services, builder.Configuration,
        mode == "api" && builder.Configuration.GetValue<bool>("Quality:RunWorker"));
    await using var app = builder.Build();
    await app.Services.GetRequiredService<IJobStore>().InitializeAsync(CancellationToken.None);
    var jobs = app.Services.GetRequiredService<JobService>();
    if (mode.StartsWith("execution-", StringComparison.Ordinal))
    {
        var service = app.Services.GetRequiredService<ExecutionRequestService>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(6));
        ExecutionRequest result;
        if (mode == "execution-create")
        {
            var parsed = ParseOptions(args.Skip(1).ToArray(), ["--job", "--target"]);
            result = await service.CreateAsync(parsed["--job"], parsed["--target"], timeout.Token);
        }
        else if (mode == "execution-list")
        {
            var parsed = ParseOptions(args.Skip(1).ToArray(), ["--job"]);
            Console.WriteLine(JsonSerializer.Serialize(new { items = await service.ListByJobAsync(parsed["--job"], timeout.Token) }, ContractJson.Options));
            return 0;
        }
        else
        {
            var required = mode == "execution-update" ? new[] { "--id", "--manifest", "--revision" }
                : mode == "execution-approve" ? ["--id", "--revision", "--sha256", "--reviewer"]
                : ["--id", "--revision"];
            var parsed = ParseOptions(args.Skip(1).ToArray(), required);
            if (!long.TryParse(parsed["--revision"], out var revision) || revision < 0)
                throw new ArgumentException("revision must be a non-negative integer");
            if (mode == "execution-update")
            {
                var path = parsed["--manifest"];
                if (!File.Exists(path) || new FileInfo(path).Length > 1024 * 1024) throw new ArgumentException("Manifest is missing or too large");
                result = await service.UpdateManifestAsync(parsed["--id"], revision, await File.ReadAllBytesAsync(path, timeout.Token), timeout.Token);
            }
            else if (mode == "execution-approve")
                result = await service.ApproveAsync(parsed["--id"], revision, parsed["--sha256"], parsed["--reviewer"], timeout.Token);
            else result = await service.LaunchAsync(parsed["--id"], revision, timeout.Token);
        }
        Console.WriteLine(JsonSerializer.Serialize(result, ContractJson.Options));
        return result.Status is ExecutionRequestStatus.Failed or ExecutionRequestStatus.InfrastructureFailed or ExecutionRequestStatus.TimedOut ? 1 : 0;
    }
    if (mode is "promote-regression" or "classify-failure")
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var service = app.Services.GetRequiredService<RegressionPromotion>();
        if (mode == "classify-failure")
        {
            var parsed = ParseOptions(args.Skip(1).ToArray(), ["--run", "--classification", "--reason"]);
            var run = await service.ClassifyFailureAsync(parsed["--run"], parsed["--classification"], parsed["--reason"], timeout.Token);
            Console.WriteLine(JsonSerializer.Serialize(run, ContractJson.Options));
            return 0;
        }
        var options = ParseOptions(args.Skip(1).ToArray(), ["--job", "--run", "--sha256"]);
        var job = await jobs.GetAsync(options["--job"], timeout.Token) ?? throw new ArgumentException("Job not found");
        var patch = await service.ProposeAsync(job, options["--run"], options["--sha256"], timeout.Token);
        Console.WriteLine(JsonSerializer.Serialize(new { status = "NeedsReview", patch }, ContractJson.Options));
        return 0;
    }
    if (mode is "init-artifacts" or "publish-artifacts" or "associate-failure")
    {
        var remoteStore = app.Services.GetService<IRemoteArtifactStore>()
            ?? throw new ArgumentException("Configure Quality__Artifacts__Mode=MinIO or Azure first");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        if (mode == "init-artifacts")
        {
            if (args.Length != 1) throw new ArgumentException("init-artifacts takes no arguments");
            await remoteStore.InitializeAsync(timeout.Token);
            Console.WriteLine(JsonSerializer.Serialize(remoteStore.Info, ContractJson.Options));
            return 0;
        }
        var publisher = app.Services.GetRequiredService<RunArtifactPublisher>();
        if (mode == "publish-artifacts")
        {
            var options = ParseOptions(args.Skip(1).ToArray(), ["--run"]);
            var run = await publisher.PublishAsync(options["--run"], timeout.Token);
            Console.WriteLine(JsonSerializer.Serialize(run, ContractJson.Options));
            return run.ArtifactUploadStatus == "Uploaded" ? 0 : 1;
        }
        var parsed = ParseOptions(args.Skip(1).ToArray(), ["--file"]);
        if (new FileInfo(parsed["--file"]).Length > 1024 * 1024) throw new ArgumentException("Analysis is too large");
        var jsonOptions = new JsonSerializerOptions(ContractJson.Options) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
        var analysis = JsonSerializer.Deserialize<FailureAnalysis>(await File.ReadAllTextAsync(parsed["--file"], timeout.Token), jsonOptions)
            ?? throw new ArgumentException("Failure analysis is required");
        Console.WriteLine(JsonSerializer.Serialize(await publisher.AssociateFailureAsync(analysis, timeout.Token), ContractJson.Options));
        return 0;
    }
    if (mode is "prepare-execution" or "execute" or "get-run")
    {
        if (mode == "get-run")
        {
            var parsed = ParseOptions(args.Skip(1).ToArray(), ["--id"]);
            var run = await app.Services.GetRequiredService<ITestRunStore>().GetAsync(parsed["--id"], CancellationToken.None);
            if (run is null) { Console.Error.WriteLine("Run not found"); return 3; }
            Console.WriteLine(JsonSerializer.Serialize(run, ContractJson.Options));
            return 0;
        }
        var options = ParseOptions(args.Skip(1).ToArray(), mode == "execute" ? ["--job", "--manifest", "--sha256"] : ["--job", "--target"]);
        var job = await jobs.GetAsync(options["--job"], CancellationToken.None);
        if (job?.Status != JobStatus.Completed || job.TestPlan is null) throw new ArgumentException("A completed planning job is required");
        if (mode == "prepare-execution")
        {
            // Human supplies selectors and concrete actions, then reviews the completed manifest.
            var draft = new ExecutionManifest(job.TestPlan.Id, ExecutionManifest.HashPlan(job.TestPlan), options["--target"],
                job.TestPlan.TestCases.Select(test => new ExecutionTest(test.Id, [])).ToArray());
            Console.WriteLine(JsonSerializer.Serialize(draft, ContractJson.Options));
            return 0;
        }
        var manifestPath = options["--manifest"];
        if (new FileInfo(manifestPath).Length > 1024 * 1024) throw new ArgumentException("Manifest is too large");
        var bytes = await File.ReadAllBytesAsync(manifestPath);
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            var run = await app.Services.GetRequiredService<IReviewedTestExecutor>()
                .ExecuteReviewedAsync(job.TestPlan, bytes, options["--sha256"], cancellation.Token);
            if (app.Services.GetService<IRemoteArtifactStore>() is not null && !cancellation.IsCancellationRequested)
                run = await app.Services.GetRequiredService<RunArtifactPublisher>().PublishAsync(run.Id, cancellation.Token);
            Console.WriteLine(JsonSerializer.Serialize(run, ContractJson.Options));
            return run.Status == "Passed" && run.ArtifactUploadStatus != "Failed" ? 0 : 1;
        }
        finally { Console.CancelKeyPress -= cancel; }
    }
    if (mode is "run" or "get" or "cancel")
    {
        var options = ParseOptions(args.Skip(1).ToArray(), mode == "run" ? ["--source", "--reference"] : ["--id"], mode == "run" ? ["--idempotency-key"] : []);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        QualityJob? result;
        if (mode == "get") result = await jobs.GetAsync(options["--id"], timeout.Token);
        else if (mode == "cancel") result = await jobs.CancelAsync(options["--id"], timeout.Token);
        else
        {
            var submitted = await jobs.SubmitAsync(new(new(options["--source"], options["--reference"])), timeout.Token, options.GetValueOrDefault("--idempotency-key"));
            result = await jobs.ProcessNextAsync(submitted.Id, timeout.Token);
            // Another worker may claim this job between submission and one-shot execution.
            while (result is null || !result.IsTerminal)
            {
                await Task.Delay(100, timeout.Token);
                result = await jobs.GetAsync(submitted.Id, timeout.Token);
            }
        }
        if (result is null) { Console.Error.WriteLine("Job not found"); return 3; }
        Console.WriteLine(JsonSerializer.Serialize(result, ContractJson.Options));
        if (mode == "cancel") return result.Status == JobStatus.Cancelled ? 0 : 4;
        return result.Status is JobStatus.Failed or JobStatus.Cancelled ? 1 : 0;
    }
    app.UseRouting();
    app.Use((context, next) => access!.InvokeAsync(context, next));
    MapMetrics(app);
    app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "quality-system" })).AllowAnonymous();
    app.MapGet("/ready", async (IJobStore store, CancellationToken ct) =>
    {
        try { await store.GetAsync("00000000000000000000000000000000", ct); return Results.Ok(new { status = "ready" }); }
        catch { return Results.StatusCode(503); }
    }).AllowAnonymous();
    app.MapPost("/jobs", async (JobRequest request, HttpRequest http, CancellationToken ct) =>
    {
        try
        {
            var keys = http.Headers["Idempotency-Key"];
            if (keys.Count > 1) return Results.BadRequest(new { error = "multiple_idempotency_keys" });
            var job = await jobs.SubmitAsync(request, ct, keys.Count == 0 ? null : keys.ToString());
            return Results.Accepted($"/jobs/{job.Id}", job);
        }
        catch (IdempotencyConflictException) { return Results.Conflict(new { error = "idempotency_key_conflict" }); }
        catch (ArgumentException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["reference"] = [ex.Message] }); }
    });
    app.MapGet("/jobs", async (int? limit, string? cursor, CancellationToken ct) =>
    {
        var size = limit ?? 20;
        if (size is < 1 or > 50) return Results.BadRequest(new { error = "limit_must_be_1_to_50" });
        try
        {
            var (beforeCreatedAt, beforeId) = DecodeCursor(cursor);
            var jobsPage = await jobs.ListAsync(size + 1, beforeCreatedAt, beforeId, ct);
            var items = jobsPage.Take(size).ToArray();
            var nextCursor = jobsPage.Count > size && items.Length > 0 ? EncodeCursor(items[^1]) : null;
            return Results.Ok(new { items, nextCursor });
        }
        catch (ArgumentException ex) { return Results.BadRequest(new { error = "invalid_job_cursor", detail = ex.Message }); }
    });
    app.MapPost("/jobs/{id}/cancel", async (string id, CancellationToken ct) =>
    {
        if (!Guid.TryParseExact(id, "N", out _)) return Results.BadRequest(new { error = "invalid_job_id" });
        var job = await jobs.CancelAsync(id, ct);
        if (job is null) return Results.NotFound();
        return job.Status == JobStatus.Cancelled ? Results.Ok(job)
            : Results.Conflict(new { error = "job_already_terminal", job });
    });
    app.MapGet("/jobs/{id}", async (string id, CancellationToken ct) =>
    {
        if (!Guid.TryParseExact(id, "N", out _)) return Results.BadRequest(new { error = "invalid_job_id" });
        var job = await jobs.GetAsync(id, ct);
        return job is null ? Results.NotFound() : Results.Ok(job);
    });
    app.MapPost("/jobs/{id}/execution-requests", async (string id, CreateExecutionRequest input, ExecutionRequestService executions, CancellationToken ct) =>
    {
        if (!Guid.TryParseExact(id, "N", out _)) return Results.BadRequest(new { error = "invalid_job_id" });
        try { return Results.Created("/execution-requests", await executions.CreateAsync(id, input.Target ?? "", ct)); }
        catch (ArgumentException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["execution"] = [ex.Message] }); }
        catch (UriFormatException) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["execution"] = ["Target must be an absolute allowlisted HTTP(S) URL"] }); }
    });
    app.MapGet("/jobs/{id}/execution-requests", async (string id, ExecutionRequestService executions, CancellationToken ct) =>
    {
        if (!Guid.TryParseExact(id, "N", out _)) return Results.BadRequest(new { error = "invalid_job_id" });
        return Results.Ok(new { items = await executions.ListByJobAsync(id, ct) });
    });
    app.MapGet("/execution-requests/{id}", async (string id, ExecutionRequestService executions, CancellationToken ct) =>
    {
        if (!Guid.TryParseExact(id, "N", out _)) return Results.BadRequest(new { error = "invalid_execution_request_id" });
        var execution = await executions.GetAsync(id, ct);
        return execution is null ? Results.NotFound() : Results.Ok(execution);
    });
    app.MapGet("/execution-policy", (ExecutionOptions execution) => Results.Ok(new
    {
        allowedOrigins = execution.AllowedOrigins.Select(ExecutionManifest.ValidateOrigin).Distinct(StringComparer.Ordinal).Order().ToArray(),
        supportedActions = new[] { "goto", "click", "fill", "expectText", "expectVisible", "expectUrl" }
    }));
    app.MapPut("/execution-requests/{id}/manifest", async (string id, UpdateExecutionManifest input, ExecutionRequestService executions, CancellationToken ct) =>
    {
        if (!Guid.TryParseExact(id, "N", out _)) return Results.BadRequest(new { error = "invalid_execution_request_id" });
        try
        {
            var bytes = Encoding.UTF8.GetBytes(input.Manifest.GetRawText());
            return Results.Ok(await executions.UpdateManifestAsync(id, input.Revision, bytes, ct));
        }
        catch (ExecutionRequestConflictException ex) { return Results.Conflict(new { error = "execution_request_conflict", detail = ex.Message }); }
        catch (ArgumentException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["manifest"] = [ex.Message] }); }
        catch (JsonException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["manifest"] = [ex.Message] }); }
    });
    app.MapPost("/execution-requests/{id}/approve", async (string id, ApproveExecutionRequest input, ExecutionRequestService executions, CancellationToken ct) =>
    {
        if (!Guid.TryParseExact(id, "N", out _)) return Results.BadRequest(new { error = "invalid_execution_request_id" });
        try { return Results.Ok(await executions.ApproveAsync(id, input.Revision, input.ReviewedManifestHash ?? "", input.Reviewer ?? "", ct)); }
        catch (ExecutionRequestConflictException ex) { return Results.Conflict(new { error = "execution_request_conflict", detail = ex.Message }); }
        catch (ArgumentException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["approval"] = [ex.Message] }); }
    });
    app.MapPost("/execution-requests/{id}/launch", async (string id, LaunchExecutionRequest input, ExecutionRequestService executions, CancellationToken ct) =>
    {
        if (!Guid.TryParseExact(id, "N", out _)) return Results.BadRequest(new { error = "invalid_execution_request_id" });
        try { return Results.Accepted($"/execution-requests/{id}", await executions.LaunchAsync(id, input.Revision, ct)); }
        catch (ExecutionRequestConflictException ex) { return Results.Conflict(new { error = "execution_request_conflict", detail = ex.Message }); }
        catch (ArgumentException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["launch"] = [ex.Message] }); }
    });
    app.MapGet("/jobs/{id}/runs", async (string id, ITestRunStore runs, CancellationToken ct) =>
    {
        if (!Guid.TryParseExact(id, "N", out _)) return Results.BadRequest(new { error = "invalid_job_id" });
        var job = await jobs.GetAsync(id, ct);
        if (job is null) return Results.NotFound();
        if (job.TestPlan is null) return Results.Ok(new { items = Array.Empty<TestRun>() });
        return Results.Ok(new { items = await runs.ListByPlanAsync(job.TestPlan.Id, ct) });
    });
    app.MapGet("/runs/{id}/artifacts", async Task<IResult> (string id, string? key, bool? download,
        HttpResponse response, ArtifactContentReader reader, CancellationToken ct) =>
    {
        if (!Guid.TryParseExact(id, "N", out _)) return Results.BadRequest(new { error = "invalid_run_id" });
        try
        {
            var artifact = await reader.OpenAsync(id, key ?? "", ct);
            if (artifact is null) return Results.NotFound(new { error = "artifact_not_found" });
            response.Headers.CacheControl = "private,no-store";
            response.Headers.XContentTypeOptions = "nosniff";
            response.Headers.ContentSecurityPolicy = "sandbox; default-src 'none'";
            response.Headers["X-Artifact-Content-Type"] = artifact.ContentType;
            var contentType = download == true ? artifact.ContentType : SafePreviewContentType(artifact.ContentType);
            return Results.Stream(artifact.Stream, contentType, download == true ? artifact.FileName : null,
                enableRangeProcessing: false);
        }
        catch (ArgumentException ex)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["artifact"] = [ex.Message] });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return Results.Json(new { error = "artifact_unavailable" }, statusCode: 503); }
    });
    app.MapPost("/runs/{id}/classification", async (string id, FailureReviewRequest review, RegressionPromotion promotion, CancellationToken ct) =>
    {
        if (!Guid.TryParseExact(id, "N", out _)) return Results.BadRequest(new { error = "invalid_run_id" });
        try { return Results.Ok(await promotion.ClassifyFailureAsync(id, review.Classification ?? "", review.Reason ?? "", ct)); }
        catch (ArgumentException ex)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["review"] = [ex.Message] });
        }
    });
    app.MapPost("/jobs/{jobId}/runs/{runId}/promotion", async (string jobId, string runId, RegressionPromotionRequest review, RegressionPromotion promotion, CancellationToken ct) =>
    {
        if (!Guid.TryParseExact(jobId, "N", out _) || !Guid.TryParseExact(runId, "N", out _))
            return Results.BadRequest(new { error = "invalid_job_or_run_id" });
        var job = await jobs.GetAsync(jobId, ct);
        if (job is null) return Results.NotFound();
        try
        {
            await promotion.ProposeAsync(job, runId, review.ReviewedManifestHash ?? "", ct);
            var proposal = await app.Services.GetRequiredService<IRegressionProposalManager>().GetAsync(runId, ct);
            return Results.Ok(proposal);
        }
        catch (ArgumentException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["promotion"] = [ex.Message] }); }
    });
    app.MapGet("/regression-proposals/{runId}", async (string runId, IRegressionProposalManager proposals, CancellationToken ct) =>
    {
        if (!Guid.TryParseExact(runId, "N", out _)) return Results.BadRequest(new { error = "invalid_run_id" });
        var proposal = await proposals.GetAsync(runId, ct);
        return proposal is null ? Results.NotFound() : Results.Ok(proposal);
    });
    app.MapPost("/regression-proposals/{runId}/apply", async (string runId, ApplyRegressionProposalRequest review, IRegressionProposalManager proposals, CancellationToken ct) =>
    {
        if (!Guid.TryParseExact(runId, "N", out _)) return Results.BadRequest(new { error = "invalid_run_id" });
        try { return Results.Ok(await proposals.ApplyAsync(runId, review.ReviewedPatchSha256 ?? "", ct)); }
        catch (ArgumentException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["proposal"] = [ex.Message] }); }
    });
    app.MapGet("/", () => Results.Content("""
        <!doctype html><html lang="en"><head><meta charset="utf-8"><title>Quality System</title></head>
        <body><main><h1>Engineering Quality System</h1><p>Portable requirement-to-test planning.</p>
        <p>Completed jobs contain plans; inspect isStub and coverageGaps before use. Execute reviewed manifests separately to obtain test results.</p>
        <a href="/health">Service health</a></main></body></html>
        """, "text/html")).AllowAnonymous();
    await app.RunAsync();
    return 0;
}
catch (ArgumentException ex) { Console.Error.WriteLine(ex.Message); return 2; }
catch (OperationCanceledException) { Console.Error.WriteLine("Operation timed out; inspect persisted job state"); return 1; }
catch (Exception ex) { Console.Error.WriteLine($"Startup or operation failed ({ex.GetType().Name})"); return 1; }

static void MapMetrics(WebApplication app)
{
    app.MapGet("/metrics", (HttpContext context, JobMetrics metrics) =>
    {
        context.Response.Headers.CacheControl = "no-store";
        return Results.Text(metrics.Render(), "text/plain; version=0.0.4; charset=utf-8");
    });
}

static string SafePreviewContentType(string contentType)
{
    var normalized = contentType.Split(';', 2)[0].Trim().ToLowerInvariant();
    return normalized is "image/png" or "image/jpeg" or "image/gif" or "image/webp"
        or "application/json" or "text/plain" ? contentType : "application/octet-stream";
}

static Dictionary<string, string> ParseOptions(string[] values, string[] allowed, string[]? optional = null)
{
    var parsed = new Dictionary<string, string>();
    if (values.Length % 2 != 0) throw new ArgumentException("Every option requires a value");
    for (var i = 0; i < values.Length; i += 2)
        if (!allowed.Contains(values[i]) && !(optional?.Contains(values[i]) ?? false) || !parsed.TryAdd(values[i], values[i + 1]))
            throw new ArgumentException($"Unknown or duplicate option: {values[i]}");
    if (allowed.Any(a => !parsed.ContainsKey(a))) throw new ArgumentException($"Required options: {string.Join(", ", allowed)}");
    return parsed;
}

static string EncodeCursor(QualityJob job)
    => Convert.ToBase64String(Encoding.UTF8.GetBytes($"{job.CreatedAt.UtcTicks}:{job.Id}"))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

static (DateTimeOffset? CreatedAt, string? Id) DecodeCursor(string? cursor)
{
    if (string.IsNullOrEmpty(cursor)) return (null, null);
    try
    {
        var encoded = cursor.Replace('-', '+').Replace('_', '/');
        encoded = encoded.PadRight(encoded.Length + (4 - encoded.Length % 4) % 4, '=');
        var parts = Encoding.UTF8.GetString(Convert.FromBase64String(encoded)).Split(':', 2);
        if (parts.Length != 2 || !long.TryParse(parts[0], out var ticks) ||
            !Guid.TryParseExact(parts[1], "N", out _)) throw new FormatException();
        return (new DateTimeOffset(ticks, TimeSpan.Zero), parts[1]);
    }
    catch (Exception ex) when (ex is FormatException or ArgumentOutOfRangeException)
    {
        throw new ArgumentException("cursor is malformed");
    }
}

static void ConfigureServices(IServiceCollection services, IConfiguration configuration, bool runWorker)
{
    services.AddSingleton(TimeProvider.System);
    services.AddSingleton<JobMetrics>();
    var lease = configuration.GetSection("Quality:Lease");
    var leaseOptions = new JobLeaseOptions
    {
        Duration = TimeSpan.FromSeconds(lease.GetValue("DurationSeconds", 300d)),
        RenewalInterval = TimeSpan.FromSeconds(lease.GetValue("RenewalIntervalSeconds", 60d)),
        RenewalTimeout = TimeSpan.FromSeconds(lease.GetValue("RenewalTimeoutSeconds", 15d)),
        CancellationPollInterval = TimeSpan.FromSeconds(lease.GetValue("CancellationPollIntervalSeconds", 1d))
    };
    leaseOptions.Validate();
    services.AddSingleton(leaseOptions);
    var retry = configuration.GetSection("Quality:Retry");
    var retryOptions = new RetryBudgetOptions(retry.GetValue("MaxWorkerAttempts", 5), retry.GetValue("MaxNormalizationAttempts", 3),
        retry.GetValue("MaxDurationSeconds", 900), retry.GetValue("InitialDelayMilliseconds", 250), retry.GetValue("MaxDelayMilliseconds", 5000));
    retryOptions.Snapshot();
    services.AddSingleton(retryOptions);
    var storeKind = configuration["Quality:Store"] ?? "File";
    if (storeKind.Equals("Postgres", StringComparison.OrdinalIgnoreCase))
    {
        var connection = configuration.GetConnectionString("Quality")
            ?? throw new ArgumentException("ConnectionStrings__Quality is required for PostgreSQL");
        services.AddSingleton(_ => NpgsqlDataSource.Create(connection));
        services.AddSingleton<PostgresJobStore>();
        services.AddSingleton<IJobStore>(sp => new MeteredJobStore(sp.GetRequiredService<PostgresJobStore>(), sp.GetRequiredService<JobMetrics>()));
        services.AddSingleton<IExecutionRequestStore, PostgresExecutionRequestStore>();
    }
    else if (storeKind.Equals("File", StringComparison.OrdinalIgnoreCase))
    {
        var dataDirectory = configuration["Quality:DataDirectory"] ?? "./data/jobs";
        services.AddSingleton<IJobStore>(sp => new MeteredJobStore(new FileJobStore(dataDirectory, sp.GetRequiredService<TimeProvider>()), sp.GetRequiredService<JobMetrics>()));
        services.AddSingleton<IExecutionRequestStore>(new FileExecutionRequestStore(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dataDirectory))!, "execution-requests")));
    }
    else throw new ArgumentException("Quality__Store must be File or Postgres");
    var sourceKind = configuration["Quality:Requirements:Mode"] ?? "Stub";
    if (sourceKind.Equals("Stub", StringComparison.OrdinalIgnoreCase))
        services.AddSingleton<IRequirementSource, StubRequirementSource>();
    else if (sourceKind.Equals("Remote", StringComparison.OrdinalIgnoreCase))
    {
        var section = configuration.GetSection("Quality:Requirements");
        services.AddSingleton(new RequirementSourceOptions(
            section["Jira:BaseUrl"], section["Jira:Email"], section["Jira:Token"], section["Jira:AcceptanceField"],
            section["Coda:Token"], section["Coda:TitleColumn"], section["Coda:DescriptionColumn"], section["Coda:AcceptanceColumn"]));
        services.AddHttpClient<IRequirementSource, RemoteRequirementSource>()
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
    }
    else throw new ArgumentException("Quality__Requirements__Mode must be Stub or Remote");
    var planningKind = configuration["Quality:Planning:Mode"] ?? "Stub";
    if (planningKind.Equals("Stub", StringComparison.OrdinalIgnoreCase))
        services.AddSingleton<ILlmProvider, StubLlmProvider>();
    else if (planningKind.Equals("OpenAI", StringComparison.OrdinalIgnoreCase))
    {
        var section = configuration.GetSection("Quality:Planning");
        var options = new PlanningOptions(section["ApiKey"] ?? configuration["OPENAI_API_KEY"] ?? "",
            section["Model"] ?? "", section.GetValue("MaxAttempts", 3),
            section.GetValue("AttemptTimeoutSeconds", 20), section.GetValue("TotalTimeoutSeconds", 75),
            section.GetValue("MaxOutputTokens", 8192));
        options.Validate();
        services.AddSingleton(options);
        services.AddSingleton(new PlanningPrompt(section["AssetDirectory"] ?? AppContext.BaseDirectory));
        services.AddHttpClient<ILlmProvider, OpenAiPlanningProvider>(client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
    }
    else throw new ArgumentException("Quality__Planning__Mode must be Stub or OpenAI");
    var artifactMode = configuration["Quality:Artifacts:Mode"] ?? "Local";
    if (artifactMode.Equals("Local", StringComparison.OrdinalIgnoreCase))
        services.AddSingleton<IArtifactStore, StubArtifactStore>();
    else if (artifactMode.Equals("MinIO", StringComparison.OrdinalIgnoreCase))
    {
        var section = configuration.GetSection("Quality:Artifacts");
        var settings = new MinioOptions(section["Endpoint"] ?? "", section["AccessKey"] ?? "", section["SecretKey"] ?? "",
            section["Bucket"] ?? "quality-artifacts", section.GetValue("RetentionDays", 30),
            section.GetValue<long>("MaxArtifactBytes", 134217728), section.GetValue("TimeoutSeconds", 60));
        settings.Validate();
        services.AddSingleton(settings);
        services.AddSingleton<Amazon.S3.IAmazonS3>(_ => MinioArtifactStore.CreateClient(settings));
        services.AddSingleton<MinioArtifactStore>();
        services.AddSingleton<IArtifactStore>(sp => sp.GetRequiredService<MinioArtifactStore>());
        services.AddSingleton<IRemoteArtifactStore>(sp => sp.GetRequiredService<MinioArtifactStore>());
    }
    else if (artifactMode.Equals("Azure", StringComparison.OrdinalIgnoreCase))
    {
        var section = configuration.GetSection("Quality:Artifacts");
        var settings = new AzureBlobOptions(section["Azure:ConnectionString"], section["Azure:ServiceUri"],
            section["Azure:Container"] ?? "quality-artifacts", section.GetValue<long>("MaxArtifactBytes", 134217728),
            section.GetValue("TimeoutSeconds", 60));
        settings.Validate();
        services.AddSingleton(settings);
        services.AddSingleton(_ => AzureBlobArtifactStore.CreateClient(settings));
        services.AddSingleton<AzureBlobArtifactStore>();
        services.AddSingleton<IArtifactStore>(sp => sp.GetRequiredService<AzureBlobArtifactStore>());
        services.AddSingleton<IRemoteArtifactStore>(sp => sp.GetRequiredService<AzureBlobArtifactStore>());
    }
    else throw new ArgumentException("Quality__Artifacts__Mode must be Local, MinIO or Azure");
    services.AddSingleton<RunArtifactPublisher>();
    var proposalWorkspace = Path.GetFullPath(configuration["Quality:SourceControl:Workspace"] ?? Directory.GetCurrentDirectory());
    services.AddSingleton(new SourceControlOptions(proposalWorkspace,
        configuration["Quality:SourceControl:ProposalDirectory"] ?? Path.Combine(proposalWorkspace, "data/proposals"),
        configuration["Quality:SourceControl:TargetRepository"]));
    services.AddSingleton<GitPatchSourceControl>();
    services.AddSingleton<ISourceControl>(sp => sp.GetRequiredService<GitPatchSourceControl>());
    services.AddSingleton<IRegressionProposalManager>(sp => sp.GetRequiredService<GitPatchSourceControl>());
    services.AddSingleton<RegressionPromotion>();
    var execution = configuration.GetSection("Quality:Execution");
    var workspace = Path.GetFullPath(execution["Workspace"] ?? Directory.GetCurrentDirectory());
    var runDirectory = execution["RunDirectory"] ?? Path.Combine(workspace, "data/executions");
    if (storeKind.Equals("Postgres", StringComparison.OrdinalIgnoreCase))
        services.AddSingleton<ITestRunStore>(sp => new PostgresTestRunStore(sp.GetRequiredService<NpgsqlDataSource>(), runDirectory));
    else services.AddSingleton<ITestRunStore>(new FileTestRunStore(runDirectory));
    services.AddSingleton<ArtifactContentReader>();
    services.AddSingleton(new ExecutionOptions(workspace,
        (execution["AllowedOrigins"] ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        execution["NodeExecutable"] ?? "node"));
    services.AddSingleton<PlaywrightTestExecutor>();
    services.AddSingleton<ITestExecutor>(sp => sp.GetRequiredService<PlaywrightTestExecutor>());
    services.AddSingleton<IReviewedTestExecutor>(sp => sp.GetRequiredService<PlaywrightTestExecutor>());
    services.AddSingleton<ExecutionRequestService>();
    services.AddSingleton<JobService>();
    if (runWorker)
    {
        services.AddHostedService<JobWorker>();
        services.AddHostedService<ExecutionWorker>();
    }
}

public partial class Program { }
public sealed record FailureReviewRequest(string? Classification, string? Reason);
public sealed record RegressionPromotionRequest(string? ReviewedManifestHash);
public sealed record ApplyRegressionProposalRequest(string? ReviewedPatchSha256);
public sealed record CreateExecutionRequest(string? Target);
public sealed record UpdateExecutionManifest(long Revision, JsonElement Manifest);
public sealed record ApproveExecutionRequest(long Revision, string? ReviewedManifestHash, string? Reviewer);
public sealed record LaunchExecutionRequest(long Revision);
