using System.Text.Json.Nodes;
using System.Text.Json;
using System.Diagnostics;
using CodexUsageWidget.Application;
using CodexUsageWidget.Infrastructure.Codex.Hooks;

namespace CodexUsageWidget.Tests;

public sealed class CodexHookConfigurationManagerTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "CodexUsageWidget.Tests",
        Guid.NewGuid().ToString("N"));

    private string HooksPath => Path.Combine(_directory, "hooks.json");

    private string ConfigPath => Path.Combine(_directory, "config.toml");

    [Fact]
    public async Task HookWhileWidgetClosedRecoversStartAndStopFromMinimalState()
    {
        var pipeName = "CodexUsageWidget.Tests." + Guid.NewGuid().ToString("N");
        var info = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", CodexActivityHookBridge.BuildCommand(pipeName, _directory) })
        {
            info.ArgumentList.Add(argument);
        }
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.StandardInput.WriteAsync("""{"hook_event_name":"UserPromptSubmit","session_id":"session","turn_id":"turn","prompt":"private content"}""");
        process.StandardInput.Close();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Contains("\"continue\":true", await stdout, StringComparison.Ordinal);
        Assert.Contains("delivery failed", await stderr, StringComparison.Ordinal);
        var path = Path.Combine(_directory, "session.json");
        var saved = File.ReadAllText(path);
        Assert.DoesNotContain("private content", saved, StringComparison.Ordinal);
        var signal = JsonSerializer.Deserialize<CodexActivitySignal>(saved)!;

        var source = new CodexActivityPipeSignalSource(pipeName, stateDirectory: _directory);
        await using var monitor = new CodexActivityMonitor(source);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var signals = 0;
        source.SignalReceived += _ => signals++;
        monitor.ActivityChanged += active => { if (active) { start.TrySetResult(); } else { stop.TrySetResult(); } };
        await monitor.StartAsync();
        await start.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(monitor.IsActive);
        Assert.True(monitor.IsUncertain);
        var stopped = signal with { Kind = CodexActivitySignalKind.TurnStopped, TimestampTicks = signal.TimestampTicks + 1 };
        File.WriteAllText(path, JsonSerializer.Serialize(stopped));
        await stop.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(monitor.IsActive);
        Assert.False(monitor.IsUncertain);
        Assert.Equal(2, signals);
    }

    [Fact]
    public void InstallCreatesNewHooksFile()
    {
        var manager = CreateManager();

        var plan = manager.PlanInstall();
        manager.Apply(plan);

        var hooks = JsonNode.Parse(File.ReadAllText(HooksPath))!["hooks"]!.AsObject();
        Assert.Single(hooks["UserPromptSubmit"]!.AsArray());
        Assert.Single(hooks["Stop"]!.AsArray());
        Assert.Single(hooks["SessionEnd"]!.AsArray());
        var content = File.ReadAllText(HooksPath);
        Assert.Contains("NamedPipeClientStream", content, StringComparison.Ordinal);
        Assert.DoesNotContain("CodexUsageWidget.exe", content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InstallMergesExistingHooksAndUnknownFields()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(HooksPath, """
            {
              "description": "keep me",
              "unknown": { "value": 42 },
              "hooks": {
                "PreToolUse": [{ "hooks": [{ "type": "command", "command": "other" }] }]
              }
            }
            """);
        var manager = CreateManager();

        manager.Apply(manager.PlanInstall());

        var root = JsonNode.Parse(File.ReadAllText(HooksPath))!.AsObject();
        Assert.Equal("keep me", root["description"]!.GetValue<string>());
        Assert.Equal(42, root["unknown"]!["value"]!.GetValue<int>());
        Assert.Single(root["hooks"]!["PreToolUse"]!.AsArray());
    }

    [Fact]
    public void RepeatedInstallIsIdempotent()
    {
        var manager = CreateManager();
        manager.Apply(manager.PlanInstall());

        var secondPlan = manager.PlanInstall();

        Assert.False(secondPlan.HasChanges);
        Assert.Null(secondPlan.Error);
    }

    [Fact]
    public void InstalledHandlerIsIndependentOfExecutableLocation()
    {
        var firstWidgetPath = WidgetPath();
        var secondWidgetPath = Path.Combine(
            _directory,
            "Another Temporary Extraction",
            "CodexUsageWidget.exe");
        var manager = CreateManager();
        manager.Apply(manager.PlanInstall());

        var content = File.ReadAllText(HooksPath);
        var secondPlan = manager.PlanInstall();

        Assert.DoesNotContain(firstWidgetPath, content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(secondWidgetPath, content, StringComparison.OrdinalIgnoreCase);
        Assert.False(secondPlan.HasChanges);
    }

    [Fact]
    public void UninstallRemovesOnlyExactWidgetHandlers()
    {
        var manager = CreateManager();
        manager.Apply(manager.PlanInstall());
        var root = JsonNode.Parse(File.ReadAllText(HooksPath))!.AsObject();
        root["hooks"]!["Stop"]!.AsArray().Add(new JsonObject
        {
            ["hooks"] = new JsonArray(new JsonObject
            {
                ["type"] = "command",
                ["command"] = "other-widget --codex-activity-hook",
                ["timeout"] = 1
            })
        });
        File.WriteAllText(HooksPath, root.ToJsonString());

        manager.Apply(manager.PlanUninstall());

        var content = File.ReadAllText(HooksPath);
        Assert.DoesNotContain(
            CodexActivityHookBridge.Command,
            content,
            StringComparison.Ordinal);
        Assert.Contains("other-widget --codex-activity-hook", content, StringComparison.Ordinal);
    }

    [Fact]
    public void MalformedExistingJsonRemainsUntouched()
    {
        Directory.CreateDirectory(_directory);
        const string Malformed = "{ not json";
        File.WriteAllText(HooksPath, Malformed);
        var manager = CreateManager();

        var plan = manager.PlanInstall();

        Assert.NotNull(plan.Error);
        Assert.False(plan.HasChanges);
        Assert.Equal(Malformed, File.ReadAllText(HooksPath));
    }

    [Fact]
    public void HookCommandRunsThroughPowerShellFromAPowerShellHookShell()
    {
        var hookDirectory = Path.Combine(_directory, "Hook's Folder");
        Directory.CreateDirectory(hookDirectory);
        var hookPath = Path.Combine(hookDirectory, "activity hook.cmd");
        File.WriteAllText(
            hookPath,
            "@echo off\r\nif not \"%~1\"==\"--codex-activity-hook\" exit /B 7\r\nexit /B 0\r\n");
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(CodexHookConfigurationManager.BuildLegacyHookCommand(hookPath));

        using var process = System.Diagnostics.Process.Start(startInfo)!;
        process.WaitForExit();

        Assert.Equal(0, process.ExitCode);
    }

    [Fact]
    public void HookCommandDoesNotSpawnNestedPowerShell()
    {
        var command = CodexHookConfigurationManager.BuildLegacyHookCommand(WidgetPath());

        Assert.StartsWith("& '", command, StringComparison.Ordinal);
        Assert.DoesNotContain("powershell.exe", command, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InstallReplacesLegacyDirectExecutableHandlers()
    {
        Directory.CreateDirectory(_directory);
        var legacyCommand = $"\"{WidgetPath()}\" --codex-activity-hook";
        File.WriteAllText(HooksPath, $$"""
            {
              "hooks": {
                "UserPromptSubmit": [{ "hooks": [{ "type": "command", "command": {{JsonValue.Create(legacyCommand)!.ToJsonString()}}, "timeout": 1 }] }],
                "Stop": [{ "hooks": [{ "type": "command", "command": {{JsonValue.Create(legacyCommand)!.ToJsonString()}}, "timeout": 1 }] }],
                "SessionEnd": [{ "hooks": [{ "type": "command", "command": {{JsonValue.Create(legacyCommand)!.ToJsonString()}}, "timeout": 1 }] }]
              }
            }
            """);

        var manager = CreateManager();
        var plan = manager.PlanInstall();

        Assert.True(plan.HasRecognizedHandlers);
        manager.Apply(plan);

        var content = File.ReadAllText(HooksPath);
        Assert.DoesNotContain(legacyCommand, content, StringComparison.Ordinal);
        var hooks = JsonNode.Parse(content)!["hooks"]!.AsObject();
        foreach (var eventName in new[] { "UserPromptSubmit", "Stop", "SessionEnd" })
        {
            var groups = hooks[eventName]!.AsArray();
            var handler = groups.Single()!["hooks"]!.AsArray().Single()!;
            Assert.Equal(
                CodexActivityHookBridge.Command,
                handler["command"]!.GetValue<string>());
            Assert.Equal(
                CodexActivityHookBridge.HookTimeoutSeconds,
                handler["timeout"]!.GetValue<int>());
        }
    }

    [Fact]
    public void InstallReplacesNestedPowerShellHandlers()
    {
        Directory.CreateDirectory(_directory);
        var escapedPath = WidgetPath().Replace("'", "''", StringComparison.Ordinal);
        var nestedCommand = "powershell.exe -NoLogo -NoProfile -NonInteractive " +
            "-ExecutionPolicy Bypass -Command " +
            $"\"& '{escapedPath}' --codex-activity-hook\"";
        File.WriteAllText(HooksPath, $$"""
            {
              "hooks": {
                "UserPromptSubmit": [{ "hooks": [{ "type": "command", "command": {{JsonValue.Create(nestedCommand)!.ToJsonString()}}, "timeout": 3 }] }],
                "Stop": [{ "hooks": [{ "type": "command", "command": {{JsonValue.Create(nestedCommand)!.ToJsonString()}}, "timeout": 3 }] }],
                "SessionEnd": [{ "hooks": [{ "type": "command", "command": {{JsonValue.Create(nestedCommand)!.ToJsonString()}}, "timeout": 3 }] }]
              }
            }
            """);

        var manager = CreateManager();
        manager.Apply(manager.PlanInstall());

        var content = File.ReadAllText(HooksPath);
        Assert.DoesNotContain("powershell.exe", content, StringComparison.OrdinalIgnoreCase);
        var hooks = JsonNode.Parse(content)!["hooks"]!.AsObject();
        foreach (var eventName in new[] { "UserPromptSubmit", "Stop", "SessionEnd" })
        {
            var handler = hooks[eventName]!.AsArray().Single()!["hooks"]!.AsArray().Single()!;
            Assert.Equal(
                CodexActivityHookBridge.Command,
                handler["command"]!.GetValue<string>());
        }
    }

    [Fact]
    public void InstallFromNewPathMigratesRecognizedHandlersFromOldPath()
    {
        var oldWidgetPath = Path.Combine(
            _directory,
            "Old Portable's Folder",
            "CodexUsageWidget.exe");
        var oldCommand = CodexHookConfigurationManager.BuildLegacyHookCommand(oldWidgetPath);
        WriteActivityHandlers((oldCommand, 3));

        var manager = CreateManager();
        manager.Apply(manager.PlanInstall());

        var content = File.ReadAllText(HooksPath);
        Assert.DoesNotContain(oldCommand, content, StringComparison.Ordinal);
        var hooks = JsonNode.Parse(content)!["hooks"]!.AsObject();
        foreach (var eventName in new[] { "UserPromptSubmit", "Stop", "SessionEnd" })
        {
            var handler = hooks[eventName]!.AsArray().Single()!["hooks"]!.AsArray().Single()!;
            Assert.Equal(
                CodexActivityHookBridge.Command,
                handler["command"]!.GetValue<string>());
        }
    }

    [Fact]
    public void MultipleRecognizedOldCopiesProduceOneCurrentHandlerPerEvent()
    {
        var oldWidgetPath = Path.Combine(_directory, "Old Folder", "CodexUsageWidget.exe");
        var escapedOldPath = oldWidgetPath.Replace("'", "''", StringComparison.Ordinal);
        var oldPowerShellCommand = CodexHookConfigurationManager.BuildLegacyHookCommand(oldWidgetPath);
        var oldLegacyCommand = $"\"{oldWidgetPath}\" --codex-activity-hook";
        var oldNestedCommand = "powershell.exe -NoLogo -NoProfile -NonInteractive " +
            "-ExecutionPolicy Bypass -Command " +
            $"\"& '{escapedOldPath}' --codex-activity-hook\"";
        WriteActivityHandlers(
            (oldPowerShellCommand, 3),
            (oldLegacyCommand, 1),
            (oldNestedCommand, 3));

        var manager = CreateManager();
        manager.Apply(manager.PlanInstall());

        var hooks = JsonNode.Parse(File.ReadAllText(HooksPath))!["hooks"]!.AsObject();
        foreach (var eventName in new[] { "UserPromptSubmit", "Stop", "SessionEnd" })
        {
            var groups = hooks[eventName]!.AsArray();
            var handlers = groups
                .Select(group => group!["hooks"]!.AsArray())
                .SelectMany(items => items)
                .ToArray();
            var handler = Assert.Single(handlers);
            Assert.Equal(
                CodexActivityHookBridge.Command,
                handler!["command"]!.GetValue<string>());
        }
    }

    [Fact]
    public void UninstallRemovesCurrentAndRecognizedOldWidgetHandlers()
    {
        var oldWidgetPath = Path.Combine(_directory, "Old Folder", "CodexUsageWidget.exe");
        var oldLegacyCommand = $"\"{oldWidgetPath}\" --codex-activity-hook";
        WriteActivityHandlers(
            (CodexActivityHookBridge.Command, CodexActivityHookBridge.HookTimeoutSeconds),
            (CodexHookConfigurationManager.BuildLegacyHookCommand(oldWidgetPath), 3),
            (oldLegacyCommand, 1));
        var manager = CreateManager();

        manager.Apply(manager.PlanUninstall());

        var content = File.ReadAllText(HooksPath);
        Assert.DoesNotContain("CodexUsageWidget.exe", content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UninstallPreservesOtherApplicationsAndUnknownDefinitions()
    {
        var otherCommand = CodexHookConfigurationManager.BuildLegacyHookCommand(
            Path.Combine(_directory, "OtherApp.exe"));
        var unknownCommand =
            CodexHookConfigurationManager.BuildLegacyHookCommand(WidgetPath()) + " --unknown";
        WriteActivityHandlers(
            (CodexActivityHookBridge.Command, CodexActivityHookBridge.HookTimeoutSeconds),
            (otherCommand, 3),
            (unknownCommand, 3));
        var root = JsonNode.Parse(File.ReadAllText(HooksPath))!.AsObject();
        root["description"] = "keep me";
        root["unknown"] = new JsonObject { ["value"] = 42 };
        root["hooks"]!["PreToolUse"] = new JsonArray(new JsonObject
        {
            ["matcher"] = "Bash",
            ["hooks"] = new JsonArray(new JsonObject
            {
                ["type"] = "command",
                ["command"] = "other-hook"
            })
        });
        File.WriteAllText(HooksPath, root.ToJsonString());
        var manager = CreateManager();

        manager.Apply(manager.PlanUninstall());

        var content = File.ReadAllText(HooksPath);
        var result = JsonNode.Parse(content)!.AsObject();
        var remainingCommands = result["hooks"]!["Stop"]!.AsArray()
            .Select(group => group!["hooks"]!.AsArray())
            .SelectMany(handlers => handlers)
            .Select(handler => handler!["command"]!.GetValue<string>())
            .ToArray();
        Assert.Contains(otherCommand, remainingCommands);
        Assert.Contains(unknownCommand, remainingCommands);
        Assert.Equal("keep me", result["description"]!.GetValue<string>());
        Assert.Equal(42, result["unknown"]!["value"]!.GetValue<int>());
        Assert.Single(result["hooks"]!["PreToolUse"]!.AsArray());
    }

    [Fact]
    public void ExplicitlyDisabledHooksProduceActionableError()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(ConfigPath, "[features]\nhooks = false\n");

        var plan = CreateManager().PlanInstall();

        Assert.Contains("explicitly disabled", plan.Error, StringComparison.Ordinal);
        Assert.Equal(CodexHookConfigurationErrorKind.HooksDisabled, plan.ErrorKind);
        Assert.False(plan.HasChanges);
    }

    [Fact]
    public void UninstallRemainsAvailableWhenHooksFeatureIsDisabled()
    {
        var manager = CreateManager();
        manager.Apply(manager.PlanInstall());
        File.WriteAllText(ConfigPath, "[features]\nhooks = false\n");

        var plan = manager.PlanUninstall();
        manager.Apply(plan);

        Assert.True(plan.HasChanges);
        Assert.Null(plan.Error);
        Assert.DoesNotContain(
            "CodexUsageWidget.exe",
            File.ReadAllText(HooksPath),
            StringComparison.OrdinalIgnoreCase);
    }

    private CodexHookConfigurationManager CreateManager() => new(HooksPath, ConfigPath);

    private void WriteActivityHandlers(params (string Command, int Timeout)[] handlers)
    {
        Directory.CreateDirectory(_directory);
        var hooks = new JsonObject();
        foreach (var eventName in new[] { "UserPromptSubmit", "Stop", "SessionEnd" })
        {
            var groups = new JsonArray();
            foreach (var (command, timeout) in handlers)
            {
                groups.Add(new JsonObject
                {
                    ["hooks"] = new JsonArray(new JsonObject
                    {
                        ["type"] = "command",
                        ["command"] = command,
                        ["timeout"] = timeout
                    })
                });
            }

            hooks[eventName] = groups;
        }

        File.WriteAllText(HooksPath, new JsonObject { ["hooks"] = hooks }.ToJsonString());
    }

    private string WidgetPath() => Path.Combine(
        _directory,
        "Widget's Folder",
        "CodexUsageWidget.exe");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
