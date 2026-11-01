using System.Text.Json;
using CodexUsageWidget.Infrastructure.Codex;

namespace CodexUsageWidget.Tests;

public sealed class CodexUsageProviderTests
{
    [Fact]
    public async Task SlowTokenStatisticsNeverBlockLimitsOrCallerCancellation()
    {
        await using var session = new FakeCodexAppServerSession(
            """{ "rateLimits": { "limitId": "codex", "primary": { "usedPercent": 30 } } }""",
            slowTokenUsage: true);
        await using var provider = new CodexUsageProvider(session);
        using var caller = new CancellationTokenSource();
        var snapshot = await provider.ReadUsageAsync(caller.Token).WaitAsync(TimeSpan.FromSeconds(2));
        caller.Cancel();
        var second = await provider.ReadUsageAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Single(snapshot.GeneralWindows);
        Assert.Single(second.GeneralWindows);
        Assert.Null(snapshot.TokenActivity);
        Assert.Equal(1, session.TokenRequests);
    }

    [Fact]
    public async Task ReadUsageKeepsRateLimitsWhenTokenActivityFails()
    {
        await using var session = new FakeCodexAppServerSession(
            """{ "rateLimits": { "limitId": "codex", "primary": { "usedPercent": 30 } } }""",
            tokenUsageError: new InvalidOperationException("not supported"));
        await using var provider = new CodexUsageProvider(session);
        string? diagnostic = null;
        provider.DiagnosticMessage += (_, message) => diagnostic = message;

        var snapshot = await provider.ReadUsageAsync();

        Assert.Single(snapshot.GeneralWindows);
        Assert.Null(snapshot.TokenActivity);
        Assert.Contains("rate limits remain active", diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProviderForwardsRateLimitNotificationsOnly()
    {
        await using var session = new FakeCodexAppServerSession(
            """{ "rateLimits": { "limitId": "codex", "primary": { "usedPercent": 30 } } }""");
        await using var provider = new CodexUsageProvider(session);
        var notifications = 0;
        provider.RateLimitsChanged += (_, _) => notifications++;

        session.RaiseNotification("thread/updated");
        session.RaiseNotification("account/rateLimits/updated");

        Assert.Equal(1, notifications);
    }

    private sealed class FakeCodexAppServerSession : ICodexAppServerSession
    {
        private readonly JsonElement _rateLimits;
        private readonly JsonElement _tokenUsage;
        private readonly Exception? _tokenUsageError;
        private readonly bool _slowTokenUsage;
        public int TokenRequests { get; private set; }

        public FakeCodexAppServerSession(string rateLimits, Exception? tokenUsageError = null, bool slowTokenUsage = false)
        {
            _rateLimits = ParseAndClone(rateLimits);
            _tokenUsage = ParseAndClone("""{ "summary": {} }""");
            _tokenUsageError = tokenUsageError;
            _slowTokenUsage = slowTokenUsage;
        }

        public event EventHandler<string>? NotificationReceived;

        public event EventHandler<string>? DiagnosticMessage
        {
            add { }
            remove { }
        }

        public Task<JsonElement> RequestAsync(
            string method,
            object? parameters,
            CancellationToken cancellationToken)
        {
            _ = parameters;
            cancellationToken.ThrowIfCancellationRequested();
            if (method == "account/rateLimits/read")
            {
                return Task.FromResult(_rateLimits);
            }

            if (_tokenUsageError is not null)
            {
                return Task.FromException<JsonElement>(_tokenUsageError);
            }

            TokenRequests++;
            if (_slowTokenUsage)
            {
                return DelayTokenAsync(cancellationToken);
            }

            return Task.FromResult(_tokenUsage);
        }

        private async Task<JsonElement> DelayTokenAsync(CancellationToken token)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return _tokenUsage;
        }

        public void RaiseNotification(string method) =>
            NotificationReceived?.Invoke(this, method);

        public ValueTask DisposeAsync()
        {
            NotificationReceived = null;
            return ValueTask.CompletedTask;
        }

        private static JsonElement ParseAndClone(string json)
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
    }
}
