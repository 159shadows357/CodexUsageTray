using System.IO.Pipes;
using System.Text;

namespace CodexUsageTray;

public sealed class SingleInstanceCoordinator : IDisposable
{
    private const string MutexName = "Local\\CodexUsageTray.1B7A1C0C";
    private const string PipeName = "CodexUsageTray.1B7A1C0C";
    private readonly Mutex _mutex = new(false, MutexName);
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _listener;
    private bool _ownsMutex;

    public event EventHandler? OpenRequested;
    public event EventHandler? ExitRequested;
    public event EventHandler? RefreshRequested;

    public bool TryAcquire()
    {
        try { _ownsMutex = _mutex.WaitOne(0, false); }
        catch (AbandonedMutexException) { _ownsMutex = true; }
        return _ownsMutex;
    }

    public void StartListening()
    {
        if (!_ownsMutex)
        {
            throw new InvalidOperationException("The instance coordinator does not own the mutex.");
        }

        _listener ??= ListenAsync(_shutdown.Token);
    }

    public static async Task SignalExistingAsync(string command = "open")
    {
        try
        {
            await using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.Asynchronous);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await pipe.ConnectAsync(timeout.Token);
            await pipe.WriteAsync(Encoding.UTF8.GetBytes(command), timeout.Token);
            await pipe.FlushAsync(timeout.Token);
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or UnauthorizedAccessException) { }
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(cancellationToken);
                using var reader = new StreamReader(pipe, Encoding.UTF8, false, 128, leaveOpen: true);
                var command = await reader.ReadToEndAsync(cancellationToken);
                if (command.Length > 0)
                {
                    if (command.StartsWith("exit", StringComparison.Ordinal)) ExitRequested?.Invoke(this, EventArgs.Empty);
                    else if (command.StartsWith("refresh", StringComparison.Ordinal)) RefreshRequested?.Invoke(this, EventArgs.Empty);
                    else if (command.StartsWith("open", StringComparison.Ordinal)) OpenRequested?.Invoke(this, EventArgs.Empty);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (IOException) { await Task.Delay(250, cancellationToken); }
        }
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        try { _listener?.Wait(TimeSpan.FromSeconds(1)); } catch { }
        if (_ownsMutex)
        {
            try { _mutex.ReleaseMutex(); } catch (ApplicationException) { }
        }
        _mutex.Dispose();
        _shutdown.Dispose();
    }
}
