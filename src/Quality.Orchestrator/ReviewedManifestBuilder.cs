using Quality.Domain;

namespace Quality.Orchestrator;

/// <summary>Builds a deterministic, review-required manifest from planned steps and a read-only page inventory.</summary>
public static class ReviewedManifestBuilder
{
    public static ExecutionManifest Build(TestPlan plan, string target, UiInspection inspection)
    {
        if (!Uri.TryCreate(target, UriKind.Absolute, out var targetUri) || inspection.Target != targetUri.AbsoluteUri)
            throw new ArgumentException("Inspection does not match the execution target");

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
            foreach (var planned in test.Steps)
                AddMappedInteraction(steps, planned, stable);
            if (assertions.Length > 0) steps.Add(new("expectVisible", assertions[index % assertions.Length].Selector, null));
            if (steps.Count == 1) steps.Add(new("expectVisible", "body", null));
            return new ExecutionTest(test.Id, steps.ToArray());
        }).ToArray();

        return new ExecutionManifest(plan.Id, ExecutionManifest.HashPlan(plan), target, tests);
    }

    private static void AddMappedInteraction(List<ExecutionStep> steps, TestStep planned, UiControl[] controls)
    {
        var text = $"{planned.Action} {planned.ExpectedResult}";
        if (ContainsAny(text, "click", "select", "press"))
        {
            var control = ExactNamedControl(text, controls, "click");
            if (control is not null && !steps.Any(step => step.Action == "click" && step.Selector == control.Selector))
                steps.Add(new("click", control.Selector, null));
            return;
        }
        if (ContainsAny(text, "fill", "enter", "type"))
        {
            var control = ExactNamedControl(text, controls, "fill");
            var value = QuotedLiteral(text);
            if (control is not null && value is not null && !steps.Any(step => step.Action == "fill" && step.Selector == control.Selector))
                steps.Add(new("fill", control.Selector, value));
        }
    }

    private static UiControl? ExactNamedControl(string text, UiControl[] controls, string capability)
        => controls.Where(control => control.Supports(capability) && !string.IsNullOrWhiteSpace(control.Label))
            .OrderByDescending(control => control.Label.Length)
            .FirstOrDefault(control => Normalize(text).Contains(Normalize(control.Label), StringComparison.Ordinal));

    private static bool ContainsAny(string value, params string[] terms)
        => terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));

    private static string? QuotedLiteral(string value)
    {
        var match = System.Text.RegularExpressions.Regex.Match(value, "[\\\"']([^\\\"']{1,200})[\\\"']");
        return match.Success && !match.Groups[1].Value.Contains("{{", StringComparison.Ordinal) ? match.Groups[1].Value : null;
    }

    private static string Normalize(string value)
        => System.Text.RegularExpressions.Regex.Replace(value.Trim().ToLowerInvariant(), "\\s+", " ");
}
