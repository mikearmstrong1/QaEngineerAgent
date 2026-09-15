using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Quality.Domain;

namespace Quality.Orchestrator;

public sealed record PlanningOptions(string ApiKey, string Model, int MaxAttempts = 3,
    int AttemptTimeoutSeconds = 20, int TotalTimeoutSeconds = 75, int MaxOutputTokens = 8192)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ApiKey) || string.IsNullOrWhiteSpace(Model) || Model.Length > 200 || Model.Any(char.IsControl))
            throw new ArgumentException("Planning API key and model are required");
        if (MaxAttempts is < 1 or > 3 || AttemptTimeoutSeconds is < 1 or > 60
            || TotalTimeoutSeconds is < 1 or > 80 || AttemptTimeoutSeconds > TotalTimeoutSeconds
            || MaxOutputTokens is < 256 or > 16384)
            throw new ArgumentException("Planning limits are outside supported bounds");
    }
}

public sealed class PlanningException(string code, PlanningMetadata metadata) : Exception(code)
{
    public string Code { get; } = code;
    public PlanningMetadata Metadata { get; } = metadata;
}

public sealed class OpenAiPlanningProvider : ILlmProvider
{
    private readonly HttpClient http;
    private readonly PlanningOptions options;
    private readonly PlanningPrompt prompt;
    public string Name => "openai";
    public const string ProviderVersion = "responses/v1;quality-planner/v1";

    public OpenAiPlanningProvider(HttpClient http, PlanningOptions options, PlanningPrompt prompt)
    {
        options.Validate();
        this.http = http;
        this.options = options;
        this.prompt = prompt;
    }

