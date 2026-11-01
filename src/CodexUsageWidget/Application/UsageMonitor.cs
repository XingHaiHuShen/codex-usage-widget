using CodexUsageWidget.Domain;
using CodexUsageWidget.Localization;

namespace CodexUsageWidget.Application;

public sealed class UsageMonitor : IAsyncDisposable
{
    private static readonly string[] TransientErrors = ["timed out", "timeout", "error sending request", "connection", "app-server stopped", "app-server exited", "503", "502"];
    private readonly IUsageProvider _provider;
    private readonly TimeSpan _refreshInterval;
    private readonly TimeSpan _requestTimeout;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private Task? _periodicRefreshTask;
    private bool _started;

    public UsageMonitor(
        IUsageProvider provider,
        TimeSpan? refreshInterval = null,
        TimeSpan? requestTimeout = null)
    {
        _provider = provider;
        _refreshInterval = refreshInterval ?? TimeSpan.FromMinutes(2);
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(20);
    }

    public event Action? RefreshStarted;

    public event Action<UsageSnapshot>? SnapshotUpdated;

    public event Action<string>? RefreshFailed;

    public event EventHandler<string>? DiagnosticMessage;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _provider.DiagnosticMessage += ProviderOnDiagnosticMessage;
        _provider.RateLimitsChanged += ProviderOnRateLimitsChanged;
        _periodicRefreshTask = RunPeriodicRefreshAsync(_lifetime.Token);
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (!await _refreshGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        await RefreshWithGateHeldAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RefreshAfterCurrentAsync(CancellationToken cancellationToken = default)
    {
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        await RefreshWithGateHeldAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RefreshWithGateHeldAsync(CancellationToken cancellationToken)
    {
        try
        {
            RefreshStarted?.Invoke();
            UsageSnapshot snapshot;
            for (var attempt = 0; ; attempt++)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
                timeout.CancelAfter(_requestTimeout);
                try
                {
                    snapshot = await _provider.ReadUsageAsync(timeout.Token).ConfigureAwait(false);
                    break;
                }
                catch (Exception ex) when (attempt == 0 && !cancellationToken.IsCancellationRequested &&
                    !_lifetime.IsCancellationRequested && IsTransient(ex))
                {
                    DiagnosticMessage?.Invoke(this, "Usage refresh temporarily failed; retrying once.");
                    using var retry = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
                    await Task.Delay(TimeSpan.FromSeconds(1), retry.Token).ConfigureAwait(false);
                }
            }
            SnapshotUpdated?.Invoke(snapshot);
        }
        catch (OperationCanceledException) when (!_lifetime.IsCancellationRequested)
        {
            DiagnosticMessage?.Invoke(this, "Usage refresh timed out; retaining the last successful reading.");
            RefreshFailed?.Invoke(Strings.Get("Error_ResponseTimeout"));
        }
        catch (Exception ex)
        {
            DiagnosticMessage?.Invoke(this, $"Usage refresh failed: {ex.Message}");
            RefreshFailed?.Invoke(UsageTextFormatter.ToFriendlyError(ex.Message));
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private static bool IsTransient(Exception ex) => ex is OperationCanceledException or System.IO.IOException ||
        TransientErrors
            .Any(value => ex.Message.Contains(value, StringComparison.OrdinalIgnoreCase));

    private void ProviderOnDiagnosticMessage(object? sender, string message) => DiagnosticMessage?.Invoke(this, message);

    private void ProviderOnRateLimitsChanged(object? sender, EventArgs e) =>
        _ = RefreshAsync(_lifetime.Token);

    private async Task RunPeriodicRefreshAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_refreshInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await RefreshAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        _provider.RateLimitsChanged -= ProviderOnRateLimitsChanged;
        _provider.DiagnosticMessage -= ProviderOnDiagnosticMessage;
        await _lifetime.CancelAsync().ConfigureAwait(false);

        if (_periodicRefreshTask is not null)
        {
            try
            {
                await _periodicRefreshTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        await _provider.DisposeAsync().ConfigureAwait(false);
        _refreshGate.Dispose();
        _lifetime.Dispose();
    }
}
