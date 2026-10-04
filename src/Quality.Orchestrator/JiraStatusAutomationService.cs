using System.Security.Cryptography;
using System.Text;
using Quality.Domain;

namespace Quality.Orchestrator;

public sealed record JiraStatusAutomationItem(string Key, string Revision, string JobId,
    AutomationWorkflowStatus? WorkflowStatus, string? WorkflowId, string? Error = null);
public sealed record JiraStatusAutomationResult(string Status, int Discovered,
    JiraStatusAutomationItem[] Items);

// A bounded, synchronous drain is useful for the CLI and reuses the same durable
// job/workflow/request records and policy gates as every other automation surface.
public sealed class JiraStatusAutomationService(IJiraIssueSearcher jira, JobService jobs,
    AutomationWorkflowService workflows, ExecutionRequestService executions)
{
    public async Task<JiraStatusAutomationResult> RunAsync(string status, int maximum, string target,
        string policy, string? idempotencyNamespace, CancellationToken ct)
    {
        var issues = await jira.SearchByStatusAsync(status, maximum, ct);
        var prefix = string.IsNullOrWhiteSpace(idempotencyNamespace) ? "jira-status" : idempotencyNamespace.Trim();
        var items = new List<JiraStatusAutomationItem>(issues.Count);
        foreach (var issue in issues)
        {
            var jobId = "";
            string? workflowId = null;
            AutomationWorkflowStatus? workflowStatus = null;
            try
            {
                var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
                    $"{prefix}:{status}:{issue.Key}:{issue.Revision}")));
                var job = await jobs.SubmitAsync(new(new("jira", issue.Key)), ct, key);
                jobId = job.Id;
                while (!job.IsTerminal)
                    job = await jobs.ProcessNextAsync(job.Id, ct) ?? throw new InvalidOperationException("Persisted job disappeared");
                if (job.Status != JobStatus.Completed)
                {
                    items.Add(new(issue.Key, issue.Revision, job.Id, null, null, job.Error ?? "planning_failed"));
                    continue;
                }

                var workflow = await workflows.CreateAsync(job.Id, target, policy, key, ct);
                workflowId = workflow.Id;
                while (workflow.Status is not (AutomationWorkflowStatus.AwaitingReview or AutomationWorkflowStatus.Completed
                    or AutomationWorkflowStatus.Failed or AutomationWorkflowStatus.Cancelled))
                {
                    await workflows.ProcessNextAsync(ct);
                    workflow = await workflows.GetAsync(workflow.Id, ct) ?? throw new InvalidOperationException("Persisted workflow disappeared");
                    if (workflow.Status == AutomationWorkflowStatus.Queued)
                        await executions.ProcessNextAsync(ct);
                }
                workflowStatus = workflow.Status;
                items.Add(new(issue.Key, issue.Revision, job.Id, workflow.Status, workflow.Id, workflow.Error));
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                // Keep the remaining stories moving; do not persist arbitrary provider text.
                items.Add(new(issue.Key, issue.Revision, jobId, workflowStatus, workflowId, ex.GetType().Name));
            }
        }
        return new(status, issues.Count, items.ToArray());
    }
}
