using Quality.Domain;

namespace Quality.Orchestrator;

public enum MappingStatus { Mapped, Ambiguous, Unmatched }
public enum MappingConfidence { None, Low, Medium, High }

public sealed record PlannedStepCoverage(string TestCaseId, int StepIndex, string PlannedAction,
    string ExpectedResult, MappingStatus ActionStatus, MappingStatus AssertionStatus,
    MappingConfidence Confidence, ExecutionStep[] ActionSteps, ExecutionStep[] AssertionSteps, string[] Reasons);
public sealed record TestCaseCoverage(string TestCaseId, PlannedStepCoverage[] Steps, bool Complete);
public sealed record ManifestPreparationResult(ExecutionManifest Manifest, TestCaseCoverage[] Tests,
    string[] Gaps, MappingConfidence OverallConfidence, bool Complete, bool HasMutations,
    string SchemaVersion = "1.0");

/// <summary>Deterministically maps planned behavior to a semantic, read-only page inventory and explains every gap.</summary>
public static class ReviewedManifestBuilder
{
    public static ExecutionManifest Build(TestPlan plan, string target, UiInspection inspection)
        => Prepare(plan, target, inspection).Manifest;

    public static ManifestPreparationResult Prepare(TestPlan plan, string target, UiInspection inspection)
    {
        if (!Uri.TryCreate(target, UriKind.Absolute, out var targetUri) || inspection.Target != targetUri.AbsoluteUri)
            throw new ArgumentException("Inspection does not match the execution target");
        var controls = inspection.Controls.Where(control => !string.IsNullOrWhiteSpace(control.Selector))
            .OrderBy(control => control.Selector, StringComparer.Ordinal).ThenBy(control => control.Label, StringComparer.Ordinal).ToArray();
        var pagePath = string.IsNullOrEmpty(targetUri.PathAndQuery) ? "/" : targetUri.PathAndQuery;
        var testCoverage = new List<TestCaseCoverage>();
        var executableTests = new List<ExecutionTest>();
        var gaps = new List<string>();

        foreach (var test in plan.TestCases)
        {
            var generated = new List<ExecutionStep>();
            var mappings = new List<PlannedStepCoverage>();
            for (var index = 0; index < test.Steps.Length; index++)
            {
                var planned = test.Steps[index];
                var action = MapAction(planned, controls, pagePath);
                var assertion = MapAssertion(planned, controls, targetUri);
                AddDistinct(generated, action.Steps);
                AddDistinct(generated, assertion.Steps);
                var mapping = new PlannedStepCoverage(test.Id, index, planned.Action, planned.ExpectedResult,
                    action.Status, assertion.Status, Minimum(action.Confidence, assertion.Confidence),
                    action.Steps, assertion.Steps, action.Reasons.Concat(assertion.Reasons).Distinct(StringComparer.Ordinal).ToArray());
                mappings.Add(mapping);
                if (mapping.ActionStatus != MappingStatus.Mapped)
                    gaps.Add($"{test.Id}:step:{index}:action:{mapping.ActionStatus.ToString().ToLowerInvariant()}");
                if (mapping.AssertionStatus != MappingStatus.Mapped)
                    gaps.Add($"{test.Id}:step:{index}:assertion:{mapping.AssertionStatus.ToString().ToLowerInvariant()}");
            }
            var complete = mappings.Count > 0 && mappings.All(mapping => mapping.ActionStatus == MappingStatus.Mapped
                && mapping.AssertionStatus == MappingStatus.Mapped);
            testCoverage.Add(new(test.Id, mappings.ToArray(), complete));
            executableTests.Add(new(test.Id, generated.ToArray()));
        }

        var allMappings = testCoverage.SelectMany(test => test.Steps).ToArray();
        var overall = allMappings.Length == 0 ? MappingConfidence.None : allMappings.Min(mapping => mapping.Confidence);
        var manifest = new ExecutionManifest(plan.Id, ExecutionManifest.HashPlan(plan), target, executableTests.ToArray());
        return new(manifest, testCoverage.ToArray(), gaps.Distinct(StringComparer.Ordinal).Order().ToArray(), overall,
            testCoverage.Count > 0 && testCoverage.All(test => test.Complete),
            manifest.Tests.SelectMany(test => test.Steps).Any(step => step.Action is "click" or "fill"));
    }

    private static Mapping MapAction(TestStep planned, UiControl[] controls, string pagePath)
    {
        var text = $"{planned.Action} {planned.ExpectedResult}";
        if (ContainsAny(planned.Action, "delete", "remove", "destroy", "deactivate", "disable account"))
            return Mapping.Unmatched("destructive_action_requires_explicit_governance");
        if (ContainsAny(planned.Action, "open", "navigate", "visit", "go to", "load"))
            return Mapping.Mapped(MappingConfidence.High, [new("goto", null, pagePath)], "navigation_mapped_to_target");
        if (ContainsAny(planned.Action, "click", "select", "press", "choose"))
            return MapControlAction(text, controls, "click", control => new("click", control.Selector, null));
        if (ContainsAny(planned.Action, "fill", "enter", "type", "input"))
        {
            var value = QuotedLiteral(planned.Action);
            if (value is null) return Mapping.Unmatched("test_data_value_not_declared");
            return MapControlAction(text, controls, "fill", control => new("fill", control.Selector, value));
        }
        return Mapping.Unmatched("unsupported_planned_action");
    }

