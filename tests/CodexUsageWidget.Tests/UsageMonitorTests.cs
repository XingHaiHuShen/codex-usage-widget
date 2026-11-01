using CodexUsageWidget.Application;
using CodexUsageWidget.Domain;

namespace CodexUsageWidget.Tests;

public sealed class UsageMonitorTests
{
    [Fact]
    public async Task TimeoutRetriesOnceAndCanRecoverWithoutReportingOffline()
    {
        var provider = new TimeoutOnceProvider();
        await using var monitor = new UsageMonitor(provider, requestTimeout: TimeSpan.FromMilliseconds(30));
        var updated = 0;
        var failed = 0;
        monitor.SnapshotUpdated += _ => updated++;
        monitor.RefreshFailed += _ => failed++;
        await monitor.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, provider.ReadCount);
        Assert.Equal(1, updated);
        Assert.Equal(0, failed);
    }

    private sealed class TimeoutOnceProvider : IUsageProvider
    {
        public int ReadCount { get; private set; }
        public event EventHandler? RateLimitsChanged { add { } remove { } }
        public event EventHandler<string>? DiagnosticMessage { add { } remove { } }
        public async Task<UsageSnapshot> ReadUsageAsync(CancellationToken cancellationToken = default)
        {
            if (++ReadCount == 1) { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            return new UsageSnapshot(new UsageRateLimits([], null, null), null, DateTimeOffset.Now);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task RefreshAfterCurrentWaitsAndReadsFreshUsage()
    {
        var provider = new BlockingUsageProvider();
        await using var monitor = new UsageMonitor(provider);

        var activeRefresh = monitor.RefreshAsync();
        await provider.FirstReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var refreshAfterCurrent = monitor.RefreshAfterCurrentAsync();
        Assert.False(refreshAfterCurrent.IsCompleted);

        provider.ReleaseFirstRead.TrySetResult();
        await Task.WhenAll(activeRefresh, refreshAfterCurrent)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, provider.ReadCount);
    }

    private sealed class BlockingUsageProvider : IUsageProvider
    {
        private int _readCount;

        public TaskCompletionSource FirstReadStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseFirstRead { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public int ReadCount => Volatile.Read(ref _readCount);

        public event EventHandler? RateLimitsChanged
        {
            add { }
            remove { }
        }

        public event EventHandler<string>? DiagnosticMessage
        {
            add { }
            remove { }
        }

        public async Task<UsageSnapshot> ReadUsageAsync(
            CancellationToken cancellationToken = default)
        {
            var read = Interlocked.Increment(ref _readCount);
            if (read == 1)
            {
                FirstReadStarted.TrySetResult();
                await ReleaseFirstRead.Task.WaitAsync(cancellationToken);
            }

            return new UsageSnapshot(
                new UsageRateLimits([], PlanType: null, ResetCredits: null),
                TokenActivity: null,
                FetchedAt: DateTimeOffset.Now);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
