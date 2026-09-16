using System.Diagnostics;
using System.Globalization;
using System.Text;
namespace Quality.Orchestrator;

public enum MetricOperation { Submit, Cancel, Claim, Save, Renew, Read, Normalize, Plan, Attempt }
public enum MetricOutcome { Success, Replayed, Conflict, Missing, Idle, Completed, Failed, Cancelled, BudgetExhausted, LeaseLost, Interrupted, Error }

// Process-local, bounded series: no caller-provided labels or stored job content.
public sealed class JobMetrics
{
    private static readonly string[] Operations = ["submit", "cancel", "claim", "save", "renew", "read", "normalize", "plan", "attempt"];
    private static readonly string[] Outcomes = ["success", "replayed", "conflict", "missing", "idle", "completed", "failed", "cancelled", "budget_exhausted", "lease_lost", "interrupted", "error"];
    private static readonly double[] Bounds = [0.005, 0.025, 0.1, 0.5, 2, 10, 60, 300];
    private readonly object gate = new();
    private readonly long[,] counts = new long[Operations.Length, Outcomes.Length];
    private readonly long[,] buckets = new long[Operations.Length, Bounds.Length];
    private readonly long[] active = new long[Operations.Length];
    private readonly long[] observations = new long[Operations.Length];
    private readonly double[] sums = new double[Operations.Length];

    public async Task<T> TrackAsync<T>(MetricOperation operation, Func<Task<T>> action, Func<T, MetricOutcome>? classify = null)
    {
        var index = (int)operation;
        if (index < 0 || index >= Operations.Length) throw new ArgumentOutOfRangeException(nameof(operation));
        lock (gate) active[index]++;
        var started = Stopwatch.GetTimestamp();
        var outcome = MetricOutcome.Error;
        try
        {
            var result = await action();
            outcome = classify?.Invoke(result) ?? MetricOutcome.Success;
            if (!Enum.IsDefined(outcome)) outcome = MetricOutcome.Error;
            return result;
        }
        catch (IdempotencyConflictException) { outcome = MetricOutcome.Conflict; throw; }
        catch (LeaseLostException) { outcome = MetricOutcome.LeaseLost; throw; }
        catch (OperationCanceledException) { outcome = MetricOutcome.Interrupted; throw; }
        finally
        {
            var seconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
            lock (gate)
            {
                active[index]--;
                counts[index, (int)outcome]++;
                observations[index]++;
                sums[index] += seconds;
                for (var bucket = 0; bucket < Bounds.Length; bucket++)
                    if (seconds <= Bounds[bucket]) buckets[index, bucket]++;
            }
        }
    }

    public string Render()
    {
        var text = new StringBuilder();
        // One coherent snapshot: counts, histogram +Inf, and active work cannot tear.
        lock (gate)
        {
            text.AppendLine("# HELP quality_operations_total Observed operation results in this process, not unique jobs.");
            text.AppendLine("# TYPE quality_operations_total counter");
            for (var op = 0; op < Operations.Length; op++)
                for (var outcome = 0; outcome < Outcomes.Length; outcome++)
                    text.Append("quality_operations_total{operation=\"").Append(Operations[op]).Append("\",outcome=\"")
                        .Append(Outcomes[outcome]).Append("\"} ").Append(counts[op, outcome].ToString(CultureInfo.InvariantCulture)).Append('\n');
            text.AppendLine("# HELP quality_operations_active Operations currently running in this process.");
            text.AppendLine("# TYPE quality_operations_active gauge");
            for (var op = 0; op < Operations.Length; op++)
                Sample(text, "quality_operations_active", op, active[op]);
            text.AppendLine("# HELP quality_operation_duration_seconds Operation duration including failed and interrupted calls.");
            text.AppendLine("# TYPE quality_operation_duration_seconds histogram");
            for (var op = 0; op < Operations.Length; op++)
            {
                for (var bucket = 0; bucket <= Bounds.Length; bucket++)
                    text.Append("quality_operation_duration_seconds_bucket{operation=\"").Append(Operations[op])
                        .Append("\",le=\"").Append(bucket == Bounds.Length ? "+Inf" : Bounds[bucket].ToString("R", CultureInfo.InvariantCulture))
                        .Append("\"} ").Append((bucket == Bounds.Length ? observations[op] : buckets[op, bucket]).ToString(CultureInfo.InvariantCulture)).Append('\n');
                Sample(text, "quality_operation_duration_seconds_sum", op, sums[op]);
                Sample(text, "quality_operation_duration_seconds_count", op, observations[op]);
            }
        }
        return text.ToString();
    }

    private static void Sample(StringBuilder text, string name, int operation, double value)
        => text.Append(name).Append("{operation=\"").Append(Operations[operation]).Append("\"} ")
            .Append(value.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
}
