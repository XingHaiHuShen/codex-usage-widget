namespace CodexUsageWidget.Application;

public enum CodexActivitySignalKind
{
    TurnStarted,
    TurnStopped,
    SessionEnded
}

public sealed record CodexActivitySignal(
    CodexActivitySignalKind Kind,
    string SessionId,
    string? TurnId = null,
    long TimestampTicks = 0,
    bool Recovered = false);
