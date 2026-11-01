using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading.Channels;
using CodexUsageWidget.Application;

namespace CodexUsageWidget.Infrastructure.Codex.Hooks;

public sealed class CodexActivityPipeSignalSource : ICodexActivitySignalSource
{
    private const int MaximumPayloadBytes = 4096;
    private const int MaximumIdentifierLength = 256;
    private const int DefaultReadTimeoutMilliseconds = 1000;

    private readonly string _pipeName;
    private readonly TimeSpan _readTimeout;
    private readonly string? _stateDirectory;
    private readonly object _signalLock = new();
    private readonly Dictionary<string, long> _lastTimestamps = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _stateVersions = new(StringComparer.Ordinal);
    private Task? _recoveryTask;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Channel<NamedPipeServerStream> _acceptedClients =
        Channel.CreateUnbounded<NamedPipeServerStream>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = false
        });
    private Task? _listenTask;
    private Task? _processTask;
    private int _disposed;

    public CodexActivityPipeSignalSource(
        string pipeName = CodexActivityPipeClient.DefaultPipeName,
        int readTimeoutMilliseconds = DefaultReadTimeoutMilliseconds,
        string? stateDirectory = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(readTimeoutMilliseconds);
        _pipeName = pipeName;
        _readTimeout = TimeSpan.FromMilliseconds(readTimeoutMilliseconds);
        _stateDirectory = stateDirectory ?? (pipeName == CodexActivityPipeClient.DefaultPipeName
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexUsageWidget", "activity-state")
            : null);
    }

    public event Action<CodexActivitySignal>? SignalReceived;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        _processTask ??= ProcessClientsAsync(_lifetime.Token);
        _listenTask ??= ListenAsync(_lifetime.Token);
        if (_stateDirectory is not null)
        {
            _recoveryTask ??= RecoverStateAsync(_lifetime.Token);
        }
        return Task.CompletedTask;
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var pipe = CreateServer();
                try
                {
                    await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    pipe.Dispose();
                    throw;
                }

                if (!_acceptedClients.Writer.TryWrite(pipe))
                {
                    pipe.Dispose();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            _acceptedClients.Writer.TryComplete();
        }
    }

    private NamedPipeServerStream CreateServer() => new(
        _pipeName,
        PipeDirection.In,
        NamedPipeServerStream.MaxAllowedServerInstances,
        PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
        MaximumPayloadBytes,
        MaximumPayloadBytes);

    private async Task ProcessClientsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var pipe in _acceptedClients.Reader
                               .ReadAllAsync(cancellationToken)
                               .ConfigureAwait(false))
            {
                using (pipe)
                {
                    using var readTimeout =
                        CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    readTimeout.CancelAfter(_readTimeout);
                    try
                    {
                        var signal = await ReadSignalAsync(pipe, readTimeout.Token)
                            .ConfigureAwait(false);
                        if (signal is not null)
                        {
                            DispatchSignal(signal);
                        }
                    }
                    catch (OperationCanceledException) when (readTimeout.IsCancellationRequested)
                    {
                    }
                    catch (IOException)
                    {
                        // One disconnected hook must not terminate delivery for all future hooks.
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            while (_acceptedClients.Reader.TryRead(out var pipe))
            {
                pipe.Dispose();
            }
        }
    }

    private void DispatchSignal(CodexActivitySignal signal)
    {
        lock (_signalLock)
        {
            if (signal.TimestampTicks > 0)
            {
                if (_lastTimestamps.TryGetValue(signal.SessionId, out var previous) && previous >= signal.TimestampTicks)
                {
                    return;
                }
                _lastTimestamps[signal.SessionId] = signal.TimestampTicks;
            }
            SignalReceived?.Invoke(signal);
        }
    }

    private async Task RecoverStateAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        try
        {
            do
            {
                try
                {
                    if (Directory.Exists(_stateDirectory))
                    {
                        foreach (var file in Directory.EnumerateFiles(_stateDirectory, "*.json"))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            try
                            {
                                var modified = File.GetLastWriteTimeUtc(file);
                                if (_stateVersions.TryGetValue(file, out var previous) && previous == modified)
                                {
                                    continue;
                                }
                                var json = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
                                var signal = JsonSerializer.Deserialize<CodexActivitySignal>(json);
                                if (IsValid(signal) && signal!.TimestampTicks > 0)
                                {
                                    DispatchSignal(signal with { Recovered = true });
                                    _stateVersions[file] = modified;
                                }
                            }
                            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                            {
                                // A state write can overlap a read. The next tick retries it.
                            }
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            } while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static async Task<CodexActivitySignal?> ReadSignalAsync(
        Stream input,
        CancellationToken cancellationToken)
    {
        var lengthPrefix = new byte[sizeof(int)];
        try
        {
            await input.ReadExactlyAsync(lengthPrefix, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is EndOfStreamException or IOException)
        {
            return null;
        }

        var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(lengthPrefix);
        if (payloadLength <= 0 || payloadLength > MaximumPayloadBytes)
        {
            return null;
        }

        var payload = new byte[payloadLength];
        try
        {
            await input.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
            var signal = JsonSerializer.Deserialize<CodexActivitySignal>(payload);
            return IsValid(signal) ? signal : null;
        }
        catch (Exception ex) when (ex is EndOfStreamException or IOException or JsonException)
        {
            return null;
        }
    }

    private static bool IsValid(CodexActivitySignal? signal) =>
        signal is not null &&
        !string.IsNullOrWhiteSpace(signal.SessionId) &&
        signal.SessionId.Length <= MaximumIdentifierLength &&
        (signal.Kind == CodexActivitySignalKind.SessionEnded ||
         (!string.IsNullOrWhiteSpace(signal.TurnId) &&
          signal.TurnId.Length <= MaximumIdentifierLength));

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _lifetime.CancelAsync().ConfigureAwait(false);
        if (_listenTask is not null)
        {
            await _listenTask.ConfigureAwait(false);
        }

        if (_processTask is not null)
        {
            await _processTask.ConfigureAwait(false);
        }

        if (_recoveryTask is not null)
        {
            await _recoveryTask.ConfigureAwait(false);
        }

        _lifetime.Dispose();
    }
}
