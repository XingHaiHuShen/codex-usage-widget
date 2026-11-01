using System.Diagnostics;
using System.Text;
using Microsoft.Win32;

namespace CodexUsageWidget.Infrastructure.Codex;

internal static class CodexProcessStartInfoFactory
{
    private static readonly string[] ProxyVariables = ["HTTPS_PROXY", "https_proxy", "ALL_PROXY", "all_proxy"];
    public static ProcessStartInfo Create()
    {
        var executable = CodexExecutableLocator.Resolve();
        var isCommandScript = executable.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
                              executable.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);

        var info = isCommandScript
            ? CreateCommandScriptStartInfo(executable)
            : new ProcessStartInfo(executable);

        if (!isCommandScript)
        {
            info.ArgumentList.Add("app-server");
        }

        info.UseShellExecute = false;
        info.RedirectStandardInput = true;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        info.CreateNoWindow = true;
        info.StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        info.StandardOutputEncoding = Encoding.UTF8;
        info.StandardErrorEncoding = Encoding.UTF8;
        ApplySystemProxy(info);
        return info;
    }

    private static void ApplySystemProxy(ProcessStartInfo info)
    {
        // The Rust CLI does not automatically inherit Windows Internet Settings.
        // Respect explicit proxy variables and apply the system proxy only to this child.
        if (ProxyVariables
            .Any(name => info.Environment.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)))
        {
            return;
        }
        using var settings = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
        if (settings?.GetValue("ProxyEnable") is not int enabled || enabled != 1 ||
            settings.GetValue("ProxyServer") is not string proxy || string.IsNullOrWhiteSpace(proxy))
        {
            return;
        }
        if (proxy.Contains('=', StringComparison.Ordinal))
        {
            proxy = proxy.Split(';').Select(part => part.Split('=', 2))
                .FirstOrDefault(pair => pair.Length == 2 && pair[0].Equals("https", StringComparison.OrdinalIgnoreCase))?[1] ?? string.Empty;
        }
        if (Uri.TryCreate(proxy.Contains("://", StringComparison.Ordinal) ? proxy : "http://" + proxy, UriKind.Absolute, out var uri) &&
            uri.Scheme is "http" or "https" or "socks5")
        {
            info.Environment["HTTPS_PROXY"] = uri.AbsoluteUri;
            info.Environment["HTTP_PROXY"] = uri.AbsoluteUri;
        }
    }

    private static ProcessStartInfo CreateCommandScriptStartInfo(string executable)
    {
        var info = new ProcessStartInfo(
            Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe");
        info.ArgumentList.Add("/d");
        info.ArgumentList.Add("/c");
        info.ArgumentList.Add("call");
        info.ArgumentList.Add(executable);
        info.ArgumentList.Add("app-server");
        return info;
    }
}
