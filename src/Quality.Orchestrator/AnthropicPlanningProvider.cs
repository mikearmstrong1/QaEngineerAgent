using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Quality.Domain;

namespace Quality.Orchestrator;

public sealed class AnthropicPlanningProvider : ILlmProvider
{
    private readonly HttpClient http;
    private readonly PlanningOptions options;
    private readonly PlanningPrompt prompt;
    public string Name => "anthropic";
    public const string ProviderVersion = "messages/2023-06-01;quality-planner/v1";

    public AnthropicPlanningProvider(HttpClient http, PlanningOptions options, PlanningPrompt prompt)
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
            return OpenAiPlanningProvider.GapPlan(requirement, gaps, metadata);
        }
        if (requirement.IsStub) return OpenAiPlanningProvider.GapPlan(requirement, gaps, metadata);

        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            model = options.Model,
            system = prompt.SystemText,
            messages = new[] { new { role = "user", content = JsonSerializer.Serialize(requirement, ContractJson.Options) } },
            output_config = new { format = new { type = "json_schema", schema = prompt.OutputSchema } },
            max_tokens = options.MaxOutputTokens
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
                using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
                request.Headers.Add("x-api-key", options.ApiKey);
                request.Headers.Add("anthropic-version", "2023-06-01");
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
                    if (delay > TimeSpan.FromSeconds(options.TotalTimeoutSeconds))
                        throw new PlanningException("planning_retry_budget_exhausted", metadata);
                }
                else
                {
                    using var body = await OpenAiPlanningProvider.ReadAsync(response, attemptTimeout.Token);
                    var root = body.RootElement;
                    metadata = metadata with
                    {
                        Model = OpenAiPlanningProvider.RequiredString(root, "model"),
                        ResponseId = OpenAiPlanningProvider.RequiredString(root, "id")
                    };
                    if (OpenAiPlanningProvider.RequiredString(root, "type") != "message"
                        || OpenAiPlanningProvider.RequiredString(root, "role") != "assistant")
                        throw new PlanningException("planning_invalid_response", metadata);
                    var stopReason = OpenAiPlanningProvider.RequiredString(root, "stop_reason");
                    if (stopReason == "refusal") throw new PlanningException("planning_refused", metadata);
                    if (stopReason != "end_turn") throw new PlanningException("planning_incomplete", metadata);
                    var texts = root.GetProperty("content").EnumerateArray().Select(item =>
                    {
                        if (OpenAiPlanningProvider.RequiredString(item, "type") != "text")
                            throw new InvalidDataException("Unexpected provider content");
                        return OpenAiPlanningProvider.RequiredString(item, "text");
                    }).ToArray();
                    if (texts.Length != 1) throw new PlanningException("planning_invalid_response", metadata);
                    using var output = JsonDocument.Parse(texts[0]);
                    OpenAiPlanningProvider.RejectDuplicateKeys(output.RootElement);
                    if (!prompt.IsValid(output.RootElement)) throw new PlanningException("planning_invalid_output", metadata);
                    return OpenAiPlanningProvider.BuildPlan(requirement, output.RootElement, gaps, metadata);
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

    private static bool Retryable(HttpStatusCode code) => code is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)code >= 500;
}
