using Microsoft.Extensions.Hosting;

namespace MassTransit.Hackathon.Workers;

/// <summary>
/// When stdin is redirected (process spawned by the Dashboard), reads command lines
/// from the pipe and dispatches them:
/// <list type="bullet">
///   <item><c>pause</c>  — suspends message processing via <see cref="PauseSignal"/>.</item>
///   <item><c>resume</c> — resumes message processing.</item>
///   <item>EOF / pipe closed — initiates graceful host shutdown.</item>
/// </list>
/// Has no effect when stdin is not redirected (standalone / interactive mode).
/// </summary>
public sealed class StdinShutdownMonitor : BackgroundService
{
    private readonly IHostApplicationLifetime _lifetime;
    private readonly PauseSignal _pauseSignal;

    public StdinShutdownMonitor(IHostApplicationLifetime lifetime, PauseSignal pauseSignal)
    {
        _lifetime    = lifetime;
        _pauseSignal = pauseSignal;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Console.IsInputRedirected)
            return Task.CompletedTask; // running standalone — nothing to watch

        // Console.ReadLine() blocks, so we run it on a dedicated thread.
        return Task.Run(() =>
        {
            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    var line = Console.ReadLine();
                    if (line is null) break; // stdin pipe closed → graceful stop

                    switch (line.Trim().ToLowerInvariant())
                    {
                        case "pause":
                            _pauseSignal.Pause();
                            break;
                        case "resume":
                            _pauseSignal.Resume();
                            break;
                        // Unknown lines are silently ignored (forward-compatible).
                    }
                }
            }
            catch (IOException) { /* pipe broken */ }
            catch (OperationCanceledException) { return; }

            if (!stoppingToken.IsCancellationRequested)
                _lifetime.StopApplication();
        }, stoppingToken);
    }
}
