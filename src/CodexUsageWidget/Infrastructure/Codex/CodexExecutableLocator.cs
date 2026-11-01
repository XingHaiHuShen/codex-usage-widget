using System.IO;

namespace CodexUsageWidget.Infrastructure.Codex;

internal static class CodexExecutableLocator
{
    private const string OverrideVariable = "CODEX_USAGE_WIDGET_CODEX_PATH";

    public static string Resolve()
    {
        var configuredPath = Environment.GetEnvironmentVariable(OverrideVariable);
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var expandedPath = Environment.ExpandEnvironmentVariables(configuredPath.Trim().Trim('"'));
            var managedBin = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin");
            if (Path.GetFullPath(expandedPath).StartsWith(managedBin + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                var current = ResolveManagedInstallation(managedBin);
                if (current is not null)
                {
                    return current;
                }
            }
            if (!File.Exists(expandedPath))
            {
                throw new FileNotFoundException(
                    $"The Codex executable configured in {OverrideVariable} does not exist.",
                    expandedPath);
            }

            return Path.GetFullPath(expandedPath);
        }

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var npmCodex = Path.Combine(appData, "npm", "codex.cmd");
        if (File.Exists(npmCodex))
        {
            return npmCodex;
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var directories = path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        foreach (var candidate in new[] { "codex.cmd", "codex.exe", "codex.bat" })
        {
            foreach (var rawDirectory in directories)
            {
                try
                {
                    var directory = rawDirectory.Trim().Trim('"');
                    var fullPath = Path.Combine(directory, candidate);
                    if (File.Exists(fullPath))
                    {
                        return fullPath;
                    }
                }
                catch (ArgumentException)
                {
                    // Ignore malformed PATH entries and continue searching.
                }
            }
        }

        throw new FileNotFoundException(
            "Codex CLI was not found. Install Codex and ensure codex.cmd is available on PATH.");
    }

    internal static string? ResolveManagedInstallation(string binDirectory) => Directory.Exists(binDirectory)
        ? Directory.EnumerateDirectories(binDirectory)
            .Select(directory => Path.Combine(directory, "codex.exe"))
            .Where(File.Exists)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault()
        : null;
}
