using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodexUsageWidget.Application;

namespace CodexUsageWidget.Infrastructure.Codex.Hooks;

public static class CodexActivityHookPayloadParser
{
    public static bool TryParse(string json, out CodexActivitySignal? signal)
    {
        try
        {
            var payload = JsonSerializer.Deserialize<HookPayloadFields>(json);
            signal = ToSignal(payload);
            return signal is not null;
        }
        catch (JsonException)
        {
            signal = null;
            return false;
        }
    }

    public static async Task<CodexActivitySignal?> ParseAsync(
        Stream input,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var payload = await JsonSerializer.DeserializeAsync<HookPayloadFields>(
                input,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return ToSignal(payload);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static CodexActivitySignal? ToSignal(HookPayloadFields? payload)
    {
        if (payload is null || string.IsNullOrWhiteSpace(payload.SessionId))
        {
            return null;
        }

        return payload.HookEventName switch
        {
            "UserPromptSubmit" when !string.IsNullOrWhiteSpace(payload.TurnId) =>
                new CodexActivitySignal(
                    CodexActivitySignalKind.TurnStarted,
                    payload.SessionId,
                    payload.TurnId),
            "Stop" or "Interrupt" when !string.IsNullOrWhiteSpace(payload.TurnId) =>
                new CodexActivitySignal(
                    CodexActivitySignalKind.TurnStopped,
                    payload.SessionId,
                    payload.TurnId),
            "SessionEnd" => new CodexActivitySignal(
                CodexActivitySignalKind.SessionEnded,
                payload.SessionId),
            _ => null
        };
    }

    private sealed class HookPayloadFields
    {
        [JsonPropertyName("hook_event_name")]
        public string? HookEventName { get; init; }

        [JsonPropertyName("session_id")]
        public string? SessionId { get; init; }

        [JsonPropertyName("turn_id")]
        public string? TurnId { get; init; }
    }
}