    public async Task<TestPlan> PlanAsync(Requirement requirement, string promptVersion, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var metadata = new PlanningMetadata(Name, options.Model, null, ProviderVersion, prompt.PromptHash, prompt.SchemaHash, 0, null);
        if (promptVersion != PlanningPrompt.Version) throw new PlanningException("planning_prompt_mismatch", metadata);
        var gaps = new List<string>();
        if (requirement.IsStub) gaps.Add("A real source requirement is needed; synthetic input cannot support a grounded test plan.");
        if (string.IsNullOrWhiteSpace(requirement.Title)) gaps.Add("Requirement title is missing.");
        if (string.IsNullOrWhiteSpace(requirement.Description)) gaps.Add("Requirement description is missing.");
        if (requirement.Actors.Length == 0) gaps.Add("Actors are not specified in the source requirement.");
        if (requirement.Preconditions.Length == 0) gaps.Add("Preconditions are not specified in the source requirement.");
        gaps.AddRange(requirement.Risks);
        if (requirement.AcceptanceCriteria.Length == 0 || requirement.AcceptanceCriteria.Any(a =>
            string.IsNullOrWhiteSpace(a.Id) || string.IsNullOrWhiteSpace(a.Description))
            || requirement.AcceptanceCriteria.Select(a => a.Id).Distinct(StringComparer.Ordinal).Count() != requirement.AcceptanceCriteria.Length)
        {
            gaps.Add("Explicit acceptance criteria with unique IDs and descriptions are required before planning tests.");
            return GapPlan(requirement, gaps, metadata);
        }
        if (requirement.IsStub) return GapPlan(requirement, gaps, metadata);

        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            model = options.Model,
            store = false,
            instructions = prompt.SystemText,
            input = new[] { new { role = "user", content = JsonSerializer.Serialize(requirement, ContractJson.Options) } },
            text = new { format = new { type = "json_schema", name = "quality_test_plan", strict = true, schema = prompt.OutputSchema } },
            max_output_tokens = options.MaxOutputTokens
        });
        if (payload.Length > 1024 * 1024) throw new PlanningException("planning_input_too_large", metadata);
        using var total = CancellationTokenSource.CreateLinkedTokenSource(ct);
        total.CancelAfter(TimeSpan.FromSeconds(options.TotalTimeoutSeconds));
        for (var attempt = 1; attempt <= options.MaxAttempts; attempt++)
        {
            metadata = metadata with { Attempts = attempt };
            var delay = TimeSpan.FromMilliseconds(250 * Math.Pow(2, attempt - 1));
            try
            {
                using var attemptTimeout = CancellationTokenSource.CreateLinkedTokenSource(total.Token);
                attemptTimeout.CancelAfter(TimeSpan.FromSeconds(options.AttemptTimeoutSeconds));
                using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
                request.Content = new ByteArrayContent(payload);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, attemptTimeout.Token);
                if (!response.IsSuccessStatusCode)
                {
                    if (!Retryable(response.StatusCode) || attempt == options.MaxAttempts)
                        throw new PlanningException("planning_http_error", metadata);
                    var retryAfter = response.Headers.RetryAfter;
                    var requestedDelay = retryAfter?.Delta ?? (retryAfter?.Date - DateTimeOffset.UtcNow);
                    if (requestedDelay > delay) delay = requestedDelay.Value;
                    // A long Retry-After exhausts this job's budget instead of retrying earlier than requested.
                    if (delay > TimeSpan.FromSeconds(options.TotalTimeoutSeconds))
                        throw new PlanningException("planning_retry_budget_exhausted", metadata);
                }
                else
                {
                    using var body = await ReadAsync(response, attemptTimeout.Token);
                    var root = body.RootElement;
                    metadata = metadata with { Model = RequiredString(root, "model"), ResponseId = RequiredString(root, "id") };
                    if (RequiredString(root, "status") != "completed") throw new PlanningException("planning_incomplete", metadata);
                    var texts = new List<string>();
                    foreach (var item in root.GetProperty("output").EnumerateArray())
                    {
                        var type = RequiredString(item, "type");
                        if (type == "reasoning") continue;
                        if (type != "message" || RequiredString(item, "role") != "assistant")
                            throw new PlanningException("planning_invalid_response", metadata);
                        foreach (var content in item.GetProperty("content").EnumerateArray())
                        {
                            var contentType = RequiredString(content, "type");
                            if (contentType == "refusal") throw new PlanningException("planning_refused", metadata);
                            if (contentType != "output_text") throw new PlanningException("planning_invalid_response", metadata);
                            texts.Add(RequiredString(content, "text"));
                        }
                    }
                    if (texts.Count != 1) throw new PlanningException("planning_invalid_response", metadata);
                    using var output = JsonDocument.Parse(texts[0]);
                    RejectDuplicateKeys(output.RootElement);
                    if (!prompt.IsValid(output.RootElement)) throw new PlanningException("planning_invalid_output", metadata);
                    return BuildPlan(requirement, output.RootElement, gaps, metadata);
                }
            }
            catch (PlanningException) { throw; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (OperationCanceledException)
            {
                if (total.IsCancellationRequested || attempt == options.MaxAttempts)
                    throw new PlanningException("planning_timeout", metadata);
            }
            catch (HttpRequestException)
            {
                if (attempt == options.MaxAttempts) throw new PlanningException("planning_transport_error", metadata);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or InvalidDataException)
            {
                throw new PlanningException("planning_invalid_output", metadata);
            }
            try { await Task.Delay(delay, total.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new PlanningException("planning_timeout", metadata); }
        }
        throw new PlanningException("planning_retry_budget_exhausted", metadata);
    }

    private static TestPlan GapPlan(Requirement requirement, List<string> gaps, PlanningMetadata metadata)
        => new(Guid.NewGuid().ToString("N"), requirement.Id, "Source information is insufficient; review coverage gaps before planning.",
            [], [], gaps.Distinct().ToArray(), PlanningPrompt.Version, false, Planning: metadata);

    private static TestPlan BuildPlan(Requirement requirement, JsonElement output, List<string> gaps, PlanningMetadata metadata)
    {
        var knownIds = requirement.AcceptanceCriteria.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        var testIds = new HashSet<string>(StringComparer.Ordinal);
        var covered = new HashSet<string>(StringComparer.Ordinal);
        var tests = new List<TestCase>();
        foreach (var item in output.GetProperty("testCases").EnumerateArray())
        {
            var id = RequiredString(item, "id");
            var links = Strings(item, "acceptanceCriterionIds");
            if (!testIds.Add(id) || links.Any(link => !knownIds.Contains(link)) || links.Distinct().Count() != links.Length)
                throw new PlanningException("planning_invalid_traceability", metadata);
            covered.UnionWith(links);
            tests.Add(new(id, requirement.Id, RequiredString(item, "title"), RequiredString(item, "category"),
                RequiredString(item, "priority"), links, item.GetProperty("steps").EnumerateArray()
                    .Select(step => new TestStep(RequiredString(step, "action"), RequiredString(step, "expectedResult"))).ToArray()));
        }
        gaps.AddRange(Strings(output, "coverageGaps"));
        gaps.AddRange(knownIds.Except(covered).Select(id => $"Acceptance criterion {id} has no proposed test coverage."));
        return new(Guid.NewGuid().ToString("N"), requirement.Id, RequiredString(output, "summary"), tests.ToArray(),
            Strings(output, "assumptions"), gaps.Distinct().ToArray(), PlanningPrompt.Version, false, Planning: metadata);
    }

    private static bool Retryable(HttpStatusCode code) => code is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)code >= 500;
    private static string RequiredString(JsonElement element, string key)
    {
        var value = element.GetProperty(key).GetString();
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidDataException("Missing provider field");
        return value;
    }
    private static string[] Strings(JsonElement element, string key) => element.GetProperty(key).EnumerateArray().Select(e =>
        !string.IsNullOrWhiteSpace(e.GetString()) ? e.GetString()! : throw new InvalidDataException("Empty provider field")).ToArray();
    private static void RejectDuplicateKeys(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate JSON key");
                RejectDuplicateKeys(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicateKeys(item);
    }
    private static async Task<JsonDocument> ReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var bytes = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(bytes, ct)) > 0)
        {
            if (buffer.Length + count > 2 * 1024 * 1024) throw new InvalidDataException("Planning response too large");
            buffer.Write(bytes, 0, count);
        }
        return JsonDocument.Parse(buffer.ToArray());
    }
}
