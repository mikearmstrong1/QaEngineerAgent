using Quality.Domain;

namespace Quality.Orchestrator;

public sealed class AutomationScheduleService(
    IAutomationScheduleStore schedules,
    AutomationWorkflowService workflows,
    IJobStore jobs,
    ExecutionPolicyCatalog policies,
    TimeProvider clock)
{
    public async Task<AutomationSchedule> CreateAsync(string name, string jobId, string target,
        string policyName, int intervalSeconds, DateTimeOffset? firstOccurrenceAt, bool enabled,
        CancellationToken ct)
    {
        name = (name ?? "").Trim();
        if (name.Length is < 1 or > 120 || name.Any(char.IsControl))
            throw new ArgumentException("Schedule name must contain 1-120 non-control characters");
        if (!Guid.TryParseExact(jobId, "N", out _)
            || await jobs.GetAsync(jobId, ct) is not { Status: JobStatus.Completed, TestPlan: not null })
            throw new ArgumentException("A completed planning job is required");
        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
            || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("Schedule target must be an absolute HTTP(S) URL without credentials or fragment");
        var policy = policies.Required(policyName);
        if (!policy.NonProduction) throw new ArgumentException("Automation schedules require a non-production policy");
        var origin = uri.GetLeftPart(UriPartial.Authority);
        if (!policy.AllowedOrigins.Select(ExecutionManifest.ValidateOrigin).Contains(origin, StringComparer.Ordinal))
            throw new ArgumentException("Target is not allowed by the automation policy");
        if (intervalSeconds is < 60 or > 31_536_000)
            throw new ArgumentException("Schedule interval must be between 60 and 31536000 seconds");
        var now = clock.GetUtcNow();
        var first = firstOccurrenceAt ?? now;
        if (first < now.AddYears(-1) || first > now.AddYears(10))
            throw new ArgumentException("First occurrence must be within the supported scheduling horizon");
        var schedule = new AutomationSchedule(Guid.NewGuid().ToString("N"), name, jobId, uri.AbsoluteUri,
            policy.Name, intervalSeconds, enabled, first, now, now);
        return await schedules.CreateAsync(schedule, ct);
    }

    public Task<AutomationSchedule?> GetAsync(string id, CancellationToken ct) => schedules.GetAsync(id, ct);
    public Task<IReadOnlyList<AutomationSchedule>> ListAsync(CancellationToken ct) => schedules.ListAsync(ct);

    public Task<AutomationSchedule> SetEnabledAsync(string id, long revision, bool enabled, CancellationToken ct)
        => schedules.SetEnabledAsync(id, revision, enabled, clock.GetUtcNow(), ct);

    public async Task<AutomationSchedule?> ProcessNextDueAsync(CancellationToken ct)
    {
        var claimed = await schedules.ClaimDueAsync(clock.GetUtcNow(), TimeSpan.FromMinutes(2), ct);
        if (claimed is null) return null;
        try
        {
            var occurrence = claimed.ClaimedOccurrenceAt
                ?? throw new InvalidOperationException("Claimed schedule occurrence is missing");
            var key = $"schedule:{claimed.Id}:{occurrence.ToUnixTimeMilliseconds()}";
            var workflow = await workflows.CreateAsync(claimed.JobId, claimed.Target, claimed.PolicyName, key, ct);
            return await schedules.CompleteClaimAsync(claimed, claimed.Revision, claimed.LeaseToken!, workflow.Id,
                clock.GetUtcNow(), ct);
        }
        catch
        {
            await schedules.ReleaseClaimAsync(claimed.Id, claimed.Revision, claimed.LeaseToken!, CancellationToken.None);
            throw;
        }
    }

    public static DateTimeOffset NextAfter(DateTimeOffset occurrence, int intervalSeconds, DateTimeOffset now)
    {
        var intervalTicks = TimeSpan.FromSeconds(intervalSeconds).Ticks;
        var elapsed = Math.Max(0, (now - occurrence).Ticks);
        var steps = elapsed / intervalTicks + 1;
        return occurrence.AddTicks(checked(steps * intervalTicks));
    }
}
