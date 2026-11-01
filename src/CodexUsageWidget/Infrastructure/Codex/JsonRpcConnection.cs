using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace CodexUsageWidget.Infrastructure.Codex;

internal sealed class JsonRpcConnection : IAsyncDisposable
{
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private Process? _process;
    private StreamWriter? _stdin;
    private Task? _readTask;
    private Task? _diagnosticTask;
    private long _nextRequestId;

    public event EventHandler<string>? NotificationReceived;

    public event EventHandler<string>? DiagnosticMessage;

    public event EventHandler? Closed;

    public bool IsRunning => _process is { HasExited: false } && _readTask is { IsCompleted: false };

    public void Start()
    {
        if (_process is not null)
        {
            throw new InvalidOperationException("This JSON-RPC connection has already been started.");
        }

        var process = new Process
        {
            StartInfo = CodexProcessStartInfoFactory.Create(),
            EnableRaisingEvents = true
        };
        process.Exited += ProcessOnExited;

        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("Unable to start codex app-server.");
        }

        _process = process;
        _stdin = process.StandardInput;
        _stdin.AutoFlush = true;
        _readTask = ReadLoopAsync(process.StandardOutput, _lifetime.Token);
        _diagnosticTask = ReadDiagnosticsAsync(process.StandardError, _lifetime.Token);
    }

    public async Task<JsonElement> RequestAsync(
        string method,
        object? parameters,
        CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _nextRequestId);
        var completion = new TaskCompletionSource<JsonElement>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(id, completion))
        {
            throw new InvalidOperationException("Could not track the Codex request.");
        }

        try
        {
            var payload = parameters is null
                ? new { method, id }
                : (object)new { method, id, @params = parameters };
            await WriteMessageAsync(payload, cancellationToken).ConfigureAwait(false);

            using var registration = cancellationToken.Register(
                () => completion.TrySetCanceled(cancellationToken));
            return await completion.Task.ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public Task NotifyAsync(string method, object parameters, CancellationToken cancellationToken) =>
        WriteMessageAsync(new { method, @params = parameters }, cancellationToken);

    private async Task WriteMessageAsync(object payload, CancellationToken cancellationToken)
    {
        var writer = _stdin ?? throw new InvalidOperationException("Codex app-server is not running.");
        var json = JsonSerializer.Serialize(payload);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await writer.WriteLineAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadLoopAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    FailPending(new IOException("Codex app-server output closed."));
                    break;
                }

                HandleIncomingLine(line);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            DiagnosticMessage?.Invoke(this, ex.Message);
            FailPending(ex);
        }
    }

    private void HandleIncomingLine(string line)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            DiagnosticMessage?.Invoke(this, $"Ignored non-JSON app-server output: {line}");
            return;
        }

        using (document)
        {
            var root = document.RootElement;
            if (TryCompletePendingRequest(root))
            {
                return;
            }

            if (root.TryGetProperty("method", out var methodElement) &&
                methodElement.GetString() is { } method)
            {
                NotificationReceived?.Invoke(this, method);
            }
        }
    }

    private bool TryCompletePendingRequest(JsonElement root)
    {
        if (!root.TryGetProperty("id", out var idElement) ||
            !idElement.TryGetInt64(out var id) ||
            !_pending.TryGetValue(id, out var completion))
        {
            return false;
        }

        if (root.TryGetProperty("error", out var error))
        {
            completion.TrySetException(new InvalidOperationException(FormatRpcError(error)));
        }
        else if (root.TryGetProperty("result", out var result))
        {
            completion.TrySetResult(result.Clone());
        }

        return true;
    }

    private async Task ReadDiagnosticsAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                if (!string.IsNullOrWhiteSpace(line))
                {
                    DiagnosticMessage?.Invoke(this, line);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void ProcessOnExited(object? sender, EventArgs e)
    {
        var exitCode = _process?.ExitCode;
        var message = exitCode == 0
            ? "Codex app-server stopped."
            : $"Codex app-server exited with code {exitCode?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}.";
        DiagnosticMessage?.Invoke(this, message);
        FailPending(new InvalidOperationException(message));
        Closed?.Invoke(this, EventArgs.Empty);
    }

    private static string FormatRpcError(JsonElement error)
    {
        var code = error.TryGetProperty("code", out var codeElement) ? codeElement.ToString() : "unknown";
        var message = error.TryGetProperty("message", out var messageElement)
            ? messageElement.GetString()
            : error.ToString();
        return $"Codex app-server error {code}: {message}";
    }

    private void FailPending(Exception exception)
    {
        foreach (var completion in _pending.Values)
        {
            completion.TrySetException(exception);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        FailPending(new OperationCanceledException("Codex usage client is shutting down."));

        if (_stdin is not null)
        {
            try
            {
                await _stdin.DisposeAsync().ConfigureAwait(false);
            }
            catch (IOException)
            {
            }
        }

        if (_process is { HasExited: false } process)
        {
            try
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
            }
        }

        await AwaitBackgroundTaskAsync(_readTask).ConfigureAwait(false);
        await AwaitBackgroundTaskAsync(_diagnosticTask).ConfigureAwait(false);

        if (_process is not null)
        {
            _process.Exited -= ProcessOnExited;
            _process.Dispose();
        }

        _writeLock.Dispose();
        _lifetime.Dispose();
    }

    private static async Task AwaitBackgroundTaskAsync(Task? task)
    {
        if (task is null)
        {
            return;
        }

        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
    }
}
