using System.Net;
using System.Text.Json;
using Quality.Domain;
using Quality.Orchestrator;
using Xunit;

namespace Quality.Tests;

public sealed class AnthropicPlanningTests
{
    private static PlanningPrompt Prompt => new(AppContext.BaseDirectory);
    private static PlanningOptions Options => new("anthropic-test-secret", "claude-test-snapshot");
    private static Requirement Requirement => new("jira:AUTH-1427", new("jira", "AUTH-1427"), "Sign in", "Account access", [], [],
        [new("AC-1", "Valid credentials grant access"), new("AC-2", "Invalid credentials are rejected")], [], "revision-1", false);
    private static string Output => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "planning-output.json"));
    private static string Envelope(string? text = null, string stopReason = "end_turn", string contentType = "text")
        => JsonSerializer.Serialize(new { id = "msg_fixture", type = "message", role = "assistant", model = "claude-returned-snapshot", stop_reason = stopReason,
            content = new[] { new { type = contentType, text = text ?? Output } } });
    private sealed class Handler(Func<int, CancellationToken, Task<HttpResponseMessage>> action) : HttpMessageHandler
    {
        public int Calls; public string? Body;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("https://api.anthropic.com/v1/messages", request.RequestUri!.AbsoluteUri);
            Assert.Equal("anthropic-test-secret", request.Headers.GetValues("x-api-key").Single());
            Assert.Equal("2023-06-01", request.Headers.GetValues("anthropic-version").Single());
            Assert.Null(request.Headers.Authorization);
            Body = await request.Content!.ReadAsStringAsync(ct);
            return await action(++Calls, ct);
        }
    }
    private static HttpResponseMessage Response(string? body = null, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(body ?? Envelope()) };
    private static AnthropicPlanningProvider Provider(HttpClient http) => new(http, Options, Prompt);

    [Fact]
    public async Task ValidPlanUsesAnthropicContractAndMetadata()
    {
        using var handler = new Handler((_, _) => Task.FromResult(Response())); using var http = new HttpClient(handler);
        var plan = await Provider(http).PlanAsync(Requirement, PlanningPrompt.Version, default);
        Assert.Equal("anthropic", plan.Planning!.Provider); Assert.Equal("claude-returned-snapshot", plan.Planning.Model);
        Assert.Equal("msg_fixture", plan.Planning.ResponseId); Assert.Equal(1, plan.Planning.Attempts);
        using var payload = JsonDocument.Parse(handler.Body!);
        Assert.Equal("json_schema", payload.RootElement.GetProperty("output_config").GetProperty("format").GetProperty("type").GetString());
        Assert.Contains("Sign in", payload.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
        Assert.DoesNotContain("Sign in", payload.RootElement.GetProperty("system").GetString());
    }

    [Theory]
    [InlineData("refusal", "planning_refused")]
    [InlineData("max_tokens", "planning_incomplete")]
    public async Task NonCompletedResponsesAreSanitized(string stopReason, string expected)
    {
        using var handler = new Handler((_, _) => Task.FromResult(Response(Envelope("private provider details", stopReason)))); using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<PlanningException>(() => Provider(http).PlanAsync(Requirement, PlanningPrompt.Version, default));
        Assert.Equal(expected, error.Code); Assert.DoesNotContain("private", error.Message); Assert.Equal("claude-returned-snapshot", error.Metadata.Model);
    }

    [Fact]
    public async Task RetryAndPermanentFailureBehaviorIsBoundedAndSanitized()
    {
        using var handler = new Handler((attempt, _) => Task.FromResult(attempt == 1 ? Response("private", HttpStatusCode.ServiceUnavailable) : Response()));
        using var http = new HttpClient(handler);
        Assert.Equal(2, (await Provider(http).PlanAsync(Requirement, PlanningPrompt.Version, default)).Planning!.Attempts);
        using var permanent = new Handler((_, _) => Task.FromResult(Response("anthropic-test-secret", HttpStatusCode.BadRequest))); using var permanentHttp = new HttpClient(permanent);
        var error = await Assert.ThrowsAsync<PlanningException>(() => Provider(permanentHttp).PlanAsync(Requirement, PlanningPrompt.Version, default));
        Assert.Equal("planning_http_error", error.Code); Assert.DoesNotContain("anthropic-test-secret", error.Message); Assert.Equal(1, permanent.Calls);
    }

    [Fact]
    public async Task StubAndMissingCriteriaNeverCallAnthropic()
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("must not call")); using var http = new HttpClient(handler);
        var stub = await Provider(http).PlanAsync(Requirement with { IsStub = true }, PlanningPrompt.Version, default);
        var incomplete = await Provider(http).PlanAsync(Requirement with { AcceptanceCriteria = [] }, PlanningPrompt.Version, default);
        Assert.Equal(0, handler.Calls); Assert.Equal(0, stub.Planning!.Attempts); Assert.Equal(0, incomplete.Planning!.Attempts);
    }

    [Fact]
    public async Task InvalidContentFailsWithoutLeakingPayload()
    {
        using var invalid = new Handler((_, _) => Task.FromResult(Response(Envelope("private", contentType: "tool_use")))); using var http = new HttpClient(invalid);
        var error = await Assert.ThrowsAsync<PlanningException>(() => Provider(http).PlanAsync(Requirement, PlanningPrompt.Version, default));
        Assert.Equal("planning_invalid_output", error.Code); Assert.DoesNotContain("private", error.Message);
    }
}
