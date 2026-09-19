using System.Net;
using Quality.Domain;
using Quality.Orchestrator;
using Xunit;

namespace Quality.Tests;

public sealed class RequirementSourceTests
{
    private static readonly RequirementSourceOptions Options = new("https://example.atlassian.net", "reader@example.com", "test-token", "customfield_10010", "coda-token", "c-title", "c-description", "c-criteria");
    private sealed class Handler(string fixture, HttpStatusCode status = HttpStatusCode.OK, int? retryAfterSeconds = null) : HttpMessageHandler
    {
        public Uri? Uri;
        public string? Scheme;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Uri = request.RequestUri;
            Scheme = request.Headers.Authorization?.Scheme;
            var response = new HttpResponseMessage(status) { Content = new StringContent(fixture) };
            if (retryAfterSeconds is { } seconds) response.Headers.RetryAfter = new(TimeSpan.FromSeconds(seconds));
            return Task.FromResult(response);
        }
    }
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name + ".json"));

    [Fact]
    public async Task RetryAfterIsPreservedWithoutExposingResponseBody()
    {
        using var http = new HttpClient(new Handler("private response", HttpStatusCode.TooManyRequests, 20));
        var error = await Assert.ThrowsAsync<RequirementRequestException>(() =>
            new RemoteRequirementSource(http, Options).NormalizeAsync(new("jira", "AUTH-1427"), default));
        Assert.Equal(TimeSpan.FromSeconds(20), error.RetryAfter);
        Assert.DoesNotContain("private", error.Message);
    }

    [Fact]
    public async Task JiraPreservesSourceAndProvenance()
    {
        using var handler = new Handler(Fixture("jira"));
        using var http = new HttpClient(handler);
        var source = new RemoteRequirementSource(http, Options);
        var result = await source.NormalizeAsync(new("jira", "AUTH-1427"), default);
        Assert.False(result.IsStub);
        Assert.Equal("Allow account access.\n", result.Description);
        var criterion = Assert.Single(result.AcceptanceCriteria);
        Assert.Equal("Valid credentials grant access.", criterion.Description);
        Assert.Equal(result.SourceRevision, criterion.Provenance!.Revision);
        Assert.Equal("10001", criterion.Provenance.ResourceId);
        Assert.Equal("customfield_10010", criterion.Provenance.Field);
        Assert.Equal("Basic", handler.Scheme);
        Assert.Equal("/rest/api/3/issue/AUTH-1427", handler.Uri!.AbsolutePath);
        var again = await source.NormalizeAsync(new("jira", "AUTH-1427"), default);
        Assert.Equal(criterion.Id, again.AcceptanceCriteria[0].Id);
    }

    [Fact]
    public async Task CodaRetainsDocumentTableRowAndColumn()
    {
        using var handler = new Handler(Fixture("coda"));
        using var http = new HttpClient(handler);
        var result = await new RemoteRequirementSource(http, Options).NormalizeAsync(new("coda", "doc/grid-table/i-row"), default);
        Assert.Equal(2, result.AcceptanceCriteria.Length);
        Assert.Equal("docs/doc/tables/grid-table/rows/i-row", result.AcceptanceCriteria[0].Provenance!.ResourceId);
        Assert.Equal("/c-criteria/1", result.AcceptanceCriteria[1].Provenance!.Locator);
        Assert.Equal("Bearer", handler.Scheme);
        Assert.Equal("coda.io", handler.Uri!.Host);
    }

    [Fact]
    public async Task MissingCriteriaRemainEmpty()
    {
        using var http = new HttpClient(new Handler(Fixture("jira")));
        var result = await new RemoteRequirementSource(http, Options with { JiraAcceptanceField = null }).NormalizeAsync(new("jira", "AUTH-1427"), default);
        Assert.Empty(result.AcceptanceCriteria);
        Assert.Single(result.Risks);
    }

    [Theory]
    [InlineData("AC")]
    [InlineData("ac:")]
    [InlineData("Acceptance Criteria")]
    [InlineData("acceptance criteria:")]
    public async Task JiraImportsMarkedDescriptionAcceptanceSection(string marker)
    {
        const string template = """
            {"id":"10013","key":"KAN-4","fields":{"summary":"Add call duration group","updated":"2026-09-19T07:00:59.199-0400","description":{"type":"doc","version":1,"content":[
              {"type":"paragraph","content":[{"type":"text","text":"Context"}]},
              {"type":"paragraph","content":[{"type":"text","text":"Continuous fields require range grouping."}]},
              {"type":"heading","attrs":{"level":2},"content":[{"type":"text","text":"MARKER"}]},
              {"type":"paragraph","content":[{"type":"text","text":"Add Call Duration to Group By."}]},
              {"type":"bulletList","content":[
                {"type":"listItem","content":[{"type":"paragraph","content":[{"type":"text","text":"0–2 minutes"}]}]},
                {"type":"listItem","content":[{"type":"paragraph","content":[{"type":"text","text":"2–5 minutes"}]}]}
              ]},
              {"type":"paragraph","content":[{"type":"text","text":"Chart Behavior"}]},
              {"type":"bulletList","content":[{"type":"listItem","content":[{"type":"paragraph","content":[{"type":"text","text":"Sort ranges in ascending order."}]}]}]}
            ]},"customfield_10010":null}}
            """;
        var fixture = template.Replace("MARKER", marker);
        using var http = new HttpClient(new Handler(fixture));
        var result = await new RemoteRequirementSource(http, Options).NormalizeAsync(new("jira", "KAN-4"), default);

        var criterion = Assert.Single(result.AcceptanceCriteria);
        Assert.Contains("Add Call Duration to Group By.", criterion.Description);
        Assert.Contains("0–2 minutes", criterion.Description);
        Assert.Contains("Chart Behavior", criterion.Description);
        Assert.DoesNotContain("Continuous fields require range grouping.", criterion.Description);
        Assert.Equal("description", criterion.Provenance!.Field);
        Assert.Equal("/description/content/3", criterion.Provenance.Locator);
        Assert.Empty(result.Risks);
    }

    [Fact]
    public async Task JiraCustomAcceptanceFieldTakesPriorityOverDescriptionSection()
    {
        var fixture = Fixture("jira").Replace("Allow account access.", "AC\\nDescription criterion must not replace the configured field.");
        using var http = new HttpClient(new Handler(fixture));
        var result = await new RemoteRequirementSource(http, Options).NormalizeAsync(new("jira", "AUTH-1427"), default);
        var criterion = Assert.Single(result.AcceptanceCriteria);
        Assert.Equal("Valid credentials grant access.", criterion.Description);
        Assert.Equal("customfield_10010", criterion.Provenance!.Field);
    }

    [Fact]
    public async Task JiraEmptyDescriptionAcceptanceSectionRemainsMissing()
    {
        const string fixture = """
            {"id":"10013","key":"KAN-4","fields":{"summary":"Empty AC","updated":"2026-09-19T07:00:59.199-0400","description":{"type":"doc","version":1,"content":[{"type":"paragraph","content":[{"type":"text","text":"AC"}]},{"type":"paragraph","content":[]}]},"customfield_10010":null}}
            """;
        using var http = new HttpClient(new Handler(fixture));
        var result = await new RemoteRequirementSource(http, Options).NormalizeAsync(new("jira", "KAN-4"), default);
        Assert.Empty(result.AcceptanceCriteria);
        Assert.Single(result.Risks);
    }

    [Fact]
    public async Task ImportedRequirementAndAuditSurvivePersistence()
    {
        var directory = Path.Combine(Path.GetTempPath(), "quality-import-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var http = new HttpClient(new Handler(Fixture("jira")));
            var store = new Quality.Persistence.FileJobStore(directory, TimeProvider.System);
            var service = new JobService(store, new RemoteRequirementSource(http, Options), new StubLlmProvider(), TimeProvider.System);
            var job = await service.SubmitAsync(new(new("jira", "AUTH-1427")), default);
            await service.ProcessNextAsync(job.Id, default);
            var loaded = (await store.GetAsync(job.Id, default))!;
            Assert.Equal(JobStatus.Completed, loaded.Status);
            Assert.False(loaded.Requirement!.IsStub);
            Assert.NotNull(loaded.Requirement.AcceptanceCriteria[0].Provenance);
            Assert.False(loaded.Decisions[0].IsStub);
            Assert.Equal("jira", loaded.Decisions[0].Provider);
            Assert.True(loaded.TestPlan!.IsStub);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task OversizedResponseFails()
    {
        using var http = new HttpClient(new Handler(new string('x', 2 * 1024 * 1024 + 1)));
        await Assert.ThrowsAsync<InvalidDataException>(() => new RemoteRequirementSource(http, Options).NormalizeAsync(new("jira", "AUTH-1427"), default));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.Redirect)]
    public async Task ProviderErrorsFailWithoutExposingBody(HttpStatusCode status)
    {
        using var http = new HttpClient(new Handler("sensitive source body", status));
        var error = await Assert.ThrowsAnyAsync<HttpRequestException>(() => new RemoteRequirementSource(http, Options).NormalizeAsync(new("jira", "AUTH-1427"), default));
        Assert.DoesNotContain("sensitive", error.Message);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("not json")]
    public async Task MalformedResponsesFail(string body)
    {
        using var http = new HttpClient(new Handler(body));
        await Assert.ThrowsAnyAsync<Exception>(() => new RemoteRequirementSource(http, Options).NormalizeAsync(new("jira", "AUTH-1427"), default));
    }

    [Fact]
    public async Task CancellationAndUnsafeConfigurationFail()
    {
        using var http = new HttpClient(new Handler(Fixture("jira")));
        await Assert.ThrowsAsync<ArgumentException>(() => new RemoteRequirementSource(http, Options with { JiraBaseUrl = "http://example.com" }).NormalizeAsync(new("jira", "AUTH-1427"), default));
        await Assert.ThrowsAsync<ArgumentException>(() => new RemoteRequirementSource(http, Options).NormalizeAsync(new("coda", "doc/../row"), default));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new RemoteRequirementSource(http, Options).NormalizeAsync(new("jira", "AUTH-1427"), cts.Token));
    }
}
