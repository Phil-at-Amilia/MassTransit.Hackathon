namespace MassTransit.Hackathon;

/// <summary>
/// Per-process async pause gate. When paused, consumers await this before processing
/// each message, which lets in-flight messages finish before the worker blocks.
/// Thread-safe and async-friendly.
/// </summary>
public sealed class PauseSignal
{
    // Starts as a completed TCS so WaitIfPausedAsync returns immediately when not paused.
    private volatile TaskCompletionSource<bool> _tcs = CreateCompleted();

    public bool IsPaused { get; private set; }

    /// <summary>
    /// If currently paused, waits asynchronously until <see cref="Resume"/> is called
    /// or <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    public Task WaitIfPausedAsync(CancellationToken cancellationToken)
    {
        var tcs = _tcs;
        if (tcs.Task.IsCompleted) return Task.CompletedTask;

        return tcs.Task.WaitAsync(cancellationToken);
    }

    public void Pause()
    {
        if (IsPaused) return;
        IsPaused = true;
        _tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public void Resume()
    {
        if (!IsPaused) return;
        IsPaused = false;
        _tcs.TrySetResult(true);
        _tcs = CreateCompleted();
    }

    private static TaskCompletionSource<bool> CreateCompleted()
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        tcs.SetResult(true);
        return tcs;
    }
}
