using Quality.Domain;

namespace Quality.Orchestrator;

/// <summary>Builds a conservative, review-required starting manifest from a read-only page inventory.</summary>
public static class ReviewedManifestBuilder
{
    public static ExecutionManifest Build(TestPlan plan, string target, UiInspection inspection)
    {
        if (!Uri.TryCreate(target, UriKind.Absolute, out var targetUri) || inspection.Target != targetUri.AbsoluteUri)
            throw new ArgumentException("Inspection does not match the execution target");

        // Only assertion actions are generated. Interactions always require an explicit human edit.
        var stable = inspection.Controls
            .Where(control => control.Selector == "h1" || control.Selector.StartsWith('#'))
            .GroupBy(control => control.Selector, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        var heading = stable.FirstOrDefault(control => control.Selector == "h1" && !string.IsNullOrWhiteSpace(control.Label));
        var assertions = stable.Where(control => control.Selector != "h1").ToArray();
        var pagePath = string.IsNullOrEmpty(targetUri.PathAndQuery) ? "/" : targetUri.PathAndQuery;

        var tests = plan.TestCases.Select((test, index) =>
        {
            var steps = new List<ExecutionStep> { new("goto", null, pagePath) };
            if (heading is not null) steps.Add(new("expectText", "h1", heading.Label));
            if (assertions.Length > 0) steps.Add(new("expectVisible", assertions[index % assertions.Length].Selector, null));
            if (steps.Count == 1) steps.Add(new("expectVisible", "body", null));
            return new ExecutionTest(test.Id, steps.ToArray());
        }).ToArray();

        return new ExecutionManifest(plan.Id, ExecutionManifest.HashPlan(plan), target, tests);
    }
}
