using Quality.Domain;
namespace Quality.Orchestrator;

public sealed class JobService(IJobStore store, IRequirementSource source, ILlmProvider llm, TimeProvider clock)
{
    public const string PromptVersion = PlanningPrompt.Version;
    public Task<QualityJob?> GetAsync(string id, CancellationToken ct) => store.GetAsync(id, ct);
    public async Task<QualityJob> SubmitAsync(JobRequest request, CancellationToken ct)
    {
        var job = QualityJob.Create(request, clock.GetUtcNow());
        await store.CreateAsync(job, ct);
        return job;
    }
    public async Task<QualityJob?> ProcessNextAsync(string? id, CancellationToken ct)
    {
        var job = await store.ClaimAsync(id, TimeSpan.FromMinutes(5), ct);
        if (job is null) return null;
        try
        {
            if (job.Status == JobStatus.Queued)
                job = await store.SaveAsync(job.TransitionTo(JobStatus.Normalizing, clock.GetUtcNow(), "Normalization started"), ct);
            if (job.Status == JobStatus.Normalizing)
            {
                var requirement = await source.NormalizeAsync(job.Reference, ct);
                job = job with { Requirement = requirement, Decisions = [..job.Decisions,
                    Decision(job, "Normalize", requirement.IsStub ? "stub" : requirement.Reference.Source, "normalize/v1",
                        requirement.IsStub ? "Created synthetic requirement" : "Imported source requirement", requirement.IsStub)] };
                job = await store.SaveAsync(job.TransitionTo(JobStatus.Planning, clock.GetUtcNow(), "Requirement persisted; planning started"), ct);
            }
            if (job.Status == JobStatus.Planning)
            {
                var plan = await llm.PlanAsync(job.Requirement!, PromptVersion, ct);
                job = job with { TestPlan = plan, Decisions = [..job.Decisions,
                    Decision(job, "Plan", llm.Name, PromptVersion,
                        plan.IsStub ? "Created structured stub test plan" : "Created validated plan; review coverage gaps", plan.IsStub, plan.Planning)] };
                job = await store.SaveAsync(job.TransitionTo(JobStatus.Completed, clock.GetUtcNow(), "Planning completed; no tests executed"), ct);
            }
            return job;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (LeaseLostException) { throw; }
        catch (PlanningException ex)
        {
            job = job with { Error = ex.Code, Decisions = [..job.Decisions,
                Decision(job, "Plan", llm.Name, PromptVersion, ex.Code, false, ex.Metadata)] };
            return await store.SaveAsync(job.TransitionTo(JobStatus.Failed, clock.GetUtcNow(), "Planning provider failed"), ct);
        }
        catch (Exception)
        {
            // Provider exception text can contain credentials or source content; do not expose it through the API.
            job = job with { Error = "provider_failure" };
            return await store.SaveAsync(job.TransitionTo(JobStatus.Failed, clock.GetUtcNow(), "Provider processing failed"), ct);
        }
    }
    private AgentDecision Decision(QualityJob job, string stage, string provider, string prompt, string summary, bool isStub = true, PlanningMetadata? planning = null)
        => new(Guid.NewGuid().ToString("N"), job.Id, stage, provider, prompt, summary, clock.GetUtcNow(), isStub, Planning: planning);
}
