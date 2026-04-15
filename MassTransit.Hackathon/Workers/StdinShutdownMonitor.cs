using Microsoft.Extensions.Hosting;

namespace MassTransit.Hackathon.Workers;

/// <summary>
/// When stdin is redirected (process spawned by the Dashboard), watches for stdin to close.
/// Closing stdin from the Dashboard is the signal for a graceful shutdown of this worker.
/// Has no effect when stdin is not redirected (standalone / interactive mode).
/// </summary>
public sealed class StdinShutdownMonitor : BackgroundService
{
    private readonly IHostApplicationLifetime _lifetime;

    public StdinShutdownMonitor(IHostApplicationLifetime lifetime) => _lifetime = lifetime;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Console.IsInputRedirected)
            return Task.CompletedTask; // running standalone — nothing to watch

        // Console.ReadLine() blocks, so we run it on a dedicated thread.
        return Task.Run(() =>
        {
            try
            {
                // ReadLine() returns null when the pipe is closed by the Dashboard.
                while (!stoppingToken.IsCancellationRequested)
                {
                    var line = Console.ReadLine();
                    if (line is null) break; // stdin pipe was closed → graceful stop
                }
            }
            catch (IOException) { /* pipe broken */ }
            catch (OperationCanceledException) { return; }

            if (!stoppingToken.IsCancellationRequested)
                _lifetime.StopApplication();
        }, stoppingToken);
    }
}
