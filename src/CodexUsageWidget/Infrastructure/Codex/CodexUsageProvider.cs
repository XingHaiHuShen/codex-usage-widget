using CodexUsageWidget.Application;
using CodexUsageWidget.Domain;

namespace CodexUsageWidget.Infrastructure.Codex;

public sealed class CodexUsageProvider : IUsageProvider
{
    private readonly ICodexAppServerSession _session;
    private readonly ICodexAppServerSession _tokenSession;
    private readonly object _tokenLock = new();
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _tokenRefresh;
    private TokenActivitySummary? _tokenActivity;
    private DateTimeOffset _nextTokenRefresh;
    private bool _disposed;

    public CodexUsageProvider(ICodexAppServerSession session, ICodexAppServerSession? tokenSession = null)
    {
        _session = session;
        _tokenSession = tokenSession ?? session;
        _session.NotificationReceived += SessionOnNotificationReceived;
        _session.DiagnosticMessage += SessionOnDiagnosticMessage;
    }

    public event EventHandler? RateLimitsChanged;

    public event EventHandler<string>? DiagnosticMessage;

    public async Task<UsageSnapshot> ReadUsageAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var rateLimitsTask = _session.RequestAsync(
            "account/rateLimits/read",
            parameters: null,
            cancellationToken);
        var rateLimits = CodexRateLimitsParser.Parse(
            await rateLimitsTask.ConfigureAwait(false));
        lock (_tokenLock)
        {
            if ((_tokenRefresh is null || _tokenRefresh.IsCompleted) && DateTimeOffset.UtcNow >= _nextTokenRefresh)
            {
                _nextTokenRefresh = DateTimeOffset.UtcNow.AddMinutes(5);
                _tokenRefresh = RefreshTokenActivityAsync();
            }

            return new UsageSnapshot(rateLimits, _tokenActivity, DateTimeOffset.Now);
        }
    }

    private async Task RefreshTokenActivityAsync()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            var result = await _tokenSession.RequestAsync(
                    "account/usage/read",
                    parameters: null,
                    timeout.Token)
                .ConfigureAwait(false);
            var activity = CodexTokenUsageParser.Parse(result);
            lock (_tokenLock)
            {
                _tokenActivity = activity;
            }
            // A later normal refresh includes the cached statistics. Core limits never wait for them.
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            lock (_tokenLock)
            {
                _nextTokenRefresh = DateTimeOffset.UtcNow.AddMinutes(1);
            }
            DiagnosticMessage?.Invoke(
                this,
                $"Token activity is unavailable; rate limits remain active. {ex.Message}");
        }
    }

    private void SessionOnNotificationReceived(object? sender, string method)
    {
        if (method == "account/rateLimits/updated")
        {
            RateLimitsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void SessionOnDiagnosticMessage(object? sender, string message) =>
        DiagnosticMessage?.Invoke(this, message);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _lifetime.CancelAsync().ConfigureAwait(false);
        if (_tokenRefresh is { } refresh)
        {
            await refresh.ConfigureAwait(false);
        }
        _session.NotificationReceived -= SessionOnNotificationReceived;
        _session.DiagnosticMessage -= SessionOnDiagnosticMessage;
        await _session.DisposeAsync().ConfigureAwait(false);
        if (!ReferenceEquals(_tokenSession, _session))
        {
            await _tokenSession.DisposeAsync().ConfigureAwait(false);
        }
        _lifetime.Dispose();
    }
}
