using System.Diagnostics;
using System.Text.Json;

namespace Quality.Orchestrator;

public sealed record UiControl(string Tag, string Role, string Label, string Selector, string[]? Capabilities = null)
{
    public bool Supports(string capability) => (Capabilities ?? []).Contains(capability, StringComparer.Ordinal);
}
public sealed record UiInspection(string Target, UiControl[] Controls);
public interface IUiInspector
{
    Task<UiInspection> InspectAsync(Uri target, CancellationToken ct);
}

public sealed class PlaywrightUiInspector(ExecutionOptions options) : IUiInspector
{
    public async Task<UiInspection> InspectAsync(Uri target, CancellationToken ct)
    {
        var origin = target.GetLeftPart(UriPartial.Authority);
        if (!options.AllowedOrigins.Contains(origin, StringComparer.Ordinal)) throw new ArgumentException("Inspection target is not allowlisted");
        var script = Path.Combine(Path.GetFullPath(options.Workspace), "playwright/execution/inspect.cjs");
        if (!File.Exists(script)) throw new ArgumentException("Execution workspace is missing the trusted inspector");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var start = new ProcessStartInfo(options.NodeExecutable) { WorkingDirectory = Path.GetFullPath(options.Workspace), UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment.Clear();
        foreach (var key in new[] { "PATH", "HOME", "TMPDIR", "TEMP", "TMP", "SystemRoot", "PLAYWRIGHT_BROWSERS_PATH" })
            if (Environment.GetEnvironmentVariable(key) is { } value) start.Environment[key] = value;
        start.ArgumentList.Add(script);
        start.ArgumentList.Add(target.AbsoluteUri);
        start.ArgumentList.Add(string.Join(';', options.AllowedOrigins));
        using var process = new Process { StartInfo = start };
        process.Start();
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { if (!process.HasExited) process.Kill(true); throw; }
        if (process.ExitCode != 0 || output.Result.Length > 256 * 1024) throw new ArgumentException("UI inspection failed");
        var inspection = JsonSerializer.Deserialize<UiInspection>(await output, Quality.Domain.ContractJson.Options)
            ?? throw new ArgumentException("UI inspection did not return controls");
        if (inspection.Target != target.AbsoluteUri || inspection.Controls.Length > 100) throw new ArgumentException("UI inspection result is invalid");
        return inspection with { Controls = inspection.Controls.Where(c => c.Tag.Length <= 40 && c.Role.Length <= 80 && c.Label.Length <= 500 && c.Selector.Length <= 2000 && (c.Capabilities ?? []).Length <= 4).ToArray() };
    }
}