    private static Mapping MapAssertion(TestStep planned, UiControl[] controls, Uri target)
    {
        var expected = Normalize(planned.ExpectedResult);
        if (expected.Length == 0) return Mapping.Unmatched("expected_result_missing");
        var expectedText = Normalize(QuotedLiteral(planned.ExpectedResult) ?? planned.ExpectedResult);
        var candidates = controls.Where(control => !string.IsNullOrWhiteSpace(control.Label)
            && (expectedText.Contains(Normalize(control.Label), StringComparison.Ordinal)
                || Normalize(control.Label).Contains(expectedText, StringComparison.Ordinal))).ToArray();
        if (candidates.Length > 1 || candidates.Any(control => !control.Unique))
            return Mapping.Ambiguous("expected_result_matches_multiple_controls");
        if (candidates.Length == 1)
        {
            var control = candidates[0];
            var confidence = Stable(control) ? MappingConfidence.High : MappingConfidence.Medium;
            if (control.Tag is "input" or "select" or "textarea")
                return ContainsAny(planned.ExpectedResult, "visible", "present", "displayed", "available")
                    ? Mapping.Mapped(confidence, [new("expectVisible", control.Selector, null)], "expected_control_visibility_matched")
                    : Mapping.Unmatched("expected_form_value_requires_supported_assertion");
            return Mapping.Mapped(confidence, [new("expectText", control.Selector, control.Label)], confidence == MappingConfidence.High
                ? "expected_text_semantically_matched" : "expected_text_uses_weak_selector");
        }
        var expectedUrl = ExtractUrlExpectation(planned.ExpectedResult, target);
        return expectedUrl is not null
            ? Mapping.Mapped(MappingConfidence.High, [new("expectUrl", null, expectedUrl)], "expected_url_matched")
            : Mapping.Unmatched("meaningful_expected_result_not_found");
    }

    private static Mapping MapControlAction(string text, UiControl[] controls, string capability, Func<UiControl, ExecutionStep> create)
    {
        var candidates = controls.Where(control => control.Supports(capability) && !control.Disabled && !control.ReadOnly
            && !string.IsNullOrWhiteSpace(control.Label) && Normalize(text).Contains(Normalize(control.Label), StringComparison.Ordinal))
            .OrderByDescending(control => control.Label.Length).ToArray();
        if (candidates.Length == 0) return Mapping.Unmatched("named_control_not_found");
        var best = candidates.Where(control => control.Label.Length == candidates[0].Label.Length).ToArray();
        if (best.Length != 1 || !best[0].Unique) return Mapping.Ambiguous("named_control_not_unique");
        var confidence = Stable(best[0]) ? MappingConfidence.High : MappingConfidence.Medium;
        return Mapping.Mapped(confidence, [create(best[0])],
            confidence == MappingConfidence.High ? "unique_semantic_control_matched" : "control_uses_weak_selector");
    }

    private static bool Stable(UiControl control) => control.Unique && (control.Selector == "h1" || control.Selector.StartsWith('#')
        || control.Selector.StartsWith("[data-testid=", StringComparison.Ordinal) || !string.IsNullOrWhiteSpace(control.TestId));
    private static string? ExtractUrlExpectation(string value, Uri target)
    {
        var literal = QuotedLiteral(value);
        if (literal is null || !ContainsAny(value, "url", "address", "navigate", "redirect")) return null;
        return Uri.TryCreate(target, literal, out var resolved)
            && resolved.GetLeftPart(UriPartial.Authority) == target.GetLeftPart(UriPartial.Authority) ? literal : null;
    }
    private static void AddDistinct(List<ExecutionStep> target, IEnumerable<ExecutionStep> additions)
    {
        foreach (var step in additions) if (!target.Contains(step)) target.Add(step);
    }
    private static MappingConfidence Minimum(MappingConfidence left, MappingConfidence right)
        => (MappingConfidence)Math.Min((int)left, (int)right);
    private static bool ContainsAny(string value, params string[] terms)
        => terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));
    private static string? QuotedLiteral(string value)
    {
        var match = System.Text.RegularExpressions.Regex.Match(value, "[\\\"']([^\\\"']{1,200})[\\\"']");
        return match.Success && !match.Groups[1].Value.Contains("{{", StringComparison.Ordinal) ? match.Groups[1].Value : null;
    }
    private static string Normalize(string value)
        => System.Text.RegularExpressions.Regex.Replace(value.Trim().ToLowerInvariant(), "\\s+", " ");

    private sealed record Mapping(MappingStatus Status, MappingConfidence Confidence, ExecutionStep[] Steps, string[] Reasons)
    {
        public static Mapping Mapped(MappingConfidence confidence, ExecutionStep[] steps, string reason)
            => new(MappingStatus.Mapped, confidence, steps, [reason]);
        public static Mapping Ambiguous(string reason) => new(MappingStatus.Ambiguous, MappingConfidence.Low, [], [reason]);
        public static Mapping Unmatched(string reason) => new(MappingStatus.Unmatched, MappingConfidence.None, [], [reason]);
    }
}
