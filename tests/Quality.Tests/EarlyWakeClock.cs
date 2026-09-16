using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Quality.Tests;

internal sealed class EarlyWakeClock : TimeProvider
{
    private long _ticks;
    private readonly Channel<PendingTimer> _channel = Channel.CreateUnbounded<PendingTimer>();

    public EarlyWakeClock() => _ticks = DateTimeOffset.UtcNow.Ticks;

    public override DateTimeOffset GetUtcNow() => new DateTimeOffset(Interlocked.Read(ref _ticks), TimeSpan.Zero);

    public void Advance(TimeSpan delta) => Interlocked.Add(ref _ticks, (long)delta.Ticks);

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        if (dueTime > TimeSpan.Zero && dueTime < TimeSpan.FromSeconds(1))
        {
            var t = new PendingTimer(callback, state, dueTime);
            _channel.Writer.TryWrite(t);
            return t;
        }
        return TimeProvider.System.CreateTimer(callback, state, dueTime, period);
    }

    public async Task<PendingTimer> NextAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        return await _channel.Reader.ReadAsync(cts.Token);
    }

    public sealed class PendingTimer : ITimer
    {
        private readonly TimerCallback _cb;
        private readonly object? _state;
        private int _disposed;
        public TimeSpan DueTime { get; }

        public PendingTimer(TimerCallback cb, object? state, TimeSpan dueTime)
        {
            _cb = cb; _state = state; DueTime = dueTime;
        }

        public void Fire()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) == 0)
                _cb(_state);
        }

        public bool Change(TimeSpan dueTime, TimeSpan period) => false;
        public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
        public ValueTask DisposeAsync() { Dispose(); return default; }
    }
}
