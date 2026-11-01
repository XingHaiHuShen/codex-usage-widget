using System.IO;
using System.Text;
using System.Text.Json.Nodes;

namespace CodexUsageWidget.Infrastructure.Codex.Hooks;

internal static class CodexActivityHookDefinition
{
    private const int LegacyPowerShellTimeoutSeconds = 3;
    private const int LegacyDirectTimeoutSeconds = 1;
    private const string HookArgument = "--codex-activity-hook";
    private const string WidgetExecutableName = "CodexUsageWidget.exe";
    private const string NestedPowerShellPrefix =
        "powershell.exe -NoLogo -NoProfile -NonInteractive " +
        "-ExecutionPolicy Bypass -Command \"& '";

    public static JsonObject CreateCurrentHandler() =>
        CreateHandler(CodexActivityHookBridge.Command, CodexActivityHookBridge.HookTimeoutSeconds);

    public static bool IsRecognized(JsonNode? handler)
    {
        if (handler is not JsonObject handlerObject ||
            handlerObject["command"] is not JsonValue commandValue ||
            !commandValue.TryGetValue<string>(out var command) ||
            !TryGetGeneratedCommandTimeout(command, out var timeoutSeconds))
        {
            return false;
        }

        return JsonNode.DeepEquals(handler, CreateHandler(command, timeoutSeconds)) ||
            (command == CodexActivityHookBridge.LegacyCommand && JsonNode.DeepEquals(handler, CreateHandler(command, 3)));
    }

    public static string BuildLegacyCommand(string processPath)
    {
        var escapedProcessPath = processPath.Replace("'", "''", StringComparison.Ordinal);
        return $"& '{escapedProcessPath}' {HookArgument}";
    }

    private static JsonObject CreateHandler(string command, int timeoutSeconds) => new()
    {
        ["type"] = "command",
        ["command"] = command,
        ["timeout"] = timeoutSeconds
    };

    private static bool TryGetGeneratedCommandTimeout(
        string command,
        out int timeoutSeconds)
    {
        if (string.Equals(command, CodexActivityHookBridge.Command, StringComparison.Ordinal))
        {
            timeoutSeconds = CodexActivityHookBridge.HookTimeoutSeconds;
            return true;
        }

        if (string.Equals(command, CodexActivityHookBridge.LegacyCommand, StringComparison.Ordinal))
        {
            timeoutSeconds = 5;
            return true;
        }

        if (TryReadSingleQuotedPath(
                command,
                "& '",
                $"' {HookArgument}",
                out var processPath) ||
            TryReadSingleQuotedPath(
                command,
                NestedPowerShellPrefix,
                $"' {HookArgument}\"",
                out processPath))
        {
            timeoutSeconds = LegacyPowerShellTimeoutSeconds;
            return IsWidgetExecutablePath(processPath);
        }

        const string legacyPrefix = "\"";
        var legacySuffix = $"\" {HookArgument}";
        if (command.StartsWith(legacyPrefix, StringComparison.Ordinal) &&
            command.EndsWith(legacySuffix, StringComparison.Ordinal) &&
            command.Length > legacyPrefix.Length + legacySuffix.Length)
        {
            var pathLength = command.Length - legacyPrefix.Length - legacySuffix.Length;
            processPath = command.Substring(legacyPrefix.Length, pathLength);
            if (!processPath.Contains('"', StringComparison.Ordinal))
            {
                timeoutSeconds = LegacyDirectTimeoutSeconds;
                return IsWidgetExecutablePath(processPath);
            }
        }

        timeoutSeconds = 0;
        return false;
    }

    private static bool TryReadSingleQuotedPath(
        string command,
        string prefix,
        string suffix,
        out string processPath)
    {
        processPath = string.Empty;
        if (!command.StartsWith(prefix, StringComparison.Ordinal) ||
            !command.EndsWith(suffix, StringComparison.Ordinal) ||
            command.Length <= prefix.Length + suffix.Length)
        {
            return false;
        }

        var escapedPath = command.AsSpan(
            prefix.Length,
            command.Length - prefix.Length - suffix.Length);
        var path = new StringBuilder(escapedPath.Length);
        for (var index = 0; index < escapedPath.Length; index++)
        {
            if (escapedPath[index] != '\'')
            {
                path.Append(escapedPath[index]);
                continue;
            }

            if (index + 1 >= escapedPath.Length || escapedPath[index + 1] != '\'')
            {
                return false;
            }

            path.Append('\'');
            index++;
        }

        processPath = path.ToString();
        return true;
    }

    private static bool IsWidgetExecutablePath(string processPath) =>
        Path.IsPathFullyQualified(processPath) &&
        string.Equals(
            Path.GetFileName(processPath),
            WidgetExecutableName,
            StringComparison.OrdinalIgnoreCase);
}
