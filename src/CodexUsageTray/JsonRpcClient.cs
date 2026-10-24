using System.Collections.Concurrent;
using System.Text.Json;

namespace CodexUsageTray;

public sealed class PendingRequestRegistry
{
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();

    public Task<JsonElement> Add(long id)
    {
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(id, completion))
        {
            throw new InvalidOperationException($"Request id {id} is already pending.");
        }

        return completion.Task;
    }

    public bool Resolve(long id, JsonElement message)
    {
        return _pending.TryRemove(id, out var completion) && completion.TrySetResult(message.Clone());
    }

    public void Remove(long id)
    {
        _pending.TryRemove(id, out _);
    }

    public void Cancel(long id, Exception error)
    {
        if (_pending.TryRemove(id, out var completion))
        {
            completion.TrySetException(error);
        }
    }

    public void FailAll(Exception error)
    {
        foreach (var id in _pending.Keys)
        {
            Cancel(id, error);
        }
    }
}

public sealed class JsonRpcClient : IAsyncDisposable
{
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly PendingRequestRegistry _requests = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly AppLogger _logger;
    private readonly Task _readerTask;
    private long _nextId;

    public JsonRpcClient(StreamReader reader, StreamWriter writer, AppLogger logger, bool startReader = true)
    {
        _reader = reader;
        _writer = writer;
        _writer.AutoFlush = true;
        _logger = logger;
        _readerTask = startReader ? ReadLoopAsync(_shutdown.Token) : Task.CompletedTask;
    }

    public event EventHandler<string>? NotificationReceived;

    public async Task<JsonElement> RequestAsync(string method, object parameters, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _nextId);
        var responseTask = _requests.Add(id);
        try
        {
            await WriteAsync(new { method, id, @params = parameters }, cancellationToken);
            var response = await responseTask.WaitAsync(timeout, cancellationToken);
            if (response.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                var code = error.TryGetProperty("code", out var codeElement) && codeElement.ValueKind == JsonValueKind.Number &&
                    codeElement.TryGetInt32(out var parsedCode) ? parsedCode : -1;
                var message = error.TryGetProperty("message", out var messageElement) && messageElement.ValueKind == JsonValueKind.String
                    ? messageElement.GetString() : "Unknown JSON-RPC error";
                throw new JsonRpcException(code, SecretSanitizer.Sanitize(message));
            }

            if (!response.TryGetProperty("result", out var result))
            {
                throw new InvalidDataException($"JSON-RPC response {id} did not include result or error.");
            }

            return result.Clone();
        }
        catch
        {
            _requests.Remove(id);
            throw;
        }
    }

    public Task NotifyAsync(string method, object parameters, CancellationToken cancellationToken) =>
        WriteAsync(new { method, @params = parameters }, cancellationToken);

    public bool ProcessLine(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.TryGetProperty("method", out var methodElement) && methodElement.ValueKind == JsonValueKind.String)
            {
                if (root.TryGetProperty("id", out var requestId) && requestId.ValueKind is JsonValueKind.Number or JsonValueKind.String)
                {
                    _ = RespondUnsupportedRequestAsync(requestId.Clone());
                    return true;
                }

                NotificationReceived?.Invoke(this, methodElement.GetString()!);
                return true;
            }

            if (root.TryGetProperty("id", out var idElement) && idElement.TryGetInt64(out var id))
            {
                return _requests.Resolve(id, root);
            }
        }
        catch (JsonException error)
        {
            _logger.Error($"Malformed app-server JSON ignored: {error.Message}");
        }

        return false;
    }

    private async Task RespondUnsupportedRequestAsync(JsonElement id)
    {
        try
        {
            await WriteAsync(
                new
                {
                    id,
                    error = new { code = -32601, message = "Method not supported by Codex Usage Tray." }
                },
                _shutdown.Token);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception error)
        {
            _logger.Error($"Could not reject unsupported app-server request: {error.Message}");
        }
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await _reader.ReadLineAsync(cancellationToken);
                if (line is null)
                {
                    throw new EndOfStreamException("Codex app-server closed stdout.");
                }

                ProcessLine(line);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception error)
        {
            _requests.FailAll(error);
            _logger.Error($"App-server read loop stopped: {error.Message}");
        }
    }

    private async Task WriteAsync<T>(T message, CancellationToken cancellationToken)
    {
        var line = JsonSerializer.Serialize(message);
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await _writer.WriteLineAsync(line.AsMemory(), cancellationToken);
            await _writer.FlushAsync(cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        _requests.FailAll(new OperationCanceledException("JSON-RPC client stopped."));
        try { await _readerTask.WaitAsync(TimeSpan.FromSeconds(1)); } catch { }
        _writeGate.Dispose();
        _shutdown.Dispose();
    }
}
